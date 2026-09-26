package com.pim.app.location.sprint

import com.pim.app.location.LocationSnapshot
import com.pim.app.location.acquisition.AcquisitionContext
import com.pim.app.location.acquisition.LocationUpdateRequest
import com.pim.app.location.policy.LocationPolicyMode
import com.pim.app.location.quality.LocationQualityGate
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.testing.InMemorySharedPreferences
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.RawLocationFix
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.awaitCancellation
import kotlinx.coroutines.test.TestScope
import kotlinx.coroutines.test.runCurrent
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * WO-ANDROID-GATE-20260926 REQ-2 / REQ-3 / REQ-4 / REQ-5 / REQ-6。
 *
 * 用**假的 runner**（可确定性驱动回调）验证冲刺窗口的行为契约。这些是需求方确认过的
 * 数值口径（30 秒、不早退、达标全留、高速档不冲、开关真的生效），
 * 因此必须由不依赖设备的单测守着。
 *
 * 时间模型：墙钟（`nowUtcMillis`）与「系统在 duration 后停止回调」都由测试显式推进，
 * 因此「等满 30 秒」是可确定复现的，而不是靠 sleep。
 */
class LocationSprintControllerTest {

    private val runner = FakeSprintRunner()
    private val ledger = RecordingSprintLedger()
    private val gate = LocationQualityGate()

    private var enabled = true
    private var nowUtcMillis = 1_700_000_000_000L
    private val windowExpiry = ReleaseGate()
    private var windowEndUtcMillis = 0L

    private val acceptedSink = mutableListOf<Pair<QualityAcceptedLocation, Float?>>()
    private val droppedSink = mutableListOf<Pair<RawLocationFix, String>>()

    private fun controller(scope: CoroutineScope): LocationSprintController {
        val controller = LocationSprintController(
            runner = runner,
            ledger = ledger,
            trackingSettingsStore = TrackingSettingsStore(InMemorySharedPreferences())
        )
        controller.testScope = scope
        controller.wallClockMillis = { nowUtcMillis }
        // 窗口时长按单调时钟度量（墙钟回拨不得影响 AC-2.2 / AC-3.1）；
        // 测试里把两者一起推进，保持「虚拟时间」单一来源。
        controller.elapsedRealtimeMillis = { nowUtcMillis }
        controller.qualityGateProvider = { gate }
        controller.sprintEnabledProvider = { enabled }
        // 虚拟延时：记下**窗口到期时刻**（= 调用时刻 + 剩余时长）并挂起，
        // 直到测试宣布「窗口该结束了」。到期时刻直接由控制器算出，
        // 因此测试中途推进时间也不会与它重复累加。
        controller.delayMillis = { requested ->
            windowEndUtcMillis = nowUtcMillis + requested
            windowExpiry.await()
        }
        controller.onAccepted = { accepted, accuracy -> acceptedSink += accepted to accuracy }
        controller.onDropped = { fix, reason -> droppedSink += fix to reason }
        return controller
    }

    // ─── REQ-5 开关 ──────────────────────────────────────────────

    /** AC-5.3 / AC-5.4：关闭时必须**不发起**冲刺，并写「跳过」记录（不是「已冲刺」）。 */
    @Test
    fun `开关关闭时不发起冲刺且记录跳过原因`() = runTest {
        enabled = false
        val controller = controller(this)

        val decision = controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        assertEquals(
            "AC-5.3：关闭后不得发起任何冲刺",
            SprintStartDecision.Skipped(SprintSkipReasons.DISABLED),
            decision
        )
        assertFalse("AC-5.3：关闭时窗口不得打开", controller.isWindowOpen())
        assertEquals("AC-5.3：不得注册任何冲刺取点流", 0, runner.streamCount)
        assertEquals(
            "AC-5.4：关闭时写的是跳过记录，不是已执行记录",
            listOf(SprintOutcome.SKIPPED),
            ledger.records.map { it.first }
        )
        assertEquals(SprintSkipReasons.DISABLED, ledger.records.single().second)
    }

    /** AC-5.3：关闭状态下连续多个周期都不冲刺（防「假开关」）。 */
    @Test
    fun `关闭状态下连续三个周期都不冲刺`() = runTest {
        enabled = false
        val controller = controller(this)

        repeat(3) {
            controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
            runCurrent()
        }

        assertEquals("AC-5.3.1：台账不得新增任何「已冲刺」记录", 0, ledger.executedCount)
        assertEquals(3, ledger.records.size)
        assertTrue(ledger.records.all { it.first == SprintOutcome.SKIPPED })
    }

