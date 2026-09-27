package com.pim.app.location.sprint

import com.pim.app.location.acquisition.AcquisitionContext
import com.pim.app.location.policy.LocationPolicyMode
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * WO-ANDROID-GATE-20260926 REQ-2 / D2：**每个采集周期执行一次冲刺**。
 *
 * 独立 review（round 2）发现的真实缺口：采集循环的唤醒周期恒为 **30 秒**
 * （`withTimeoutOrNull(30_000L)`，另加运动信号/fix 驱动的即时唤醒），
 * 而策略档的注册间隔是 45 / 120 / 600 秒。若每轮唤醒都发起冲刺，
 * 步行档会变成「每 30 秒冲一次」而不是「每 45 秒冲一次」，
 * 与工单 §1 的占空比推算（步行 ≈67%）以及 D2「每拍都冲」都不符。
 *
 * [SprintPeriodGate] 负责把「循环唤醒」收敛成「采集周期」：
 * 只有当上一拍的注册间隔真的过去（或此前从未冲过）才放行。
 *
 * **运动/车载档照冲、窗口与周期相接**（AC-2.5 / A8）在这里也必须成立：
 * 该档位间隔 = 30 秒，与循环唤醒同周期，因此每一轮都放行。
 */
class SprintPeriodGateTest {

    @Test
    fun `首次唤醒即放行`() {
        val gate = SprintPeriodGate()

        assertTrue(
            "REQ-2：采集开始后的第一个周期必须发起冲刺",
            gate.shouldStart(nowUtcMillis = 1_000_000L, requestIntervalMillis = 45_000L)
        )
    }

    @Test
    fun `注册间隔未到时不放行`() {
        val gate = SprintPeriodGate()
        gate.shouldStart(nowUtcMillis = 1_000_000L, requestIntervalMillis = 45_000L)
        gate.onWindowStarted(startedAtUtcMillis = 1_000_000L)

        // 循环每 30 秒醒一次：30 秒时还没到 45 秒的下一拍
        assertTrue(
            "D2：45 秒档在 30 秒唤醒点不得再发起一次冲刺",
            !gate.shouldStart(nowUtcMillis = 1_030_000L, requestIntervalMillis = 45_000L)
        )
    }

    @Test
    fun `注册间隔到达后放行`() {
        val gate = SprintPeriodGate()
        gate.shouldStart(nowUtcMillis = 1_000_000L, requestIntervalMillis = 45_000L)
        gate.onWindowStarted(startedAtUtcMillis = 1_000_000L)

        assertTrue(
            "D2：45 秒档在第 45 秒必须发起下一拍",
            gate.shouldStart(nowUtcMillis = 1_045_000L, requestIntervalMillis = 45_000L)
        )
    }

    @Test
    fun `各档位按各自周期放行`() {
        listOf(30_000L, 45_000L, 120_000L, 600_000L).forEach { interval ->
            val gate = SprintPeriodGate()
            gate.shouldStart(nowUtcMillis = 0L, requestIntervalMillis = interval)
            gate.onWindowStarted(startedAtUtcMillis = 0L)

            assertTrue(
                "间隔 $interval：未到点不得放行",
                !gate.shouldStart(nowUtcMillis = interval - 1, requestIntervalMillis = interval)
            )
            assertTrue(
                "间隔 $interval：到点必须放行",
                gate.shouldStart(nowUtcMillis = interval, requestIntervalMillis = interval)
            )
        }
    }

    @Test
    fun `运动车载档每轮都放行`() {
        val gate = SprintPeriodGate()
        gate.shouldStart(nowUtcMillis = 0L, requestIntervalMillis = 30_000L)
        gate.onWindowStarted(startedAtUtcMillis = 0L)

        // AC-2.5：30 秒档下窗口与周期相接，每一轮唤醒都要冲
        assertTrue(
            "AC-2.5：运动/车载档（30 秒硬下限）每一轮都必须照常发起",
            gate.shouldStart(nowUtcMillis = 30_000L, requestIntervalMillis = 30_000L)
        )
    }

    @Test
    fun `间隔变化后按新间隔计算`() {
        val gate = SprintPeriodGate()
        gate.shouldStart(nowUtcMillis = 0L, requestIntervalMillis = 600_000L)
        gate.onWindowStarted(startedAtUtcMillis = 0L)

        // 用户把档位改快（600s → 30s）：不得继续等 600 秒才冲
        assertTrue(
            "间隔改快后必须按新间隔放行（否则用户会以为冲刺坏了）",
            gate.shouldStart(nowUtcMillis = 30_000L, requestIntervalMillis = 30_000L)
        )
    }

