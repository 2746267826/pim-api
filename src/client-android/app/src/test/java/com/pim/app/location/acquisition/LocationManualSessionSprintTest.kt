package com.pim.app.location.acquisition

import com.google.android.gms.location.Priority
import com.pim.app.location.LocationSnapshot
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.RawLocationFix
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.testing.InMemorySharedPreferences
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test

/**
 * WO-ANDROID-GATE-20260926 REQ-3 / REQ-4 / REQ-6 / AC-3.1 / AC-3.2 / AC-4.1 / AC-4.2 /
 * AC-4.4 / AC-6.4 / AC-6.5 / D6。
 *
 * **D6 钉死的一条（README「最容易做错的四处」第 1 条）**：
 * 手动定位也跑满 30 秒 —— 既有「首个达标点即结束」（`onQualityAccepted → cancel`）
 * 已被本工单取消。中途拿到好点也必须继续取点，并把期间**所有达标点**都收下。
 *
 * 手动无达标点时**仍走既有 `low-quality-accuracy` 兜底**（这是 AC-4.4 的显式例外，
 * 需求方 2026-09-26 明确：手动不论开关都跑满 30 秒，开关只控制是否冲刺）。
 */
class LocationManualSessionSprintTest {

    private lateinit var runner: FakeLocationAcquisitionRunner
    private lateinit var prerequisiteChecker: FakePrerequisiteChecker
    private lateinit var operations: TestLocationAcquisitionOperations
    private lateinit var coordinator: LocationAcquisitionCoordinator
    private var uuidCounter = 0L
    private var wallClockTime = 1_000_000L
    private var elapsedTime = 0L

    private fun snapshot(timeMillis: Long, accuracy: Float, altitude: Double? = 10.0) =
        LocationSnapshot(
            latitude = 31.23, longitude = 121.47,
            horizontalAccuracyMeters = accuracy,
            provider = "gps", source = "test",
            altitudeMeters = altitude,
            speedMetersPerSecond = null, bearingDegrees = null,
            timeMillis = timeMillis
        )

    @Before
    fun setUp() {
        runner = FakeLocationAcquisitionRunner()
        prerequisiteChecker = FakePrerequisiteChecker()
        operations = TestLocationAcquisitionOperations()
        uuidCounter = 0L
        wallClockTime = 1_000_000L
        elapsedTime = 0L
    }

    private fun createCoordinator(scope: CoroutineScope) {
        coordinator = LocationAcquisitionCoordinator(
            runner = runner,
            prerequisiteChecker = prerequisiteChecker,
            operations = operations,
            json = Json,
            trackingSettingsStore = TrackingSettingsStore(InMemorySharedPreferences())
        )
        coordinator.testScope = scope
        coordinator.uuidGenerator = { "test-session-${++uuidCounter}" }
        coordinator.wallClockMillis = { wallClockTime }
        coordinator.elapsedRealtimeMillis = { elapsedTime }
    }

    /**
     * AC-6.4（本工单明确取消的行为）：首个达标点到达后**不得**结束手动会话。
     */
    @Test
    fun `手动会话在早期达标点之后仍然保持采集`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()

        // 第一条就达标（既有无条件结束的触发点）
        runner.emitCandidate(snapshot(timeMillis = 1_000L, accuracy = 5f))
        runCurrent()

        assertTrue(
            "AC-6.4：手动既有「首个达标点即结束」已被取消，会话必须仍在采集",
            coordinator.state.value.isBusy
        )
        assertTrue(
            "AC-6.4：引擎不得被取消（后续还要继续取点）",
            runner.isAcquireActive
        )