    /** AC-5.3.2：关闭后不得为冲刺新增/变更任何定位请求注册。 */
    @Test
    fun `关闭状态下不注册任何定位请求`() = runTest {
        enabled = false
        val controller = controller(this)

        repeat(5) {
            controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
            runCurrent()
        }

        assertEquals(
            "AC-5.3.2：关闭后观测不到任何为冲刺而新增的定位请求注册",
            0,
            runner.streamCount
        )
    }

    /** AC-5.5：开关关闭后**不重启**再开启，自下一个周期起重新冲刺。 */
    @Test
    fun `开关在运行期间切换无需重启即生效`() = runTest {
        val controller = controller(this)

        enabled = false
        assertEquals(
            SprintStartDecision.Skipped(SprintSkipReasons.DISABLED),
            controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        )
        runCurrent()

        // AC-5.5：不重启应用/采集服务，只改设置 → 下一个周期必须真的开始冲刺
        enabled = true
        val decision = controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        assertTrue(
            "AC-5.5：改开关后无需重启，下一个周期必须开始冲刺",
            decision is SprintStartDecision.Started
        )
        assertTrue(controller.isWindowOpen())
        assertEquals("AC-5.5：下一个周期确实注册了冲刺取点流", 1, runner.streamCount)
        controller.abort()
    }

    /** 反面：开启状态改回关闭后，下一个周期同样立即停止冲刺（双向生效）。 */
    @Test
    fun `开启后再关闭同样无需重启即生效`() = runTest {
        val controller = controller(this)

        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()
        assertEquals(1, runner.streamCount)

        // 让本窗口自然结束
        finishWindow(controller)
        assertEquals(1, ledger.executedCount)

        enabled = false
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        assertEquals("AC-5.3：关闭后不得再注册新的冲刺流", 1, runner.streamCount)
        assertEquals("AC-5.3.1：关闭后不得新增已执行记录", 1, ledger.executedCount)
    }

    /** AC-5.6：冲刺必须是**独立的高频注册**，不得改写主流的注册间隔与周期锚点。 */
    @Test
    fun `冲刺不得改动主流的注册间隔`() = runTest {
        val controller = controller(this)
        val context = context(requestIntervalMillis = 180_000L, motionSignal = "Still")

        controller.startSprint(context, mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        val sprintRequest = runner.lastStreamRequest!!
        assertEquals(
            "AC-5.6：冲刺用自己的注册间隔（≈1 秒，D3）",
            LocationSprintContract.SAMPLE_INTERVAL_MILLIS,
            sprintRequest.intervalMillis
        )
        assertEquals(
            "AC-5.6：冲刺窗口有界（≤ 30 秒），不是常驻高频模式",
            LocationSprintContract.WINDOW_MILLIS,
            sprintRequest.durationMillis
        )
        assertEquals(
            "AC-5.6：冲刺不触碰主流上下文里的注册间隔（主流仍是 180 秒）",
            180_000L,
            context.requestIntervalMillis
        )
        controller.abort()
    }

    // ─── REQ-2 触发与边界 ─────────────────────────────────────────

    /** AC-2.4：高速档（2.5 秒一拍）期间不发起冲刺。 */
    @Test
    fun `高速档期间不发起冲刺并记录原因`() = runTest {
        val controller = controller(this)

        val decision = controller.startSprint(context(), mode = LocationPolicyMode.HighSpeed)
        runCurrent()

        assertEquals(
            "AC-2.4：高速档已是密集采样，不得叠加冲刺",
            SprintStartDecision.Skipped(SprintSkipReasons.HIGH_SPEED),
            decision
        )
        assertEquals(0, runner.streamCount)
        assertEquals(SprintSkipReasons.HIGH_SPEED, ledger.records.single().second)
    }

    /** AC-10.3：非采集时段（Off）不冲刺。 */
    @Test
    fun `非采集时段不发起冲刺`() = runTest {
        val controller = controller(this)

        val decision = controller.startSprint(context(), mode = LocationPolicyMode.Off)
        runCurrent()

        assertEquals(SprintStartDecision.Skipped(SprintSkipReasons.NOT_COLLECTING), decision)
        assertEquals(0, runner.streamCount)
    }

    /**
     * AC-2.3：同一时刻不得存在两个并发窗口；AC-2.5：周期相接时**不得跳过**。
     *
     * 两者一起看：本窗口结束前到来的下一拍被记为「待发起」，窗口之间不重叠，
     * 但每一拍都真的冲到（不会退化成隔拍才冲）。
     */
    @Test
    fun `窗口未结束时到来的下一拍记为待发起而不跳过`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        val second = controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        assertTrue(
            "AC-2.5：周期相接时下一拍也必须照常发起（不得跳过）",
            second is SprintStartDecision.Started
        )
        assertTrue("AC-2.5：下一拍必须被记为待发起", controller.hasPendingStart())
        assertEquals(
            "AC-2.3：同一时刻仍然只能有一个冲刺取点流（窗口不重叠）",
            1,
            runner.streamCount
        )

        // 本窗口结束后，待发起的那一拍必须立刻接上
        finishWindow(controller)
        runCurrent()
        assertEquals(
            "AC-2.5：上一窗口结束后必须立刻接上下一拍",
            2,
            runner.streamCount
        )
        // 接上的新窗口也要收尾，测试协程才能结束
        controller.abort()
        runCurrent()
    }