    @Test
    fun `停止后重新开始立即放行`() {
        val gate = SprintPeriodGate()
        gate.shouldStart(nowUtcMillis = 0L, requestIntervalMillis = 600_000L)
        gate.onWindowStarted(startedAtUtcMillis = 0L)

        gate.reset()

        assertTrue(
            "采集重启后必须立即恢复冲刺（不得沿用旧锚点）",
            gate.shouldStart(nowUtcMillis = 1_000L, requestIntervalMillis = 600_000L)
        )
    }

    @Test
    fun `放行时刻成为下一个周期的锚点`() {
        val gate = SprintPeriodGate()
        gate.shouldStart(nowUtcMillis = 0L, requestIntervalMillis = 45_000L)
        gate.onWindowStarted(startedAtUtcMillis = 0L)

        // 第 45 秒放行（不是第 30 秒）→ 锚点必须落在 45 秒
        gate.shouldStart(nowUtcMillis = 45_000L, requestIntervalMillis = 45_000L)
        gate.onWindowStarted(startedAtUtcMillis = 45_000L)

        assertTrue(!gate.shouldStart(nowUtcMillis = 75_000L, requestIntervalMillis = 45_000L))
        assertTrue(gate.shouldStart(nowUtcMillis = 90_000L, requestIntervalMillis = 45_000L))
    }

    /**
     * I-2（独立 review round 3）：45 秒档在 30 秒唤醒网格上不对齐时，
     * 实际节奏会退化成 60 秒一拍（占空比 67% → 约 50%）。循环需要知道
     * 「把等待收敛到边界」，且**不得超过** 30 秒兜底上限。
     */
    @Test
    fun `给出建议等待以对齐周期边界且不超过上限`() {
        val gate = SprintPeriodGate()
        gate.shouldStart(nowUtcMillis = 0L, requestIntervalMillis = 45_000L)
        gate.onWindowStarted(startedAtUtcMillis = 0L)

        assertEquals(
            "30 秒唤醒点距 45 秒边界还有 15 秒 → 等 15 秒（而不是 30 秒）",
            15_000L,
            gate.suggestedWaitMillis(nowUtcMillis = 30_000L, requestIntervalMillis = 45_000L, capMillis = 30_000L)
        )
        // 600 秒档：边界远在上限之外，因此只等到上限（不加长 30 秒兜底）。
        // 先让门控见过这个间隔（生产顺序：shouldStart → 循环等待）。
        val longGate = SprintPeriodGate()
        longGate.shouldStart(nowUtcMillis = 0L, requestIntervalMillis = 600_000L)
        longGate.onWindowStarted(startedAtUtcMillis = 0L)
        assertEquals(
            "距边界超过上限时只等到上限（不加长 30 秒兜底）",
            30_000L,
            longGate.suggestedWaitMillis(
                nowUtcMillis = 0L,
                requestIntervalMillis = 600_000L,
                capMillis = 30_000L
            )
        )
    }

    @Test
    fun `档位间隔不超过唤醒上限时无需对齐`() {
        val gate = SprintPeriodGate()
        gate.shouldStart(nowUtcMillis = 0L, requestIntervalMillis = 30_000L)
        gate.onWindowStarted(startedAtUtcMillis = 0L)

        assertEquals(
            "AC-2.5：运动/车载档（30 秒）与唤醒同周期，不加额外对齐等待",
            0L,
            gate.suggestedWaitMillis(nowUtcMillis = 0L, requestIntervalMillis = 30_000L, capMillis = 30_000L)
        )
    }

    /** AC-2.1 的目标节奏：周期档位不受冲刺影响（防「临时改间隔」回归）。 */
    @Test
    fun `门控不改变传入的注册间隔`() {
        val gate = SprintPeriodGate()
        val intervals = mutableListOf<Long>()
        listOf(45_000L, 45_000L, 120_000L).forEach { interval ->
            if (gate.shouldStart(nowUtcMillis = gate.lastDecisionAtUtcMillisForTest() + interval, requestIntervalMillis = interval)) {
                gate.onWindowStarted(startedAtUtcMillis = gate.lastDecisionAtUtcMillisForTest() + interval)
            }
            intervals += interval
        }
        assertEquals(
            "AC-5.6：门控只做放行判断，不得改写档位间隔",
            listOf(45_000L, 45_000L, 120_000L),
            intervals
        )
    }
}
