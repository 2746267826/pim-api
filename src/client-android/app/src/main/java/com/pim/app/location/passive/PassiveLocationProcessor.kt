package com.pim.app.location.passive

import com.pim.app.location.policy.GeoDistance
import com.pim.app.location.policy.PolicyLocation
import com.pim.app.location.quality.LocationQualityGate
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.QualityDecision
import com.pim.app.location.quality.RawLocationFix

/** 被动定位回调的原始 fix（平台 `LocationManager` 投递，provider 为原始提供方）。 */
data class PassiveFix(
    val latitude: Double,
    val longitude: Double,
    val horizontalAccuracyMeters: Float?,
    val altitudeMeters: Double?,
    val provider: String,
    val recordedAtMillis: Long
)

/** 被动点经质量门收下后的结果（`source` 由 REQ-14 规则 6 固定为 passive）。 */
data class PassiveAcceptedLocation(
    val accepted: QualityAcceptedLocation,
    val source: String
)

/** 被动点的三数对账计数（AC-14.2 / AC-14.3）。 */
data class PassiveLocationCounters(
    /** 被动回调总数（**去重之前**，AC-14.2 的对账分母）。 */
    val callbackCount: Int,
    /** 入库数（达标且非重复）。 */
    val acceptedCount: Int,
    /** 丢弃记录数（不达标 / 缺精度）。 */
    val droppedCount: Int,
    /** 重复记录数（与主动流同一 fix，AC-14.5）。 */
    val duplicateCount: Int
) {
    /** 对账缺口：回调 = 入库 + 丢弃 + 重复 时必须为 0（AC-14.2 / AC-14.6）。 */
    val unaccountedCount: Int
        get() = callbackCount - acceptedCount - droppedCount - duplicateCount
}

/**
 * 被动点的判定器（WO-ANDROID-GATE-20260926 REQ-14 / A9）。
 *
 * 职责：走**同一道**质量门（D11：共用 30 米门槛），逐条判定并记账。
 *
 * **刻意不持有的东西**（每条都对应一个反面 AC，改动前请先读工单）：
 * - **没有任何限流参数**（无最小间隔、无每小时上限、无节流窗口）—— REQ-14 规则 2 /
 *   AC-14.3 明确「不设限流」。这不是靠注释保证，而是本类里根本不存在这类字段。
 * - **没有任何点级裁剪**（REQ-15 已取消全部客户端裁剪）。
 * - **不持有定位引擎/采集协调器** —— AC-14.7 要求被动监听不得改变主动流的注册间隔
 *   与周期锚点；结构上不接触，就不可能扰动。
 * - **不产出冲刺台账口径** —— AC-14.8 要求被动点不计入 `sprintSampleCount` /
 *   `sprintAcceptedCount`，以免污染 AC-4.5 / AC-5.3 的证据。
 *
 * 丢弃与重复都通过 [onDrop] 留痕（reason 编码，AC-14.6），绝不静默丢弃。
 */
