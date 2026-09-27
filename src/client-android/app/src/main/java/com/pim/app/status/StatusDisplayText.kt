package com.pim.app.status

import com.pim.app.location.passive.PassiveLocationContract
import com.pim.app.schedule.ScheduleCacheFreshness

object StatusDisplayText {
    fun apiReason(code: String?): String = when (code) {
        null, "" -> "暂无"
        "missing" -> "未配置"
        "invalid-api-url" -> "地址格式不正确"
        else -> "未知状态"
    }

    fun profile(value: String?): String = when (value) {
        "power-saving" -> "省电"
        "standard" -> "标准"
        "high-precision" -> "高精度"
        "custom" -> "自定义"
        null, "" -> "暂无"
        else -> "未知状态"
    }

    /**
     * 丢弃原因中文文案。
     *
     * WO-ANDROID-GATE-20260926 REQ-14 规则 4：被动点采用**原因编码**（`passive-*`）留痕，
     * **未映射的原因会显示「其他原因」**，因此被动来源的三个编码必须在这里登记，
     * 否则验收方在「丢弃原因」页看到的是一片「其他原因」，无法区分来源。
     */
    fun droppedReason(value: String?): String = when (value) {
        "missing-horizontal-accuracy" -> "缺少水平精度"
        "horizontal-accuracy-too-low" -> "定位精度不达标"
        "altitude-missing-timeout" -> "等待高度超时"
        PassiveLocationContract.REASON_ACCURACY_TOO_LOW -> "被动定位精度不达标"
        PassiveLocationContract.REASON_MISSING_ACCURACY -> "被动定位缺少水平精度"
        PassiveLocationContract.REASON_DUPLICATE_FIX -> "被动定位与主动流重复"
        PassiveLocationContract.REASON_ENQUEUE_FAILED -> "被动定位入库失败"
    "sprint-enqueue-failed" -> "冲刺点入库失败"
        "sprint-enqueue-failed" -> "冲刺点入库失败"
        null, "" -> "暂无"
        else -> "其他原因"
    }

    fun policyMode(value: String?): String = when (value) {
        "Off" -> "已停止"
        "PowerSavingNormal" -> "常规省电"
        "ScheduleLowFrequency" -> "日程低频"
        "MotionObservation" -> "运动观察"
        "MovementRecovery" -> "移动恢复"
        "SyncFallback" -> "同步兜底"
        "HighSpeed" -> "高速轨迹"
        null, "" -> "暂无"
        else -> "未知状态"
    }

    fun scheduleFreshness(freshness: ScheduleCacheFreshness): String = when (freshness) {
        ScheduleCacheFreshness.Fresh -> "新鲜"
        ScheduleCacheFreshness.Stale -> "可能过期"
        ScheduleCacheFreshness.Missing -> "暂无"
    }

    fun scheduleReason(reason: String?): String = when {
        reason.isNullOrBlank() -> "暂无"
        reason in SAFE_SCHEDULE_REASONS -> reason
        reason.matches(SCHEDULE_DISTANCE_REASON) -> reason
        reason.startsWith(MOTION_REASON_PREFIX) &&
            reason.removePrefix(MOTION_REASON_PREFIX) in SAFE_MOTION_NAMES -> reason
        else -> "策略已更新"
    }

    fun heartbeat(value: String?): String = when (value) {
        "心跳上报成功" -> "正常"
        "心跳上报失败" -> "最近上报异常"
        null, "" -> "暂无"
        else -> "未知状态"
    }

    private const val MOTION_REASON_PREFIX = "检测到运动状态："
    private val SCHEDULE_DISTANCE_REASON = Regex("日程期间位置变化超过 \\d+(?:\\.\\d+)? 米")
    private val SAFE_MOTION_NAMES = setOf("未知", "静止", "步行", "跑步", "骑行", "车载")
    private val SAFE_SCHEDULE_REASONS = setOf(
        "默认省电档",
        "已暂停",
        "缺少精确或后台定位权限",
        "Google Play Services 不可用",
        "系统定位服务未开启",
        "连续采集未开启",
        "当前日程时段，降低定位频率"
    )
}
