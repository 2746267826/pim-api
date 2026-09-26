package com.pim.app.location.service

import android.Manifest
import android.app.Notification
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.location.LocationManager
import android.os.Build
import android.os.IBinder
import android.os.SystemClock
import androidx.core.content.ContextCompat
import com.google.android.gms.common.ConnectionResult
import com.google.android.gms.common.GoogleApiAvailability
import com.google.android.gms.location.Priority
import com.pim.app.location.LocationSnapshot
import com.pim.app.location.acquisition.AcquisitionContext
import com.pim.app.location.acquisition.AcquisitionPhase
import com.pim.app.location.acquisition.LocationAcquisitionCoordinator
import com.pim.app.location.acquisition.SessionStartResult
import com.pim.app.location.acquisition.TriggerType

import com.pim.app.location.passive.PassiveLocationCoordinator
import com.pim.app.location.quality.RawLocationFix
import com.pim.app.location.sprint.LocationSprintRuntime
import com.pim.app.location.sprint.SprintWindowResult
import com.pim.app.location.highspeed.HighSpeedMode
import com.pim.app.location.motion.MotionSignalRepository
import com.pim.app.location.policy.LocationPolicyEngine
import com.pim.app.location.policy.LocationPolicyInput
import com.pim.app.location.policy.LocationPolicyMode
import com.pim.app.location.policy.PolicyDecision
import com.pim.app.location.policy.PolicyLocation
import com.pim.app.mobile.sync.MobileSyncScheduler
import com.pim.app.notifications.LocationNotificationRenderer
import com.pim.app.notifications.LocationNotificationState
import com.pim.app.schedule.ScheduleCacheFreshness
import com.pim.app.schedule.ScheduleCacheSnapshot
import com.pim.app.schedule.ScheduleWindowRepository
import com.pim.app.schedule.ScheduleWindowSelector
import com.pim.app.settings.TrackingSettings
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.settings.toTrackingPolicy
import com.pim.app.status.QueueStatusRepository
import dagger.hilt.android.AndroidEntryPoint
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import javax.inject.Inject
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.filter
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.merge
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

@AndroidEntryPoint
class ForegroundLocationService : Service() {
    @Inject lateinit var trackingSettingsStore: TrackingSettingsStore
    @Inject lateinit var motionSignalRepository: MotionSignalRepository
    @Inject lateinit var scheduleWindowRepository: ScheduleWindowRepository
    @Inject lateinit var mobileSyncScheduler: MobileSyncScheduler
    @Inject lateinit var locationAcquisitionCoordinator: LocationAcquisitionCoordinator
    @Inject lateinit var queueStatusRepository: QueueStatusRepository
    @Inject lateinit var locationSprintRuntime: LocationSprintRuntime
    @Inject lateinit var passiveLocationCoordinator: PassiveLocationCoordinator

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var scheduleRefreshJob: Job? = null
    private var snapshotCollectJob: Job? = null
    private var automaticLoopJob: Job? = null
    private var queueObservationJob: Job? = null
    private var passiveCounterFlushJob: Job? = null
    private var policyEngine: LocationPolicyEngine? = null
    private var scheduleFreshness: ScheduleCacheFreshness = ScheduleCacheFreshness.Missing
    private var scheduleLastSuccessAtMillis: Long? = null
    private var scheduleLastAttemptAtMillis: Long? = null
    private var scheduleLastError: String? = null
    private var currentDecision = PolicyDecision(
        mode = LocationPolicyMode.PowerSavingNormal,
        requestIntervalMillis = 3 * 60 * 1000L,
        nextExpectedLocationAtMillis = System.currentTimeMillis() + 3 * 60 * 1000L,
        reason = "默认省电档",
        scheduleLowFrequency = false
    )
    private val policyTransitionDeduper = PolicyTransitionDeduper()
    private val policyTransitionWriteMutex = Mutex()
    private var lastAcceptedLocationText = "无"
    private var lastAccuracyText = "无"
    private var pendingUploadTotal = 0
    private var apiState = "等待日程数据"
    private var lastDroppedReason: String? = null
    private var isPausing = false
    private var explicitTeardown = false
    // The manual session this instance actually started and therefore owns.
    // Only this session may be cancelled from an unexpected onDestroy(); sessions
    // started by other instances, the UI/controller or a manual-sync teardown are
    // never this instance's to cancel.
    private var ownedManualSessionId: String? = null
    private var policyTransitionWriteJob: Job? = null
    internal var policyTransitionWriter: (suspend (LocationPolicyMode?, PolicyDecision) -> Unit)? = null
    // 最近一次流入库 fix 的 GPS 速度：高速轨迹状态机（policyEngine.highSpeedTracker）
    // 以它为输入，自动循环每次重算时观察。null 表示尚未收到任何入库 fix。
    private var lastSpeedMetersPerSecond: Float? = null
    // ── WO-ANDROID-GATE-20260926：冲刺与被动定位的运行时状态 ──
    /** 最近一次冲刺窗口结果（供状态页/通知读取）。 */
    private var lastSprintResult: SprintWindowResult? = null
    // fix 入库信号：自动循环等待它唤醒，从而在速度变化时立即重算策略并
    // （按需）重注册常驻流，让 2.5s 密集采样与 10s/60s 防抖即时生效。
    private val fixRecordedSignal = MutableStateFlow(0L)
    // 上次策略重算时看到的 fix 信号值：本轮无新 fix（仅 30s 兜底或运动信号
    // 唤醒）时 GPS 速度视为未知（null），让 GPS 失锁/停车无 fix 时高速档
    // 也能按设计的 null 语义回落。
    private var lastSeenFixSignal = 0L

