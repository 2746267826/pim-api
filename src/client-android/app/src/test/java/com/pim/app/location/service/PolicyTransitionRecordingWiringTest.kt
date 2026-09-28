package com.pim.app.location.service

import android.app.Application
import android.content.Context
import android.os.Looper
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.TestPimApp
import com.pim.app.data.AppDatabase
import com.pim.app.data.MobileDataDao
import com.pim.app.di.PolicyTransitionProductionWiring
import com.pim.app.location.LocationQueueRepository
import com.pim.app.location.service.ForegroundLocationRuntimeState
import com.pim.app.location.PolicyTransitionWriteFailureStore
import com.pim.app.location.policy.LocationPolicyMode
import com.pim.app.location.policy.PolicyDecision
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.testing.InMemorySharedPreferences
import java.util.concurrent.TimeUnit
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.annotation.LooperMode

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 **REQ-2 真实接线测试**。
 *
 * 为什么需要它：缺陷版本的 `ForegroundLocationServiceTest` 里 3 个用例用反射**自带**一个写入器，
 * 于是「生产路径到底有没有接线」完全不在验证范围内——写入链断了整整一个半月，CI 一直全绿
 * （工单 §二-4 / 附录 A5）。
 *
 * 本文件的用例：
 * - **不注入写入替身**：写入依赖由 [PolicyTransitionProductionWiring.recorder] 装配，即
 *   Hilt 生产图里 `PolicyTransitionModule` 用的**同一段代码**，落到真实
 *   [LocationQueueRepository] + 真实 Room 表；
 * - **不用反射碰写入字段**：`policyTransitionRecorder` 是普通属性，直接装配（对
 *   `applyDecision` 的反射调用与既有用例一致，那是被测方法的入口，不是写入字段）；
 * - 断言的是 **DAO 真的多了一行**（行内容与决策逐字段对齐），不是「替身被调用过」；
 * - 覆盖「重启首条」（`fromMode` 为空）与「两次决策 = 两行」计数对齐。
 *
 * 边界（如实说明）：Robolectric 单测跑不起真实 Hilt 图（仓库未引入 `hilt-android-testing`），
 * 因此这里执行的是**生产装配函数**而不是 Hilt 注入本身；Hilt 图的绑定形状由
 * `PolicyTransitionModuleWiringTest` 的源码谓词守住，二者合起来才是完整证据链。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = TestPimApp::class)
class PolicyTransitionRecordingWiringTest {

    private lateinit var db: AppDatabase
    private lateinit var dao: MobileDataDao

    @After
    fun tearDown() {
        if (::db.isInitialized) db.close()
        resetServiceRuntimeState()
    }

    /**
     * 本文件不建整套服务依赖（那会掩盖"接线"本身），因此不调 `onDestroy()`；
     * 但它会把 `ForegroundLocationService.runtimeState.isRunning` 置为 true，
     * 污染同 JVM 内后续用例（`RunningStateRestorerTest` 会看到 AlreadyRunning）。
     * 显式把它复位。
     */
    private fun resetServiceRuntimeState() {
        val field = ForegroundLocationService::class.java
            .getDeclaredField("_runtimeState")
            .apply { isAccessible = true }
        @Suppress("UNCHECKED_CAST")
        (field.get(null) as MutableStateFlow<ForegroundLocationRuntimeState>)
            .value = ForegroundLocationRuntimeState()
    }

    private fun newDao(): MobileDataDao {
        val context = ApplicationProvider.getApplicationContext<Application>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .setQueryExecutor { it.run() }
            .setTransactionExecutor { it.run() }
            .build()
        dao = db.mobileDataDao()
        return dao
    }

    /** 写入侧时钟（注入而不是 `System.currentTimeMillis()`，断言才能落在具体时间戳上）。 */
    private var nowMillis = 1_800_000_000_000L

    /** 真实仓库 + 真实 Room（默认新建库），沿用生产装配函数使用的构造函数。 */
    private fun productionRepository(dao: MobileDataDao = newDao()): LocationQueueRepository =
        LocationQueueRepository(dao) { nowMillis }

    /**
     * 生产装配：真实 Room + 真实仓库，再经 [PolicyTransitionProductionWiring] ——
     * 与 `PolicyTransitionModule` 用的是**同一段**装配代码。
     */
    private fun productionService(
        repository: LocationQueueRepository = productionRepository(),
        now: () -> Long = { nowMillis }
    ): ForegroundLocationService {
        val service = Robolectric.buildService(ForegroundLocationService::class.java).get()
        service.policyTransitionRecorder = PolicyTransitionProductionWiring.recorder(repository)
        service.policyTransitionWriteFailures = newFailureStore(now)
        return service
    }

    /** AC-1.5 用：故意**不**装写入依赖。 */
    private fun productionServiceWithoutRecorder(): ForegroundLocationService {
        val service = Robolectric.buildService(ForegroundLocationService::class.java).get()
        service.policyTransitionWriteFailures = newFailureStore(System::currentTimeMillis)
        return service
    }

