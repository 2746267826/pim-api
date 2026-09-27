package com.pim.app.location.sprint

import com.pim.app.location.LocationSnapshot
import com.pim.app.location.quality.QualityAcceptedLocation

/**
 * 冲刺窗口的纯状态机（REQ-2 / REQ-3 / REQ-4 / REQ-8）。
 *
 * 职责边界：**只记账，不取点、不判定、不写库**。取点由
 * [LocationSprintRunner] 驱动，质量门判定与入库由既有采集引擎完成，
 * 本类只负责把窗口内发生的事按 §6 口径累加起来，供台账与状态页使用。
 *
 * 不依赖 Android 类，可脱离设备直接单测。
 */
class LocationSprintWindow(val startedAtUtcMillis: Long) {

    /** 窗口是否仍然开放（AC-2.3：结束后才允许下一个窗口）。 */
    var isOpen: Boolean = true
        private set

    /** 回调条数（**去重之前**，§6 口径）；与入库条数分开统计，供 AC-4.5 对账。 */
    var sampleCount: Int = 0
        private set

    /** 入库条数（通过质量门并写入台账）。 */
    var acceptedCount: Int = 0
        private set

    /** 窗口内**达标**点中的最小水平精度；无达标点时为 null（AC-4.1：不谎报）。 */
    var bestAccuracyMeters: Float? = null
        private set

    /** 窗口内出现过的全部回调精度（仅用于诊断，不参与收点）。 */
    private val sampledAccuracies = mutableListOf<Float>()

    /** 记一次定位回调（去重之前；无论是否达标都要记，AC-4.5 的对账分母）。 */
    fun onSample(snapshot: LocationSnapshot) {
        if (!isOpen) return
        sampleCount += 1
        snapshot.horizontalAccuracyMeters
            ?.takeIf { it.isFinite() }
            ?.let(sampledAccuracies::add)
    }

    /**
     * 记一条**已达标**的入库点（REQ-4：窗口期间达标的点全部保留，不只留最好一条）。
     *
     * 未达标的点**不得**调用本方法 —— 门槛由既有质量门判定，冲刺不回退也不放宽（AC-4.3）。
     */
    fun onAccepted(accepted: QualityAcceptedLocation, accuracyMeters: Float?) {
        if (!isOpen) return
        acceptedCount += 1
        val accuracy = accuracyMeters?.takeIf { it.isFinite() } ?: return
        val current = bestAccuracyMeters
        if (current == null || accuracy < current) {
            bestAccuracyMeters = accuracy
        }
    }

    /** 窗口内出现过的回调精度快照（诊断用）。 */
    fun sampledAccuracySnapshot(): List<Float> = sampledAccuracies.toList()

    /**
     * 结束窗口并产出台账结果。幂等：重复调用返回同一结果（AC-2.3 不得叠加窗口）。
     */
    fun finish(endedAtUtcMillis: Long): SprintWindowResult {
        isOpen = false
        return SprintWindowResult(
            startedAtUtcMillis = startedAtUtcMillis,
            endedAtUtcMillis = endedAtUtcMillis,
            sampleCount = sampleCount,
            bestAccuracyMeters = bestAccuracyMeters,
            acceptedCount = acceptedCount
        )
    }
}