    override fun onCreate() {
        super.onCreate()
        publishRuntimeState(isRunning = true)
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ForegroundLocationController.ACTION_PAUSE_COLLECTION -> {
                isPausing = true
                explicitTeardown = true
                trackingSettingsStore.setContinuousCollectionEnabled(false)
                applyDecision(
                    currentDecision.copy(
                        mode = LocationPolicyMode.Off,
                        requestIntervalMillis = 0L,
                        nextExpectedLocationAtMillis = Long.MAX_VALUE,
                        reason = "已暂停",
                        scheduleLowFrequency = false
                    ),
                    isRunning = false
                )
                stopCollection()
                val nm = getSystemService(Context.NOTIFICATION_SERVICE) as android.app.NotificationManager
                nm.notify(LocationNotificationRenderer.NOTIFICATION_ID, notification())
                stopSelf(startId)
                return START_NOT_STICKY
            }
            ForegroundLocationController.ACTION_STOP_COLLECTION -> {
                isPausing = false
                explicitTeardown = true
                trackingSettingsStore.setContinuousCollectionEnabled(false)
                stopCollection()
                val nm = getSystemService(Context.NOTIFICATION_SERVICE) as android.app.NotificationManager
                nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)
                stopSelf(startId)
                return START_NOT_STICKY
            }
            ForegroundLocationController.ACTION_SYNC_NOW -> {
                runManualSync(startId)
            }
            ForegroundLocationController.ACTION_RESUME_COLLECTION -> startCollection(
                enableCollection = true,
                startId = startId,
                persistCollectionIntentBeforePrerequisites = true
            )
            ForegroundLocationController.ACTION_START_COLLECTION -> startCollection(
                enableCollection = true,
                startId = startId
            )
            ForegroundLocationController.ACTION_START_MANUAL_SESSION -> startManualSession(startId)
            ForegroundLocationController.ACTION_CANCEL_LOCATION_SESSION -> {
                val sessionId = intent.getStringExtra(ForegroundLocationController.EXTRA_SESSION_ID)
                if (sessionId == null) {
                    // Fail-closed: a cancel without a session id must never be
                    // forwarded as the coordinator's wildcard cancellation of the
                    // current session. Leave the active session, the 7101
                    // foreground notification and the service untouched; the
                    // owning terminal waiter (if any) retires this instance's
                    // foreground when its session ends.
                    return START_STICKY
                }
                val cancelled = locationAcquisitionCoordinator.cancelCurrentSession(sessionId)
                if (!cancelled) {
                    // Fail-closed: a missing/stale/wrong session id (or a
                    // non-cancellable terminal phase) must not stop a valid
                    // current session, remove the 7101 foreground notification,
                    // or induce onDestroy() to cancel the valid session. Leave
                    // everything untouched; the owning terminal waiter (if any)
                    // retires this instance's foreground when its session ends.
                    return START_STICKY
                }
                // manual-only 实例（无自动循环且连续采集未启用）可能没有终结
                // waiter（例如新实例处理上一个 waiter 已停止的旧实例留下的
                // cancel）；此时取消必须移除非前台通知并无条件停止服务、返回
                // 非 sticky，否则该实例成为僵尸且可能被 null sticky intent
                // 重建。自动采集启用/运行中时不得停止其服务或 7101 通知。
                if (automaticLoopJob?.isActive != true &&
                    !trackingSettingsStore.read().continuousCollectionEnabled
                ) {
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    stopSelf()
                    return START_NOT_STICKY
                }
            }
            null -> startCollection(enableCollection = false, startId = startId)
        }
        return START_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        scope.cancel()
        // Foreground-notification cleanup is an ownership decision: only an
        // instance that engaged its own foreground lifecycle (an active
        // automatic loop, an owned manual session, or an explicit teardown)
        // may remove the shared 7101 notification. Sync-only and Busy-adopted
        // instances never own another session's 7101 and must not cancel an
        // automatic session they did not start.
        val ownsForegroundLifecycle = ownedManualSessionId != null ||
            automaticLoopJob?.isActive == true ||
            explicitTeardown
        if (!isPausing && ownsForegroundLifecycle) {
            stopCollection()
            val nm = getSystemService(Context.NOTIFICATION_SERVICE) as android.app.NotificationManager
            nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)
        }
        locationAcquisitionCoordinator.onRecorded = null
        if (!explicitTeardown) {
            // Unexpected service destruction: cancel only the manual session this
            // instance actually started AND that is still in a cancellable
            // capture phase. An owned terminal result must be preserved — the
            // waiter clears ownership once the session ends. Sessions started
            // by another service instance, the UI/controller or an unrelated
            // ACTION_SYNC_NOW teardown are never this instance's to cancel.
            val ownedId = ownedManualSessionId
            if (ownedId != null) {
                val current = locationAcquisitionCoordinator.state.value
                if (current.sessionId == ownedId &&
                    current.phase in setOf(
                        com.pim.app.location.acquisition.AcquisitionPhase.Preparing,
                        com.pim.app.location.acquisition.AcquisitionPhase.Acquiring,
                        com.pim.app.location.acquisition.AcquisitionPhase.Evaluating
                    )
                ) {
                    locationAcquisitionCoordinator.cancelCurrentSession(ownedId)
                }
            }
        }
        publishRuntimeState(isRunning = false)
        super.onDestroy()
    }

    private fun startCollection(
        enableCollection: Boolean,
        startId: Int,
        persistCollectionIntentBeforePrerequisites: Boolean = false
    ) {
        isPausing = false
        explicitTeardown = false
        // 注意：**不**把 startForeground 提到前置检查之前。
        // 既有契约测试明确要求「先检查定位就绪再 startForeground」
        // （AndroidV2CollectionControlContractTest、ForegroundLocationServiceTest），
        // 该顺序属本工单范围外，保持不动。
        if (enableCollection && persistCollectionIntentBeforePrerequisites) {
            trackingSettingsStore.setContinuousCollectionEnabled(true)
        }
        if (!hasRequiredLocationPermissions()) {
            lastDroppedReason = "缺少精确或后台定位权限"
            applyDecision(
                currentDecision.copy(
                    mode = LocationPolicyMode.Off,
                    requestIntervalMillis = 0L,
                    nextExpectedLocationAtMillis = Long.MAX_VALUE,
                    reason = "缺少精确或后台定位权限",
                    scheduleLowFrequency = false
                ),
                isRunning = false
            )
            stopCollection()
            stopSelf(startId)
            return
        }

        if (GoogleApiAvailability.getInstance()
                .isGooglePlayServicesAvailable(this) != ConnectionResult.SUCCESS
        ) {
            lastDroppedReason = "Google Play Services 不可用"
            applyDecision(
                currentDecision.copy(
                    mode = LocationPolicyMode.Off,
                    requestIntervalMillis = 0L,
                    nextExpectedLocationAtMillis = Long.MAX_VALUE,
                    reason = "Google Play Services 不可用",
                    scheduleLowFrequency = false
                ),
                isRunning = false
            )
            stopCollection()
            stopSelf(startId)
            return
        }

        if (!isLocationEnabled()) {
            lastDroppedReason = "系统定位服务未开启"
            applyDecision(
                currentDecision.copy(
                    mode = LocationPolicyMode.Off,
                    requestIntervalMillis = 0L,
                    nextExpectedLocationAtMillis = Long.MAX_VALUE,
                    reason = "系统定位服务未开启",
                    scheduleLowFrequency = false
                ),
                isRunning = false
            )
            stopCollection()
            stopSelf(startId)
            return
        }

        if (enableCollection) {
            trackingSettingsStore.setContinuousCollectionEnabled(true)
        }

        val settings = trackingSettingsStore.read()
        // 本次新增的接线（冲刺台账 + 被动监听注册）放在 startForeground **之后**：
        // 被动监听要经 LocationManager 注册，可能较慢。这是相对基线的唯一顺序调整，
        // 只影响我们新加的这段初始化，不触碰上面既有的「先检查再进前台」约定。
        startForeground(LocationNotificationRenderer.NOTIFICATION_ID, notification())
        initializeAutomaticRuntime(settings)

        if (!settings.continuousCollectionEnabled) {
            lastDroppedReason = "连续采集未开启"
            stopCollection()
            stopSelf(startId)
            return
        }

        startAutomaticLoop()
    }

    /**
     * 冲刺与被动源的运行时接线（WO-ANDROID-GATE-20260926 REQ-6 / REQ-8 / REQ-14）。
     *
     * 具体判定/入库/留痕都在各自的编排类里（`LocationSprintRuntime` /
     * `PassiveLocationCoordinator`），服务只负责生命周期与「每拍发起一次」——
     * 既有契约测试要求服务自身不出现质量门与丢弃诊断符号。
     */
    private fun wireSprintLedger(settings: TrackingSettings) {
        locationSprintRuntime.wire()
        locationSprintRuntime.abort()
        // AC-14.1：被动监听随采集服务常驻；注册成功会留日志（含 provider 与时刻）。
        passiveLocationCoordinator.start(::activeStreamFixes)
    }

    /**
     * 手动定位的冲刺（REQ-6 / AC-6.1 / AC-6.3）。
     *
     * - 开关**开**：发起一次冲刺窗口并落「已执行」台账（AC-6.1 证据）；
     * - 开关**关**：记一条「跳过（开关关闭）」，**不**注册冲刺流（AC-6.3）。
     *
     * 无论哪种，手动会话本身照常跑满 30 秒（用户确认口径，D6）。
     */
    private fun maybeRunManualSprint() {
        // I-1：**必须**走 onManualSession，不能走 onPeriod。
        // 手动会话自带 ≈1 秒节奏，若经周期门控会把门控的 lastIntervalMillis 写成 1000，
        // 使下一个自动周期立刻再次放行 —— 同一周期出现两次冲刺（违反 D2），
        // 并与手动会话自己的取点流重复入库。
        locationSprintRuntime.onManualSession()
    }

    /**
     * 主动流最近的入库 fix 快照（AC-14.5 重复判定用）。
     *
     * 只读，且不触碰任何注册状态 —— 被动监听不得扰动主流（AC-14.7）。
     */
    private fun activeStreamFixes(): List<RawLocationFix> {
        val latest = locationAcquisitionCoordinator.streamState.value.latestFix
            ?: return emptyList()
        return listOf(
            RawLocationFix(
                latitude = latest.latitude,
                longitude = latest.longitude,
                horizontalAccuracyMeters = latest.horizontalAccuracyMeters,
                altitudeMeters = latest.altitudeMeters,
                provider = latest.provider,
                recordedAtMillis = latest.timeMillis,
                policyMode = currentDecision.mode.name,
                scheduleLowFrequency = currentDecision.scheduleLowFrequency,
                motionSignal = motionSignalRepository.status.value.signal.name
            )
        )
    }

    private fun stopCollection() {
        // 被动计数在本窗口停止前落台账（AC-14.2 的三数对账分母）。
        scope.launch { runCatching { passiveLocationCoordinator.stop() } }
        locationSprintRuntime.abort()
        automaticLoopJob?.cancel()
        automaticLoopJob = null
        queueObservationJob?.cancel()
        queueObservationJob = null
        passiveCounterFlushJob?.cancel()
        passiveCounterFlushJob = null
        scheduleRefreshJob?.cancel()
        snapshotCollectJob?.cancel()
        runCatching { motionSignalRepository.unregister() }
        // 停止采集后高速档状态立即复位，暂停/停止通知与运行时状态
        // 不得残留「高速轨迹记录中」
        policyEngine?.highSpeedTracker?.reset()
        lastSpeedMetersPerSecond = null
        lastSeenFixSignal = fixRecordedSignal.value
        publishRuntimeState(isRunning = false)
        locationAcquisitionCoordinator.stopAutomaticStream()
        // 死实例的回调不得再发通知/写共享 runtime 状态
        locationAcquisitionCoordinator.onRecorded = null
        stopForeground(STOP_FOREGROUND_REMOVE)
    }

    private fun startManualSession(startId: Int) {
        // A PAUSE/STOP on the same instance must not leak its teardown flags into
        // a fresh manual session: later unexpected destruction then cancels the
        // new session and removes the notification instead of preserving it.
        isPausing = false
        explicitTeardown = false
        val result = locationAcquisitionCoordinator.startManualSession()
        if (result is SessionStartResult.Rejected) {
            stopSelf(startId)
            return
        }
        val startedId = (result as? SessionStartResult.Started)?.sessionId
        if (startedId != null) {
            ownedManualSessionId = startedId
        } else {
            // 防御：coordinator 现已保证启动即 Started，Busy 不再产生；
            // 若未来回归，拒绝滞留前台。
            stopSelf(startId)
            return
        }

        startForeground(LocationNotificationRenderer.NOTIFICATION_ID, notification())
        val settings = trackingSettingsStore.read()
        if (settings.continuousCollectionEnabled &&
            automaticLoopJob?.isActive != true &&
            hasRequiredLocationPermissions()
        ) {
            initializeAutomaticRuntime(settings)
            startAutomaticLoop()
            return
        }
        // REQ-6 / AC-6.1：手动定位同样执行冲刺，并留下可核对的台账记录。
        // 用户 2026-09-26 确认：手动会话**无论开关都跑满 30 秒**，开关只决定是否冲刺。
        // 因此这里按开关决定发起冲刺还是记「跳过」，会话时长不受影响。
        maybeRunManualSprint()

        scope.launch {
            // 观察本会话的终结状态；若该会话在 waiter 挂起期间被新的手动
            // 会话替换（会话 ID 变化且不再回到本会话），waiter 不得永远等待，
            // 也不得误把替换会话的终态当成自己的终态。
            locationAcquisitionCoordinator.state.first { acqState ->
                acqState.sessionId != startedId ||
                    acqState.phase in setOf(
                        AcquisitionPhase.Completed,
                        AcquisitionPhase.TimedOut,
                        AcquisitionPhase.Failed,
                        AcquisitionPhase.Cancelled
                    )
            }
            // 终结状态被观察到后、前台/服务拆除前，重新校验当前会话
            // 仍是本 waiter 捕获的 startedId；若已被新的手动会话替换，
            // 只退役本实例的本地服务，不得拆除新会话的前台通知或取消
            // 该会话（同时清除所有权，防止随后的 onDestroy 误删其 7101）。
            if (locationAcquisitionCoordinator.state.value.sessionId != startedId) {
                if (ownedManualSessionId == startedId) {
                    ownedManualSessionId = null
                    stopSelf()
                }
                return@launch
            }
            // M-2 + R5-2：**取消**时才中止手动自己发起的冲刺窗口。
            //
            // - 取消（用户按「停止」）：窗口没跑完，若不停掉，控制器会继续在自己的
            //   scope 上占着高频注册最长 30 秒（本实例可能已经拆除）。
            // - 正常终结（Completed / TimedOut / Failed）：手动冲刺与手动会话几乎
            //   同时起跑（都是 30 秒），此时中止会把**即将写完**的窗口掐掉，
            //   让「已执行」记录永远不落台账 —— 而真机操作卡 §6 正是要看这条记录。
            //
            // 两种情况都**只动手动自己的窗口**：自动采集循环正在跑的冲刺不归手动会话管，
            // 取消它会丢掉自动那一拍，并重置周期锚点导致提前再冲一次。
            if (locationAcquisitionCoordinator.state.value.phase == AcquisitionPhase.Cancelled) {
                locationSprintRuntime.abortManualWindow()
            }
            // 本实例拥有的会话已终结：清除所有权，使随后的 onDestroy() 不再
            // 尝试取消该会话。
            ownedManualSessionId = null
            if (!trackingSettingsStore.read().continuousCollectionEnabled) {
                stopForeground(STOP_FOREGROUND_REMOVE)
                // The captured startId may be older than a later
                // ACTION_CANCEL_LOCATION_SESSION startId, which Android
                // ignores for stopSelf(startId); stop unconditionally so
                // the manual-only service really terminates.
                stopSelf()
            }
        }
    }

    private fun initializeAutomaticRuntime(settings: TrackingSettings) {
        policyEngine = LocationPolicyEngine(settings.toTrackingPolicy())
        locationAcquisitionCoordinator.onRecorded = { snapshot ->
            recordAccepted(snapshot)
        }
        wireSprintLedger(settings)
        applyDecision(
            policyEngine!!.reduce(
                LocationPolicyInput(
                    nowMillis = System.currentTimeMillis(),
                    collectionEnabled = settings.continuousCollectionEnabled
                )
            )
        )
        refreshScheduleWindows()
        motionSignalRepository.register()
        observeQueueStatus()
    }

    private suspend fun recordAccepted(snapshot: LocationSnapshot) {
        policyEngine?.onAcceptedLocation(
            PolicyLocation(
                latitude = snapshot.latitude,
                longitude = snapshot.longitude,
                recordedAtMillis = snapshot.timeMillis
            )
        )
        lastSpeedMetersPerSecond = snapshot.speedMetersPerSecond
        fixRecordedSignal.value = fixRecordedSignal.value + 1L
        lastAcceptedLocationText = timeFormatter.format(
            Instant.ofEpochMilli(snapshot.timeMillis)
                .atZone(ZoneId.systemDefault())
        )
        lastAccuracyText = "${snapshot.horizontalAccuracyMeters?.toInt() ?: 0}m"
        lastDroppedReason = null
        // currentDecision 的所有写必须串行在 Main 线程（自动循环与各 ACTION
        // 分支也在 Main 写）：fix 驱动的唤醒使并发读-改-写碰撞窗口从分钟级
        // 缩到 2.5s 级，跨线程 copy 会丢失整对象赋值（applyDecision）。
        withContext(Dispatchers.Main.immediate) {
            currentDecision = currentDecision.copy(
                nextExpectedLocationAtMillis = System.currentTimeMillis() +
                    currentDecision.requestIntervalMillis
            )
            updateNotification()
        }
    }

    private fun startAutomaticLoop() {
        automaticLoopJob?.cancel()
        automaticLoopJob = scope.launch {
            while (trackingSettingsStore.read().continuousCollectionEnabled) {
                refreshScheduleWindows()
                applyScheduleSnapshot(scheduleWindowRepository.snapshotForCurrentServer())
                val decision = recomputePolicyDecision()
                // 只按关键字段比较：nextExpectedLocationAtMillis 每轮必然不同
                // （now + interval），不能进 data class equals。
                if (decision.mode != currentDecision.mode ||
                    decision.requestIntervalMillis != currentDecision.requestIntervalMillis ||
                    decision.scheduleLowFrequency != currentDecision.scheduleLowFrequency ||
                    decision.reason != currentDecision.reason
                ) {
                    applyDecision(decision)
                    updateNotification()
                }
                if (decision.mode != LocationPolicyMode.Off) {
                    val context = AcquisitionContext(
                        policyMode = decision.mode.name,
                        scheduleLowFrequency = decision.scheduleLowFrequency,
                        motionSignal = motionSignalRepository.status.value.signal.name,
                        requestIntervalMillis = decision.requestIntervalMillis.coerceAtLeast(1_000L)
                    )
                    if (!locationAcquisitionCoordinator.isAutomaticStreamActive()) {
                        locationAcquisitionCoordinator.startAutomaticStream(context)
                    } else {
                        locationAcquisitionCoordinator.updateAutomaticStream(context)
                    }
                    // REQ-2 / D2：每个采集周期发起一次冲刺。
                    // AC-5.6：冲刺走**自己的**注册，本行不触碰上面主流的 context/注册；
                    // AC-2.5：运动/车载档（30 秒硬下限）照常发起，不做任何「节拍过密即跳过」判断。
                    locationSprintRuntime.onPeriod(context, decision.mode)
                    updateNotification()
                } else {
                    // AC-10.3：非采集时段不冲刺，但**要记录未冲刺原因**（AC-8.2）。
                    locationSprintRuntime.onPeriod(
                        AcquisitionContext(
                            policyMode = decision.mode.name,
                            scheduleLowFrequency = decision.scheduleLowFrequency,
                            motionSignal = motionSignalRepository.status.value.signal.name,
                            requestIntervalMillis = 0L
                        ),
                        decision.mode
                    )
                    locationAcquisitionCoordinator.stopAutomaticStream()
                }
                // 运动信号变化或新 fix 入库即时唤醒（高速档依赖 fix 驱动重算）；
                // 最迟 30s 重算一次（覆盖日程/设置变化）。
                //
                // 注意：本循环的兜底唤醒粒度是 30 秒，而冲刺周期可能是 45/120/600 秒。
                // 冲刺的周期门控（SprintPeriodGate）保证**不会多冲**（一周期一次），
                // 但若某次唤醒恰好落在周期边界之前、下一次唤醒又偏晚，该周期可能被
                // 顺延一拍 —— 真机实测 60 秒档出现过 60s/89s 的间隔抖动。
                // 这是「唤醒粒度 vs 周期对齐」的已知抖动，只影响个别周期的**起点**，
                // 不影响占空比上界与「不早退」；未在此处强改等待时长，
                // 以免动到既有「最迟 30s 重算一次」的循环契约。
                // I-2：把等待收敛到下一个冲刺周期边界。45 秒档在 30 秒唤醒网格上
                // 不对齐，会让实际节奏退化成 60 秒一拍；对齐后恢复「一周期一次」。
                // 仍然保留 30 秒兜底上限的**字面与语义**（只缩短，不加长）。
                // 等待下一个唤醒。上限恒为 [LOOP_WAIT_CAP_MILLIS]（30 秒兜底，
                // 覆盖日程/设置变化）；若冲刺周期边界更早则提前醒来，避免
                // 45 秒档在 30 秒唤醒网格上退化成 60 秒一拍（I-2）。
                // 注意：**只缩短、不加长**，30 秒上限语义不变。
                withTimeoutOrNull(loopWaitMillis(decision.requestIntervalMillis)) {
                    val currentSignal = motionSignalRepository.status.value.signal
                    val lastFixSignal = fixRecordedSignal.value
                    merge(
                        motionSignalRepository.status.filter { it.signal != currentSignal },
                        fixRecordedSignal.filter { it != lastFixSignal }
                    ).first()
                }
            }
            // The loop exits because collection became disabled; this is an
            // explicit shutdown, so the imminent onDestroy must not cancel an
            // active manual session or keep a foreground notification.
            explicitTeardown = true
            isPausing = false
            locationAcquisitionCoordinator.stopAutomaticStream()
            stopCollection()
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    /**
     * 采集循环单次等待时长：兜底上限 [LOOP_WAIT_CAP_MILLIS]，但若更早到冲刺周期
     * 边界就提前醒来（I-2）。
     *
     * 30 秒是**上限**（工单与本循环的既有约定），不是固定值：对齐只可能缩短等待。
     */
    private fun loopWaitMillis(requestIntervalMillis: Long): Long =
        resolveLoopWaitMillis(
            locationSprintRuntime.suggestedLoopWaitMillis(
                requestIntervalMillis = requestIntervalMillis,
                capMillis = LOOP_WAIT_CAP_MILLIS
            )
        )

    private fun recomputePolicyDecision(): PolicyDecision {
        val now = System.currentTimeMillis()
        val settings = trackingSettingsStore.read()
        // 本轮循环没有新 fix 入库（仅 30s 兜底或运动信号唤醒）：GPS 速度未知，
        // 按 null 观察状态机，让 GPS 失锁/停车无 fix 时高速档也能按设计回落；
        // 有新 fix 时用其最新 GPS 速度。
        val currentFixSignal = fixRecordedSignal.value
        val speed = if (currentFixSignal > lastSeenFixSignal) {
            lastSpeedMetersPerSecond
        } else {
            null
        }
        lastSeenFixSignal = currentFixSignal
        return policyEngine?.reduce(
            LocationPolicyInput(
                nowMillis = now,
                collectionEnabled = settings.continuousCollectionEnabled,
                currentScheduleWindow = ScheduleWindowSelector.current(
                    scheduleWindowRepository.snapshotForCurrentServer().windows,
                    now
                ),
                motionSignal = motionSignalRepository.status.value.signal,
                speedMetersPerSecond = speed
            )
        ) ?: currentDecision
    }

    private fun observeQueueStatus() {
        queueObservationJob?.cancel()
        queueObservationJob = scope.launch {
            queueStatusRepository.observe().collect { snapshot ->
                pendingUploadTotal = snapshot.pendingUploadTotal
                publishRuntimeState()
                updateNotification()
            }
        }
        // AC-14.2：被动点的三数计数必须**周期性**落台账，不能只在停止时写 ——
        // 服务可能被系统杀掉或被 force-stop，那时 onDestroy/stopCollection 都不执行，
        // 「回调总数」这个分母就丢了（真机实测确认过）。这里按固定周期刷窗口。
        passiveCounterFlushJob?.cancel()
        passiveCounterFlushJob = scope.launch {
            while (true) {
                delay(PASSIVE_COUNTER_FLUSH_MILLIS)
                runCatching { passiveLocationCoordinator.flushWindow() }
            }
        }
    }

    private fun runManualSync(_startId: Int) {
        // A manual sync shares the foreground lifecycle: clear stale teardown
        // flags so a later unexpected destruction is not treated as explicit.
        isPausing = false
        explicitTeardown = false
        val hasLoop = automaticLoopJob?.isActive == true
        // A same-instance sync must not retire this manual-only instance while
        // it owns a currently active manual session (Preparing/Acquiring/
        // Evaluating): tearing down here stops the instance and onDestroy would
        // then cancel the very session this instance is running. The owning
        // terminal waiter retires the foreground/service when that session ends.
        val coordinatorState = locationAcquisitionCoordinator.state.value
        val ownsActiveManualSession = ownedManualSessionId != null &&
            coordinatorState.sessionId == ownedManualSessionId &&
            coordinatorState.phase in setOf(
                com.pim.app.location.acquisition.AcquisitionPhase.Preparing,
                com.pim.app.location.acquisition.AcquisitionPhase.Acquiring,
                com.pim.app.location.acquisition.AcquisitionPhase.Evaluating
            )
        val stopAfterSync = !hasLoop &&
            !trackingSettingsStore.read().continuousCollectionEnabled &&
            !ownsActiveManualSession
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as android.app.NotificationManager
        val restorePausedNotification = stopAfterSync && nm.activeNotifications.any {
            it.id == LocationNotificationRenderer.NOTIFICATION_ID &&
                (it.notification.flags and Notification.FLAG_ONGOING_EVENT) == 0
        }
        if (restorePausedNotification) {
            markPausedState()
        }
        apiState = "同步中"
        startForeground(LocationNotificationRenderer.NOTIFICATION_ID, notification())
        updateNotification()

        scope.launch {
            try {
                mobileSyncScheduler.enqueueNow()
                apiState = "同步请求已提交。"
                updateNotification()
            } catch (ex: CancellationException) {
                throw ex
            } catch (_: Exception) {
                apiState = "同步请求提交失败"
                updateNotification()
            } finally {
                if (stopAfterSync) {
                    stopForeground(STOP_FOREGROUND_REMOVE)
                    if (restorePausedNotification) {
                        markPausedState()
                        nm.notify(LocationNotificationRenderer.NOTIFICATION_ID, notification())
                    }
                    // 使用无条件 stopSelf()：若期间收到更新的 cancel/sync
                    // intent，startId 不再匹配时 stopSelf(startId) 会被
                    // Android 忽略，导致手动专用服务成为僵尸。
                    stopSelf()
                }
            }
        }
    }

    private fun markPausedState() {
        applyDecision(
            currentDecision.copy(
                mode = LocationPolicyMode.Off,
                requestIntervalMillis = 0L,
                nextExpectedLocationAtMillis = Long.MAX_VALUE,
                reason = "已暂停",
                scheduleLowFrequency = false
            ),
            isRunning = false
        )
        isPausing = true
    }

    private fun ensureSnapshotObserver() {
        if (snapshotCollectJob?.isActive == true) return
        if (!::scheduleWindowRepository.isInitialized) return
        snapshotCollectJob = scope.launch {
            scheduleWindowRepository.snapshot.collect { snapshot ->
                applyScheduleSnapshot(snapshot)
                publishRuntimeState()
            }
        }
    }

    private fun refreshScheduleWindows(force: Boolean = false) {
        ensureSnapshotObserver()
        if (scheduleRefreshJob?.isActive == true) return
        scheduleRefreshJob = scope.launch {
            try {
                applyScheduleSnapshot(scheduleWindowRepository.snapshotForCurrentServer())
                val snapshot = withContext(Dispatchers.IO) {
                    scheduleWindowRepository.refreshIfStale(force = force)
                }
                applyScheduleSnapshot(snapshot)
                updateNotification()
            } catch (ex: CancellationException) {
                throw ex
            } catch (_: Exception) {
                applyScheduleSnapshot(scheduleWindowRepository.snapshotForCurrentServer())
                updateNotification()
            }
        }
    }

    private fun applyScheduleSnapshot(snapshot: ScheduleCacheSnapshot) {
        scheduleFreshness = snapshot.freshness
        scheduleLastSuccessAtMillis = snapshot.lastSuccessAtMillis
        scheduleLastAttemptAtMillis = snapshot.lastAttemptAtMillis
        scheduleLastError = snapshot.lastError
        apiState = scheduleApiStateText(snapshot)
    }

    private fun scheduleApiStateText(snapshot: ScheduleCacheSnapshot): String {
        return when {
            snapshot.freshness == ScheduleCacheFreshness.Fresh && snapshot.lastError != null -> "日程缓存异常"
            snapshot.freshness == ScheduleCacheFreshness.Fresh && snapshot.lastError == null -> "正常"
            snapshot.freshness == ScheduleCacheFreshness.Stale -> "日程缓存可能过期"
            snapshot.freshness == ScheduleCacheFreshness.Missing && snapshot.lastError != null -> "日程暂不可用"
            snapshot.freshness == ScheduleCacheFreshness.Missing &&
                snapshot.lastError == null &&
                snapshot.lastSuccessAtMillis == null -> "等待日程数据"
            else -> "正常"
        }
    }

    private fun applyDecision(decision: PolicyDecision, isRunning: Boolean = isRunning()) {
        currentDecision = decision
        val transition = policyTransitionDeduper.note(decision)
        if (transition != null) {
            policyTransitionWriteJob = scope.launch {
                policyTransitionWriteMutex.withLock {
                    try {
                        val writer = policyTransitionWriter
                        if (writer != null) {
                            writer(transition.fromMode, transition.decision)
                        }
                    } catch (ex: CancellationException) {
                        throw ex
                    } catch (_: Exception) {
                    }
                }
            }
        }
        publishRuntimeState(isRunning = isRunning)
    }

    private fun isLocationEnabled(): Boolean {
        val lm = getSystemService(Context.LOCATION_SERVICE) as LocationManager
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            lm.isLocationEnabled
        } else {
            @Suppress("DEPRECATION")
            lm.isProviderEnabled(LocationManager.NETWORK_PROVIDER) ||
                lm.isProviderEnabled(LocationManager.GPS_PROVIDER)
        }
    }

    private fun notification() = LocationNotificationRenderer.build(
        this,
        LocationNotificationState(
            mode = currentDecision.mode,
            nextExpectedLocationText = nextExpectedLocationText(currentDecision),
            lastAcceptedLocationText = lastAcceptedLocationText,
            lastAccuracyText = lastAccuracyText,
            pendingUploadTotal = pendingUploadTotal,
            apiState = apiState,
            lastDroppedReason = lastDroppedReason,
            highSpeedActive = highSpeedTrackerMode() == HighSpeedMode.Active,
            highSpeedElapsedSeconds = highSpeedElapsedSeconds()
        )
    )

    private fun highSpeedTrackerMode(): HighSpeedMode? =
        policyEngine?.highSpeedTracker?.mode

    private fun highSpeedElapsedSeconds(): Long {
        val since = policyEngine?.highSpeedTracker?.activeSinceElapsedRealtimeMillis ?: return 0L
        return (SystemClock.elapsedRealtime() - since).coerceAtLeast(0L) / 1_000L
    }

    private fun updateNotification() {
        publishRuntimeState()
        val notificationManager = getSystemService(Context.NOTIFICATION_SERVICE) as android.app.NotificationManager
        notificationManager.notify(LocationNotificationRenderer.NOTIFICATION_ID, notification())
    }

    private fun publishRuntimeState(isRunning: Boolean = isRunning()) {
        val highSpeedSince = policyEngine?.highSpeedTracker?.activeSinceElapsedRealtimeMillis
        _runtimeState.value = ForegroundLocationRuntimeState(
            isRunning = isRunning,
            currentPolicyMode = currentDecision.mode.name,
            currentPolicyReason = currentDecision.reason,
            requestIntervalMillis = currentDecision.requestIntervalMillis.takeUnless { it <= 0L },
            nextExpectedLocationAtMillis = currentDecision.nextExpectedLocationAtMillis
                .takeUnless { it == Long.MAX_VALUE },
            lastAcceptedLocationText = lastAcceptedLocationText,
            lastAccuracyText = lastAccuracyText,
            pendingUploadTotal = pendingUploadTotal,
            apiState = apiState,
            lastDroppedReason = lastDroppedReason,
            scheduleFreshness = scheduleFreshness,
            scheduleLastSuccessAtMillis = scheduleLastSuccessAtMillis,
            scheduleLastAttemptAtMillis = scheduleLastAttemptAtMillis,
            scheduleLastError = scheduleLastError,
            highSpeedActive = highSpeedSince != null,
            highSpeedElapsedSeconds = highSpeedSince?.let {
                (SystemClock.elapsedRealtime() - it).coerceAtLeast(0L) / 1_000L
            } ?: 0L,
            highSpeedSinceElapsedRealtimeMillis = highSpeedSince
        )
    }

    private fun hasRequiredLocationPermissions(): Boolean {
        val fine = ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION)
        val background = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            ContextCompat.checkSelfPermission(
                this,
                Manifest.permission.ACCESS_BACKGROUND_LOCATION
            ) == PackageManager.PERMISSION_GRANTED
        } else {
            true
        }
        return fine == PackageManager.PERMISSION_GRANTED && background
    }

    private fun nextExpectedLocationText(decision: PolicyDecision): String {
        if (decision.nextExpectedLocationAtMillis == Long.MAX_VALUE) return "暂停"
        val remainingMillis = (decision.nextExpectedLocationAtMillis - System.currentTimeMillis()).coerceAtLeast(0L)
        val minutes = (remainingMillis + 59_999L) / 60_000L
        return if (minutes <= 0L) "即将定位" else "$minutes 分钟后"
    }

    companion object {
        /**
         * 采集循环单次等待时长的纯计算（供单测断言**行为**，而不只是源码里出现常量）。
         *
         * 上限恒为 [LOOP_WAIT_CAP_MILLIS]：冲刺周期对齐只可能缩短等待，绝不放长。
         */
        internal fun resolveLoopWaitMillis(suggested: Long): Long =
            suggested.takeIf { it in 1..LOOP_WAIT_CAP_MILLIS } ?: LOOP_WAIT_CAP_MILLIS

        private val _runtimeState = MutableStateFlow(ForegroundLocationRuntimeState())
        val runtimeState: StateFlow<ForegroundLocationRuntimeState> = _runtimeState.asStateFlow()

        fun isRunning(): Boolean = runtimeState.value.isRunning

        val timeFormatter: DateTimeFormatter = DateTimeFormatter.ofPattern("HH:mm")

        /**
         * 被动计数落台账的周期（AC-14.2）。
         *
         * 1 分钟：既保证服务被杀时最多丢 1 分钟的分母，又不会把台账写爆
         * （被动源本身不设限流，日志/台账写入量要可控）。
         */
        const val PASSIVE_COUNTER_FLUSH_MILLIS = 60_000L

        /**
         * 采集循环单次等待的**上限**（工单 §6「最迟 30s 重算一次」的既有约定）。
         *
         * 冲刺周期对齐只可能**缩短**本次等待（让 45/120/600 秒档在边界处醒来），
         * 绝不会加长它。
         */
        const val LOOP_WAIT_CAP_MILLIS = 30_000L

        fun resolveRequestInterval(intervalMillis: Long): Long {
            require(intervalMillis > 0L) { "intervalMillis must be positive" }
            return intervalMillis
        }

        fun resolveLocationPriority(mode: LocationPolicyMode): Int =
            Priority.PRIORITY_HIGH_ACCURACY
    }
}