    /** AC-2.5：运动/车载档（30 秒硬下限）下冲刺照常发起，不得以「过于频繁」跳过。 */
    @Test
    fun `运动档下冲刺照常发起`() = runTest {
        val controller = controller(this)

        val decision = controller.startSprint(
            context(requestIntervalMillis = 30_000L, motionSignal = "Running"),
            mode = LocationPolicyMode.MotionObservation
        )
        runCurrent()

        assertTrue(
            "AC-2.5：运动/车载档（30 秒硬下限）下冲刺必须照常发起",
            decision is SprintStartDecision.Started
        )
        assertEquals(1, runner.streamCount)
        assertEquals(
            "AC-2.5：窗口不得被裁剪到比 30 秒更短",
            LocationSprintContract.WINDOW_MILLIS,
            runner.lastStreamRequest!!.durationMillis
        )
        controller.abort()
    }

    /** AC-2.5：周期与窗口相接时连续多拍都照冲（不因「节拍过密」跳过）。 */
    @Test
    fun `运动档下连续多拍都照常冲刺`() = runTest {
        val controller = controller(this)
        val context = context(requestIntervalMillis = 30_000L, motionSignal = "InVehicle")

        val decisions = (0 until 3).map {
            val decision = controller.startSprint(context, mode = LocationPolicyMode.MotionObservation)
            runCurrent()
            finishWindow(controller)
            decision
        }

        assertTrue(
            "AC-2.5：运动/车载档每一拍都必须照常发起冲刺（不得加「节拍过密即跳过」规则）",
            decisions.all { it is SprintStartDecision.Started }
        )
        assertEquals(3, runner.streamCount)
        assertEquals(3, ledger.executedCount)
    }

    // ─── REQ-3 / REQ-4 收点规则 ───────────────────────────────────

    /** AC-3.1 / AC-3.2：窗口中途出现极精确点也不早退，窗口跑满 30 秒。 */
    @Test
    fun `窗口中途出现极精确点也不早退`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        runner.emit(snapshot(accuracy = 3f))

        // 窗口必须仍然开着（不得因达标而提前结束）
        assertTrue("AC-3.2：不存在「达标即提前结束窗口」的路径", controller.isWindowOpen())

        // 窗口已经跑了一段时间后，再给一条极精确的点；仍不得提前结束
        nowUtcMillis += MID_WINDOW_MILLIS
        runner.emit(snapshot(accuracy = 4f))
        assertTrue("AC-3.2：中途出现极精确点后窗口仍必须开着", controller.isWindowOpen())

        // 走完剩余时间：窗口必须自己跑满 30 秒，而不是被任何达标点提前关掉。
        finishWindow(controller)

