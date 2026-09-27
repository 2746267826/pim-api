package com.pim.app.location.acquisition

import com.google.android.gms.location.Priority
import com.pim.app.location.LocationSnapshot
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.RawLocationFix
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.testing.InMemorySharedPreferences
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.advanceTimeBy
import kotlinx.coroutines.test.advanceUntilIdle
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.double
import kotlinx.serialization.json.float
import kotlinx.serialization.json.jsonArray
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference
import kotlin.concurrent.thread

@OptIn(ExperimentalCoroutinesApi::class)
class LocationAcquisitionCoordinatorTest {

    private lateinit var runner: FakeLocationAcquisitionRunner
    private lateinit var prerequisiteChecker: FakePrerequisiteChecker
    private lateinit var operations: TestLocationAcquisitionOperations
    private lateinit var coordinator: LocationAcquisitionCoordinator
    private var uuidCounter = 0L
    private var wallClockTime = 1_000_000L
    private var elapsedTime = 0L

    private val aSnapshot = LocationSnapshot(
        latitude = 31.23, longitude = 121.47,
        horizontalAccuracyMeters = 5f,
        provider = "gps", source = "test",
        altitudeMeters = 10.0,
        speedMetersPerSecond = null, bearingDegrees = null,
        timeMillis = 100L
    )

    private val lowQualitySnapshot = LocationSnapshot(
        latitude = 31.24, longitude = 121.48,
        horizontalAccuracyMeters = 100f,
        provider = "gps", source = "test",
        altitudeMeters = null,
        speedMetersPerSecond = null, bearingDegrees = null,
        timeMillis = 200L
    )

    private val mediumQualitySnapshot = LocationSnapshot(
        latitude = 31.24, longitude = 121.48,
        horizontalAccuracyMeters = 40f,
        provider = "gps", source = "test",
        altitudeMeters = 10.0,
        speedMetersPerSecond = null, bearingDegrees = null,
        timeMillis = 200L
    )

