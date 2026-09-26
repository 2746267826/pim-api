package com.pim.app.location.sprint

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.data.AppDatabase
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.settings.TrackingSettingsStore
import kotlinx.coroutines.test.runTest
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * WO-ANDROID-GATE-20260926 REQ-8 / AC-8.1 / AC-8.2 / AC-8.3 / AC-5.4 / AC-5.3.1。
 *
 * 台账字段齐备、未冲刺原因非空、负载不含经纬度，
 * 且**关闭状态下写的是「跳过」而不是「已冲刺」**（AC-5.4 不得伪造已执行记录）。
 *
 * 走真实 Room（内存库）而不是假件：幂等键、按类型倒序读取、24 小时窗口这三条
 * 都是 SQL 语义，假件上「通过」说明不了问题。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class LocationSprintLedgerTest {

    private lateinit var db: AppDatabase
    private lateinit var ledger: LocationSprintLedger

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        val settings = TrackingSettingsStore(
            context.getSharedPreferences("sprint-ledger-test", Context.MODE_PRIVATE)
        )
        ledger = LocationSprintLedger(
            db.forensicEventDao(),
            StructuredLogRepository(context, settings) { 0L }
        )
    }

    @After
    fun tearDown() {
        db.close()
    }

    /** AC-8.1：已执行记录的字段齐备。 */
    @Test
    fun `AC-8_1 已执行记录字段齐备`() = runTest {
        val started = 1_700_000_000_000L
        ledger.recordExecuted(
            SprintWindowResult(
                startedAtUtcMillis = started,
                endedAtUtcMillis = started + 30_200L,
                sampleCount = 28,
                bestAccuracyMeters = 7.5f,
                acceptedCount = 12
            )
        )

        val row = rows().single()
        val payload = JSONObject(row.payloadJson)
        assertEquals(LocationSprintEventTypes.SPRINT, row.eventType)
        assertEquals(SprintOutcome.EXECUTED, payload.getString("outcome"))
        assertEquals(started, payload.getLong("sprintStartedAtUtcMillis"))
        assertEquals(started + 30_200L, payload.getLong("sprintEndedAtUtcMillis"))
        assertEquals(30_200L, payload.getLong("sprintDurationMillis"))
        assertEquals(28, payload.getInt("sprintSampleCount"))
        assertEquals(7.5, payload.getDouble("sprintBestAccuracyMeters"), 0.001)
        assertEquals(12, payload.getInt("sprintAcceptedCount"))
        assertTrue("AC-8.1：已执行记录里未冲刺原因必须为空", payload.isNull("sprintSkipReason"))
    }

    /** AC-8.1：无达标点时「最好精度」为 null，不谎报。 */
    @Test
    fun `AC-8_1 无达标点时最好精度为空`() = runTest {
        ledger.recordExecuted(
            SprintWindowResult(1_700_000_000_000L, 1_700_000_030_000L, 25, null, 0)
        )

        val payload = JSONObject(rows().single().payloadJson)
        assertTrue(payload.isNull("sprintBestAccuracyMeters"))
        assertEquals(0, payload.getInt("sprintAcceptedCount"))
    }

    /** AC-8.2 / AC-5.4：未冲刺记录必须带非空原因，且 outcome 是 skipped 而非 executed。 */
    @Test
    fun `AC-8_2 AC-5_4 未冲刺记录原因非空且不记为已执行`() = runTest {
        listOf(
            SprintSkipReasons.DISABLED,
            SprintSkipReasons.HIGH_SPEED,
            SprintSkipReasons.NOT_COLLECTING,
            SprintSkipReasons.PREREQUISITE_BLOCKED
        ).forEachIndexed { index, reason ->
            ledger.recordSkipped(1_700_000_000_000L + index * 1_000L, reason)
        }

        val all = rows()
        assertEquals(4, all.size)
        all.forEach { row ->
            val payload = JSONObject(row.payloadJson)
            assertEquals(
                "AC-5.4：跳过记录不得记为已执行",
                SprintOutcome.SKIPPED,
                payload.getString("outcome")
            )
            assertFalse("AC-8.2：未冲刺原因不得为空", payload.isNull("sprintSkipReason"))
            assertTrue(payload.getString("sprintSkipReason").isNotBlank())
        }
        assertEquals(
            "AC-8.2：关闭原因必须是明确的「开关关闭」编码",
            SprintSkipReasons.DISABLED,
            JSONObject(all.first { JSONObject(it.payloadJson).getString("sprintSkipReason") == SprintSkipReasons.DISABLED }.payloadJson)
                .getString("sprintSkipReason")
        )
    }

    /** AC-8.3：负载不得包含经纬度明文等敏感字段。 */
    @Test
    fun `AC-8_3 台账负载不含经纬度`() = runTest {
        val started = 1_700_000_000_000L
        ledger.recordExecuted(SprintWindowResult(started, started + 30_000L, 30, 6f, 30))
        ledger.recordSkipped(started + 60_000L, SprintSkipReasons.DISABLED)

        rows().forEach { row ->
            listOf("latitude", "longitude", "lat", "lon", "lng").forEach { field ->
                assertFalse(
                    "AC-8.3：冲刺台账负载不得含 $field 明文",
                    row.payloadJson.contains("\"$field\"")
                )
            }
        }
    }

    /** AC-5.3.1 / REQ-9：24 小时计数只数「已执行」，跳过记录不得计入。 */
    @Test
    fun `AC-5_3_1 已执行计数排除跳过记录与窗口外记录`() = runTest {
        val now = 1_700_000_000_000L
        val within24h = now - 60_000L
        val olderThan24h = now - 25L * 60L * 60L * 1_000L

        ledger.recordExecuted(SprintWindowResult(within24h, within24h + 30_000L, 30, 8f, 30))
        ledger.recordExecuted(SprintWindowResult(within24h + 45_000L, within24h + 75_000L, 30, 9f, 30))
        ledger.recordSkipped(within24h + 90_000L, SprintSkipReasons.DISABLED)
        ledger.recordExecuted(SprintWindowResult(olderThan24h, olderThan24h + 30_000L, 30, 8f, 30))

        val since = now - 24L * 60L * 60L * 1_000L
        assertEquals(
            "AC-5.3.1：只数已执行记录；跳过记录与 24 小时外的记录都不得计入",
            2,
            ledger.executedCountSince(since)
        )
    }

    /** 幂等：同一窗口重复写只落一条。 */
    @Test
    fun `同一窗口重复写入只落一条`() = runTest {
        val started = 1_700_000_000_000L
        val result = SprintWindowResult(started, started + 30_000L, 30, 8f, 30)

        ledger.recordExecuted(result)
        ledger.recordExecuted(result)

        assertEquals(1, rows().size)
    }

    /**
     * R5-3：最近一次窗口跨度必须走 SQL，不能被高速档的跳过记录挤出扫描范围。
     *
     * 原实现「读最近 64 条再过滤」在高速档（2.5 秒一条跳过，约 34560 条/天）下
     * 约 160 秒后就把已执行记录挤出扫描窗口，状态页那一行凭空消失。
     */
    @Test
    fun `R5_3 海量跳过记录不会让最近一次窗口跨度消失`() = runTest {
        val now = 1_700_000_000_000L
        val ledger = LocationSprintLedger(
            db.forensicEventDao(),
            StructuredLogRepository(
                ApplicationProvider.getApplicationContext(),
                TrackingSettingsStore(
                    ApplicationProvider.getApplicationContext<Context>()
                        .getSharedPreferences("sprint-ledger-flood", Context.MODE_PRIVATE)
                )
            ) { now }
        )
        // 一条已执行窗口（较早）
        ledger.recordExecuted(
            SprintWindowResult(now - 3_600_000L, now - 3_570_000L, 30, 8f, 30)
        )
        // 之后涌入远超 64 条的跳过记录（模拟高速档）
        repeat(500) { index ->
            ledger.recordSkipped(now - 3_500_000L + index * 2_500L, SprintSkipReasons.HIGH_SPEED)
        }

        assertEquals(
            "R5-3：已执行窗口跨度必须仍能读到（走 SQL，不受跳过记录数量影响）",
            30_000L,
            ledger.lastExecutedWindowDurationMillis()
        )
    }

    /** AC-9.2：台账存在性判定（状态页「暂无」空态的三条件之一）。 */
    @Test
    fun `AC-9_2 台账存在性判定`() = runTest {
        assertFalse(ledger.hasAnySprintLedgerSince(0L))

        ledger.recordSkipped(1_700_000_000_000L, SprintSkipReasons.DISABLED)
        assertTrue(ledger.hasAnySprintLedgerSince(1_699_000_000_000L))
        assertFalse(
            "窗口外的记录不得算作有数据",
            ledger.hasAnySprintLedgerSince(1_700_000_001_000L)
        )
    }

    /** AC-9.1：24 小时计数走 SQL，不受「读最近 N 条」上限影响。 */
    @Test
    fun `AC-9_1 大量跳过记录不会把已执行记录挤出计数`() = runTest {
        val now = 1_700_000_000_000L
        val ledger = LocationSprintLedger(
            db.forensicEventDao(),
            StructuredLogRepository(
                ApplicationProvider.getApplicationContext(),
                TrackingSettingsStore(
                    ApplicationProvider.getApplicationContext<Context>()
                        .getSharedPreferences("sprint-ledger-bulk", Context.MODE_PRIVATE)
                )
            ) { now }
        )
        // 一条早已执行的记录（窗口内、时间最早）
        ledger.recordExecuted(SprintWindowResult(now - 3_600_000L, now - 3_570_000L, 30, 8f, 30))
        // 之后涌入大量跳过记录（模拟高速档 2.5 秒一拍的「跳过」）
        repeat(5_000) { index ->
            ledger.recordSkipped(now - 3_500_000L + index, SprintSkipReasons.HIGH_SPEED)
        }

        assertEquals(
            "AC-9.1：已执行计数必须走 SQL 全窗口统计，不能被海量跳过记录挤掉",
            1,
            ledger.executedCountSince(now - 24L * 60L * 60L * 1_000L)
        )
    }

    /** AC-8.2：跳过原因编码有中文文案。 */
    @Test
    fun `跳过原因与结果都有中文文案`() {
        SprintSkipReasons.ALL.forEach { reason ->
            assertTrue(
                "跳过原因 $reason 必须有中文文案",
                SprintSkipReasons.label(reason).isNotBlank()
            )
        }
        assertEquals("已冲刺", SprintOutcome.label(SprintOutcome.EXECUTED))
        assertEquals("已跳过", SprintOutcome.label(SprintOutcome.SKIPPED))
        assertEquals("未知结果", SprintOutcome.label("some-unknown"))
    }

    /** AC-8.4：事件类型三处登记一致（设备端 / Pim.Core / MobileForensicIngestService）。 */
    @Test
    fun `AC-8_4 冲刺事件类型取值与单词表登记一致`() {
        assertEquals("location-sprint", LocationSprintEventTypes.SPRINT)
        assertEquals(setOf("location-sprint"), LocationSprintEventTypes.ALL)
        assertEquals(setOf("passive-location-counter"), PassiveLocationEventTypes.ALL)
    }

    private suspend fun rows() =
        db.forensicEventDao().recentByType(LocationSprintEventTypes.SPRINT, 100)
}