    private fun newFailureStore(now: () -> Long): PolicyTransitionWriteFailureStore {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val prefs = context.getSharedPreferences(
            "policy_transition_wiring_" + System.nanoTime(),
            Context.MODE_PRIVATE
        ).also { it.edit().clear().commit() }
        val logs = StructuredLogRepository(
            context,
            TrackingSettingsStore(InMemorySharedPreferences())
        ) { now() }
        return PolicyTransitionWriteFailureStore(prefs, logs, now)
    }

    private fun decision(
        mode: LocationPolicyMode = LocationPolicyMode.PowerSavingNormal,
        interval: Long = 180_000L,
        reason: String = "默认省电档"
    ) = PolicyDecision(
        mode = mode,
        requestIntervalMillis = interval,
        nextExpectedLocationAtMillis = 1_000L,
        reason = reason,
        scheduleLowFrequency = false
    )

    private fun invokeApplyDecision(service: ForegroundLocationService, decision: PolicyDecision) {
        ForegroundLocationService::class.java
            .getDeclaredMethod(
                "applyDecision",
                PolicyDecision::class.java,
                Boolean::class.javaPrimitiveType
            )
            .apply { isAccessible = true }
            .invoke(service, decision, true)
    }

    /** 全表快照，按 `id` 升序（AC-1.1 的判定顺序）。 */
    private fun rows(): List<com.pim.app.data.MobileLocationPolicyTransitionEntity> =
        runBlocking {
            dao.recentPolicyTransitions(limit = 100).first().sortedBy { it.id }
        }

