package com.pim.app.data

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner

@RunWith(RobolectricTestRunner::class)
class MobileDataDaoDiagnosticTest {
    private lateinit var db: AppDatabase
    private lateinit var dao: MobileDataDao
    private lateinit var appUsageDao: AppUsageDao

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        dao = db.mobileDataDao()
        appUsageDao = db.appUsageDao()
    }

    @After
    fun tearDown() {
        db.close()
    }

    @Test
    fun diagnosticDatabaseCountsAppUsageCountMatches() = runTest {
        appUsageDao.insertAll(
            listOf(
                AppUsageEntity(
                    packageName = "a", startTime = 1, endTime = 2,
                    durationMs = 1, lastTimeUsed = 1
                ),
                AppUsageEntity(
                    packageName = "b", startTime = 1, endTime = 2,
                    durationMs = 1, lastTimeUsed = 1
                )
            )
        )
        val counts = dao.diagnosticDatabaseCounts()
        assertEquals(2, counts.appUsageRowCount)
    }

    @Test
    fun diagnosticDatabaseCountsAllMobileTableCounts() = runTest {
        dao.insertUsageEvents(listOf(event()))
        dao.insertUsageSummaries(listOf(summary()))
        dao.upsertAppMetadata(listOf(meta()))
        dao.insertLocationPoint(point())
        dao.insertDroppedLocationDiagnostic(droppedDiagnostic())
        dao.insertPolicyTransition(policyTransition())
        dao.insertSyncBatch(batch())
        dao.insertLogs(listOf(log()))
        dao.upsertDeviceProfile(profile())

        val counts = dao.diagnosticDatabaseCounts()
        assertEquals(1, counts.mobileUsageEventsRowCount)
        assertEquals(1, counts.mobileUsageSummariesRowCount)
        assertEquals(1, counts.mobileAppMetadataRowCount)
        assertEquals(1, counts.mobileLocationPointsRowCount)
        assertEquals(1, counts.mobileLocationDroppedDiagnosticsRowCount)
        assertEquals(1, counts.mobileLocationPolicyTransitionsRowCount)
        assertEquals(1, counts.mobileSyncBatchesRowCount)
        assertEquals(1, counts.mobileLogsRowCount)
        assertEquals(1, counts.mobileDeviceProfileRowCount)
    }

    @Test
    fun diagnosticSyncHistoryReturnsRowsOrderedDescAndLimited() = runTest {
        dao.insertSyncBatch(batch(batchId = "b1", entityType = "t1", createdAtUtc = 100L))
        dao.insertSyncBatch(batch(batchId = "b2", entityType = "t2", createdAtUtc = 200L))
        dao.insertSyncBatch(batch(batchId = "b3", entityType = "t3", createdAtUtc = 300L))

        val history = dao.diagnosticSyncHistory(limit = 2)
        assertEquals(2, history.size)
        assertEquals("t3", history[0].entityType)
        assertEquals("t2", history[1].entityType)
    }

    @Test
    fun diagnosticSyncHistoryExposesOnlySafeFields() = runTest {
        dao.insertSyncBatch(
            batch(
                batchId = "secret-batch",
                entityType = "usage_events",
                rowCount = 42,
                startedAtUtc = 100L,
                finishedAtUtc = 200L,
                syncStatus = MobileSyncStatus.SYNCED,
                createdAtUtc = 300L
            )
        )

        val history = dao.diagnosticSyncHistory(limit = 10)
        assertEquals(1, history.size)
        val row = history[0]
        assertEquals("usage_events", row.entityType)
        assertEquals(42, row.rowCount)
        assertEquals(100L, row.startedAtUtc)
        assertEquals(200L, row.finishedAtUtc)
        assertEquals(MobileSyncStatus.SYNCED, row.syncStatus)
        assertEquals(300L, row.createdAtUtc)
    }

    @Test
    fun diagnosticLocationsReturnsRowsWithinWindowInAscOrder() = runTest {
        dao.insertLocationPoint(point(recordedAtUtc = 100L, latitude = 1.0))
        dao.insertLocationPoint(point(recordedAtUtc = 200L, latitude = 2.0))
        dao.insertLocationPoint(point(recordedAtUtc = 300L, latitude = 3.0))
        dao.insertLocationPoint(point(recordedAtUtc = 900L, latitude = 9.0))

        val locations = dao.diagnosticLocations(from = 100L, to = 300L)
        assertEquals(3, locations.size)
        assertEquals(1.0, locations[0].latitude, 0.0)
        assertEquals(2.0, locations[1].latitude, 0.0)
        assertEquals(3.0, locations[2].latitude, 0.0)
    }

    @Test
    fun diagnosticLocationsExposesOnlySafeFields() = runTest {
        dao.insertLocationPoint(
            point(latitude = 12.34, longitude = 56.78, recordedAtUtc = 1000L)
        )
        val locations = dao.diagnosticLocations(from = 0L, to = 9999L)
        assertEquals(1, locations.size)
        val loc = locations[0]
        assertEquals(12.34, loc.latitude, 0.0)
        assertEquals(56.78, loc.longitude, 0.0)
    }

    @Test
    fun deleteAllMobileLogsClearsOnlyMobileLogs() = runTest {
        dao.insertLogs(listOf(log(), log()))
        dao.insertUsageEvents(listOf(event()))

        val deleted = dao.deleteAllMobileLogs()
        assertEquals(2, deleted)
        assertEquals(0, dao.diagnosticDatabaseCounts().mobileLogsRowCount)
        assertEquals(1, dao.diagnosticDatabaseCounts().mobileUsageEventsRowCount)
    }

    @Test
    fun deleteAllMobileLocationDroppedDiagnosticsClearsOnlyDroppedDiagnostics() = runTest {
        dao.insertDroppedLocationDiagnostic(droppedDiagnostic())
        dao.insertLocationPoint(point())

        val deleted = dao.deleteAllMobileLocationDroppedDiagnostics()
        assertEquals(1, deleted)
        assertEquals(0, dao.diagnosticDatabaseCounts().mobileLocationDroppedDiagnosticsRowCount)
        assertEquals(1, dao.diagnosticDatabaseCounts().mobileLocationPointsRowCount)
    }

    @Test
    fun deleteAllMobileLocationPolicyTransitionsClearsOnlyPolicyTransitions() = runTest {
        dao.insertPolicyTransition(policyTransition())
        dao.insertLocationPoint(point())

        val deleted = dao.deleteAllMobileLocationPolicyTransitions()
        assertEquals(1, deleted)
        assertEquals(0, dao.diagnosticDatabaseCounts().mobileLocationPolicyTransitionsRowCount)
        assertEquals(1, dao.diagnosticDatabaseCounts().mobileLocationPointsRowCount)
    }

    @Test
    fun diagnosticCleanupsPreserveBusinessTablesAndSyncBatches() = runTest {
        dao.insertUsageEvents(listOf(event()))
        dao.insertUsageSummaries(listOf(summary()))
        dao.upsertAppMetadata(listOf(meta()))
        dao.insertLocationPoint(point())
        dao.insertSyncBatch(batch())
        dao.upsertDeviceProfile(profile())

        dao.deleteAllMobileLogs()
        dao.deleteAllMobileLocationDroppedDiagnostics()
        dao.deleteAllMobileLocationPolicyTransitions()

        val counts = dao.diagnosticDatabaseCounts()
        assertEquals(1, counts.mobileUsageEventsRowCount)
        assertEquals(1, counts.mobileUsageSummariesRowCount)
        assertEquals(1, counts.mobileAppMetadataRowCount)
        assertEquals(1, counts.mobileLocationPointsRowCount)
        assertEquals(1, counts.mobileSyncBatchesRowCount)
        assertEquals(1, counts.mobileDeviceProfileRowCount)
    }

    private fun event() = MobileUsageEventEntity(
        packageName = "com.test",
        eventType = 1,
        eventName = "test",
        eventTimeUtc = 1000L,
        source = "test",
        sourceWindowStartUtc = 0L,
        sourceWindowEndUtc = 2000L,
        collectedAtUtc = 1000L,
        rawJson = "{}"
    )

    private fun summary() = MobileUsageSummaryEntity(
        packageName = "com.test",
        windowStartUtc = 1000L,
        windowEndUtc = 2000L,
        totalTimeForegroundMs = 500L,
        lastTimeUsedUtc = 1500L,
        firstTimeStampUtc = 1000L,
        lastTimeStampUtc = 1500L,
        source = "test",
        sourceWindowStartUtc = 0L,
        sourceWindowEndUtc = 0L,
        collectedAtUtc = 1000L,
        rawJson = "{}"
    )

    private fun meta() = MobileAppMetadataEntity(
        packageName = "com.test.${java.util.UUID.randomUUID()}",
        label = "Test",
        versionCode = 1,
        firstInstallTimeUtc = 0L,
        lastUpdateTimeUtc = 0L,
        isSystemApp = false,
        collectedAtUtc = 1000L,
        rawJson = "{}"
    )

    private fun point(
        recordedAtUtc: Long = 1000L,
        latitude: Double = 0.0,
        longitude: Double = 0.0
    ) = MobileLocationPointEntity(
        latitude = latitude,
        longitude = longitude,
        recordedAtUtc = recordedAtUtc,
        source = "test",
        collectedAtUtc = 1000L,
        rawJson = "{}"
    )

    private fun droppedDiagnostic() = MobileLocationDroppedDiagnosticEntity(
        recordedAtUtc = 1000L, provider = "gps", accuracyMeters = 10f,
        policyMode = "Active", reason = "test"
    )

    private fun policyTransition() = MobileLocationPolicyTransitionEntity(
        fromMode = "PowerSavingNormal", toMode = "Active",
        reason = "test", occurredAtUtc = 1000L
    )

    private fun policyTransitionAt(occurredAtUtc: Long, reason: String = "test") =
        MobileLocationPolicyTransitionEntity(
            fromMode = null, toMode = "PowerSavingNormal",
            reason = reason, occurredAtUtc = occurredAtUtc
        )

    // ── WO-ANDROID-POLICY-TRANSITION-20260928 REQ-4 / REQ-5 / REQ-6 ──────────

    @Test
    fun policyTransitionPurgeKeepsExactlyThirtyDaysOld() = runTest {
        val day = 24L * 60L * 60L * 1000L
        val now = 1_756_684_800_000L
        dao.insertPolicyTransition(policyTransitionAt(now - 31 * day, "31 天前"))
        dao.insertPolicyTransition(policyTransitionAt(now - 30 * day, "恰好 30 天"))
        dao.insertPolicyTransition(policyTransitionAt(now - 29 * day, "29 天前"))

        val deleted = dao.deletePolicyTransitionsOlderThan(now - 30 * day)

        assertEquals("严格小于判据：只有 31 天前那条被删", 1, deleted)
        assertEquals(2, dao.policyTransitionCount())
    }

    @Test
    fun latestPolicyTransitionPicksNewestAndBreaksTiesByIdDescending() = runTest {
        val tieMillis = 1_782_000_000_000L
        val first = dao.insertPolicyTransition(policyTransitionAt(tieMillis, "并列第一条"))
        val second = dao.insertPolicyTransition(policyTransitionAt(tieMillis, "并列第二条"))
        dao.insertPolicyTransition(policyTransitionAt(tieMillis - 1_000L, "更早"))

        val latest = dao.latestPolicyTransition().first()
        assertEquals(second, latest?.id)
        assertEquals("并列第二条", latest?.reason)
        assertTrue(second > first)
    }

    @Test
    fun policyTransitionsSinceFiltersByWindow() = runTest {
        val day = 24L * 60L * 60L * 1000L
        val now = 1_756_684_800_000L
        val since = now - 30 * day
        dao.insertPolicyTransition(policyTransitionAt(now - 31 * day, "窗口外"))
        dao.insertPolicyTransition(policyTransitionAt(now - 2 * day, "窗口内旧"))
        dao.insertPolicyTransition(policyTransitionAt(now - 1 * day, "窗口内新"))

        val rows = dao.policyTransitionsSince(since).first()
        assertEquals(listOf("窗口内新", "窗口内旧"), rows.map { it.reason })
    }

    private fun batch(
        batchId: String = java.util.UUID.randomUUID().toString(),
        entityType: String = "test",
        rowCount: Int = 1,
        startedAtUtc: Long? = null,
        finishedAtUtc: Long? = null,
        syncStatus: String = MobileSyncStatus.PENDING,
        createdAtUtc: Long = System.currentTimeMillis()
    ) = MobileSyncBatchEntity(
        batchId = batchId,
        entityType = entityType,
        rowCount = rowCount,
        startedAtUtc = startedAtUtc,
        finishedAtUtc = finishedAtUtc,
        syncStatus = syncStatus,
        createdAtUtc = createdAtUtc
    )

    private fun profile() = MobileDeviceProfileEntity(
        profileId = "default", deviceId = "test-device",
        manufacturer = "Test", brand = "Test", model = "Test",
        hardware = "Test", androidVersion = "14", sdkInt = 34,
        collectedAtUtc = 1000L, rawJson = "{}"
    )

    private fun log() = MobileLogEntity(
        level = "INFO", message = "test", occurredAtUtc = 1000L,
        source = "test", collectedAtUtc = 1000L, rawJson = "{}"
    )
}
