package com.pim.app.status

import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-3：写入失败提示的**同一份文案**。
 *
 * 主位是状态页既有告警区（「需要处理」）的一条 `Warning`，辅位是设置页「策略切换历史」
 * 板块顶部；两处必须逐字一致（AC-3.1 ④⑤、§四 文案规格）。
 */
object PolicyTransitionDisplay {
    private val TIME_FORMATTER: DateTimeFormatter = DateTimeFormatter.ofPattern("MM-dd HH:mm")

    /** 逐字：`策略切换记录写入失败 N 次 · 最近 MM-dd HH:mm`。 */
    fun writeFailureText(
        consecutiveFailures: Int,
        lastFailureAtMillis: Long?,
        zoneId: ZoneId = ZoneId.systemDefault()
    ): String {
        val count = consecutiveFailures.coerceAtLeast(0)
        val time = lastFailureAtMillis
            ?.takeIf { it > 0L }
            ?.let { millis ->
                runCatching { TIME_FORMATTER.format(Instant.ofEpochMilli(millis).atZone(zoneId)) }
                    .getOrDefault(UNKNOWN_TIME)
            }
            ?: UNKNOWN_TIME
        return "策略切换记录写入失败 $count 次 · 最近 $time"
    }

    private const val UNKNOWN_TIME = "未知"
}
