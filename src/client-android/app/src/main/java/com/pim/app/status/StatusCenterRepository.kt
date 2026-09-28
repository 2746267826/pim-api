package com.pim.app.status

import com.pim.app.data.AppDatabase
import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileLocationPolicyTransitionEntity
import com.pim.app.location.PolicyTransitionWriteFailure
import com.pim.app.location.PolicyTransitionWriteFailureSource
import com.pim.app.location.service.ForegroundLocationService
import com.pim.app.location.service.ForegroundLocationRuntimeState
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.mobile.sync.MobileSyncCoordinator
import com.pim.app.mobile.sync.MobileSyncState
import com.pim.app.permissions.PermissionStatusRepository
import com.pim.app.schedule.ScheduleCacheSnapshot
import com.pim.app.schedule.ScheduleWindowRepository
import com.pim.app.settings.TrackingSettingsStore
import com.pim.core.auth.TokenManager
import com.pim.core.settings.ServerSettingsStore
import com.pim.core.settings.PimServerEndpoints
import com.pim.core.settings.ServerUrlValidator
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.flowOn
import kotlinx.coroutines.flow.transform

private data class CoreFacts(
    val queues: QueueStatusSnapshot,
    val diagnostics: DiagnosticSnapshot,
    val syncState: MobileSyncState,
    val runtime: ForegroundLocationRuntimeState,
    val policyTransitionWriteFailure: PolicyTransitionWriteFailure
)

private data class ScheduleFacts(
    val scheduleSnapshot: ScheduleCacheSnapshot,
    val latestTransition: PolicyTransitionSnapshot?
)

private data class ExternalFacts(
    val probeResult: ConnectionProbeResult?,
    val networkAvailability: NetworkAvailability,
    val workInfos: StatusWorkInfos,
    val permanentRejected: Int,
    val accepted: StatusAcceptedState
)

internal data class StatusEmission(
    val state: StatusCenterState,
    val clearAcceptedGenerationAfterEmission: Long?
)

internal fun Flow<StatusEmission>.emitStates(clearAccepted: (Long) -> Unit): Flow<StatusCenterState> =
    transform { emission ->
        val generation = emission.clearAcceptedGenerationAfterEmission
        if (generation == null) {
            emit(emission.state)
            return@transform
        }
        try {
            emit(emission.state)
        } finally {
            clearAccepted(generation)
        }
    }

