package com.pim.app.location.sprint

import com.pim.app.location.acquisition.AcquisitionContext
import com.pim.app.location.acquisition.LocationAcquisitionOperations
import com.pim.app.location.policy.LocationPolicyMode
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.RawLocationFix
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.testing.InMemorySharedPreferences
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * WO-ANDROID-GATE-20260926 REQ-2 / D2 —— **运行时确实用了周期门控**。
 *
 * 独立 review round 3 指出：`SprintPeriodGateTest` 只测了门控类本身，
 * 把 `LocationSprintRuntime.onPeriod` 里的**调用**删掉仍然全绿。
 * 本用例补上「接线」这一层：同一档位在一周期内只允许发起一次。
 */
class LocationSprintRuntimeCadenceTest {

    private val runner = RecordingSprintSource()
    private val ledger = RecordingSprintLedger()
    private var now = 1_000_000L



    /** 立即执行的测试 scope：`launch` 体同步跑起来，注册计数当场可见。 */
    private fun eagerScope() = kotlinx.coroutines.CoroutineScope(
        kotlinx.coroutines.Dispatchers.Unconfined
    )

    private fun runtime(): LocationSprintRuntime {
        val controller = LocationSprintController(
            runner = runner,
            ledger = ledger,
            trackingSettingsStore = TrackingSettingsStore(InMemorySharedPreferences())
        )
        controller.wallClockMillis = { now }
        controller.elapsedRealtimeMillis = { now }
        // 窗口立即结束，避免测试依赖协程调度。
        // 窗口挂起直到测试放行（等价于 30 秒窗口进行中），
        // 否则窗口会瞬间结束，注册计数在断言前就归位了。
        controller.testScope = eagerScope()
        controller.delayMillis = { holdWindow() }
        return LocationSprintRuntime(
            controller = controller,
            operations = NoopOperations(),
            nowUtcMillis = { now }
        )
    }

    /** 让窗口保持打开（等价于「30 秒窗口进行中」），直到测试放行。 */
    private val releaseSignal = kotlinx.coroutines.channels.Channel<Unit>(
        kotlinx.coroutines.channels.Channel.UNLIMITED
    )

    private suspend fun holdWindow() {
        releaseSignal.receive()
    }

    /** 放行一个正在进行的窗口，使其正常结束（计入已执行台账）。 */
    private fun releaseWindow() {
        releaseSignal.trySend(Unit)
    }

    /** D2：同一采集周期内重复唤醒不得重复发起冲刺。 */
    @Test
    fun `同一周期内重复唤醒只发起一次冲刺`() = kotlinx.coroutines.test.runTest {
        val runtime = runtime()
        val context = context(requestIntervalMillis = 120_000L)

        val first = runtime.onPeriod(context, LocationPolicyMode.PowerSavingNormal)
        val second = runtime.onPeriod(context, LocationPolicyMode.PowerSavingNormal)
        val third = runtime.onPeriod(context, LocationPolicyMode.PowerSavingNormal)

        assertTrue("第一拍必须发起冲刺", first is SprintStartDecision.Started)
        assertEquals(
            "D2：同一周期内后续唤醒不得重复发起",
            SprintStartDecision.Skipped(SprintSkipReasons.NOT_THIS_PERIOD),
            second
        )
        assertEquals(SprintStartDecision.Skipped(SprintSkipReasons.NOT_THIS_PERIOD), third)
        assertEquals("只允许一个冲刺注册", 1, runner.streamCount)
    }

    /** D2：下一个周期到点后必须恢复冲刺。 */
    @Test
    fun `下一周期到点后恢复冲刺`() = kotlinx.coroutines.test.runTest {
        val runtime = runtime()
        val context = context(requestIntervalMillis = 45_000L)

        runtime.onPeriod(context, LocationPolicyMode.PowerSavingNormal)
        releaseWindow()
        now += 45_000L

        assertTrue(
            "到点后必须发起下一拍",
            runtime.onPeriod(context, LocationPolicyMode.PowerSavingNormal) is SprintStartDecision.Started
        )
        assertEquals(2, runner.streamCount)
    }