    private val automaticContext = AcquisitionContext(
        policyMode = "PowerSavingNormal",
        scheduleLowFrequency = false,
        motionSignal = "Still",
        requestIntervalMillis = 60_000L
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

    private fun createCoordinator(
        scope: CoroutineScope,
        trackingSettingsStore: TrackingSettingsStore = TrackingSettingsStore(InMemorySharedPreferences())
    ) {
        coordinator = LocationAcquisitionCoordinator(
            runner = runner,
            prerequisiteChecker = prerequisiteChecker,
            operations = operations,
            json = Json,
            trackingSettingsStore = trackingSettingsStore
        )
        coordinator.testScope = scope
        coordinator.uuidGenerator = { "test-session-${++uuidCounter}" }
        coordinator.wallClockMillis = { wallClockTime }
        coordinator.elapsedRealtimeMillis = { elapsedTime }
    }

    // ─── 手动一次性采集：达标即入库 ────────────────────────────────

    @Test
    fun `manual success enqueues directly with source manual and schedules sync once`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val result = coordinator.startManualSession()
        assertTrue(result is SessionStartResult.Started)

        runner.waitForAcquire()
        assertEquals(AcquisitionPhase.Acquiring, coordinator.state.value.phase)

        runner.emitCandidate(aSnapshot)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = aSnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)
        assertEquals(aSnapshot, coordinator.state.value.bestLocation)
        assertEquals(1, operations.enqueueCount)
        assertEquals(1, operations.syncCount.get())
        assertEquals("manual", operations.lastSource)
        assertTrue(coordinator.state.value.lastQualityFlags.isEmpty())
    }

    @Test
    fun `manual success invokes onRecorded with the recorded snapshot`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()
        var recorded: LocationSnapshot? = null
        coordinator.onRecorded = { recorded = it }

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.emitCandidate(aSnapshot)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = aSnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(aSnapshot.copy(source = "acquisition"), recorded)
    }

    @Test
    fun `manual enqueue failure reaches Failed with errorReason and is not retried`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.emitCandidate(aSnapshot)
        operations.failNextEnqueue = true
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = aSnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(AcquisitionPhase.Failed, coordinator.state.value.phase)
        assertEquals(aSnapshot, coordinator.state.value.bestLocation)
        assertNotNull(coordinator.state.value.errorReason)
        assertEquals(0, operations.enqueueCount)
    }

    // ─── 低质量回退（仅手动） ─────────────────────────────────────

    @Test
    fun `manual session falls back to best fix with low-quality flag when gate rejects it`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.emitCandidate(mediumQualitySnapshot) // 40m ≥ 20m 门 → Drop
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = mediumQualitySnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)
        assertEquals(1, operations.enqueueCount)
        assertTrue(
            "rawJson must carry the low-quality flag",
            operations.enqueued.single().rawJson.contains("low-quality-accuracy")
        )
        assertEquals(
            setOf("low-quality-accuracy"),
            coordinator.state.value.lastQualityFlags
        )
        // 门拒绝的 fix 照常记录 drop 诊断
        assertEquals(1, operations.recordDroppedCount)
    }

    @Test
    fun `manual fallback point is visible through onRecorded`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()
        var recorded: LocationSnapshot? = null
        coordinator.onRecorded = { recorded = it }

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.emitCandidate(mediumQualitySnapshot)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = mediumQualitySnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(mediumQualitySnapshot.copy(source = "acquisition"), recorded)
    }

    @Test
    fun `manual session with no fix at all ends TimedOut without enqueue`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(AcquisitionPhase.TimedOut, coordinator.state.value.phase)
        assertEquals("获取位置超时，未获得任何定位结果", coordinator.state.value.errorReason)
        assertEquals(0, operations.enqueueCount)
    }

    @Test
    fun `no candidate with engine Failed completion reaches Failed with errorReason`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.Failed("engine exploded")
        ))
        runCurrent()

        assertEquals(AcquisitionPhase.Failed, coordinator.state.value.phase)
        assertEquals("engine exploded", coordinator.state.value.errorReason)
    }

    // ─── Precheck ────────────────────────────────────────────────

    @Test
    fun `precheck failure never invokes the Engine`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.block("缺少权限")
        var acquired = false
        runner.onAcquire = { acquired = true }

        val result = coordinator.startManualSession()

        assertTrue(result is SessionStartResult.Rejected)
        assertFalse(acquired)
        assertEquals(AcquisitionPhase.Idle, coordinator.state.value.phase)
        assertEquals("缺少权限", coordinator.state.value.errorReason)
    }

    @Test
    fun `precheck blocked reason is preserved and allows retry`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.block("缺少定位开关")
        coordinator.startManualSession()
        assertEquals("缺少定位开关", coordinator.state.value.errorReason)

        prerequisiteChecker.ready()
        val result = coordinator.startManualSession()
        assertTrue(result is SessionStartResult.Started)
        runner.waitForAcquire()
        assertNull(coordinator.state.value.errorReason)
        coordinator.cancelCurrentSession(coordinator.state.value.sessionId)
        runCurrent()
    }

    // ─── 手动重启语义 ─────────────────────────────────────────────

    @Test
    fun `manual restart replaces an in-flight one-shot session`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val first = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(0)

        val second = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(1)

        assertNotEquals(first.sessionId, second.sessionId)
        assertEquals(AcquisitionPhase.Acquiring, coordinator.state.value.phase)
        assertEquals(second.sessionId, coordinator.state.value.sessionId)
        coordinator.cancelCurrentSession(second.sessionId)
        runCurrent()
    }

    @Test
    fun `manual restart while cancelled terminal state starts fresh`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val first = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(0)
        assertTrue(coordinator.cancelCurrentSession(first.sessionId))
        runCurrent()
        assertEquals(AcquisitionPhase.Cancelled, coordinator.state.value.phase)

        val second = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(1)
        assertEquals(AcquisitionPhase.Acquiring, coordinator.state.value.phase)
        assertNotEquals(first.sessionId, second.sessionId)
        coordinator.cancelCurrentSession(second.sessionId)
        runCurrent()
    }

    // ─── 取消 ────────────────────────────────────────────────────

    @Test
    fun `matching session cancellation reaches Cancelled and retains sessionId`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val result = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire()

        assertTrue(coordinator.cancelCurrentSession(result.sessionId))
        runCurrent()

        assertEquals(AcquisitionPhase.Cancelled, coordinator.state.value.phase)
        assertEquals(result.sessionId, coordinator.state.value.sessionId)
    }

    @Test
    fun `stale session ID is ignored by cancelCurrentSession`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()

        assertFalse(coordinator.cancelCurrentSession("stale-session"))
        assertEquals(AcquisitionPhase.Acquiring, coordinator.state.value.phase)
        coordinator.cancelCurrentSession(coordinator.state.value.sessionId)
        runCurrent()
    }

    @Test
    fun `cancel after a terminal session is a no-op and preserves the result`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.emitCandidate(aSnapshot)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = aSnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()
        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)

        assertFalse(coordinator.cancelCurrentSession(coordinator.state.value.sessionId))
        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)
        assertEquals(1, operations.enqueueCount)
    }

    @Test
    fun `late engine callbacks carrying an old session ID cannot mutate current state`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val first = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(0)
        runner.emitCandidate(aSnapshot, index = 0)
        runner.completeCurrent(
            LocationEngineResult(
                sessionId = first.sessionId,
                bestLocation = aSnapshot,
                completion = LocationEngineCompletion.TimedOut
            ),
            index = 0
        )
        runCurrent()
        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)

        // 旧会话的迟到候选不得改写新状态
        runner.emitCandidate(lowQualitySnapshot, index = 0)
        runCurrent()
        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)
        assertEquals(aSnapshot, coordinator.state.value.bestLocation)
    }

    // ─── 准备阶段 ────────────────────────────────────────────────

    @Test
    fun `startManualSession exposes Preparing synchronously then Acquiring before runner`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        assertTrue(
            coordinator.state.value.phase == AcquisitionPhase.Preparing ||
                coordinator.state.value.phase == AcquisitionPhase.Acquiring
        )

        runner.waitForAcquire()
        assertEquals(AcquisitionPhase.Acquiring, coordinator.state.value.phase)
        coordinator.cancelCurrentSession(coordinator.state.value.sessionId)
        runCurrent()
    }

    // ─── 墙钟回拨回归（用户 bug 报告） ────────────────────────────

    @Test
    fun `wall clock rollback after candidate arrival does not hang the session`() = runTest {
        val sessionStartWall = 1_000_000L
        wallClockTime = sessionStartWall
        createCoordinator(this)
        var rolledBack = false
        coordinator.wallClockMillis = {
            if (rolledBack) sessionStartWall + testScheduler.currentTime - 600_000L
            else sessionStartWall + testScheduler.currentTime
        }
        prerequisiteChecker.ready()

        val missingAltitude = LocationSnapshot(
            latitude = 31.23,
            longitude = 121.47,
            horizontalAccuracyMeters = 5f,
            provider = "gps",
            source = "test",
            altitudeMeters = null,
            speedMetersPerSecond = null,
            bearingDegrees = null,
            timeMillis = sessionStartWall + 5_000L
        )

        val started = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire()
        advanceTimeByAndRun(5_000L)
        rolledBack = true
        runner.emitCandidate(missingAltitude)
        runner.complete(
            LocationEngineResult(
                sessionId = started.sessionId,
                bestLocation = missingAltitude,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        runCurrent()

        advanceTimeByAndRun(30_000L)
        assertTrue(
            "session must end within the 30s deadline; wall-clock rollback must not stretch the wait",
            coordinator.state.value.phase == AcquisitionPhase.Completed ||
                coordinator.state.value.phase == AcquisitionPhase.TimedOut ||
                coordinator.state.value.phase == AcquisitionPhase.Failed ||
                coordinator.state.value.phase == AcquisitionPhase.Cancelled
        )
        assertFalse(runner.isAcquireActive)
    }

    // ─── 海拔等待 ────────────────────────────────────────────────

    @Test
    fun `altitude wait accepts at overall session wall-clock deadline cap`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val missingAltitude = LocationSnapshot(
            latitude = 31.23,
            longitude = 121.47,
            horizontalAccuracyMeters = 5f,
            provider = "gps",
            source = "test",
            altitudeMeters = null,
            speedMetersPerSecond = null,
            bearingDegrees = null,
            timeMillis = 1_000_000L
        )

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.emitCandidate(missingAltitude)
        advanceTimeByAndRun(15_000L) // 超过 15s 海拔等待
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = missingAltitude,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)
        assertEquals(1, operations.enqueueCount)
        val raw = operations.enqueued.single().rawJson
        assertTrue(raw.contains("altitude-missing-timeout"))
    }

    // ─── 并发清理 ────────────────────────────────────────────────

    @Test
    fun `old session cleanup does not orphan the replacement session job`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val first = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(0)

        // 替换会话时，旧会话的 ticker/清理不能清掉新会话的 job 所有权
        val second = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(1)
        assertNotEquals(first.sessionId, second.sessionId)

        runCurrent()
        println("DIAG phase=" + coordinator.state.value.phase + " sessionId=" + coordinator.state.value.sessionId)
        assertEquals(AcquisitionPhase.Acquiring, coordinator.state.value.phase)
        assertEquals(second.sessionId, coordinator.state.value.sessionId)
        coordinator.cancelCurrentSession(second.sessionId)
        runCurrent()
        println("DIAG2 phase=" + coordinator.state.value.phase)
    }

    @Test
    fun `cancellation cleanup keeps a new session owner intact when a new session starts after the Cancelled claim wins`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val first = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(0)
        val cancelLatch = CountDownLatch(1)
        coordinator.afterSessionCancelledClaim = {
            // 只在本测试的第一次取消时触发，避免后续取消再次抢跑新会话
            coordinator.afterSessionCancelledClaim = null
            // Cancelled 已写入但 job 尚未取消：此时新会话抢跑
            val second = coordinator.startManualSession()
            assertTrue(second is SessionStartResult.Started)
            cancelLatch.countDown()
        }

        var cancelled = false
        val cancelThread = thread {
            cancelled = coordinator.cancelCurrentSession(first.sessionId)
        }
        assertTrue(cancelLatch.await(5, TimeUnit.SECONDS))
        cancelThread.join()

        assertTrue(cancelled)
        runCurrent()
        // 新会话必须存活且处于采集状态
        assertEquals(AcquisitionPhase.Acquiring, coordinator.state.value.phase)
        assertNotEquals(first.sessionId, coordinator.state.value.sessionId)
        coordinator.cancelCurrentSession(coordinator.state.value.sessionId)
        // 终态取消后 ticker 已死，可以安全推进虚拟时间排空取消级联
        advanceUntilIdle()
    }

    // ─── rawJson 编码 ────────────────────────────────────────────

    @Test
    fun `enqueueAccepted rawJson encodes control characters and parses back via kotlinx Json`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val tricky = LocationSnapshot(
            latitude = 31.23,
            longitude = 121.47,
            horizontalAccuracyMeters = 5f,
            provider = "gps\n\"quoted\"",
            source = "test",
            altitudeMeters = 10.0,
            speedMetersPerSecond = 1.5f,
            bearingDegrees = 45f,
            timeMillis = 100L
        )

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.emitCandidate(tricky)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = tricky,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(1, operations.enqueueCount)
        val raw = operations.enqueued.single().rawJson
        val parsed = Json.parseToJsonElement(raw).jsonObject

        assertEquals(31.23, parsed["latitude"]!!.jsonPrimitive.double, 0.0)
        assertEquals(121.47, parsed["longitude"]!!.jsonPrimitive.double, 0.0)
        assertEquals(5.0f, parsed["horizontalAccuracyMeters"]!!.jsonPrimitive.float, 0.0f)
        assertEquals("manual", parsed["source"]!!.jsonPrimitive.content)
        assertEquals("gps\n\"quoted\"", parsed["provider"]!!.jsonPrimitive.content)
        assertEquals(10.0, parsed["altitudeMeters"]!!.jsonPrimitive.double, 0.0)
        assertEquals(1.5f, parsed["speedMetersPerSecond"]!!.jsonPrimitive.float, 0.0f)
        assertEquals(45f, parsed["bearingDegrees"]!!.jsonPrimitive.float, 0.0f)
        assertTrue(parsed["qualityFlags"]!!.jsonArray.isEmpty())
    }

    // ─── 固定 20m 质量门 ─────────────────────────────────────────

    @Test
    fun `manual session applies the fixed 20m quality gate and records the drop`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startManualSession()
        runner.waitForAcquire()
        runner.emitCandidate(mediumQualitySnapshot) // 40m 超门
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = mediumQualitySnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        // 门拒绝 → drop 诊断 + 低质量回退入库
        assertEquals(1, operations.recordDroppedCount)
        assertEquals(1, operations.enqueueCount)
        assertTrue(coordinator.state.value.lastQualityFlags.contains("low-quality-accuracy"))
    }

    // ─── 自动常驻流 ──────────────────────────────────────────────

    @Test
    fun `startAutomaticStream runs a warm-up acquisition then registers the persistent stream`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        // 预热 = 一次性采集（HIGH_ACCURACY）
        runner.waitForAcquire()
        assertEquals(Priority.PRIORITY_HIGH_ACCURACY, runner.acquiredRequest!!.priority)
        runner.emitCandidate(aSnapshot)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = aSnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        // 预热入库 + 常驻流注册
        assertEquals(1, operations.enqueueCount)
        assertEquals("auto", operations.lastSource)
        runner.waitForStreamStart()
        assertEquals(60_000L, runner.streamRequest!!.intervalMillis)
        assertEquals(0L, runner.streamRequest!!.durationMillis)
        assertEquals(Priority.PRIORITY_HIGH_ACCURACY, runner.streamRequest!!.priority)
        assertTrue(coordinator.isAutomaticStreamActive())
        assertEquals(60_000L, coordinator.streamState.value.requestIntervalMillis)
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    @Test
    fun `warm-up without an acceptable fix still registers the stream without enqueue`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertEquals(0, operations.enqueueCount)
        runner.waitForStreamStart()
        assertTrue(coordinator.isAutomaticStreamActive())
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    @Test
    fun `stream fix below gate is enqueued with source auto and updates streamState`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()
        runner.waitForStreamStart()

        runner.emitStreamCandidate(aSnapshot)
        runCurrent()

        assertEquals(1, operations.enqueueCount)
        assertEquals("auto", operations.lastSource)
        assertEquals(1, operations.syncCount.get())
        assertEquals(aSnapshot, coordinator.streamState.value.latestFix)
        assertTrue(coordinator.streamState.value.latestQualityFlags.isEmpty())
        assertEquals(aSnapshot, runner.lastStreamCandidate)
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    @Test
    fun `stream fix above the gate is dropped with diagnostics and never enqueued`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()
        runner.waitForStreamStart()

        runner.emitStreamCandidate(mediumQualitySnapshot) // 40m ≥ 20m
        runCurrent()

        assertEquals(0, operations.enqueueCount)
        assertEquals(1, operations.recordDroppedCount)
        // 被门拒绝的点不更新 streamState（未记录任何 fix）
        assertNull(coordinator.streamState.value.latestFix)
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    @Test
    fun `stream fix without altitude is accepted with the altitude-missing flag`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()
        runner.waitForStreamStart()

        runner.emitStreamCandidate(lowQualitySnapshot.copy(horizontalAccuracyMeters = 5f))
        runCurrent()

        assertEquals(1, operations.enqueueCount)
        assertTrue(operations.enqueued.single().rawJson.contains("altitude-missing"))
        assertEquals(
            setOf("altitude-missing"),
            coordinator.streamState.value.latestQualityFlags
        )
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    @Test
    fun `updateAutomaticStream with a new interval re-registers the stream without warm-up`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire(0)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ), index = 0)
        runCurrent()
        runner.waitForStreamStart()

        val faster = automaticContext.copy(requestIntervalMillis = 30_000L)
        coordinator.updateAutomaticStream(faster)
        runCurrent()

        // 间隔变化 → 重注册；预热不再执行（acquire 计数不变）
        assertEquals(1, runner.acquireCount.get())
        runner.waitForStreamStart()
        assertEquals(30_000L, runner.streamRequest!!.intervalMillis)
        assertEquals(30_000L, coordinator.streamState.value.requestIntervalMillis)
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    @Test
    fun `updateAutomaticStream with the same interval does not re-register`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()
        runner.waitForStreamStart()
        val firstRequest = runner.streamRequest

        coordinator.updateAutomaticStream(automaticContext)
        runCurrent()

        assertEquals(firstRequest, runner.streamRequest)
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    @Test
    fun `stopAutomaticStream cancels the stream and resets streamState`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()
        runner.waitForStreamStart()

        coordinator.stopAutomaticStream()
        runCurrent()

        assertFalse(coordinator.isAutomaticStreamActive())
        assertEquals(AutomaticStreamState(), coordinator.streamState.value)
        runner.waitForStreamCancelled()
    }

    @Test
    fun `stream enqueue failure sets lastError and the stream keeps running`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()
        runner.waitForStreamStart()

        operations.failNextEnqueue = true
        runner.emitStreamCandidate(aSnapshot)
        runCurrent()

        assertNotNull(coordinator.streamState.value.lastError)
        assertTrue(coordinator.isAutomaticStreamActive())
        assertEquals(0, operations.enqueueCount)

        // 下一个 fix 重试成功
        runner.emitStreamCandidate(aSnapshot)
        runCurrent()
        assertEquals(1, operations.enqueueCount)
        assertNull(coordinator.streamState.value.lastError)
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    @Test
    fun `stream job ending in error resets active so the next registration recovers`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire(0)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ), index = 0)
        advanceUntilIdle()
        runner.waitForStreamStart()

        // 流注册后立即失败（如 GMS 抖动）：job 结束必须复位 active，
        // 否则 service 下轮看到 isAutomaticStreamActive()==true 永不重注册。
        runner.failStreamWith = IllegalStateException("simulated stream failure")
        runner.emitStreamCandidate(aSnapshot)
        runCurrent()

        assertFalse(coordinator.isAutomaticStreamActive())
        assertNotNull(coordinator.streamState.value.lastError)
        runner.waitForStreamCancelled()

        // 下一轮注册（service 循环路径）重新走预热+常驻流，自愈成功
        runner.failStreamWith = null
        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire(1)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ), index = 1)
        advanceUntilIdle()
        runner.waitForStreamStart()
        assertTrue(coordinator.isAutomaticStreamActive())
        assertNull(coordinator.streamState.value.lastError)
        coordinator.stopAutomaticStream()
        advanceUntilIdle()
    }

    @Test
    fun `updateAutomaticStream with the same interval but changed context re-registers`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire(0)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ), index = 0)
        advanceUntilIdle()
        runner.waitForStreamStart()
        assertEquals(1, runner.streamCount.get())

        // 间隔不变但运动标注变化（如 Walking→Moving）：必须重注册，
        // 否则流内 fix 的 rawJson 携带过期 motionSignal
        coordinator.updateAutomaticStream(automaticContext.copy(motionSignal = "Walking"))
        advanceUntilIdle()

        runner.waitForStreamStart()
        assertEquals(2, runner.streamCount.get())
        assertEquals(60_000L, runner.streamRequest!!.intervalMillis)
        assertEquals("Walking", coordinator.streamState.value.motionSignal)
        coordinator.stopAutomaticStream()
        advanceUntilIdle()
    }

    /**
     * WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 改写了本用例的语义。
     *
     * 基线：GMS 对全新请求立即回调最近缓存位置，与预热点 `recordedAt` 相同时被
     * **2 秒去重静默跳过**（既不入库也不留诊断）。该去重已按 REQ-15 整体取消
     * （客户端只做精度过滤），因此**每一条回调都必须逐条入库**。
     */
    @Test
    fun `stream first fix duplicating the warm-up point is now enqueued per REQ-15`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire(0)
        runner.emitCandidate(aSnapshot) // 预热接受并入库（timeMillis=100）
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = aSnapshot,
            completion = LocationEngineCompletion.TimedOut
        ), index = 0)
        runCurrent()
        assertEquals(1, operations.enqueueCount)

        runner.waitForStreamStart()
        // AC-15.1：与预热点同一时刻的缓存 fix 也不得被吞 —— 后台去重已取消
        runner.emitStreamCandidate(aSnapshot)
        runCurrent()
        assertEquals(
            "AC-15.1：客户端已无 2 秒去重，重复时刻的达标点必须逐条入库",
            2,
            operations.enqueueCount
        )
        assertEquals(0, operations.recordDroppedCount)

        runner.emitStreamCandidate(aSnapshot.copy(timeMillis = 100_000L))
        runCurrent()
        assertEquals(3, operations.enqueueCount)
        coordinator.stopAutomaticStream()
        advanceUntilIdle()
    }

    /**
     * WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 / AC-15.3 改写了本用例的语义。
     *
     * 基线：重注册后 GMS 立即回调 0.5 秒前的缓存 fix，被 2 秒去重**静默跳过**
     * （命中即 `return`，无任何记录）。去重取消后每一条回调都必须逐条入库。
     */
    @Test
    fun `re-registration immediate cached fix is now enqueued per REQ-15`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire(0)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ), index = 0)
        runCurrent()
        runner.waitForStreamStart()

        runner.emitStreamCandidate(aSnapshot.copy(timeMillis = 10_000L))
        runCurrent()
        assertEquals(1, operations.enqueueCount)

        // 间隔变化触发重注册：新流的立即回调是 0.5s 前的缓存 fix
        coordinator.updateAutomaticStream(automaticContext.copy(requestIntervalMillis = 30_000L))
        runCurrent()
        runner.waitForStreamStart()
        runner.emitStreamCandidate(aSnapshot.copy(timeMillis = 10_500L))
        runCurrent()
        assertEquals(
            "AC-15.3：重注册后的缓存 fix 也必须入库，不得静默丢弃",
            2,
            operations.enqueueCount
        )

        runner.emitStreamCandidate(aSnapshot.copy(timeMillis = 40_000L))
        runCurrent()
        assertEquals(3, operations.enqueueCount)
        coordinator.stopAutomaticStream()
        advanceUntilIdle()
    }

    @Test
    fun `manual one-shot can run while the automatic stream is active`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        coordinator.startAutomaticStream(automaticContext)
        runner.waitForAcquire(0)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = null,
            completion = LocationEngineCompletion.TimedOut
        ), index = 0)
        runCurrent()
        runner.waitForStreamStart()

        val manual = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire(1)
        runner.emitCandidate(aSnapshot, index = 1)
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = aSnapshot,
            completion = LocationEngineCompletion.TimedOut
        ), index = 1)
        runCurrent()

        assertEquals(AcquisitionPhase.Completed, coordinator.state.value.phase)
        assertEquals("manual", operations.lastSource)
        assertTrue(coordinator.isAutomaticStreamActive())
        // 预热无 fix 不入库，手动一次性入库一次
        assertEquals(1, operations.enqueueCount)
        coordinator.stopAutomaticStream()
        runCurrent()
    }

    // ─── 测试缝兼容（并发取消） ──────────────────────────────────

    @Test
    fun `cancel racing a manual acceptance keeps the in-flight enqueue from landing`() = runTest {
        createCoordinator(this)
        prerequisiteChecker.ready()

        val started = coordinator.startManualSession() as SessionStartResult.Started
        runner.waitForAcquire()
        val latch = CountDownLatch(1)
        coordinator.beforeCancellingSessionJob = { latch.countDown() }

        var cancelled = false
        val t = thread {
            cancelled = coordinator.cancelCurrentSession(started.sessionId)
        }
        assertTrue(latch.await(5, TimeUnit.SECONDS))
        runner.emitCandidate(aSnapshot)
        t.join()
        runner.complete(LocationEngineResult(
            sessionId = runner.acquiredRequest!!.sessionId,
            bestLocation = aSnapshot,
            completion = LocationEngineCompletion.TimedOut
        ))
        runCurrent()

        assertTrue(cancelled)
        // 取消后迟到候选不得入库
        assertEquals(0, operations.enqueueCount)
        assertEquals(AcquisitionPhase.Cancelled, coordinator.state.value.phase)
    }

    private fun TestScope.advanceTimeByAndRun(ms: Long) {
        testScheduler.advanceTimeBy(ms)
        runCurrent()
    }
}

