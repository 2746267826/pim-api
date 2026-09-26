package com.pim.app.location.sprint

import com.pim.app.location.LocationPointPayload
import com.pim.app.location.acquisition.AcquisitionContext
import com.pim.app.location.acquisition.LocationAcquisitionOperations
import com.pim.app.location.acquisition.TriggerType
import com.pim.app.location.policy.LocationPolicyMode
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException

/**
 * 冲刺的运行时编排（WO-ANDROID-GATE-20260926 REQ-2 ~ REQ-8）。
 *
 * 把「按周期发起 → 入库/留痕 → 窗口结束回调」收敛到一个类，
 * 让 [com.pim.app.location.service.ForegroundLocationService] 只负责
 * 「每拍调用一次」与生命周期，保持既有边界（既有契约测试要求
 * 服务自身不出现 `recordDropped` / 质量门符号）。
 */
@Singleton
class LocationSprintRuntime internal constructor(
    private val controller: LocationSprintController,
    private val operations: LocationAcquisitionOperations,
    private val nowUtcMillis: () -> Long
) {
    @Inject
    constructor(
        controller: LocationSprintController,
        operations: LocationAcquisitionOperations
    ) : this(controller, operations, System::currentTimeMillis)

    /** 最近一次窗口结果（诊断/状态页用）。 */
    val lastWindowResult: SprintWindowResult?
        get() = controller.lastWindowResult

    /**
     * 周期门控（REQ-2 / D2）：把「采集循环的 30 秒唤醒」收敛成「采集周期」。
     *
     * 循环唤醒周期恒为 30 秒，而档位是 30/45/120/600 秒；没有这道门控时
     * 45 秒档会退化成 30 秒一拍（独立 review round 2 发现的真实缺口）。
     */
    private val periodGate = SprintPeriodGate()

    /** 组装入库/丢弃出口。构造一次即可，重复调用是幂等的。 */
    fun wire() {
        controller.onAccepted = { accepted, _ ->
            try {
                val raw = LocationPointPayload.encode(
                    accepted = accepted,
                    // 冲刺点属于自动采集流（source = auto），与主流同一口径。
                    source = TriggerType.AUTOMATIC.storageSource,
                    submittedAtMillis = nowUtcMillis()
                )
                operations.enqueueAccepted(
                    accepted,
                    raw,
                    TriggerType.AUTOMATIC.storageSource
                )
                operations.scheduleSync()
            } catch (ex: CancellationException) {
                throw ex
            } catch (ex: Exception) {
                // 入库失败必须留痕（AC-15.3 不得静默）：转成丢弃诊断记录。
                runCatching { operations.recordDropped(accepted.fix, SPRINT_ENQUEUE_FAILED) }
            }
        }
        controller.onDropped = { fix, reason ->
            // AC-4.3：冲刺窗口内未达标的点照常走丢弃诊断。
            runCatching { operations.recordDropped(fix, reason) }
        }
    }

    /**
     * 按当前周期发起一次冲刺（每拍调用，D2）。
     *
     * @param mode 当前策略档（AC-2.4 高速档不冲、AC-10.3 Off 不冲）。
     */
    fun onPeriod(
        context: AcquisitionContext,
        mode: LocationPolicyMode,
        blockedReason: String? = null
    ): SprintStartDecision {
        val now = nowUtcMillis()
        // D2：只在**本采集周期**真的到点时发起冲刺（运动/车载档间隔 = 30 秒，
        // 与唤醒同周期，因此每一轮都放行 —— AC-2.5 照冲）。
        if (!periodGate.shouldStart(now, context.requestIntervalMillis)) {
            return SprintStartDecision.Skipped(SprintSkipReasons.NOT_THIS_PERIOD)
        }
        val decision = controller.startSprint(context, mode, blockedReason, now)
        return when (decision) {
            is SprintStartDecision.Started -> {
                // 锚点 = 放行时刻（AC-5.6：周期锚点不得被窗口长度拖后）。
                periodGate.onWindowStarted(now)
                decision
            }
            // 未真正发起（开关关闭/高速档等）不推进锚点：这些是本拍的**决策**，
            // 不是「已经冲过」，下次到点仍应重新判断（AC-5.5 改开关后一个周期内生效）。
            is SprintStartDecision.Skipped -> decision
        }
    }

    /**
     * 手动定位会话的冲刺（REQ-6 / AC-6.1 / AC-6.3）。
     *
     * **不经过周期门控**：手动会话不是自动采集周期的一部分，用它去推进周期锚点
     * 会让下一个自动周期提前/重复触发（独立 review round 3 的 I-1）。
     */
    fun onManualSession(): SprintStartDecision {
        val context = AcquisitionContext(
            policyMode = LocationPolicyMode.PowerSavingNormal.name,
            scheduleLowFrequency = false,
            motionSignal = "Unknown",
            requestIntervalMillis = MANUAL_SESSION_INTERVAL_MILLIS
        )
        return controller.startManualSprint(context, nowUtcMillis())
    }

    /**
     * 供采集循环把等待收敛到**下一个周期边界**（I-2：45 秒档在 30 秒唤醒网格上
     * 会退化成 60 秒一拍）。只缩短等待、不放宽规则，也不超过兜底上限。
     */
    fun suggestedLoopWaitMillis(requestIntervalMillis: Long, capMillis: Long): Long =
        periodGate.suggestedWaitMillis(nowUtcMillis(), requestIntervalMillis, capMillis)

    /** 停止采集/服务销毁时中止窗口（不产出「已执行」记录）。 */
    fun abort() {
        periodGate.reset()
        controller.abort()
    }

    /**
     * **手动会话终结**时只中止手动自己发起的窗口（R5-2）。
     *
     * 手动结束不得取消自动采集循环正在跑的冲刺，也不得重置周期锚点 ——
     * 那会让自动循环提前再冲一次，且那一拍的「已执行」记录永久丢失。
     *
     * @return true 表示确实中止了一个手动窗口。
     */
    fun abortManualWindow(): Boolean = controller.abortManualWindow()

    fun isWindowOpen(): Boolean = controller.isWindowOpen()

    companion object {
        /** 冲刺点入库失败的原因编码（可核对，不静默）。 */
        const val SPRINT_ENQUEUE_FAILED = "sprint-enqueue-failed"

        /**
         * 手动会话的取点节奏（基线 `LocationUpdateSource` 的 1000/800 毫秒）。
         *
         * 只用于标注，不参与自动周期的门控计算。
         */
        const val MANUAL_SESSION_INTERVAL_MILLIS = 1_000L
    }
}