        // 让会话按 30 秒截止正常收尾（否则测试协程不会结束）。
        runner.complete(
            LocationEngineResult(
                sessionId = runner.acquiredRequest!!.sessionId,
                bestLocation = snapshot(timeMillis = 1_000L, accuracy = 5f),
                completion = LocationEngineCompletion.TimedOut
            )
        )
        runCurrent()
        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)
    }

    /** AC-4.1 / AC-4.2：窗口期间达标的点全部入库，不只留第一条。 */
    @Test
    fun `手动会话期间所有达标点全部入库`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()

        runner.emitCandidate(snapshot(timeMillis = 1_000L, accuracy = 12f))
        runCurrent()
        runner.emitCandidate(snapshot(timeMillis = 2_000L, accuracy = 25f))
        runCurrent()
        runner.emitCandidate(snapshot(timeMillis = 3_000L, accuracy = 18f))
        runCurrent()

        assertEquals(
            "AC-4.2：不得只保留达标的第一条，达标的点必须全部入库",
            3,
            operations.enqueueCount
        )
        // 结束会话
        runner.complete(
            LocationEngineResult(
                sessionId = runner.acquiredRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        runCurrent()
        assertEquals(3, operations.enqueueCount)
    }

    /** AC-4.3 / AC-4.4：中途未达标的点丢弃；无达标点时仍走 low-quality 兜底 1 条。 */
    @Test
    fun `手动无达标点时仍走既有兜底入库一条`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()

        runner.emitCandidate(snapshot(timeMillis = 1_000L, accuracy = 80f, altitude = null))
        runCurrent()
        assertEquals("AC-4.3：未达标点不得被收下", 0, operations.enqueueCount)

        runner.complete(
            LocationEngineResult(
                sessionId = runner.acquiredRequest!!.sessionId,
                bestLocation = snapshot(timeMillis = 1_000L, accuracy = 80f, altitude = null),
                completion = LocationEngineCompletion.TimedOut
            )
        )
        runCurrent()

        assertEquals(
            "AC-4.4 显式例外：手动无达标点时保持既有 low-quality 兜底入库 1 条",
            1,
            operations.enqueueCount
        )
        assertTrue(
            "兜底点必须带 low-quality-accuracy 标记",
            operations.enqueued.single().rawJson.contains("low-quality-accuracy")
        )
    }

    /** AC-6.5：手动总时长上限 = 30 秒窗口 + 首点等待（会话截止仍是 30 秒）。 */
    @Test
    fun `手动会话截止时刻为三十秒`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()

        val deadline = coordinator.state.value.deadlineAtElapsedRealtimeMs!!
        val started = coordinator.state.value.startedAtElapsedRealtimeMs!!
        assertEquals(
            "AC-6.5：手动会话截止 = 起始 + 30 秒（窗口上限）",
            30_000L,
            deadline - started
        )
        runner.complete(
            LocationEngineResult(
                sessionId = runner.acquiredRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        runCurrent()
    }

    /**
     * I-D（独立 review round 3）：low-quality 兜底与「最佳位置」必须取**精度最优**的
     * 一条，而不是最后一条。基线在交付全部候选后，逐条覆盖会让最好一条被最后一条冲掉。
     */
    @Test
    fun `最佳位置与兜底取精度最优的一条而非最后一条`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()

        // 两条都不达标：40 米（较好）在前，80 米（更差）在后
        runner.emitCandidate(snapshot(timeMillis = 1_000L, accuracy = 40f, altitude = null))
        runCurrent()
        runner.emitCandidate(snapshot(timeMillis = 2_000L, accuracy = 80f, altitude = null))
        runCurrent()

        assertEquals(
            "会话展示的「最佳位置」必须是 40 米那条，而不是最后一条 80 米",
            40f,
            coordinator.state.value.bestLocation!!.horizontalAccuracyMeters!!
        )

        runner.complete(
            LocationEngineResult(
                sessionId = runner.acquiredRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        runCurrent()

        assertEquals("兜底入库 1 条", 1, operations.enqueueCount)
        assertEquals(
            "AC-4.4 兜底必须取精度最优的一条",
            40f,
            operations.enqueued.single().accepted.fix.horizontalAccuracyMeters!!
        )
    }

    /** AC-15.3：手动会话里被丢弃的点也要留诊断记录（不得静默）。 */
    @Test
    fun `手动会话丢弃的点留诊断记录`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()

        runner.emitCandidate(snapshot(timeMillis = 1_000L, accuracy = 45f))
        runCurrent()
        runner.emitCandidate(snapshot(timeMillis = 2_000L, accuracy = 60f))
        runCurrent()

        assertEquals(
            "AC-15.3：手动会话里未达标的点必须逐条留丢弃记录",
            2,
            operations.recordDroppedCount
        )
        runner.complete(
            LocationEngineResult(
                sessionId = runner.acquiredRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        runCurrent()
    }
}
