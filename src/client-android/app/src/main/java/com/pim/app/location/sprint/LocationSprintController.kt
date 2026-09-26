package com.pim.app.location.sprint

import com.pim.app.location.LocationSnapshot
import com.pim.app.location.acquisition.AcquisitionContext
import com.pim.app.location.acquisition.LocationUpdateRequest
import com.pim.app.location.policy.LocationPolicyMode
import com.pim.app.location.quality.LocationQualityGate
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.QualityDecision
import com.pim.app.location.quality.RawLocationFix
import android.os.SystemClock
import com.google.android.gms.location.Priority
import com.pim.app.settings.TrackingSettingsStore
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import javax.inject.Inject
import javax.inject.Singleton

/** 一次冲刺请求的结果（含被跳过的原因，AC-8.2）。 */
sealed interface SprintStartDecision {
    data class Started(val startedAtUtcMillis: Long) : SprintStartDecision
    data class Skipped(val reason: String) : SprintStartDecision
}

/**
 * 30 秒定位冲刺的编排器（WO-ANDROID-GATE-20260926 REQ-2 ~ REQ-8）。
 *
 * 设计要点（**每条都对应工单里的反面条款，改动前请先读工单**）：
 *
 * - **独立注册**：冲刺用**自己的** [LocationUpdateRequest]（≈1 秒、duration = 30 秒），
 *   绝不改写主流的注册间隔或周期锚点（AC-5.6 明令禁止「临时改间隔到 1 秒再改回」——
 *   那会把下一拍从改回时刻重新计时，周期被拉长）。
 * - **有界窗口**：窗口最长 30 秒且**不早退**（REQ-3 / A6 / D6）；窗口由周期驱动，
 *   不是不受开关控制的常驻高频模式（AC-10.3）。
 * - **运动/车载档照冲**（AC-2.5 / A8）：30 秒硬下限下窗口与周期相接、接近连续采样，
 *   属预期行为，**不得**以「过于频繁」为由跳过或裁剪窗口。因此这里对
 *   `requestIntervalMillis` **不做任何节拍检查**。
 * - **开关真生效**（AC-5.3 / AC-5.5）：每个周期都重新读 [sprintEnabledProvider]，
 *   不做一次性快照，因此切换后无需重启即生效。
 */
