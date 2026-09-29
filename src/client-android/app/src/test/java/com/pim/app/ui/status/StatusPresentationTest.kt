package com.pim.app.ui.status

import com.pim.app.status.StatusCenterState
import com.pim.app.status.PolicyTransitionSnapshot
import com.pim.app.status.StatusDisplayText
import com.pim.app.status.SyncPhase
import java.time.Instant
import java.time.ZoneId
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class StatusPresentationTest {
    @Test
    fun epochMillisAreFormattedAsAnAbsoluteTime() {
        val timestamp = Instant.parse("2026-07-14T10:10:00Z").toEpochMilli()

        assertEquals("07-14 10:10", formatEpochMillis(timestamp, ZoneId.of("UTC")))
    }

    @Test
    fun syncButtonEnablementCoversEveryPhaseAndLoading() {
        val enabled = setOf(
            SyncPhase.Idle,
            SyncPhase.Waiting,
            SyncPhase.Completed,
            SyncPhase.Failed,
            SyncPhase.Cancelled
        )

        SyncPhase.entries.forEach { phase ->
            val state = StatusCenterState.empty().copy(isLoading = false, syncPhase = phase)
            assertEquals("phase=$phase enabled=${phase in enabled}", phase in enabled, syncButtonEnabled(state))
        }

        assertFalse(syncButtonEnabled(StatusCenterState.empty().copy(isLoading = true, syncPhase = SyncPhase.Idle)))
    }

    @Test
    fun phaseAndButtonLabelsExplainQueuedWork() {
        assertEquals("当前空闲", syncPhaseLabel(SyncPhase.Idle))
        assertEquals("等待网络或系统调度", syncPhaseLabel(SyncPhase.Waiting))
        assertEquals("同步条件未满足", syncPhaseLabel(SyncPhase.Blocked))
        assertEquals("补传中", syncPhaseLabel(SyncPhase.CatchingUp))
        assertEquals("请求已接受", syncButtonLabel(SyncPhase.Accepted))
        assertEquals("暂不可同步", syncButtonLabel(SyncPhase.Blocked))
        assertEquals("再次同步", syncButtonLabel(SyncPhase.Completed))
        assertEquals("补传中", syncButtonLabel(SyncPhase.CatchingUp))
        assertTrue(syncButtonLabel(SyncPhase.Failed).contains("重新"))
        assertFalse(syncButtonEnabled(StatusCenterState.empty().copy(isLoading = false, syncPhase = SyncPhase.Blocked)))
    }

    @Test
    fun `scheduleFreshnessLabelMatchesDisplayText`() {
        assertEquals("新鲜", com.pim.app.status.StatusDisplayText.scheduleFreshness(com.pim.app.schedule.ScheduleCacheFreshness.Fresh))
        assertEquals("可能过期", com.pim.app.status.StatusDisplayText.scheduleFreshness(com.pim.app.schedule.ScheduleCacheFreshness.Stale))
        assertEquals("暂无", com.pim.app.status.StatusDisplayText.scheduleFreshness(com.pim.app.schedule.ScheduleCacheFreshness.Missing))
    }

    @Test
    fun `policyReasonLabelReturnsSafeText`() {
        assertEquals("当前日程时段，降低定位频率", StatusDisplayText.scheduleReason("当前日程时段，降低定位频率"))
        assertEquals("暂无", StatusDisplayText.scheduleReason(null))
        assertEquals("暂无", StatusDisplayText.scheduleReason(""))
        assertEquals("策略已更新", StatusDisplayText.scheduleReason("internal_code=123"))
        assertEquals("检测到运动状态：步行", StatusDisplayText.scheduleReason("检测到运动状态：步行"))
        assertEquals("策略已更新", StatusDisplayText.scheduleReason("检测到运动状态：flying"))
        assertEquals(
            "日程期间位置变化超过 100 米",
            StatusDisplayText.scheduleReason("日程期间位置变化超过 100 米")
        )
    }

    @Test
    fun `policyTransitionSummaryIncludesTimeModesAndSafeReason`() {
        val transition = PolicyTransitionSnapshot(
            fromMode = "PowerSavingNormal",
            toMode = "ScheduleLowFrequency",
            reason = "当前日程时段，降低定位频率",
            occurredAtMillis = Instant.parse("2026-07-14T10:10:00Z").toEpochMilli()
        )

        assertEquals(
            "07-14 10:10 · 常规省电 → 日程低频 · 当前日程时段，降低定位频率",
            formatPolicyTransition(transition, ZoneId.of("UTC"))
        )
    }

    @Test
    fun `policyTransitionReasonIsVerbatimEvenOutsideTheRuntimeWhitelist`() {
        // AC-4.5：原因必须与库中 reason 逐字一致。策略引擎会写出不在
        // StatusDisplayText.scheduleReason 白名单里的原因（运动名「移动中」、高速档三条），
        // 这些**不得**被改写成「策略已更新」。
        val verbatimReasons = listOf(
            "检测到运动状态：移动中",
            "高速轨迹模式：持续高速运动（≥8km/h）",
            "检测到高速运动，高速轨迹确认中",
            "高速轨迹模式",
            "策略已更新"
        )
        verbatimReasons.forEach { reason ->
            val transition = PolicyTransitionSnapshot(
                fromMode = "PowerSavingNormal",
                toMode = "MotionObservation",
                reason = reason,
                occurredAtMillis = Instant.parse("2026-07-14T10:10:00Z").toEpochMilli()
            )
            assertEquals(
                "reason=$reason",
                "07-14 10:10 · 常规省电 → 运动观察 · $reason",
                formatPolicyTransition(transition, ZoneId.of("UTC"))
            )
        }
        // 运行时那一行（「策略原因」）的白名单行为保持不变。
        assertEquals("策略已更新", StatusDisplayText.scheduleReason("高速轨迹模式"))
    }

    @Test
    fun `policyIntervalUsesReadableMinutesAndSeconds`() {
        assertEquals("5 分钟", formatPolicyInterval(300_000L))
        assertEquals("1分30秒", formatPolicyInterval(90_000L))
        assertEquals("30 秒", formatPolicyInterval(30_000L))
        assertEquals("未安排", formatPolicyInterval(0L))
        assertEquals("未安排", formatPolicyInterval(-1L))
    }

    // ── WO-ANDROID-POLICY-TRANSITION-20260928 REQ-4（D-2 / P5）──────────────

    @Test
    fun `已持续时长按工单分档渲染含全部边界`() {
        // 无记录
        assertEquals("未知", formatPolicyDuration(null))
        // < 1 分钟（含 59 秒）
        assertEquals("不足 1 分钟", formatPolicyDuration(0L))
        assertEquals("不足 1 分钟", formatPolicyDuration(59_000L))
        // < 1 小时（含 1 分钟 / 59 分钟）
        assertEquals("1 分钟", formatPolicyDuration(60_000L))
        assertEquals("59 分钟", formatPolicyDuration(59L * 60_000L))
        // < 24 小时（1 小时 / 23 小时 59 分）
        assertEquals("1 小时", formatPolicyDuration(60L * 60_000L))
        assertEquals("1 小时 30 分钟", formatPolicyDuration(90L * 60_000L))
        assertEquals("23 小时 59 分钟", formatPolicyDuration(24L * 60L * 60_000L - 60_000L))
        // ≥ 24 小时（整 24 小时省略「0 小时」；Y = 0 时省略）
        assertEquals("1 天", formatPolicyDuration(24L * 60L * 60_000L))
        assertEquals("1 天 5 小时", formatPolicyDuration(29L * 60L * 60_000L))
        assertEquals("30 天", formatPolicyDuration(30L * 24L * 60L * 60_000L))
    }

    @Test
    fun `当前状态值由档位模式名与已持续时长组成`() {
        assertEquals(
            "标准 · 运动观察 · 已持续 2 小时 5 分钟",
            formatCurrentPolicyState(
                profile = "standard",
                policyMode = "MotionObservation",
                durationMillis = (2L * 60L + 5L) * 60_000L
            )
        )
        // 空库：档位 / 模式仍来自运行时快照，时长显示「未知」（AC-4.3）。
        assertEquals(
            "标准 · 运动观察 · 已持续 未知",
            formatCurrentPolicyState(
                profile = "standard",
                policyMode = "MotionObservation",
                durationMillis = null
            )
        )
    }

    @Test
    fun `已持续时长与上次切换取自同一条记录的时刻`() {
        val occurredAt = Instant.parse("2026-09-28T02:00:00Z").toEpochMilli()
        val transition = PolicyTransitionSnapshot(
            fromMode = "PowerSavingNormal",
            toMode = "MotionObservation",
            reason = "检测到运动状态：步行",
            occurredAtMillis = occurredAt
        )
        val now = Instant.parse("2026-09-28T04:30:00Z").toEpochMilli()

        // 状态页这次渲染用的两个值来自同一快照：now − occurredAt。
        assertEquals("2 小时 30 分钟", formatPolicyDuration(now - occurredAt))
        assertEquals(
            "09-28 02:00 · 常规省电 → 运动观察 · 检测到运动状态：步行",
            formatPolicyTransition(transition, ZoneId.of("UTC"))
        )
    }

    @Test
    fun `写入失败文案在状态页与设置页是同一份`() {
        val at = Instant.parse("2026-09-28T04:30:00Z").toEpochMilli()
        assertEquals(
            "策略切换记录写入失败 2 次 · 最近 09-28 12:30",
            com.pim.app.status.PolicyTransitionDisplay.writeFailureText(
                consecutiveFailures = 2,
                lastFailureAtMillis = at,
                zoneId = ZoneId.of("Asia/Shanghai")
            )
        )
        assertEquals(
            "策略切换记录写入失败 2 次 · 最近 未知",
            com.pim.app.status.PolicyTransitionDisplay.writeFailureText(
                consecutiveFailures = 2,
                lastFailureAtMillis = null,
                zoneId = ZoneId.of("Asia/Shanghai")
            )
        )
    }

    @Test
    fun waitingSyncCanRequestOneTimeNetworkOverride() {
        val state = StatusCenterState.empty().copy(
            isLoading = false,
            syncPhase = SyncPhase.Waiting
        )

        assertTrue(syncButtonEnabled(state))
        assertEquals("立即同步", syncButtonLabel(SyncPhase.Waiting))
    }
}