// ─── Test fakes ─────────────────────────────────────────────────

class FakeLocationAcquisitionRunner : LocationAcquisitionRunner {

    private val acquireCounter = java.util.concurrent.atomic.AtomicInteger(0)
    val acquireCount: java.util.concurrent.atomic.AtomicInteger get() = acquireCounter
    private val streamCounter = java.util.concurrent.atomic.AtomicInteger(0)
    val streamCount: java.util.concurrent.atomic.AtomicInteger get() = streamCounter

    class AcquireSession(
        val request: LocationEngineRequest,
        val onCandidate: suspend (LocationSnapshot) -> Unit,
        val result: CompletableDeferred<LocationEngineResult> = CompletableDeferred(),
        var completionHold: CompletableDeferred<Unit>? = null
    )

    private val sessions = mutableListOf<AcquireSession>()
    private val streamCandidates = Channel<LocationSnapshot>(Channel.UNLIMITED)

    val acquiredRequest: LocationEngineRequest?
        get() = sessions.lastOrNull()?.request

    val streamRequest: LocationUpdateRequest? get() = streamRequestRef.get()
    val streamStarted: CompletableDeferred<Unit> = CompletableDeferred()
    val streamCancelled: CompletableDeferred<Unit> = CompletableDeferred()
    val lastStreamCandidate: LocationSnapshot? get() = lastStreamCandidateRef.get()