    /** AC-2.5：运动/车载档（30 秒）每一拍都照常发起。 */
    @Test
    fun `运动档每一拍都发起`() = kotlinx.coroutines.test.runTest {
        val runtime = runtime()
        val context = context(requestIntervalMillis = 30_000L)

        val decisions = (0 until 3).map {
            val decision = runtime.onPeriod(context, LocationPolicyMode.MotionObservation)
            releaseWindow()
            now += 30_000L
            decision
        }

        assertTrue(
            "AC-2.5：30 秒档每一拍都必须照常发起（不得加节拍过密规则）",
            decisions.all { it is SprintStartDecision.Started }
        )
        assertEquals(3, runner.streamCount)
    }

    /** AC-5.3：开关关闭时周期门控不得放行（先判开关再看周期）。 */
    @Test
    fun `开关关闭时不发起冲刺`() = kotlinx.coroutines.test.runTest {
        val store = TrackingSettingsStore(InMemorySharedPreferences())
        store.setSprintEnabled(false)
        val controller = LocationSprintController(
            runner = runner,
            ledger = ledger,
            trackingSettingsStore = store
        )
        controller.wallClockMillis = { now }
        controller.testScope = eagerScope()
        val runtime = LocationSprintRuntime(
            controller = controller,
            operations = NoopOperations(),
            nowUtcMillis = { now }
        )

        val decision = runtime.onPeriod(
            context(requestIntervalMillis = 120_000L),
            LocationPolicyMode.PowerSavingNormal
        )

        assertEquals(SprintStartDecision.Skipped(SprintSkipReasons.DISABLED), decision)
        assertEquals("AC-5.3：关闭时不得注册冲刺流", 0, runner.streamCount)
    }

    /** AC-5.5：中途打开开关，下一个周期即生效（不需要重启）。 */
    @Test
    fun `中途打开开关下一个周期即生效`() = kotlinx.coroutines.test.runTest {
        val store = TrackingSettingsStore(InMemorySharedPreferences())
        store.setSprintEnabled(false)
        val controller = LocationSprintController(
            runner = runner,
            ledger = ledger,
            trackingSettingsStore = store
        )
        controller.wallClockMillis = { now }
        controller.elapsedRealtimeMillis = { now }
        controller.testScope = eagerScope()
        controller.delayMillis = { holdWindow() }
        val runtime = LocationSprintRuntime(
            controller = controller,
            operations = NoopOperations(),
            nowUtcMillis = { now }
        )
        val context = context(requestIntervalMillis = 45_000L)

        runtime.onPeriod(context, LocationPolicyMode.PowerSavingNormal)
        releaseWindow()
        store.setSprintEnabled(true)
        now += 45_000L

        assertTrue(
            "AC-5.5：打开开关后下一个周期必须开始冲刺",
            runtime.onPeriod(context, LocationPolicyMode.PowerSavingNormal) is SprintStartDecision.Started
        )
    }

    @org.junit.After
    fun releaseWindows() {
        // 放行所有仍在等待的窗口，避免遗留协程。
        repeat(8) { releaseSignal.trySend(Unit) }
    }

    private fun context(requestIntervalMillis: Long) = AcquisitionContext(
        policyMode = LocationPolicyMode.PowerSavingNormal.name,
        scheduleLowFrequency = false,
        motionSignal = "Still",
        requestIntervalMillis = requestIntervalMillis
    )

    private class NoopOperations : LocationAcquisitionOperations {
        override suspend fun enqueueAccepted(
            accepted: QualityAcceptedLocation,
            rawJson: String,
            source: String
        ) = Unit

        override suspend fun recordDropped(fix: RawLocationFix, reason: String) = Unit
        override fun scheduleSync() = Unit
    }

    /** 只记注册次数；窗口立即由 delayMillis 空实现结束。 */
    private class RecordingSprintSource : SprintUpdateSource {
        var streamCount = 0
            private set

        override suspend fun streamSprintWindow(
            request: com.pim.app.location.acquisition.LocationUpdateRequest,
            onSnapshot: suspend (com.pim.app.location.LocationSnapshot) -> Unit
        ) {
            streamCount += 1
        }
    }

    private class RecordingSprintLedger : SprintLedgerPort {
        val executed = mutableListOf<SprintWindowResult>()
        val skipped = mutableListOf<String>()

        override suspend fun recordExecuted(result: SprintWindowResult): Boolean {
            executed += result
            return true
        }

        override suspend fun recordSkipped(occurredAtUtcMillis: Long, reason: String): Boolean {
            skipped += reason
            return true
        }
    }
}