        assertEquals(SprintOutcome.EXECUTED, ledger.records.single().first)
        val result = ledger.results.single()
        assertEquals(
            "AC-3.1：窗口必须跑满 30 秒（+ε ≤ 2 秒）",
            LocationSprintContract.WINDOW_MILLIS,
            result.durationMillis
        )
    }

    /** AC-3.1 / AC-3.2：没有任何早退阈值常量。 */
    @Test
    fun `不存在早退阈值常量`() {
        val source = java.io.File("").canonicalFile.let { dir ->
            generateSequence(dir) { it.parentFile }
                .map { it.resolve("src/main/java/com/pim/app/location/sprint") }
                .first { it.isDirectory }
        }.walkTopDown().filter { it.isFile && it.extension == "kt" }
            .joinToString("\n") { it.readText(Charsets.UTF_8) }

        listOf("EARLY_EXIT", "earlyExit", "minAcceptableAccuracy", "EXIT_THRESHOLD").forEach { needle ->
            assertFalse(
                "AC-3.2：不得存在早退阈值常量（命中：$needle）",
                source.contains(needle)
            )
        }
    }

    /** AC-4.1 / AC-4.2：窗口内达标的点全部入库（不只留最好一条）。 */
    @Test
    fun `窗口内达标的点全部入库`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        listOf(25f, 12f, 18f).forEach { runner.emit(snapshot(accuracy = it)) }
        finishWindow(controller)

        assertEquals(
            "AC-4.1 / AC-4.2：三条达标点必须全部入库",
            listOf(25f, 12f, 18f),
            acceptedSink.map { it.second }
        )
        val result = ledger.results.single()
        assertEquals(3, result.acceptedCount)
        assertEquals("AC-4.1：最好的一条可区分", 12f, result.bestAccuracyMeters!!, 0.001f)
    }

    /** AC-4.3 / AC-4.4：未达标的点不得被收下，且窗口无达标点时入库 0 条。 */
    @Test
    fun `未达标的点被丢弃且无达标点时入库 0 条`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        listOf(30f, 45f, 200f).forEach { runner.emit(snapshot(accuracy = it)) }
        finishWindow(controller)

        assertEquals("AC-4.4：自动流窗口内无达标点 → 入库 0 条", 0, acceptedSink.size)
        assertEquals("AC-4.3：未达标的点必须走丢弃诊断", 3, droppedSink.size)
        assertEquals(
            setOf("horizontal-accuracy-too-low"),
            droppedSink.map { it.second }.toSet()
        )
        assertEquals(0, ledger.results.single().acceptedCount)
        assertEquals(
            "AC-4.1：无达标点时最好精度为 null，不谎报",
            null,
            ledger.results.single().bestAccuracyMeters
        )
    }

    /** AC-4.5 / AC-15.3：每条回调都被计数（去重前），入库条数 = 达标条数，禁绝静默丢弃。 */
    @Test
    fun `回调条数与入库条数可对账`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        // 10 条回调：6 条达标 + 4 条不达标，且含重复精度（客户端已无去重，必须逐个计数）
        val accuracies = listOf(10f, 10f, 12f, 40f, 15f, 40f, 20f, 90f, 25f, 40f)
        accuracies.forEach { runner.emit(snapshot(accuracy = it)) }
        finishWindow(controller)

        val result = ledger.results.single()
        assertEquals(
            "AC-4.5：sampleCount 统计去重前的回调条数",
            accuracies.size,
            result.sampleCount
        )
        assertEquals(
            "AC-4.5：入库条数 = 窗口内达标条数（无去重、无裁剪）",
            accuracies.count { it < 30f },
            result.acceptedCount
        )
        assertEquals(6, acceptedSink.size)
        assertEquals(4, droppedSink.size)
        assertEquals(
            "AC-15.3：回调 = 入库 + 丢弃，缺口必须为 0",
            result.sampleCount,
            acceptedSink.size + droppedSink.size
        )
    }

    /** AC-2.1：窗口内取点节奏 ≈1 秒（用请求参数表达，D3）。 */
    @Test
    fun `窗口内取点节奏约为一秒`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        val request = runner.lastStreamRequest!!
        assertEquals(1_000L, request.intervalMillis)
        assertEquals(
            "AC-2.1：下界 800 毫秒来自基线 minUpdateIntervalMillis",
            800L,
            request.minUpdateIntervalMillis
        )
        controller.abort()
    }

    /**
     * AC-10.3 / AC-5.3 反面（独立 review round 2 指出）：`abort()` 之后，
     * 已经挂起在 `finishWindow`（写台账）里的协程**不得**再开出待发起的窗口 ——
     * 否则停止采集/服务销毁后还在冲刺。
     */
    @Test
    fun `中止后待发起的下一拍不得再开窗口`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()
        // 让下一拍进入待发起槽位（AC-2.5）
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()
        assertTrue(controller.hasPendingStart())

        controller.abort()
        runCurrent()

        assertFalse("中止后不得再开窗口", controller.isWindowOpen())
        assertFalse("中止后待发起槽位必须清空", controller.hasPendingStart())
        assertEquals("中止后不得注册新的冲刺流", 1, runner.streamCount)
    }

    /** AC-5.3 反面：窗口还开着时把开关关掉，待发起的下一拍**不得**被接上。 */
    @Test
    fun `窗口开着时关闭开关则待发起的下一拍不得接上`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()
        assertTrue(controller.hasPendingStart())

        // 关掉开关（无需重启）→ 本窗口结束也不得再开新的
        enabled = false
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        assertFalse("AC-5.3：关闭后待发起槽位必须清空", controller.hasPendingStart())

        finishWindow(controller)
        runCurrent()

        assertEquals(
            "AC-5.3：关闭后不得因待发起而在本窗口结束后再接上一拍",
            1,
            runner.streamCount
        )
        assertFalse("关闭后不得有窗口在跑", controller.isWindowOpen())
    }

    /**
     * I-1（独立 review round 3）：手动会话**不得**经过周期门控，也不得在
     * 已有窗口时排队 —— 否则同一采集周期会出现两次冲刺（违反 D2），
     * 并与手动会话自己的取点流重复入库。
     */
    @Test
    fun `手动冲刺不与自动窗口叠加`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()
        assertEquals(1, runner.streamCount)

        val manual = controller.startManualSprint(context())
        runCurrent()

        assertEquals(
            "I-1：手动在已有自动窗口时不得再开一个窗口",
            SprintStartDecision.Skipped(SprintSkipReasons.WINDOW_ALREADY_OPEN),
            manual
        )
        assertEquals("I-1：同一时刻仍然只有一个冲刺流", 1, runner.streamCount)
        assertFalse("I-1：手动不得排队形成第二个窗口", controller.hasPendingStart())
        controller.abort()
    }

    /** I-1：没有窗口在跑时，手动照常发起冲刺（AC-6.1）。 */
    @Test
    fun `手动冲刺在没有窗口时照常发起`() = runTest {
        val controller = controller(this)

        val manual = controller.startManualSprint(context())
        runCurrent()

        assertTrue("AC-6.1：手动必须能发起冲刺", manual is SprintStartDecision.Started)
        assertEquals(1, runner.streamCount)
        controller.abort()
    }

    /** AC-6.3：开关关闭时手动也不冲刺，且记「跳过」。 */
    @Test
    fun `开关关闭时手动冲刺被跳过`() = runTest {
        enabled = false
        val controller = controller(this)

        val manual = controller.startManualSprint(context())
        runCurrent()

        assertEquals(
            SprintStartDecision.Skipped(SprintSkipReasons.DISABLED),
            manual
        )
        assertEquals("AC-6.3：关闭时不得注册冲刺流", 0, runner.streamCount)
        controller.abort()
    }

    /**
     * I-A（独立 review round 3）：`abort()` 发生在 `finishWindow` **正在写台账**时，
     * `windowJob` 已被置空，取消不到那个协程 —— 只能靠代数（generation）拦住它
     * 开出待发起的下一拍。中止必须**精确插在写台账那一刻**，否则测不出这条回归。
     */
    @Test
    fun `写台账期间中止不得再开窗口`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()
        // 让下一拍进入待发起槽位（AC-2.5）
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()
        assertTrue(controller.hasPendingStart())

        // 在本窗口写台账的**那一刻**中止（等价于「写台账期间服务被销毁/停止采集」）
        ledger.onBeforeWrite = { controller.abort() }

        finishWindow(controller)
        runCurrent()

        assertEquals(
            "I-A：写台账期间被中止，待发起的下一拍不得开出新窗口",
            1,
            runner.streamCount
        )
        assertFalse("中止后不得有窗口在跑", controller.isWindowOpen())
    }

    /** 中止的窗口不产出「已执行」记录（不得拿未跑完的窗口充证据）。 */
    @Test
    fun `中止的窗口不写已执行记录`() = runTest {
        val controller = controller(this)
        controller.startSprint(context(), mode = LocationPolicyMode.PowerSavingNormal)
        runCurrent()

        controller.abort()
        runCurrent()

        assertEquals("中止的窗口不得产出已执行记录", 0, ledger.executedCount)
        assertFalse(controller.isWindowOpen())
    }

    /**
     * 让当前窗口自然结束：先把墙钟推到期满，再让被挂起的注册流返回。
     */
    private suspend fun TestScope.finishWindow(controller: LocationSprintController) {
        // 墙钟推到控制器自己算出的窗口到期时刻，然后放行等待。
        nowUtcMillis = windowEndUtcMillis
        // 先让被挂起的冲刺注册返回（等价于系统按 duration 停止回调），再放行窗口到期等待。
        runner.releaseStream()
        windowExpiry.release()
        runCurrent()
        // 注意：AC-2.5 允许「待发起」的下一拍在本窗口结束后**立刻**接上，
        // 因此这里不断言窗口已关闭（那要与待发起语义打架）；各用例按需自行断言。
    }

    private fun context(
        requestIntervalMillis: Long = 180_000L,
        motionSignal: String = "Still"
    ) = AcquisitionContext(
        policyMode = LocationPolicyMode.PowerSavingNormal.name,
        scheduleLowFrequency = false,
        motionSignal = motionSignal,
        requestIntervalMillis = requestIntervalMillis
    )

    private val MID_WINDOW_MILLIS = 15_000L

    private fun snapshot(accuracy: Float) = LocationSnapshot(
        latitude = 31.230416,
        longitude = 121.473701,
        horizontalAccuracyMeters = accuracy,
        provider = "gps",
        source = "realtime",
        altitudeMeters = 10.0,
        speedMetersPerSecond = null,
        bearingDegrees = null,
        timeMillis = nowUtcMillis
    )
}