    private fun idleUntil(timeoutMillis: Long = 5_000L, predicate: () -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis)
        while (!predicate()) {
            shadowOf(Looper.getMainLooper()).idle()
            if (System.nanoTime() > deadline) {
                throw AssertionError("condition not met within ${timeoutMillis}ms")
            }
            Thread.yield()
        }
        shadowOf(Looper.getMainLooper()).idle()
    }

    // ── REQ-2 / AC-1.1 / AC-1.2 ───────────────────────────────────────────────

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun `生产装配下一次决策变化恰好落库一行`() {
        val service = productionService()
        assertEquals("初始库应为空", 0, runBlocking { dao.policyTransitionCount() })

        invokeApplyDecision(service, decision())
        idleUntil { runBlocking { dao.policyTransitionCount() } == 1 }

        val rows = rows()
        assertEquals(1, rows.size)
        // 重启 / 首次决策语义：fromMode 为空，记「暂无 → 新模式」（AC-1.2）。
        assertNull("首条决策的 fromMode 必须为空", rows[0].fromMode)
        assertEquals(LocationPolicyMode.PowerSavingNormal.name, rows[0].toMode)
        assertEquals("默认省电档", rows[0].reason)
        assertEquals("落库时间来自注入时钟", nowMillis, rows[0].occurredAtUtc)
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun `连续两次不同决策落库两行且与决策顺序一致`() {
        val service = productionService()
        val firstAt = nowMillis

        invokeApplyDecision(service, decision())
        nowMillis += 5_000L
        val secondAt = nowMillis
        invokeApplyDecision(
            service,
            decision(
                mode = LocationPolicyMode.ScheduleLowFrequency,
                reason = "当前日程时段，降低定位频率"
            )
        )
        idleUntil { runBlocking { dao.policyTransitionCount() } == 2 }

        val rows = rows()
        assertEquals("两次决策变化必须正好两行（不丢不重）", 2, rows.size)
        assertNull(rows[0].fromMode)
        assertEquals(LocationPolicyMode.PowerSavingNormal.name, rows[0].toMode)
        assertEquals(LocationPolicyMode.PowerSavingNormal.name, rows[1].fromMode)
        assertEquals(LocationPolicyMode.ScheduleLowFrequency.name, rows[1].toMode)
        assertEquals("当前日程时段，降低定位频率", rows[1].reason)
        assertEquals(firstAt, rows[0].occurredAtUtc)
        assertEquals(secondAt, rows[1].occurredAtUtc)
        assertTrue(
            "按 id 升序时 occurred_at_utc 非递减",
            rows[0].occurredAtUtc <= rows[1].occurredAtUtc
        )
        assertEquals("决策顺序与 id 升序一一对应", listOf(1L, 2L), rows.map { it.id })
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun `同一决策重复出现只落库一行`() {
        val service = productionService()
        val stable = decision()

        invokeApplyDecision(service, stable)
        idleUntil { runBlocking { dao.policyTransitionCount() } == 1 }
        // 模式 / 间隔 / 原因全同 → 去重器不产生 transition，不得再写。
        invokeApplyDecision(service, stable.copy(nextExpectedLocationAtMillis = 99_000L))
        shadowOf(Looper.getMainLooper()).idle()
        Thread.sleep(50)
        shadowOf(Looper.getMainLooper()).idle()

        assertEquals(1, runBlocking { dao.policyTransitionCount() })
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun `服务重启后的首条决策记暂无到新模式`() {
        val first = productionService()
        invokeApplyDecision(first, decision())
        idleUntil { runBlocking { dao.policyTransitionCount() } == 1 }

        // 重启 = 新服务实例（去重器状态清零），同一张库表。
        val restarted = productionService(productionRepository(dao))
        invokeApplyDecision(
            restarted,
            decision(mode = LocationPolicyMode.MotionObservation, reason = "检测到运动状态：步行")
        )
        idleUntil { runBlocking { dao.policyTransitionCount() } == 2 }

        val rows = rows()
        assertNull("重启后的首条决策仍必须记「暂无 → 新模式」", rows[1].fromMode)
        assertEquals(LocationPolicyMode.MotionObservation.name, rows[1].toMode)
    }

    // ── AC-1.1：三次决策变化 = 三行，且顺序与决策一致 ─────────────────────────

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun `三次决策变化落库三行且按 id 与决策顺序一一对应`() {
        val service = productionService()
        val stamps = mutableListOf<Long>()

        val first = decision()
        val second = decision(
            mode = LocationPolicyMode.ScheduleLowFrequency,
            reason = "当前日程时段，降低定位频率"
        )
        val third = decision(
            mode = LocationPolicyMode.MotionObservation,
            reason = "检测到运动状态：步行"
        )

        invokeApplyDecision(service, first)
        idleUntil { runBlocking { dao.policyTransitionCount() } == 1 }

        nowMillis += 1_000L
        stamps += nowMillis
        invokeApplyDecision(service, second)
        idleUntil { runBlocking { dao.policyTransitionCount() } == 2 }

        nowMillis += 1_000L
        stamps += nowMillis
        invokeApplyDecision(service, third)
        idleUntil { runBlocking { dao.policyTransitionCount() } == 3 }

        stamps.add(0, 1_800_000_000_000L)
        val rows = rows()
        assertEquals(3, rows.size)
        assertEquals(listOf(1L, 2L, 3L), rows.map { it.id })
        assertEquals(
            listOf(
                LocationPolicyMode.PowerSavingNormal.name,
                LocationPolicyMode.ScheduleLowFrequency.name,
                LocationPolicyMode.MotionObservation.name
            ),
            rows.map { it.toMode }
        )
        assertEquals(stamps, rows.map { it.occurredAtUtc })
        assertTrue(
            "occurred_at_utc 必须非递减",
            rows.zipWithNext().all { (a, b) -> a.occurredAtUtc <= b.occurredAtUtc }
        )
    }

    // ── AC-1.4：清空表后的一次决策变化仍要落库 ────────────────────────────────

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun `清空表之后的一次决策变化仍然落库`() {
        val service = productionService()
        invokeApplyDecision(service, decision())
        idleUntil { runBlocking { dao.policyTransitionCount() } == 1 }

        // 等价于「清除诊断数据」（DiagnosticExportRepository 会删这三张表）。
        runBlocking { dao.deleteAllMobileLocationPolicyTransitions() }
        assertEquals("清空后表必须为空", 0, runBlocking { dao.policyTransitionCount() })

        invokeApplyDecision(
            service,
            decision(mode = LocationPolicyMode.MotionObservation, reason = "清空后的第一次变化")
        )
        idleUntil { runBlocking { dao.policyTransitionCount() } == 1 }

        val rows = rows()
        assertEquals(1, rows.size)
        assertEquals(LocationPolicyMode.MotionObservation.name, rows[0].toMode)
        assertEquals("清空后的第一次变化", rows[0].reason)
        assertEquals(
            "清空后仍必须记录「上一档位 → 新模式」",
            LocationPolicyMode.PowerSavingNormal.name,
            rows[0].fromMode
        )
    }

    // ── AC-1.5：装配缺失必须显式失败 ─────────────────────────────────────────
    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun `写入依赖未装配时服务显式失败而不是静默跳过`() {
        newDao()
        val service = productionServiceWithoutRecorder()

        val thrown = runCatching { invokeApplyDecision(service, decision()) }.exceptionOrNull()

        assertNotNull("未装配写入依赖必须显式失败，而不是静默跳过", thrown)
        val failure = generateSequence(thrown) { it.cause }
            .firstOrNull { it is IllegalStateException }
        assertNotNull("失败必须是明确的装配错误（实际：$thrown）", failure)
        assertTrue(
            "失败信息必须指明是装配错误（实际：${failure!!.message}）",
            failure.message.orEmpty().contains("装配错误")
        )
        assertEquals("显式失败后不得写入任何行", 0, runBlocking { dao.policyTransitionCount() })
    }
}