    private val streamRequestRef = AtomicReference<LocationUpdateRequest?>(null)
    private val lastStreamCandidateRef = AtomicReference<LocationSnapshot?>(null)

    var onAcquire: (() -> Unit)? = null

    override suspend fun acquire(
        request: LocationEngineRequest,
        onCandidate: suspend (LocationSnapshot) -> Unit,
        onAvailabilityChanged: suspend (Boolean) -> Unit
    ): LocationEngineResult {
        val session = AcquireSession(request, onCandidate)
        sessions += session
        waiters.toList().forEach { it.complete(sessions.lastIndex) }
        waiters.clear()
        acquireCounter.incrementAndGet()
        onAcquire?.invoke()
        try {
            val value = session.result.await()
            awaitCompletionHold(session)
            return value
        } catch (e: CancellationException) {
            awaitCompletionHold(session)
            if (!session.result.isCompleted) {
                session.result.cancel(e)
            }
            throw e
        }
    }

    private suspend fun awaitCompletionHold(session: AcquireSession) {
        val hold = session.completionHold ?: return
        withContext(NonCancellable) {
            try {
                withTimeout(5_000L) { hold.await() }
            } catch (error: TimeoutCancellationException) {
                throw AssertionError("completion hold was not released", error)
            }
        }
    }

