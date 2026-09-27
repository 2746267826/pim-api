package com.pim.app.ui.status

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.pim.app.mobile.diagnostics.DiagnosticExportException
import com.pim.app.mobile.diagnostics.DiagnosticExportResult
import com.pim.app.mobile.diagnostics.DiagnosticOperations
import com.pim.app.forensics.DroppedReasonUiState
import com.pim.app.forensics.ForensicLivenessRepository
import com.pim.app.forensics.LivenessUiSnapshot
import com.pim.app.mobile.sync.MobileSyncScheduler
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.status.ConnectionProbeService
import com.pim.app.status.ConnectionProbeStore
import com.pim.app.status.StatusAcceptedSignal
import com.pim.app.status.StatusActionRoute
import com.pim.app.status.StatusActionTarget
import com.pim.app.status.StatusCenterRepository
import com.pim.app.status.StatusCenterState
import com.pim.app.status.StatusIssue
import com.pim.app.status.StatusSyncActionRunner
import com.pim.app.status.probeRefreshDelayMillis
import com.pim.app.status.resolveProbeResult
import com.pim.core.settings.ServerSettingsStore
import com.pim.core.settings.PimServerEndpoints
import dagger.hilt.android.lifecycle.HiltViewModel
import java.io.File
import javax.inject.Inject
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch

enum class StatusActionFeedback {
    ProbeChecking,
    ProbeCompleted,
    ProbeFailed,
    SyncSubmitFailed
}

enum class DiagnosticExportFeedback {
    PackageReady,
    PackageReadyWithLocations,
    ShareOpened,
    ShareUnavailable,
    InsufficientStorage,
    ExportFailed
}

internal data class ExportFeedbackState(
    val isExporting: Boolean = false,
    val feedback: DiagnosticExportFeedback? = null,
    val exportedFile: File? = null,
    val coordinateCount: Int = 0
)

internal suspend fun runExportWithFeedback(
    includeRecentLocations: Boolean,
    export: suspend () -> DiagnosticExportResult,
    onState: (ExportFeedbackState) -> Unit
) {
    onState(ExportFeedbackState(isExporting = true))
    try {
        val result = export()
        val feedback = if (includeRecentLocations) {
            DiagnosticExportFeedback.PackageReadyWithLocations
        } else {
            DiagnosticExportFeedback.PackageReady
        }
        onState(
            ExportFeedbackState(
                isExporting = false,
                feedback = feedback,
                exportedFile = result.file,
                coordinateCount = result.coordinateCount
            )
        )
    } catch (e: DiagnosticExportException) {
        if (e.code == "INSUFFICIENT_STORAGE") {
            onState(
                ExportFeedbackState(
                    isExporting = false,
                    feedback = DiagnosticExportFeedback.InsufficientStorage
                )
            )
        } else {
            onState(
                ExportFeedbackState(
                    isExporting = false,
                    feedback = DiagnosticExportFeedback.ExportFailed
                )
            )
        }
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        onState(
            ExportFeedbackState(
                isExporting = false,
                feedback = DiagnosticExportFeedback.ExportFailed
            )
        )
    }
}

internal suspend fun runProbeWithFeedback(
    probe: suspend () -> Unit,
    feedbackSetter: (StatusActionFeedback?) -> Unit
) {
    feedbackSetter(StatusActionFeedback.ProbeChecking)
    try {
        probe()
        feedbackSetter(StatusActionFeedback.ProbeCompleted)
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        feedbackSetter(StatusActionFeedback.ProbeFailed)
    }
}

internal suspend fun runSyncWithFeedback(
    sync: suspend () -> Unit,
    feedbackSetter: (StatusActionFeedback?) -> Unit
) {
    feedbackSetter(null)
    try {
        sync()
    } catch (e: CancellationException) {
        throw e
    } catch (_: Exception) {
        feedbackSetter(StatusActionFeedback.SyncSubmitFailed)
    }
}

data class DiagnosticExportUiState(
    val includeRecentLocations: Boolean = false,
    val showLocationConfirmation: Boolean = false,
    val isExporting: Boolean = false,
    val exportedFile: File? = null,
    val coordinateCount: Int = 0,
    val feedback: DiagnosticExportFeedback? = null
)

internal fun beginDiagnosticExport(
    current: DiagnosticExportUiState
): DiagnosticExportUiState? {
    if (current.isExporting) return null
    return current.copy(
        showLocationConfirmation = false,
        isExporting = true,
        exportedFile = null,
        coordinateCount = 0,
        feedback = null
    )
}

