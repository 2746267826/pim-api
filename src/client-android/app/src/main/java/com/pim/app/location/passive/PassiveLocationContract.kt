package com.pim.app.location.passive

/**
 * WO-ANDROID-GATE-20260926 REQ-14（A9）领域契约。
 *
 * 被动定位（平台 `LocationManager.PASSIVE_PROVIDER`）：**自己不发起定位**，
 * 只接收系统中其他应用/服务已经触发的定位结果。不依赖 Android 类，可脱离设备单测。
 */
object PassiveLocationContract {

    /**
     * 被动点写入点表 `source` 列的值（D12 / REQ-14 规则 6）。
     *
     * **`provider` 保持原始提供方**（gps/network/fused），不得改写为 passive ——
     * 平台语义就是「返回点的 getProvider() 是原始提供方」，改写会丢掉真实信息。
     */
    const val SOURCE = "passive"

    /**
     * 精度不足的被动点丢弃原因编码（D12：需求方选定 **2A 原因编码**，**不改库结构**）。
     *
     * 丢弃诊断表**没有 `source` 列**，其 `provider` 是原始提供方（不能充当来源标记），
     * 因此来源只能靠**原因前缀**区分（AC-14.4）。
     */
    const val REASON_ACCURACY_TOO_LOW = "passive-horizontal-accuracy-too-low"

    /** 精度字段缺失的被动点丢弃原因编码（与主动流的 `missing-horizontal-accuracy` 对应）。 */
    const val REASON_MISSING_ACCURACY = "passive-missing-horizontal-accuracy"

    /**
     * 重复 fix 的留痕原因编码（REQ-14 规则 5 / AC-14.5）。
     *
     * 同一个 fix 可能同时从主动流与被动通道到达：服务端已有唯一索引会再合并一次，
     * **客户端不留痕就看不到重复率**。
     *
     * 前缀必须是 `passive-`：AC-14.4 要求丢弃记录**可按 reason 前缀 `passive-` 筛选**，
     * 因此不能沿用工单里作为**示例**出现的 `duplicate-fix`（那样会漏出前缀筛选）。
     */
    const val REASON_DUPLICATE_FIX = "passive-duplicate-fix"

    /**
     * 入库失败（写库异常）的留痕原因编码。
     *
     * 若不允许这条，写库失败的点会「计数显示缺口为 0、点却不见了」——
     * 伪装成对账通过的静默丢弃（AC-14.2 / AC-14.6 的反面）。
     */
    const val REASON_ENQUEUE_FAILED = "passive-enqueue-failed"

    /** 被动来源的丢弃原因前缀（AC-14.4：按 `passive-` 前缀筛选）。 */
    const val REASON_PREFIX = "passive-"

    /**
     * 重复判定容差（D10）：时间戳差 ≤ 1 秒 **且** 坐标差 ≤ 0.5 米 → 视为同一 fix。
     *
     * 该数值为代拍板项，**最终以真机实测为准**（AC-14.5 要求给出实测重复率）。
     */
    const val DUPLICATE_MAX_TIME_DIFF_MILLIS = 1_000L
    const val DUPLICATE_MAX_DISTANCE_METERS = 0.5

    fun isPassiveReason(reason: String): Boolean = reason.startsWith(REASON_PREFIX)
}

/**
 * 被动点的丢弃原因中文文案（AC-14.4 要求客户端文案映射补齐）。
 *
 * 注意：**未映射的原因会显示「其他原因」**，因此新增编码必须同步登记在这里。
 */
object PassiveDropReasons {
    val ALL = listOf(
        PassiveLocationContract.REASON_ACCURACY_TOO_LOW,
        PassiveLocationContract.REASON_MISSING_ACCURACY,
        PassiveLocationContract.REASON_DUPLICATE_FIX,
        PassiveLocationContract.REASON_ENQUEUE_FAILED
    )

    fun label(reason: String): String? = when (reason) {
        PassiveLocationContract.REASON_ACCURACY_TOO_LOW -> "被动定位精度不达标"
        PassiveLocationContract.REASON_MISSING_ACCURACY -> "被动定位缺少水平精度"
        PassiveLocationContract.REASON_DUPLICATE_FIX -> "被动定位与主动流重复"
        PassiveLocationContract.REASON_ENQUEUE_FAILED -> "被动定位入库失败"
        else -> null
    }
}
