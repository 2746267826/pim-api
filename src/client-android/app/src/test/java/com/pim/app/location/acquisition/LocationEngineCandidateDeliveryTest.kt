package com.pim.app.location.acquisition

import com.pim.app.location.LocationSnapshot
import kotlinx.coroutines.async
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Test

/**
 * WO-ANDROID-GATE-20260926 REQ-4 / REQ-15（AC-4.1 / AC-4.2 / AC-15.3）。
 *
 * **独立 review（Grok）的故障对照暴露出的真实缺口**：`LocationAcquisitionEngine.acquire`
 * 基线只在「精度比当前最好更好」时才调用 `onCandidate`。手动会话走的就是这个引擎，
 * 因此同一个 30 秒窗口里 12 / 18 / 25 米三条达标点会被吞掉两条 ——
 * 与 A5「期间达标的都留」、REQ-15「客户端只做精度过滤」直接冲突，
 * 而且是一条**静默**的点级取舍（AC-15.3 明令禁止）。
 *
 * 本用例永久守住这条：**每一条候选都必须被交付**。
 */
class LocationEngineCandidateDeliveryTest {

    /** AC-4.1 / AC-4.2：精度不是单调改善的达标点也必须全部交付。 */
    @Test
    fun `精度非单调改善的达标点也必须全部交付`() = runTest {
        val source = FakeLocationUpdateSource()
        val engine = LocationAcquisitionEngine(source)
        val request = LocationEngineRequest(
            sessionId = "s",
            priority = 100,
            timeoutMillis = 10_000L,
            startedAtWallClockMillis = 0L
        )
        val candidates = mutableListOf<LocationSnapshot>()
        val job = launch {
            engine.acquire(request, onCandidate = { candidates += it }, onAvailabilityChanged = { })
        }

        // 12 / 25 / 18 米：第二条比第一条差，第三条又比第二条好 —— 基线的
        // 「只有更好才交付」会只送出 12 米那一条。
        source.emit(candidate(accuracy = 12f, timeMillis = 100L))
        source.emit(candidate(accuracy = 25f, timeMillis = 200L))
        source.emit(candidate(accuracy = 18f, timeMillis = 300L))
        source.complete()
        job.join()

        assertEquals(
            "AC-4.2：不得只交付精度改善的那一条，窗口内每条候选都要交付",
            3,
            candidates.size
        )
        assertEquals(
            "交付顺序必须与回调顺序一致",
            listOf(100L, 200L, 300L),
            candidates.map { it.timeMillis }
        )
    }

    /** AC-15.3：重复精度/同坐标的候选同样逐个交付（客户端已无去重）。 */
    @Test
    fun `完全相同精度的候选也逐个交付`() = runTest {
        val source = FakeLocationUpdateSource()
        val engine = LocationAcquisitionEngine(source)
        val request = LocationEngineRequest(
            sessionId = "s",
            priority = 100,
            timeoutMillis = 10_000L,
            startedAtWallClockMillis = 0L
        )
        val candidates = mutableListOf<LocationSnapshot>()
        val job = launch {
            engine.acquire(request, onCandidate = { candidates += it }, onAvailabilityChanged = { })
        }

        repeat(5) { index -> source.emit(candidate(accuracy = 15f, timeMillis = 100L + index)) }
        source.complete()
        job.join()

        assertEquals("AC-15.3：不得静默吞掉同精度的候选", 5, candidates.size)
    }

    /** 引擎仍要回报「本轮最好一条」（预热与 low-quality 兜底依赖它）。 */
    @Test
    fun `bestLocation 仍是本轮精度最优的一条`() = runTest {
        val source = FakeLocationUpdateSource()
        val engine = LocationAcquisitionEngine(source)
        val request = LocationEngineRequest(
            sessionId = "s",
            priority = 100,
            timeoutMillis = 10_000L,
            startedAtWallClockMillis = 0L
        )
        val deferred = async {
            engine.acquire(request, onCandidate = { }, onAvailabilityChanged = { })
        }

        source.emit(candidate(accuracy = 12f, timeMillis = 100L))
        source.emit(candidate(accuracy = 25f, timeMillis = 200L))
        source.emit(candidate(accuracy = 8f, timeMillis = 300L))
        source.complete()
        val result = deferred.await()

        assertEquals(
            "bestLocation 必须仍是精度最优的一条（与「交付全部」解耦）",
            8f,
            result.bestLocation!!.horizontalAccuracyMeters!!,
            0.001f
        )
    }

    private fun candidate(accuracy: Float, timeMillis: Long) = LocationUpdateEvent.Candidate(
        LocationSnapshot(
            latitude = 31.23,
            longitude = 121.47,
            horizontalAccuracyMeters = accuracy,
            provider = "gps",
            source = "test",
            altitudeMeters = 10.0,
            speedMetersPerSecond = null,
            bearingDegrees = null,
            timeMillis = timeMillis
        )
    )

    /** 与既有 LocationAcquisitionEngineTest 同口径的假定位源。 */
    private class FakeLocationUpdateSource : LocationUpdateSource {
        private val events = kotlinx.coroutines.channels.Channel<LocationUpdateEvent>(
            kotlinx.coroutines.channels.Channel.UNLIMITED
        )

        override fun updates(
            request: LocationUpdateRequest
        ): kotlinx.coroutines.flow.Flow<LocationUpdateEvent> =
            kotlinx.coroutines.flow.flow {
                for (event in events) emit(event)
            }

        fun emit(event: LocationUpdateEvent) {
            events.trySend(event)
        }

        fun complete() {
            events.close()
        }
    }
}