    var failStreamWith: Exception? = null

    override suspend fun stream(
        request: LocationUpdateRequest,
        onCandidate: suspend (LocationSnapshot) -> Unit
    ) {
        streamRequestRef.set(request)
        streamCounter.incrementAndGet()
        streamStarted.complete(Unit)
        failStreamWith?.let { throw it }
        try {
            for (snapshot in streamCandidates) {
                failStreamWith?.let { throw it }
                lastStreamCandidateRef.set(snapshot)
                onCandidate(snapshot)
            }
        } finally {
            streamCancelled.complete(Unit)
        }
    }

    val isAcquireActive: Boolean get() = sessions.lastOrNull()?.let { !it.result.isCompleted } ?: false

    private val waiters = mutableListOf<CompletableDeferred<Int>>()

    suspend fun waitForAcquire(index: Int = 0): LocationEngineRequest {
        while (sessions.size <= index) {
            val waiter = CompletableDeferred<Int>()
            waiters += waiter
            if (sessions.size > index) {
                waiters.remove(waiter)
                break
            }
            waiter.await()
        }
        return sessions[index].request
    }

    suspend fun waitForStreamStart() {
        streamStarted.await()
    }

    suspend fun waitForStreamCancelled() {
        streamCancelled.await()
    }

    suspend fun emitCandidate(snapshot: LocationSnapshot, index: Int = sessions.lastIndex) {
        sessions[index].onCandidate(snapshot)
    }