@Singleton
class StatusCenterRepository internal constructor(
    private val permissionStatusRepository: PermissionStatusRepository,
    private val serverSettingsStore: ServerSettingsStore,
    private val tokenManager: TokenManager,
    private val trackingSettingsStore: TrackingSettingsStore,
    private val database: AppDatabase,
    private val syncCoordinator: MobileSyncCoordinator,
    private val refreshSignal: StatusRefreshSignal,
    private val logRepository: StructuredLogRepository,
    private val connectionProbeStore: ConnectionProbeStore,
    private val networkStatusProvider: NetworkStatusProvider,
    private val workInfoStatusProvider: WorkInfoStatusProvider,
    private val acceptedSignal: StatusAcceptedSignal,
    private val scheduleWindowRepository: ScheduleWindowRepository,
    private val queueStatusRepository: QueueStatusRepository,
    private val policyTransitionHistorySource: PolicyTransitionHistorySource,
    private val policyTransitionWriteFailureSource: PolicyTransitionWriteFailureSource,
    private val nowMillis: () -> Long
) {
    @Inject
    constructor(
        permissionStatusRepository: PermissionStatusRepository,
        serverSettingsStore: ServerSettingsStore,
        tokenManager: TokenManager,
        trackingSettingsStore: TrackingSettingsStore,
        database: AppDatabase,
        syncCoordinator: MobileSyncCoordinator,
        refreshSignal: StatusRefreshSignal,
        logRepository: StructuredLogRepository,
        connectionProbeStore: ConnectionProbeStore,
        networkStatusProvider: NetworkStatusProvider,
        workInfoStatusProvider: WorkInfoStatusProvider,
        acceptedSignal: StatusAcceptedSignal,
        scheduleWindowRepository: ScheduleWindowRepository,
        queueStatusRepository: QueueStatusRepository,
        policyTransitionHistorySource: PolicyTransitionHistorySource,
        policyTransitionWriteFailureSource: PolicyTransitionWriteFailureSource
    ) : this(
        permissionStatusRepository = permissionStatusRepository,
        serverSettingsStore = serverSettingsStore,
        tokenManager = tokenManager,
        trackingSettingsStore = trackingSettingsStore,
        database = database,
        syncCoordinator = syncCoordinator,
        refreshSignal = refreshSignal,
        logRepository = logRepository,
        connectionProbeStore = connectionProbeStore,
        networkStatusProvider = networkStatusProvider,
        workInfoStatusProvider = workInfoStatusProvider,
        acceptedSignal = acceptedSignal,
        scheduleWindowRepository = scheduleWindowRepository,
        queueStatusRepository = queueStatusRepository,
        policyTransitionHistorySource = policyTransitionHistorySource,
        policyTransitionWriteFailureSource = policyTransitionWriteFailureSource,
        nowMillis = System::currentTimeMillis
    ) { }

    private val dao: MobileDataDao = database.mobileDataDao()

    fun observe(): Flow<StatusCenterState> {
        val coreFlow = combine(
            queueStatusRepository.observe(),
            diagnosticSnapshotFlow(),
            syncCoordinator.currentState,
            ForegroundLocationService.runtimeState,
            policyTransitionWriteFailureSource.state
        ) { queues, diagnostics, syncState, runtime, writeFailure ->
            CoreFacts(queues, diagnostics, syncState, runtime, writeFailure)
        }

        // REQ-4：不再取「最近 5 条」，只取最新 1 条；已持续时长在**每次重建快照时**重算（D-12）。
        val scheduleFlow = scheduleWindowRepository.snapshot
            .combine(policyTransitionHistorySource.observeLatest()) { snap, latest ->
                ScheduleFacts(
                    scheduleSnapshot = snap,
                    latestTransition = latest
                )
            }

        val externalFlow = combine(
            connectionProbeStore.result,
            networkStatusProvider.availability,
            workInfoStatusProvider.syncWorkInfos,
            dao.aggregateRejectedCount(),
            acceptedSignal.state
        ) { probeResult, availability, workInfos, rejected, accepted ->
            ExternalFacts(probeResult, availability, workInfos, rejected, accepted)
        }

        return combine(coreFlow, scheduleFlow, externalFlow) { core, scheduleFacts, external ->
            val mergedDiagnostics = core.diagnostics.copy(
                lastHeartbeatStatus = core.syncState.heartbeatStatus,
                lastLogMessage = core.diagnostics.lastLogMessage ?: core.syncState.lastError,
                recentLogMessages = core.diagnostics.recentLogMessages.ifEmpty {
                    listOfNotNull(core.syncState.lastError)
                }
            )
            val snapshot = buildSnapshot(
                core.queues,
                mergedDiagnostics,
                core.runtime,
                scheduleFacts,
                core.policyTransitionWriteFailure
            )
            val state = StatusResultMapper.buildState(
                snapshot = snapshot,
                syncState = core.syncState,
                workInfos = external.workInfos,
                permanentRejected = external.permanentRejected,
                networkAvailability = external.networkAvailability,
                probeResult = external.probeResult,
                justAccepted = external.accepted.isAccepted
            )
            val shouldClear = StatusResultMapper.shouldClearAcceptedSignal(
                external.accepted.isAccepted,
                external.workInfos.immediate
            )
            StatusEmission(
                state = state,
                clearAcceptedGenerationAfterEmission = external.accepted.generation.takeIf { shouldClear }
            )
        }.flowOn(Dispatchers.IO)
            .emitStates(acceptedSignal::clearIfGeneration)
    }

    fun requestRefresh() {
        refreshSignal.requestRefresh()
    }

    private fun buildSnapshot(
        queues: QueueStatusSnapshot,
        diagnostics: DiagnosticSnapshot,
        runtime: ForegroundLocationRuntimeState,
        scheduleFacts: ScheduleFacts,
        policyTransitionWriteFailure: PolicyTransitionWriteFailure
    ): StatusCenterSnapshot {
        val baseUrl = serverSettingsStore.getBaseUrl()
        val validation = ServerUrlValidator.validate(baseUrl)
        val expectedServerIdentity = runCatching {
            PimServerEndpoints.from(baseUrl).apiBaseUrl.toString()
        }.getOrNull()
        val settings = trackingSettingsStore.read()
        return StatusCenterSnapshot(
            permissions = permissionStatusRepository.snapshot(),
            api = ApiConnectionSnapshot(
                address = baseUrl,
                isValid = validation.isValid,
                reasonCode = validation.reasonCode,
                warnings = validation.warnings
            ),
            auth = AuthStatusSnapshot(
                hasAccessToken = !tokenManager.getAccessTokenForServer(baseUrl).isNullOrBlank(),
                isExpired = tokenManager.isExpiredForServer(baseUrl)
            ),
            service = ForegroundServiceSnapshot(
                continuousCollectionEnabled = settings.continuousCollectionEnabled,
                serviceRunning = runtime.isRunning
            ),
            tracking = StatusTrackingMapper.fromRuntime(settings.profile, runtime),
            queues = queues,
            diagnostics = diagnostics,
            schedule = scheduleFacts.scheduleSnapshot.toScheduleCacheStatusSnapshot(expectedServerIdentity),
            latestPolicyTransition = scheduleFacts.latestTransition,
            // D-12：不新增定时轮询，但在**本次快照构建时**按当前时刻重算，页面刷新即刷新。
            currentPolicyDurationMillis = currentPolicyDurationMillis(
                scheduleFacts.latestTransition,
                nowMillis()
            ),
            policyTransitionWriteFailure = policyTransitionWriteFailure
        )
    }

    private fun diagnosticSnapshotFlow(): Flow<DiagnosticSnapshot> {
        return combine(
            dao.recentDroppedLocationDiagnostics(limit = 1),
            refreshSignal.version
        ) { dropped, _ ->
            val latestDrop = dropped.firstOrNull()
            val logs = logRepository.recent(6)
            val latestLog = logs.firstOrNull()
            DiagnosticSnapshot(
                lastDroppedReason = latestDrop?.reason,
                lastDroppedAtMillis = latestDrop?.recordedAtUtc,
                lastLogMessage = latestLog?.message,
                lastHeartbeatStatus = null,
                recentLogMessages = logs.map { it.message }
            )
        }
    }
}

internal fun MobileLocationPolicyTransitionEntity.toPolicyTransitionSnapshot(): PolicyTransitionSnapshot =
    PolicyTransitionSnapshot(
        fromMode = fromMode,
        toMode = toMode,
        reason = reason,
        occurredAtMillis = occurredAtUtc
    )

internal fun ScheduleCacheSnapshot.toScheduleCacheStatusSnapshot(
    expectedServerIdentity: String? = serverIdentity
): ScheduleCacheStatusSnapshot =
    if (expectedServerIdentity == null || serverIdentity != expectedServerIdentity) {
        ScheduleCacheStatusSnapshot()
    } else ScheduleCacheStatusSnapshot(
        freshness = freshness,
        hasCachedWindows = windows.isNotEmpty(),
        lastSuccessAtMillis = lastSuccessAtMillis,
        lastAttemptAtMillis = lastAttemptAtMillis,
        lastError = lastError
    )
