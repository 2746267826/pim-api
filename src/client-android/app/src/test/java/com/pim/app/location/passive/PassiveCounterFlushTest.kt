package com.pim.app.location.passive

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.data.AppDatabase
import com.pim.app.location.LocationSnapshot
import com.pim.app.location.acquisition.LocationAcquisitionOperations
import com.pim.app.location.quality.RawLocationFix
import com.pim.app.location.sprint.PassiveLocationEventTypes
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.settings.TrackingSettingsStore
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * WO-ANDROID-GATE-20260926 AC-14.2：被动点的三数对账分母必须**周期性**落到台账。
 *
 * 真机实测暴露的缺口：只在 `stop()` 时写计数，服务被系统杀掉或被 force-stop
 * 时那一行永远不会出现（`onDestroy`/`stopCollection` 都不保证执行），
 * 验收方看到「入库 N 条」却无从判断是否丢点。因此必须有独立的**窗口刷新**。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class PassiveCounterFlushTest {

    private lateinit var db: AppDatabase
    private lateinit var coordinator: PassiveLocationCoordinator
    private var now = 1_700_000_000_000L

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        val settings = TrackingSettingsStore(
            context.getSharedPreferences("passive-flush-test", Context.MODE_PRIVATE)
        )
        val logs = StructuredLogRepository(context, settings) { now }
        coordinator = PassiveLocationCoordinator(
            source = FakePassiveSource(context, settings, logs),
            operations = NoopOperations(),
            ledger = PassiveLocationLedger(db.forensicEventDao(), logs),
            logs = logs,
            nowUtcMillis = { now }
        )
    }

    @After
    fun tearDown() {
        db.close()
    }

    private suspend fun counters() =
        db.forensicEventDao().recentByType(PassiveLocationEventTypes.PASSIVE_COUNTER, 10)

    /** AC-14.2：窗口刷新一次，台账就出现一条可对账的计数行（缺口 = 0）。 */
    @Test
    fun `刷新窗口后计数落台账且缺口为零`() = runTest {
        coordinator.start { emptyList() }
        val processor = coordinator.activeProcessorForTest()!!

        repeat(10) { processor.handle(passiveFix(accuracy = 10f)) }
        repeat(4) { processor.handle(passiveFix(accuracy = 50f)) }

        val flushed = coordinator.flushWindow()

        assertEquals(14, flushed!!.callbackCount)
        assertEquals(10, flushed.acceptedCount)
        assertEquals(4, flushed.droppedCount)
        assertEquals("回调 = 入库 + 丢弃 + 重复，缺口必须为 0", 0, flushed.unaccountedCount)

        val payload = JSONObject(counters().single().payloadJson)
        assertEquals(14, payload.getInt("passiveCallbackCount"))
        assertEquals(10, payload.getInt("passiveAcceptedCount"))
        assertEquals(4, payload.getInt("passiveDroppedCount"))
        assertEquals(
            "AC-14.2：台账里的对账缺口必须为 0",
            0,
            payload.getInt("passiveUnaccountedCount")
        )
    }

    /** 刷新后计数清零，新窗口从 0 开始（否则会重复累加）。 */
    @Test
    fun `刷新后开启新窗口不重复累加`() = runTest {
        coordinator.start { emptyList() }
        val processor = coordinator.activeProcessorForTest()!!

        processor.handle(passiveFix(accuracy = 10f))
        coordinator.flushWindow()
        assertNull("没有新回调时不再写第二条", coordinator.flushWindow())

        now += 60_000L
        processor.handle(passiveFix(accuracy = 12f))
        val second = coordinator.flushWindow()

        assertEquals("新窗口只统计新回调", 1, second!!.callbackCount)
        assertEquals(2, counters().size)
    }

    /**
     * AC-14.2（独立 review round 2 指出缺的守卫）：写入失败时**必须保留计数**，
     * 不得因为一次失败就丢掉整个窗口的分母。
     */
    @Test
    fun `写入失败时保留计数等待重试`() = runTest {
        val failingLedger = object : PassiveLocationLedger(
            db.forensicEventDao(),
            StructuredLogRepository(
                ApplicationProvider.getApplicationContext(),
                TrackingSettingsStore(
                    ApplicationProvider.getApplicationContext<Context>()
                        .getSharedPreferences("passive-flush-fail", Context.MODE_PRIVATE)
                )
            ) { now }
        ) {
            var failNext = true
            override suspend fun recordCounters(
                occurredAtUtcMillis: Long,
                windowStartUtcMillis: Long,
                windowSequence: Long,
                callbackCount: Int,
                acceptedCount: Int,
                droppedCount: Int,
                duplicateCount: Int
            ): Boolean {
                if (failNext) {
                    failNext = false
                    return false
                }
                return super.recordCounters(
                    occurredAtUtcMillis,
                    windowStartUtcMillis,
                    windowSequence,
                    callbackCount,
                    acceptedCount,
                    droppedCount,
                    duplicateCount
                )
            }
        }
        val context = ApplicationProvider.getApplicationContext<Context>()
        val settings = TrackingSettingsStore(
            context.getSharedPreferences("passive-flush-retry", Context.MODE_PRIVATE)
        )
        val logs = StructuredLogRepository(context, settings) { now }
        val retrying = PassiveLocationCoordinator(
            source = FakePassiveSource(context, settings, logs),
            operations = NoopOperations(),
            ledger = failingLedger,
            logs = logs,
            nowUtcMillis = { now }
        )

        retrying.start { emptyList() }
        val processor = retrying.activeProcessorForTest()!!
        repeat(5) { processor.handle(passiveFix(accuracy = 10f)) }

        val firstAttempt = retrying.flushWindow()
        assertEquals("第一次写入失败也要把计数返回（供诊断）", 5, firstAttempt!!.callbackCount)
        assertEquals(
            "写入失败后计数必须保留，不得清零",
            5,
            retrying.activeProcessorForTest()!!.countersSnapshot().callbackCount
        )

        val secondAttempt = retrying.flushWindow()
        assertEquals("重试时必须仍是同一个窗口计数", 5, secondAttempt!!.callbackCount)
        assertEquals(
            "重试成功后计数才清零",
            0,
            retrying.activeProcessorForTest()!!.countersSnapshot().callbackCount
        )
        assertEquals("重试成功后台账恰好一行", 1, counters().size)
    }

    /** AC-14.1：未注册时刷新是安全的空操作（服务未启动也能被周期任务调用）。 */
    @Test
    fun `未注册时刷新是空操作`() = runTest {
        assertNull(coordinator.flushWindow())
        assertTrue(counters().isEmpty())
    }

    private fun passiveFix(accuracy: Float) = PassiveFix(
        latitude = 31.230416,
        longitude = 121.473701,
        horizontalAccuracyMeters = accuracy,
        altitudeMeters = 10.0,
        provider = "gps",
        recordedAtMillis = now
    )

    private class NoopOperations : LocationAcquisitionOperations {
        override suspend fun enqueueAccepted(
            accepted: com.pim.app.location.quality.QualityAcceptedLocation,
            rawJson: String,
            source: String
        ) = Unit

        override suspend fun recordDropped(fix: RawLocationFix, reason: String) = Unit
        override fun scheduleSync() = Unit
    }

    /** 假被动源：只暴露处理器，不碰真实 LocationManager。 */
    private class FakePassiveSource(
        context: Context,
        settings: TrackingSettingsStore,
        logs: StructuredLogRepository
    ) : PassiveLocationSource(context, settings, logs) {
        override fun register(processor: PassiveLocationProcessor): PassiveRegistrationResult =
            PassiveRegistrationResult.Registered("passive", 0L)
    }
}