    fun emitStreamCandidate(snapshot: LocationSnapshot) {
        streamCandidates.trySend(snapshot)
    }

    fun complete(result: LocationEngineResult, index: Int = sessions.lastIndex) {
        sessions[index].result.complete(result)
    }

    fun completeCurrent(result: LocationEngineResult, index: Int = sessions.lastIndex) = complete(result, index)

    fun holdCompletion(index: Int = sessions.lastIndex) {
        val session = sessions.getOrNull(index) ?: error("no acquire session at index $index")
        session.completionHold = CompletableDeferred()
    }
}

class FakePrerequisiteChecker : LocationPrerequisiteChecker {
    private var result: LocationPrerequisiteResult = LocationPrerequisiteResult.Ready

    fun ready() {
        result = LocationPrerequisiteResult.Ready
    }

    fun block(reason: String) {
        result = LocationPrerequisiteResult.Blocked(reason)
    }

    override fun check(triggerType: TriggerType): LocationPrerequisiteResult = result
}

class TestLocationAcquisitionOperations : LocationAcquisitionOperations {
    data class Enqueued(val accepted: QualityAcceptedLocation, val rawJson: String, val source: String)

    val enqueued = mutableListOf<Enqueued>()
    val enqueueCount: Int get() = enqueued.size
    val syncCount = java.util.concurrent.atomic.AtomicInteger(0)
    var recordDroppedCount = 0
    var failNextEnqueue = false
    val lastSource: String? get() = enqueued.lastOrNull()?.source

    override suspend fun enqueueAccepted(
        accepted: QualityAcceptedLocation,
        rawJson: String,
        source: String
    ) {
        if (failNextEnqueue) {
            failNextEnqueue = false
            throw IllegalStateException("simulated enqueue failure")
        }
        enqueued += Enqueued(accepted, rawJson, source)
    }

    override suspend fun recordDropped(fix: RawLocationFix, reason: String) {
        recordDroppedCount++
    }

    override fun scheduleSync() {
        syncCount.incrementAndGet()
    }
}
