package com.pim.app.forensics

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.data.AppDatabase
import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileLocationDroppedDiagnosticEntity
import com.pim.app.data.MobileLocationPolicyTransitionEntity
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.settings.TrackingSettingsStore
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * REQ-6 / REQ-9 的本地保留策略（AC-6.2 / AC-9.3）：两条取证通道都不设条数上限，
 * 只按 30 天时间清理兜底。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class ForensicRetentionTest {

    private lateinit var db: AppDatabase
    private lateinit var mobileDao: MobileDataDao
    private lateinit var ledger: ForensicLedger
    private lateinit var retention: ForensicRetention

    private val day = 24L * 60L * 60L * 1000L
    private val now = 1_756_684_800_000L

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        mobileDao = db.mobileDataDao()
        val logs = StructuredLogRepository(
            context,
            TrackingSettingsStore(context.getSharedPreferences("retention-test", Context.MODE_PRIVATE))
        ) { now }
        ledger = ForensicLedger(db.forensicEventDao(), logs)
        retention = ForensicRetention(db.forensicEventDao(), mobileDao, logs)
    }

    @After
    fun tearDown() {
        db.close()
    }

    private suspend fun dropped(reason: String, recordedAtUtc: Long) {
        mobileDao.insertDroppedLocationDiagnostic(
            MobileLocationDroppedDiagnosticEntity(
                recordedAtUtc = recordedAtUtc,
                provider = "gps",
                accuracyMeters = 55f,
                policyMode = "PowerSavingNormal",
                reason = reason
            )
        )
    }

    @Test
    fun `AC-9_3 dropped detail older than thirty days is removed and the rest is kept`() = runTest {
        dropped("horizontal-accuracy-too-low", now - 31 * day)
        dropped("horizontal-accuracy-too-low", now - 29 * day)
        dropped("missing-horizontal-accuracy", now - 1 * day)
        ledger.recordHeartbeat(now - 31 * day, "{}")

        val removed = retention.purgeExpired(now)

        // 只有"超过 30 天"的那些会被移除：1 条过期丢弃明细 + 1 条过期心搏。
        assertEquals(2, removed)
        val remaining = mobileDao.diagnosticDroppedDetailRows()
        assertEquals(2, remaining.size)
        assertEquals(2, mobileDao.droppedDiagnosticCount())
    }

    @Test
    fun `AC-9_3 purging the detail does not make the export fail`() = runTest {
        dropped("horizontal-accuracy-too-low", now - 40 * day)

        retention.purgeExpired(now)

        // 清理后导出取数仍然可用，且明细为空（不是异常、也不是残留）。
        assertEquals(0, mobileDao.diagnosticDroppedDetailRows().size)
        assertEquals(0, mobileDao.droppedDiagnosticCount())
    }

    @Test
    fun `AC-6_2 unuploaded entries inside the window survive the purge`() = runTest {
        ledger.recordHeartbeat(now - 29 * day, "{}")
        ledger.recordHeartbeat(now - 1 * day, "{}")

        retention.purgeExpired(now)

        // 30 天内的条目（不论是否已上传）都不因清理丢失，待上传计数与条数一致。
        assertEquals(2, ledger.totalCount())
        assertEquals(2, ledger.pendingCount())
    }

    @Test
    fun `retention window is the confirmed thirty days`() {
        assertEquals(30L * day, retention.windowMillis)
    }

    @Test
    fun `there is no row cap - a large backlog is kept in full inside the window`() = runTest {
        // R4-P3：本地不设条数上限。写入远超任何"合理上限"的条数，30 天内必须一条不少。
        repeat(2_000) { index ->
            ledger.recordHeartbeat(now - 1 * day + index * 1_000L, "{\"n\":$index}")
        }

        retention.purgeExpired(now)

        assertEquals(2_000, ledger.totalCount())
    }

    // ── WO-ANDROID-POLICY-TRANSITION-20260928 REQ-6 ───────────────────────────

    private suspend fun transition(name: String, occurredAtUtc: Long) {
        mobileDao.insertPolicyTransition(
            MobileLocationPolicyTransitionEntity(
                fromMode = null,
                toMode = "PowerSavingNormal",
                reason = name,
                occurredAtUtc = occurredAtUtc
            )
        )
    }

    @Test
    fun `REQ-6 策略切换历史按三十天窗口清理且边界沿用严格小于`() = runTest {
        // 31 天前 → 删；恰好 30 天 → 保留；29 天前 → 保留（AC-6.1 / AC-6.2）。
        transition("31 天前", now - 31 * day)
        transition("恰好 30 天", now - 30 * day)
        transition("29 天前", now - 29 * day)
        val before = mobileDao.policyTransitionCount()

        val removed = retention.purgeExpired(now)

        assertEquals(3, before)
        assertEquals("恰好 30 天的记录必须保留（严格小于判据）", 2, mobileDao.policyTransitionCount())
        assertEquals(1, removed)
    }

    @Test
    fun `REQ-6 清理策略切换历史不影响其它表`() = runTest {
        transition("31 天前", now - 31 * day)
        dropped("horizontal-accuracy-too-low", now - 1 * day)
        ledger.recordHeartbeat(now - 1 * day, "{}")
        mobileDao.insertLocationPoint(
            com.pim.app.data.MobileLocationPointEntity(
                latitude = 1.0,
                longitude = 2.0,
                accuracyMeters = 5f,
                provider = "gps",
                recordedAtUtc = now - 1 * day,
                source = "auto",
                collectedAtUtc = now - 1 * day,
                rawJson = "{}",
                syncStatus = com.pim.app.data.MobileSyncStatus.PENDING,
                createdAtUtc = now - 1 * day
            )
        )
        val locationPointsBefore = mobileDao.diagnosticDatabaseCounts().mobileLocationPointsRowCount
        val droppedBefore = mobileDao.droppedDiagnosticCount()
        val heartbeatsBefore = ledger.totalCount()

        retention.purgeExpired(now)

        assertEquals(locationPointsBefore, mobileDao.diagnosticDatabaseCounts().mobileLocationPointsRowCount)
        assertEquals(droppedBefore, mobileDao.droppedDiagnosticCount())
        assertEquals(heartbeatsBefore, ledger.totalCount())
    }
}