private class RecordingSprintLedger : SprintLedgerPort {
    /** 在写台账「那一刻」触发的钩子（用于把中止精确插进挂起点）。 */
    var onBeforeWrite: (() -> Unit)? = null

    /** (outcome, skipReason) */
    val records = mutableListOf<Pair<String, String?>>()
    val results = mutableListOf<SprintWindowResult>()

    val executedCount: Int get() = records.count { it.first == SprintOutcome.EXECUTED }

    override suspend fun recordExecuted(result: SprintWindowResult): Boolean {
        onBeforeWrite?.invoke()
        records += SprintOutcome.EXECUTED to null
        results += result
        return true
    }

    override suspend fun recordSkipped(occurredAtUtcMillis: Long, reason: String): Boolean {
        records += SprintOutcome.SKIPPED to reason
        return true
    }
}

/** 单次放行的闸门：`await()` 挂起直到 `release()`。 */
private class ReleaseGate {
    private var gate = CompletableDeferred<Unit>()

    suspend fun await() {
        gate.await()
    }

    fun release() {
        val released = gate
        gate = CompletableDeferred()
        released.complete(Unit)
    }
}

/**
 * 假的定位引擎：把回调暴露给测试，并让 `stream` 挂起直到测试显式放行
 * （等价于「系统在请求 duration 到期后停止回调」）。
 */
private class FakeSprintRunner : SprintUpdateSource {
    var streamCount = 0
        private set
    var lastStreamRequest: LocationUpdateRequest? = null
        private set

    private var onCandidateRef: (suspend (LocationSnapshot) -> Unit)? = null
    private val started = CompletableDeferred<Unit>()
    private var release = CompletableDeferred<Unit>()

    suspend fun releaseStream() {
        val current = release
        release = CompletableDeferred()
        current.complete(Unit)
    }

    suspend fun emit(snapshot: LocationSnapshot) {
        onCandidateRef?.invoke(snapshot)
    }

    override suspend fun streamSprintWindow(
        request: LocationUpdateRequest,
        onCandidate: suspend (LocationSnapshot) -> Unit
    ) {
        streamCount += 1
        lastStreamRequest = request
        onCandidateRef = onCandidate
        started.complete(Unit)
        // 等到测试宣布「系统已按 duration 停止回调」。
        release.await()
        awaitCancellation()
    }
}