@Singleton
class LocationSprintController @Inject constructor(
    private val runner: SprintUpdateSource,
    private val ledger: SprintLedgerPort,
    private val trackingSettingsStore: TrackingSettingsStore
) {
    private val internalScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)

    /** 测试接缝：注入 TestScope（与 [LocationAcquisitionCoordinator] 同一约定）。 */
    internal var testScope: CoroutineScope? = null
    private val scope: CoroutineScope get() = testScope ?: internalScope

    internal var wallClockMillis: () -> Long = { System.currentTimeMillis() }

    /**
     * 单调时钟（窗口时长的唯一依据）。
     *
     * 窗口跨度必须按**单调时钟**度量：墙钟可能被 NTP 校正或用户手动调整，
     * 回拨会把窗口拉长到超过 AC-2.2 的 30 秒 + ε，前拨又会立刻结束窗口而违反
     * AC-3.1（这与会话内海拔等待的既有做法一致：`AltitudeWaitCoordinator`
     * 同样用 `elapsedRealtime` 兜住墙钟回拨）。
     */
    internal var elapsedRealtimeMillis: () -> Long = { SystemClock.elapsedRealtime() }

    /**
     * 质量门来源。默认**每拍**按当前设置构造，因此「门槛固定 30 米、无可调项」
     * （REQ-7）与「海拔等待沿用既有设置」两条都保持既有语义。
     */
    internal var qualityGateProvider: () -> LocationQualityGate = {
        LocationQualityGate.fromTrackingSettings(trackingSettingsStore.read())
    }

    /**
     * 冲刺开关来源（REQ-5 / AC-5.5）。**每次发起冲刺都重新读**，
     * 因此切换后无需重启即自下一个周期起生效。
     */
    internal var sprintEnabledProvider: () -> Boolean = {
        trackingSettingsStore.read().sprintEnabled
    }

    /** 可注入的延时（测试用虚拟时钟；生产为 `delay`）。 */
    internal var delayMillis: suspend (Long) -> Unit = { delay(it) }
    /**
     * (accepted, accuracy) 回调：由采集引擎负责真正入库（逐条入库，REQ-15）。
     * 冲刺只负责「判定 + 记账」，不直接写库，避免两处入库口径分叉。
     */
    var onAccepted: (suspend (QualityAcceptedLocation, Float?) -> Unit)? = null

    /** 被质量门丢弃的 fix（reason 沿用既有编码，AC-4.3）。 */
    var onDropped: (suspend (RawLocationFix, String) -> Unit)? = null

    /** 窗口结束回调（供状态页/通知刷新）。 */
    var onWindowFinished: (suspend (SprintWindowResult) -> Unit)? = null

    private var window: LocationSprintWindow? = null
    private var windowJob: Job? = null

    /** 窗口的单调时钟起点（判定剩余时长用；账台账仍记墙钟时刻供人对账）。 */
    private var windowStartedElapsedRealtimeMillis: Long = 0L

    /** 当前窗口是否由手动会话发起。 */
    private var windowManual: Boolean = false

    /**
     * 「下一拍待发起」槽位（AC-2.5 / A8 方案 B）。
     *
     * 运动/车载档（30 秒硬下限）下窗口会与周期相接：下一个周期的请求往往在本窗口
     * 结束前就到。此时**不能跳过**（那会让「每一拍都冲」变成隔拍才冲），而是记在这里，
     * 本窗口一结束立刻接着开 —— 窗口之间不重叠（AC-2.3），但每一拍都真的冲到。
     *
     * 只保留**一个**待发起请求（合并多余的重复请求），避免窗口串成无限长的高频模式。
     */
    private var pendingStart: PendingSprint? = null

    /**
     * 中止代数（abort generation）。
     *
     * `finishWindow` 会在**挂起**（写台账）之后才启动待发起的下一拍；这期间
     * `abort()`（停止采集/服务销毁）与「开关被关掉」都可能已经发生。
     * 独立 review round 2 指出：那时 `windowJob` 已是 null，`abort()` 取消不到
     * 这个协程，于是它仍会开出一个新窗口 —— 停止采集后还在冲刺。
     *
     * 每次 [abort] 递增本代数；启动待发起窗口前必须复核代数未变**且**开关仍开。
     */
    private var abortGeneration: Long = 0L

    private data class PendingSprint(
        val context: AcquisitionContext,
        val requestedAtUtcMillis: Long
    )

    /** 窗口是否开放（AC-2.3 用；也供验收方在状态页观察）。 */
    @Synchronized
    fun isWindowOpen(): Boolean = window?.isOpen == true

    /** 最近一次窗口结果（供状态页展示；未冲刺时为 null）。 */
    @Volatile
    var lastWindowResult: SprintWindowResult? = null
        private set

    /**
     * 请求发起一次冲刺（由采集周期的每一拍调用，D2）。
     *
     * @param mode 当前策略档；[LocationPolicyMode.HighSpeed] 时不冲刺（AC-2.4），
     *   [LocationPolicyMode.Off] 时记录「非采集时段」（AC-10.3）。
     * @param blockedReason 前置条件阻塞原因（权限/系统定位未就绪）；非空时记录原因而不是静默失败。
     */
    fun startSprint(
        context: AcquisitionContext,
        mode: LocationPolicyMode,
        blockedReason: String? = null,
        nowUtcMillis: Long = wallClockMillis()
    ): SprintStartDecision {
        // REQ-5 / AC-5.3：开关每拍重新读，关闭后必须**真的不再发起任何冲刺**。
        if (!sprintEnabledProvider()) {
            // 关闭时清掉待发起的下一拍：否则它会在本窗口结束后被接上（AC-5.3 反面）。
            synchronized(this) { pendingStart = null }
            return skip(SprintSkipReasons.DISABLED, nowUtcMillis)
        }
        if (blockedReason != null) {
            return skip(SprintSkipReasons.PREREQUISITE_BLOCKED, nowUtcMillis)
        }
        // AC-10.3：非采集时段不冲刺。
        if (mode == LocationPolicyMode.Off) {
            return skip(SprintSkipReasons.NOT_COLLECTING, nowUtcMillis)
        }
        // AC-2.4：高速档（2.5 秒一拍）已是密集采样，不叠加冲刺。
        if (mode == LocationPolicyMode.HighSpeed) {
            return skip(SprintSkipReasons.HIGH_SPEED, nowUtcMillis)
        }
        synchronized(this) {
            if (window?.isOpen == true) {
                // 已经有一个窗口在跑。是否把本拍排进「待发起」取决于**触发来源**：
                //
                // - **周期驱动**（自动采集，D2）：AC-2.5 / A8 方案 B 要求运动/车载档
                //   窗口与周期相接、接近连续采样，因此**不得**以「过于频繁」为由跳过 ——
                //   记为「下一拍待发起」，本窗口一结束立刻接着开。
                // - **手动驱动**（用户在同一段时间里按了手动定位）：手动会话与自动周期
                //   是两个独立触发源，把手动那一拍排进自动周期会让同一周期出现两次冲刺
                //   （违反 D2「每个采集周期一次」），还会与手动会话自己的取点流重复入库。
                //   因此手动在窗口已开时**不排队**，只如实记「跳过」。
                pendingStart = PendingSprint(context = context, requestedAtUtcMillis = nowUtcMillis)
                return SprintStartDecision.Started(nowUtcMillis)
            }
            return startWindowLocked(context, nowUtcMillis)
        }
    }

    /**
     * 发起一次**手动**会话的冲刺（REQ-6 / AC-6.1 / AC-6.3）。
     *
     * 与周期驱动分开的理由：手动会话自带 ≈1 秒/最长 30 秒的取点过程，若把它也算作
     * 「一个采集周期」，会污染周期锚点（`SprintPeriodGate`），使下一个自动周期
     * 提前或重复触发（独立 review round 3 的 I-1）。
     *
     * 因此这里：
     * - **不经过周期门控**，也不改写周期锚点；
     * - 已有窗口在跑时**不排队**（避免同一周期两次冲刺）；
     * - 开关关闭时照常记「跳过」且不注册（AC-6.3）。
     */
    fun startManualSprint(
        context: AcquisitionContext,
        nowUtcMillis: Long = wallClockMillis()
    ): SprintStartDecision {
        if (!sprintEnabledProvider()) {
            return skip(SprintSkipReasons.DISABLED, nowUtcMillis)
        }
        synchronized(this) {
            if (window?.isOpen == true) {
                // 本周期已有窗口在跑：手动这一拍不排队，只记跳过（原因可核对）。
                return skip(SprintSkipReasons.WINDOW_ALREADY_OPEN, nowUtcMillis)
            }
            return startWindowLocked(context, nowUtcMillis, manual = true)
        }
    }

    /** 在锁内真正开一个窗口（调用方必须已持有锁）。 */
    private fun startWindowLocked(
        context: AcquisitionContext,
        nowUtcMillis: Long,
        manual: Boolean = false
    ): SprintStartDecision {
        val fresh = LocationSprintWindow(startedAtUtcMillis = nowUtcMillis)
        window = fresh
        windowManual = manual
        windowStartedElapsedRealtimeMillis = elapsedRealtimeMillis()
        windowJob = scope.launch { runWindow(fresh, context) }
        return SprintStartDecision.Started(nowUtcMillis)
    }

    /**
     * 只中止**手动会话自己发起**的窗口（AC-6.1 / R5-2）。
     *
     * 自动采集循环的窗口不归手动会话管：手动结束不能把它取消掉，否则那一拍
     * 会永远丢掉「已执行」记录；也不能重置周期锚点（那会让自动循环提前再冲一次）。
     *
     * @return true 表示确实中止了一个手动窗口。
     */
    fun abortManualWindow(): Boolean {
        synchronized(this) {
            if (window?.isOpen != true || !windowManual) return false
        }
        abort()
        return true
    }

    /**
     * 中止当前窗口（采集停止 / 服务销毁 / 手动会话取消）。
     *
     * 中止**不写**已执行记录：窗口没有跑完，不该产出可被当成「已冲刺」的证据（AC-5.4 同理）。
     */
    fun abort() {
        synchronized(this) {
            // 递增代数：让正在 `finishWindow` 里挂起（写台账）的那个协程
            // 在恢复后也**无法**再开出待发起的窗口。
            abortGeneration += 1L
            window?.let { it.finish(wallClockMillis()) }
            window = null
            windowJob?.cancel()
            windowJob = null
            // 中止时一并清掉待发起：避免停止采集后还冒出一个窗口。
            pendingStart = null
        }
    }

    /** 是否存在待发起的下一拍（诊断用）。 */
    @Synchronized
    fun hasPendingStart(): Boolean = pendingStart != null

    /**
     * 当前窗口是否由**手动会话**发起（用于「只中止自己那一个窗口」）。
     *
     * 采集停止/服务销毁时无条件中止是对的；但**手动会话终结**时只能中止
     * 手动自己开的那一个窗口 —— 否则会把自动采集循环正在跑的冲刺一并取消，
     * 让那一拍丢失（独立 review round 5 的 R5-2）。
     */
    @Synchronized
    fun isWindowManual(): Boolean = windowManual

    private suspend fun runWindow(active: LocationSprintWindow, context: AcquisitionContext) {
        // AC-5.6：冲刺必须独立注册；主流注册的 interval 与锚点完全不受影响。
        //
        // durationMillis 让**系统侧**知道这是一段有界请求；但 GMS 到期后只是不再回调，
        // `callbackFlow` 本身不会关闭，因此窗口的**结束必须由这里自己保证**：
        // 用 `remaining` 等待到期再取消自己的注册。绝不能依赖流自然返回 ——
        // 那样窗口永不结束，REQ-3 的台账与 AC-2.2 的跨度都无从谈起。
        val request = LocationUpdateRequest(
            priority = Priority.PRIORITY_HIGH_ACCURACY,
            intervalMillis = LocationSprintContract.SAMPLE_INTERVAL_MILLIS,
            minUpdateIntervalMillis = LocationSprintContract.MIN_SAMPLE_INTERVAL_MILLIS,
            durationMillis = LocationSprintContract.WINDOW_MILLIS
        )
        try {
            coroutineScope {
                val sprintRegistration = launch {
                    try {
                        runner.streamSprintWindow(request) { snapshot ->
                            if (!active.isOpen) return@streamSprintWindow
                            handleSample(active, snapshot, context)
                        }
                    } catch (e: CancellationException) {
                        throw e
                    } catch (_: Exception) {
                        // 注册失败不静默：窗口照常按时间结束并落台账，sampleCount 如实为 0，
                        // 验收方据此可见「这一拍没取到点」而不是以为冲刺正常。
                    }
                }
                // REQ-3：窗口**不早退** —— 无论中途是否已拿到达标/极精确的点，都等满 30 秒。
                // 剩余时长按**单调时钟**算：墙钟回拨不会把窗口拉长（AC-2.2），
                // 前拨也不会提前结束窗口（AC-3.1）。
                val remaining = LocationSprintContract.WINDOW_MILLIS -
                    (elapsedRealtimeMillis() - windowStartedElapsedRealtimeMillis)
                if (remaining > 0L) {
                    delayMillis(remaining)
                }
                sprintRegistration.cancel()
            }
        } catch (e: CancellationException) {
            // 外部中止（abort()）：窗口未跑完，不产出「已执行」记录。
            throw e
        }
        finishWindow(active)
    }

    private suspend fun handleSample(
        active: LocationSprintWindow,
        snapshot: LocationSnapshot,
        context: AcquisitionContext
    ) {
        // §6 口径：sampleCount 统计**去重之前**的回调条数。
        // REQ-15 已取消常规流 2 秒去重，这里也没有任何去重/裁剪。
        active.onSample(snapshot)

        val fix = snapshot.toRawFix(context)
        when (val decision = qualityGateProvider().evaluate(fix, wallClockMillis())) {
            is QualityDecision.AcceptNow -> {
                active.onAccepted(decision.accepted, fix.horizontalAccuracyMeters)
                onAccepted?.invoke(decision.accepted, fix.horizontalAccuracyMeters)
            }
            is QualityDecision.WaitForAltitude -> {
                // 与流模式一致：冲刺窗口内不等待海拔（窗口有界），带标记收下，
                // 避免整个窗口被一条缺海拔的点挂住。
                val accepted = QualityAcceptedLocation(
                    fix = fix,
                    altitudeMeters = null,
                    acceptedAtMillis = wallClockMillis(),
                    qualityFlags = setOf(SPRINT_ALTITUDE_MISSING_FLAG)
                )
                active.onAccepted(accepted, fix.horizontalAccuracyMeters)
                onAccepted?.invoke(accepted, fix.horizontalAccuracyMeters)
            }
            is QualityDecision.Drop -> {
                // AC-4.3：不得因处于冲刺窗口而放宽门槛。
                onDropped?.invoke(decision.fix, decision.reason)
            }
        }
    }

    private suspend fun finishWindow(active: LocationSprintWindow) {
        val result = active.finish(wallClockMillis())
        val next: PendingSprint?
        val generationAtFinish: Long
        synchronized(this) {
            next = pendingStart
            generationAtFinish = abortGeneration
            pendingStart = null
            if (window === active) {
                window = null
                windowJob = null
            }
        }
        lastWindowResult = result
        ledger.recordExecuted(result)
        onWindowFinished?.invoke(result)

        // AC-2.5：本窗口结束后立刻接上待发起的那一拍（窗口与周期相接）。
        if (next != null) {
            val generationAtCapture = generationAtFinish
            synchronized(this) {
                // ① 期间被中止（停止采集/服务销毁）→ 不得再开新窗口。
                // ② 期间开关被关掉 → 同样不得再开（AC-5.3：关了必须真的不冲）。
                val cancelled = abortGeneration != generationAtCapture
                if (!cancelled && sprintEnabledProvider() && window?.isOpen != true) {
                    startWindowLocked(next.context, wallClockMillis())
                    return
                }
                // 未接上时**不静默丢弃**：按规则记「跳过」原因并清掉待发起，
                // 让台账如实反映「这一拍没冲」（AC-8.2）。
                pendingStart = null
            }
            if (abortGeneration != generationAtCapture) {
                // 已中止：连「跳过」也不必记（停止采集不是一次采集决策）。
                return
            }
            if (!sprintEnabledProvider()) {
                skip(SprintSkipReasons.DISABLED, wallClockMillis())
            }
        }
    }

    private fun skip(reason: String, nowUtcMillis: Long): SprintStartDecision {
        scope.launch { ledger.recordSkipped(nowUtcMillis, reason) }
        return SprintStartDecision.Skipped(reason)
    }

    private fun LocationSnapshot.toRawFix(context: AcquisitionContext) = RawLocationFix(
        latitude = latitude,
        longitude = longitude,
        horizontalAccuracyMeters = horizontalAccuracyMeters,
        altitudeMeters = altitudeMeters,
        provider = provider,
        recordedAtMillis = timeMillis,
        policyMode = context.policyMode,
        scheduleLowFrequency = context.scheduleLowFrequency,
        motionSignal = context.motionSignal,
        speedMetersPerSecond = speedMetersPerSecond,
        bearingDegrees = bearingDegrees
    )

    companion object {
        /** 冲刺窗口内缺海拔的标记（沿用流模式口径，不新增语义）。 */
        const val SPRINT_ALTITUDE_MISSING_FLAG = "altitude-missing"
    }
}
