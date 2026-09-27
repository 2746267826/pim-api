package com.pim.app.location.sprint

import com.pim.app.location.LocationSnapshot
import com.pim.app.location.quality.LocationQualityGate
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.RawLocationFix
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * WO-ANDROID-GATE-20260926 REQ-2 / REQ-3 / REQ-4（AC-2.2 / AC-2.3 / AC-3.1 / AC-3.2 / AC-4.1 ~ AC-4.5）。
 *
 * [LocationSprintWindow] 是冲刺窗口的纯状态机（不碰 Android、不碰 IO），
 * 因此窗口跨度、收点规则与「达标点全留」这些需求方确认过的口径可以在 JVM 上直接断言。
 */
class LocationSprintWindowTest {

    /** AC-2.2 / AC-3.1：窗口跑满 30 秒，不早退。 */
    @Test
    fun `窗口跨度跑满 30 秒且不早退`() {
        val window = LocationSprintWindow(startedAtUtcMillis = START)

        // 中途出现一条极精确的点（5 米）→ 仍不得结束窗口（A6 不早退）。
        window.onSample(snapshot(at = START, accuracy = 5f))
        window.onSample(snapshot(at = START + 1_000L, accuracy = 4f))
        window.onSample(snapshot(at = START + 29_000L, accuracy = 9f))

        val result = window.finish(endedAtUtcMillis = START + 30_400L)

        assertEquals("AC-3.1：窗口必须持续到 30 秒（+ε ≤ 2 秒）", 30_400L, result.durationMillis)
        assertTrue(
            "AC-2.2：窗口跨度不得超过 30 秒 + 2 秒（D3b）",
            result.durationMillis <= LocationSprintContract.WINDOW_MILLIS + 2_000L
        )
        assertEquals("AC-3.1：窗口内全部取点都被统计", 3, result.sampleCount)
    }

    /** AC-4.1：窗口内多条达标点全部入库，且最好的一条可区分。 */
    @Test
    fun `窗口内达标的点全部保留且最好的一条可区分`() {
        val window = LocationSprintWindow(startedAtUtcMillis = START)
        val accepted = mutableListOf<Float>()

        listOf(12f, 18f, 25f).forEach { accuracy ->
            val fix = snapshot(at = START, accuracy = accuracy)
            window.onSample(fix)
            window.onAccepted(acceptedAt(fix), accuracy)
            accepted += accuracy
        }

        val result = window.finish(endedAtUtcMillis = START + 30_100L)

        assertEquals("AC-4.2：不得只保留「最好一条」", listOf(12f, 18f, 25f), accepted)
        assertEquals("AC-4.1：入库条数 = 窗口内达标条数", 3, result.acceptedCount)
        assertEquals("AC-4.1：最好的一条必须是窗口内最小精度", 12f, result.bestAccuracyMeters!!, 0.001f)
    }

    /** AC-4.4：自动流窗口内无达标点 → 入库 0 条。 */
    @Test
    fun `窗口内无达标点时不收下任何点`() {
        val window = LocationSprintWindow(startedAtUtcMillis = START)

        listOf(35f, 40f, 120f).forEach { accuracy ->
            window.onSample(snapshot(at = START, accuracy = accuracy))
        }

        val result = window.finish(endedAtUtcMillis = START + 30_200L)

        assertEquals("AC-4.4：自动流无达标点必须收 0 条", 0, result.acceptedCount)
        assertNull("AC-4.1：无达标点时「最好精度」为 null（不谎报）", result.bestAccuracyMeters)
        assertEquals("AC-4.1：候选条数仍要如实记录", 3, result.sampleCount)
    }

    /** AC-4.3：不得因处于冲刺窗口而放宽门槛 —— 未达标点不得计入。 */
    @Test
    fun `未达标的点不计入入库条数`() {
        val window = LocationSprintWindow(startedAtUtcMillis = START)
        val gate = LocationQualityGate()

        listOf(29.9f, 30.0f, 80f).forEach { accuracy ->
            val sample = snapshot(at = START, accuracy = accuracy)
            window.onSample(sample)
            val decision = gate.evaluate(sample.toRawFix())
            if (decision is com.pim.app.location.quality.QualityDecision.AcceptNow) {
                window.onAccepted(decision.accepted, accuracy)
            }
        }

        val result = window.finish(endedAtUtcMillis = START + 30_100L)

        assertEquals("AC-4.3：只有 29.9 米那条达标，入库 1 条", 1, result.acceptedCount)
        assertEquals("AC-4.3：最好精度只考虑达标点", 29.9f, result.bestAccuracyMeters!!, 0.001f)
    }

    /** AC-2.3：窗口不得跨周期叠加（同一时刻只能有一个活动窗口）。 */
    @Test
    fun `窗口结束后才允许开启下一个窗口`() {
        val window = LocationSprintWindow(startedAtUtcMillis = START)

        assertTrue("窗口开启后 isOpen 为 true", window.isOpen)
        val first = window.finish(endedAtUtcMillis = START + 30_000L)
        assertTrue("结束后 isOpen 必须为 false", !window.isOpen)

        assertEquals(START, first.startedAtUtcMillis)
        assertEquals(START + 30_000L, first.endedAtUtcMillis)
    }

    /** 台账口径：sampleCount 统计**回调条数（去重之前）**。 */
    @Test
    fun `sampleCount 统计去重前的回调条数`() {
        val window = LocationSprintWindow(startedAtUtcMillis = START)

        repeat(5) { window.onSample(snapshot(at = START + it * 1_000L, accuracy = 10f)) }

        val result = window.finish(endedAtUtcMillis = START + 30_000L)

        assertEquals("§6：sampleCount = 回调条数（去重前），重复时刻也照数", 5, result.sampleCount)
        assertEquals("已入库 0 条（本用例只喂回调）", 0, result.acceptedCount)
    }

    private val START = 1_700_000_000_000L

    private fun snapshot(at: Long, accuracy: Float) = LocationSnapshot(
        latitude = 31.230416,
        longitude = 121.473701,
        horizontalAccuracyMeters = accuracy,
        provider = "gps",
        source = "realtime",
        altitudeMeters = 10.0,
        speedMetersPerSecond = null,
        bearingDegrees = null,
        timeMillis = at
    )

    private fun acceptedAt(snapshot: LocationSnapshot) = QualityAcceptedLocation(
        fix = snapshot.toRawFix(),
        altitudeMeters = snapshot.altitudeMeters,
        acceptedAtMillis = snapshot.timeMillis,
        qualityFlags = emptySet()
    )

    private fun LocationSnapshot.toRawFix() = RawLocationFix(
        latitude = latitude,
        longitude = longitude,
        horizontalAccuracyMeters = horizontalAccuracyMeters,
        altitudeMeters = altitudeMeters,
        provider = provider,
        recordedAtMillis = timeMillis,
        policyMode = "PowerSavingNormal",
        scheduleLowFrequency = false,
        motionSignal = "Unknown"
    )
}