class PassiveLocationProcessor(
    private val qualityGate: LocationQualityGate,
    /**
     * 主动流最近的入库 fix（用于 AC-14.5 的重复判定）。
     *
     * 传的是**快照**而不是可变集合：被动判定不得回头改动主动流状态（AC-14.7）。
     */
    private val activeFixProvider: () -> List<RawLocationFix> = { emptyList() },
    /** 重复判定容差（D10，可注入以便真机校准）。 */
    private val duplicateMaxTimeDiffMillis: Long =
        PassiveLocationContract.DUPLICATE_MAX_TIME_DIFF_MILLIS,
    private val duplicateMaxDistanceMeters: Double =
        PassiveLocationContract.DUPLICATE_MAX_DISTANCE_METERS
) {
    /**
     * 丢弃/重复留痕出口（reason 编码，AC-14.6）。
     *
     * **每个丢掉的被动点都必须经过这里**：不达标、缺精度、重复三条路径都调用它，
     * 因此不会出现「命中即 return 且无记录」。
     */
    var onDrop: (suspend (RawLocationFix, String) -> Unit)? = null

    /** 达标点的入库出口（逐条入库，`source = passive`）。 */
    var onAccepted: (suspend (PassiveAcceptedLocation) -> Unit)? = null

    private val lock = Any()
    private var callbackCount = 0
    private var acceptedCount = 0
    private var droppedCount = 0
    private var duplicateCount = 0
    private var lastReason: String? = null
    private var lastEnqueueError: Exception? = null

    /**
     * 判定一条被动点。
     *
     * @return 收下的点（`source = passive`）；不达标或重复时返回 null 并**已留痕**。
     */
    suspend fun handle(fix: PassiveFix): PassiveAcceptedLocation? {
        synchronized(lock) { callbackCount += 1 }

        val raw = fix.toRawFix()
        return when (val decision = qualityGate.evaluate(raw, fix.recordedAtMillis)) {
            is QualityDecision.Drop -> {
                // AC-14.2 / AC-14.6：不达标必须留下记录（原因编码带 passive- 前缀）。
                val reason = when (decision.reason) {
                    "missing-horizontal-accuracy" -> PassiveLocationContract.REASON_MISSING_ACCURACY
                    else -> PassiveLocationContract.REASON_ACCURACY_TOO_LOW
                }
                recordDrop(raw, reason, duplicate = false)
                null
            }
            is QualityDecision.WaitForAltitude -> {
                // 被动点不做 15 秒海拔等待：被动源不归我们调度，等不到也不该挂着。
                // 与流模式一致地收下并带 altitude-missing 标记，绝不静默丢弃。
                accept(
                    QualityAcceptedLocation(
                        fix = raw,
                        altitudeMeters = null,
                        acceptedAtMillis = fix.recordedAtMillis,
                        qualityFlags = setOf(PASSIVE_ALTITUDE_MISSING_FLAG)
                    ),
                    raw
                )
            }
            is QualityDecision.AcceptNow -> accept(decision.accepted, raw)
        }
    }

    /** 当前三数计数快照（AC-14.2）。 */
    fun countersSnapshot(): PassiveLocationCounters = synchronized(lock) {
        PassiveLocationCounters(callbackCount, acceptedCount, droppedCount, duplicateCount)
    }

    /** 最近一次留痕原因（诊断与测试用）。 */
    fun lastDropReason(): String? = synchronized(lock) { lastReason }

    /** 最近一次入库失败（诊断用；非空说明有过写库异常，已转成丢弃留痕）。 */
    fun lastEnqueueFailure(): Exception? = synchronized(lock) { lastEnqueueError }

    /** 当前窗口计数快照（**不清零**）——先落库、成功后再清零，避免丢分母。 */
    fun peekCounters(): PassiveLocationCounters = synchronized(lock) {
        PassiveLocationCounters(
            callbackCount = callbackCount,
            acceptedCount = acceptedCount,
            droppedCount = droppedCount,
            duplicateCount = duplicateCount
        )
    }

    /**
     * 扣除**已落库的那一份**计数（在台账写入成功后调用）。
     *
     * 用「减去快照」而不是「清零」：写台账是挂起操作，期间到达的回调
     * （`peekCounters()` 之后、写入完成之前）不属于刚写的那一行；直接清零会把它们
     * 一起抹掉 —— 分母偏小，AC-14.2 的三数对账就失去意义（独立 review round 4 的 M-1）。
     * 减去快照能让这些增量留在窗口里，等下一次刷新落库。
     */
    fun subtractCounters(snapshot: PassiveLocationCounters) {
        synchronized(lock) {
            callbackCount = (callbackCount - snapshot.callbackCount).coerceAtLeast(0)
            acceptedCount = (acceptedCount - snapshot.acceptedCount).coerceAtLeast(0)
            droppedCount = (droppedCount - snapshot.droppedCount).coerceAtLeast(0)
            duplicateCount = (duplicateCount - snapshot.duplicateCount).coerceAtLeast(0)
        }
    }

    private suspend fun accept(
        accepted: QualityAcceptedLocation,
        raw: RawLocationFix
    ): PassiveAcceptedLocation? {
        if (isDuplicateOfActiveStream(raw)) {
            // AC-14.5：同一个 fix 可能同时从主动流与被动通道到达。
            // 只入库一条，但**重复也必须留痕**（客户端不留痕就看不到重复率）。
            recordDrop(raw, PassiveLocationContract.REASON_DUPLICATE_FIX, duplicate = true)
            return null
        }
        val result = PassiveAcceptedLocation(
            accepted = accepted,
            source = PassiveLocationContract.SOURCE
        )
        // AC-14.2 / AC-14.6：**先确认真的落库，再计入入库数**。
        // 若先计数后写库，一旦写库抛异常（或没有出口），计数会显示缺口为 0
        // 而点其实丢了 —— 那正是「静默丢弃」的伪装。
        try {
            // REQ-15 / AC-15.1：达标点逐条入库（无裁剪、无限流）。
            onAccepted?.invoke(result)
        } catch (e: kotlinx.coroutines.CancellationException) {
            throw e
        } catch (e: Exception) {
            // 落库失败必须转换成**丢弃留痕**，而不是让点数凭空消失。
            recordDrop(raw, PassiveLocationContract.REASON_ENQUEUE_FAILED, duplicate = false)
            lastEnqueueError = e
            return null
        }
        synchronized(lock) { acceptedCount += 1 }
        return result
    }

    private fun isDuplicateOfActiveStream(raw: RawLocationFix): Boolean {
        val active = activeFixProvider()
        if (active.isEmpty()) return false
        return active.any { candidate ->
            val timeDiff = kotlin.math.abs(candidate.recordedAtMillis - raw.recordedAtMillis)
            if (timeDiff > duplicateMaxTimeDiffMillis) return@any false
            val distance = GeoDistance.metersBetween(
                PolicyLocation(candidate.latitude, candidate.longitude, candidate.recordedAtMillis),
                PolicyLocation(raw.latitude, raw.longitude, raw.recordedAtMillis)
            )
            distance <= duplicateMaxDistanceMeters
        }
    }

    private suspend fun recordDrop(raw: RawLocationFix, reason: String, duplicate: Boolean) {
        synchronized(lock) {
            if (duplicate) duplicateCount += 1 else droppedCount += 1
            lastReason = reason
        }
        // AC-14.6：留痕出口由调用方落到既有丢弃诊断（原因编码，不改库结构）。
        onDrop?.invoke(raw, reason)
    }

    private fun PassiveFix.toRawFix() = RawLocationFix(
        latitude = latitude,
        longitude = longitude,
        horizontalAccuracyMeters = horizontalAccuracyMeters,
        altitudeMeters = altitudeMeters,
        provider = provider,
        recordedAtMillis = recordedAtMillis,
        policyMode = PASSIVE_POLICY_MODE,
        scheduleLowFrequency = false,
        motionSignal = PASSIVE_MOTION_SIGNAL
    )

    companion object {
        /** 被动点缺海拔的标记（沿用流模式口径）。 */
        const val PASSIVE_ALTITUDE_MISSING_FLAG = "altitude-missing"

        /**
         * 被动点的策略档标注。被动监听随采集服务常驻，与周期策略无关（D9），
         * 因此**不谎报**某个策略档，用被动专属标注。
         */
        const val PASSIVE_POLICY_MODE = "Passive"

        /** 被动点的运动信号标注：被动通道不携带运动状态（不猜）。 */
        const val PASSIVE_MOTION_SIGNAL = "Unknown"
    }
}