internal class ManualSyncController(
    private val requiresConfirmation: () -> Boolean,
    private val submit: (Boolean) -> Unit
) {
    private val _showConfirmation = MutableStateFlow(false)
    val showConfirmation: StateFlow<Boolean> = _showConfirmation.asStateFlow()

    fun request() {
        if (requiresConfirmation()) {
            _showConfirmation.value = true
        } else {
            submit(false)
        }
    }

    fun confirm() {
        if (!_showConfirmation.value) return
        _showConfirmation.value = false
        submit(true)
    }

    fun dismiss() {
        if (!_showConfirmation.value) return
        _showConfirmation.value = false
    }
}

@HiltViewModel
class StatusCenterViewModel @Inject constructor(
    private val repository: StatusCenterRepository,
    private val mobileSyncScheduler: MobileSyncScheduler,
    private val serverSettingsStore: ServerSettingsStore,
    private val connectionProbeService: ConnectionProbeService,
    private val connectionProbeStore: ConnectionProbeStore,
    private val acceptedSignal: StatusAcceptedSignal,
    private val diagnosticOperations: DiagnosticOperations,
    private val trackingSettingsStore: TrackingSettingsStore,
    private val forensicLivenessRepository: ForensicLivenessRepository,
    private val sprintSummaryRepository: com.pim.app.location.sprint.LocationSprintSummaryRepository
) : ViewModel() {

    /**
     * 状态页「高频冲刺」区块（REQ-9 / AC-9.1 / AC-9.2）。
     *
     * 口径 = 设备端本地台账 + 本地设置，**不新增接口**（REQ-9）。
     */
    private val _sprintSummary = MutableStateFlow(
        com.pim.app.location.sprint.SprintSummary(
            enabled = null,
            count = null,
            // §5 加载态：首次读取完成前显示「—」，而不是「暂无」
            // （「暂无」是「确认没有采集数据」的结论，不能在读之前就下）。
            countDisplay = com.pim.app.location.sprint.SprintCountDisplay.Loading
        )
    )
    val sprintSummary: StateFlow<com.pim.app.location.sprint.SprintSummary> =
        _sprintSummary.asStateFlow()

    /** 上次成功读取到的次数：§5 错误态要求「保留上次值」。 */
    private var lastSprintCount: Int? = null

    /**
     * 刷新冲刺概况（AC-9.1：与台账交叉核对一致）。
     *
     * 读取失败时**保留上次值**并把展示置为「读取失败」（§5 错误态），
     * 不把「读不到」伪装成「暂无」或「0 次」。
     */
    fun refreshSprintSummary() {
        viewModelScope.launch {
            val summary = try {
                sprintSummaryRepository.read(previous = lastSprintCount)
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                com.pim.app.location.sprint.SprintSummary(
                    enabled = null,
                    count = lastSprintCount,
                    countDisplay = com.pim.app.location.sprint.SprintCountDisplay.Failed
                )
            }
            if (summary.count != null) lastSprintCount = summary.count
            _sprintSummary.value = summary
        }
    }

    /** 状态页顶部存活区块（REQ-8）。 */
    private val _liveness = MutableStateFlow<LivenessUiSnapshot?>(null)
    val liveness: StateFlow<LivenessUiSnapshot?> = _liveness.asStateFlow()

    /** 「丢弃原因」页（REQ-9）：打开/关闭与数据都挂在状态页同域。 */
    private val _droppedReasons = MutableStateFlow<DroppedReasonUiState?>(null)
    val droppedReasons: StateFlow<DroppedReasonUiState?> = _droppedReasons.asStateFlow()

    private val _showDroppedReasons = MutableStateFlow(false)
    val showDroppedReasons: StateFlow<Boolean> = _showDroppedReasons.asStateFlow()

    init {
        refreshLiveness()
        refreshSprintSummary()
    }

    /** 读取本地取证台账并刷新存活区块；不依赖网络（AC-8.2 离线也要给出本地结论）。 */
    fun refreshLiveness() {
        // 「刷新状态」按钮同时刷新冲刺概况（REQ-9：沿用既有刷新入口，无下拉刷新）。
        refreshSprintSummary()
        viewModelScope.launch {
            _liveness.value = try {
                forensicLivenessRepository.snapshot()
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                null
            }
        }
    }

    fun openDroppedReasons() {
        _showDroppedReasons.value = true
        viewModelScope.launch {
            _droppedReasons.value = try {
                forensicLivenessRepository.droppedReasons()
            } catch (e: CancellationException) {
                throw e
            } catch (_: Exception) {
                null
            }
        }
    }

    fun closeDroppedReasons() {
        _showDroppedReasons.value = false
    }
    val state: StateFlow<StatusCenterState> = repository.observe()
        .stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5_000),
            initialValue = StatusCenterState.empty()
        )

    private val _feedback = MutableStateFlow<StatusActionFeedback?>(null)
    val feedback: StateFlow<StatusActionFeedback?> = _feedback.asStateFlow()

    private val syncRunner = StatusSyncActionRunner(
        syncNow = { allowMeteredOnce -> mobileSyncScheduler.enqueueNow(allowMeteredOnce) },
        refresh = { repository.requestRefresh() },
        acceptedSignal = acceptedSignal
    )

    private val controller = ManualSyncController(
        requiresConfirmation = { trackingSettingsStore.read().syncOnUnmeteredOnly },
        submit = { allowMeteredOnce -> submitSync(allowMeteredOnce) }
    )
    val showMeteredSyncConfirmation: StateFlow<Boolean> = controller.showConfirmation

    private val _exportState = MutableStateFlow(DiagnosticExportUiState())
    val exportState: StateFlow<DiagnosticExportUiState> = _exportState.asStateFlow()

    fun setIncludeRecentLocations(include: Boolean) {
        if (_exportState.value.isExporting) return
        _exportState.value = _exportState.value.copy(
            includeRecentLocations = include,
            showLocationConfirmation = if (include) {
                _exportState.value.showLocationConfirmation
            } else false
        )
    }

    fun requestDiagnosticExport() {
        val current = _exportState.value
        if (current.isExporting) return
        if (current.includeRecentLocations) {
            _exportState.value = current.copy(showLocationConfirmation = true)
        } else {
            performExport(includeRecentLocations = false)
        }
    }

    fun confirmLocationExport() {
        _exportState.value = _exportState.value.copy(showLocationConfirmation = false)
        performExport(includeRecentLocations = true)
    }

    fun dismissLocationConfirmation() {
        _exportState.value = _exportState.value.copy(showLocationConfirmation = false)
    }

    fun reportShareResult(opened: Boolean) {
        if (_exportState.value.exportedFile == null) return
        _exportState.value = _exportState.value.copy(
            feedback = if (opened) DiagnosticExportFeedback.ShareOpened
            else DiagnosticExportFeedback.ShareUnavailable
        )
    }

    private fun performExport(includeRecentLocations: Boolean) {
        val started = beginDiagnosticExport(_exportState.value) ?: return
        _exportState.value = started
        viewModelScope.launch {
            runExportWithFeedback(
                includeRecentLocations = includeRecentLocations,
                export = { diagnosticOperations.export(includeRecentLocations) },
                onState = { state ->
                    _exportState.value = _exportState.value.copy(
                        isExporting = state.isExporting,
                        feedback = state.feedback,
                        exportedFile = state.exportedFile,
                        coordinateCount = state.coordinateCount
                    )
                }
            )
        }
    }

    fun onIssueAction(issue: StatusIssue): StatusActionTarget {
        repository.requestRefresh()
        refreshLiveness()
        return issue.target
    }

    fun syncNow() {
        controller.request()
    }

    private fun submitSync(allowMeteredOnce: Boolean) {
        viewModelScope.launch {
            runSyncWithFeedback(
                sync = { syncRunner.run(StatusActionRoute.TriggerSync, allowMeteredOnce) },
                feedbackSetter = { _feedback.value = it }
            )
        }
    }

    fun confirmMeteredSync() {
        controller.confirm()
    }

    fun dismissMeteredSyncConfirmation() {
        controller.dismiss()
    }

    fun forceConnectionProbe() {
        viewModelScope.launch {
            runProbeWithFeedback(
                probe = { manualProbeOutcome() },
                feedbackSetter = { _feedback.value = it }
            )
        }
    }

    private suspend fun manualProbeOutcome() {
        val serverUrl = serverSettingsStore.getBaseUrl()
        try {
            val result = connectionProbeService.probe(serverUrl)
            if (serverSettingsStore.getBaseUrl() == serverUrl) {
                connectionProbeStore.save(result)
            }
        } finally {
            repository.requestRefresh()
        }
    }

    suspend fun refreshConnectionForVisibleScreen(force: Boolean = false): Long {
        val serverUrl = serverSettingsStore.getBaseUrl()
        val serverIdentity = runCatching {
            PimServerEndpoints.from(serverUrl).apiBaseUrl.toString()
        }.getOrNull()
        try {
            resolveProbeResult(
                force = force,
                serverIdentity = serverIdentity,
                store = connectionProbeStore,
                probe = { connectionProbeService.probe(serverUrl) },
                save = { result ->
                    if (serverSettingsStore.getBaseUrl() == serverUrl) {
                        connectionProbeStore.save(result)
                    } else false
                },
                nowMillis = System.currentTimeMillis()
            )
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            repository.requestRefresh()
            return PROBE_RETRY_MILLIS
        }
        repository.requestRefresh()
        return probeRefreshDelayMillis(
            result = connectionProbeStore.result.value,
            serverIdentity = serverIdentity,
            nowMillis = System.currentTimeMillis()
        )
    }
}

private const val PROBE_RETRY_MILLIS = 30_000L
