package com.pim.app.mobile.diagnostics

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.data.AppDatabase
import com.pim.app.data.AppUsageEntity
import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileLocationPointEntity
import com.pim.app.data.MobileLogEntity
import com.pim.app.data.MobileLocationDroppedDiagnosticEntity
import com.pim.app.data.MobileLocationPolicyTransitionEntity
import com.pim.app.location.PolicyTransitionWriteFailure
import com.pim.app.data.MobileSyncBatchEntity
import com.pim.app.location.service.ForegroundLocationRuntimeState
import com.pim.app.schedule.ScheduleCacheFreshness
import com.pim.app.schedule.ScheduleCacheSnapshot
import com.pim.app.schedule.ScheduleRefreshErrorKind
import com.pim.app.data.MobileSyncStatus
import com.pim.app.data.MobileUsageEventEntity
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.settings.TrackingSettings
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.status.ConnectionProbeOutcome
import com.pim.app.status.ConnectionProbeResult
import com.pim.app.status.ConnectionProbeStage
import com.pim.app.status.ConnectionProbeStore
import com.pim.app.status.PermissionStatusSnapshot
import com.pim.app.status.ServerCapabilities
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch
import kotlinx.coroutines.test.runTest
import kotlinx.serialization.json.Json
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.zip.ZipFile

@RunWith(RobolectricTestRunner::class)
class DiagnosticExportRepositoryTest {
    private lateinit var context: Context
    private lateinit var db: AppDatabase
    private lateinit var dao: MobileDataDao
    private lateinit var settingsStore: TrackingSettingsStore
    private lateinit var structuredLogRepo: StructuredLogRepository
    private lateinit var probeStore: ConnectionProbeStore
    private lateinit var redactor: DiagnosticRedactor

    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = false }
    private val baseNow = 10_000_000L
    private val logNamePattern = Regex("""^logs/mobile-\d{4}-\d{2}-\d{2}\.jsonl$""")
    private val coreEntries = setOf(
        "manifest.json", "status.json", "settings.json",
        "database-counts.json", "sync-history.json",
        // 阶段一新增：丢弃明细（REQ-9 AC-9.1）与取证台账（REQ-1~REQ-4）。
        "dropped-locations.jsonl", "forensics.jsonl"
    )

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries().build()
        dao = db.mobileDataDao()
        val logPrefs = context.getSharedPreferences("test_log_prefs", Context.MODE_PRIVATE)
        logPrefs.edit().clear().apply()
        settingsStore = TrackingSettingsStore(logPrefs)
        settingsStore.write(TrackingSettings.defaults())
        structuredLogRepo = StructuredLogRepository(context, settingsStore) { baseNow }
        val probePrefs = context.getSharedPreferences("test_probe_prefs", Context.MODE_PRIVATE)
        probePrefs.edit().clear().apply()
        probeStore = ConnectionProbeStore(probePrefs, json)
        redactor = DiagnosticRedactor()
    }

    @After
    fun tearDown() {
        db.close()
        File(context.filesDir, "diagnostics/exports").deleteRecursively()
        File(context.filesDir, "logs").deleteRecursively()
    }

    private fun createRepo(
        nowMillis: Long = baseNow,
        availableBytes: Long = Long.MAX_VALUE,
        publish: ((File, File) -> Unit)? = null,
        permissionSnapshot: PermissionStatusSnapshot = defaultPermissionSnapshot(),
        serviceRunning: Boolean = true,
        dispatcher: CoroutineDispatcher = Dispatchers.IO,
        clearProbe: (() -> Boolean)? = null,
        structuredLogRepository: StructuredLogRepository = structuredLogRepo,
        deleteExportFile: ((File) -> Boolean)? = null,
        scheduleSnapshot: () -> ScheduleCacheSnapshot = { defaultScheduleSnapshot() },
        runtimeSnapshot: () -> ForegroundLocationRuntimeState = { ForegroundLocationRuntimeState() },
        policyTransitionWriteFailure: () -> PolicyTransitionWriteFailure = {
            PolicyTransitionWriteFailure.None
        }
    ): DiagnosticExportRepository {
        val actualPublish: (File, File) -> Unit = if (publish != null) publish else { tmp, final ->
            Files.move(tmp.toPath(), final.toPath())
            Unit
        }
        return DiagnosticExportRepository(
            context = context,
            db = db,
            structuredLogRepository = structuredLogRepository,
            trackingSettingsStore = settingsStore,
            redactor = redactor,
            connectionProbeStore = probeStore,
            permissionSnapshot = { permissionSnapshot },
            serviceRunning = { serviceRunning },
            dispatcher = dispatcher,
            clearProbe = clearProbe ?: { probeStore.clear() },
            nowMillis = { nowMillis },
            availableBytes = { availableBytes },
            publish = actualPublish,
            deleteExportFile = deleteExportFile ?: { it.delete() },
            scheduleSnapshot = scheduleSnapshot,
            runtimeSnapshot = runtimeSnapshot,
            policyTransitionWriteFailure = policyTransitionWriteFailure
        )
    }

    private fun defaultScheduleSnapshot() = ScheduleCacheSnapshot(
        serverIdentity = "",
        windows = emptyList(),
        freshness = ScheduleCacheFreshness.Missing,
        lastAttemptAtMillis = null,
        lastSuccessAtMillis = null,
        lastError = null,
        errorKind = null
    )

    private fun defaultPermissionSnapshot() = PermissionStatusSnapshot(
        notificationGranted = true,
        preciseLocationGranted = true,
        backgroundLocationGranted = true,
        usageAccessGranted = true,
        activityRecognitionGranted = true,
        batteryOptimizationGranted = true
    )

    private fun ZipFile.entryNameList(): List<String> =
        entries().asSequence().map { it.name }.toList()

    private fun ZipFile.entryNames(): Set<String> =
        entryNameList().toSet()

    private fun ZipFile.readEntry(name: String): String? {
        val entry = getEntry(name) ?: return null
        return getInputStream(entry).reader(Charsets.UTF_8).readText()
    }

    // --- Default export entry set ---

    @Test
    fun defaultExport_includesExpectedEntries_excludesLocations() = runTest {
        structuredLogRepo.info("op", "log-entry")
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 1.0, longitude = 2.0, recordedAtUtc = baseNow,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val names = zip.entryNames()
            val logNames = names.filter { it.startsWith("logs/") }.toSet()
            assertTrue(logNames.isNotEmpty())
            assertTrue(logNames.all { logNamePattern.matches(it) })
            assertTrue(logNames.all { DiagnosticExportRepository.isValidLogName(it) })
            assertEquals(coreEntries + logNames, names)
            assertFalse(names.contains("locations.jsonl"))

            val manifest = JSONObject(zip.readEntry("manifest.json")!!)
            assertEquals(1, manifest.getInt("formatVersion"))
            assertEquals(baseNow, manifest.getLong("createdAtUtc"))
            assertFalse(manifest.getBoolean("includeRecentLocations"))
            assertTrue(manifest.isNull("coordinateRangeStartUtc"))
            assertTrue(manifest.isNull("coordinateRangeEndUtc"))
            assertEquals(0, manifest.getInt("coordinateCount"))

            val entriesArr = manifest.getJSONArray("entries")
            val entriesList = (0 until entriesArr.length()).map { entriesArr.getString(it) }.toSet()
            assertEquals(names, entriesList)
        }
    }

    // --- include=true exports locations within 24h ---

    @Test
    fun exportWithLocations_includesPointsIn24hWindow() = runTest {
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 10.0, longitude = 20.0, recordedAtUtc = baseNow - 12 * 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 30.0, longitude = 40.0, recordedAtUtc = baseNow - 6 * 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 50.0, longitude = 60.0, recordedAtUtc = baseNow - 1 * 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = true)

        ZipFile(result.file).use { zip ->
            val locsRaw = zip.readEntry("locations.jsonl")
            assertNotNull(locsRaw)
            val lines = locsRaw!!.trim().lines()
            assertEquals(3, lines.size)

            val locs = lines.map { JSONObject(it) }
            assertEquals(10.0, locs[0].getDouble("latitude"), 0.0)
            assertEquals(baseNow - 12 * 3600_000L, locs[0].getLong("recordedAtUtc"))
            assertEquals(30.0, locs[1].getDouble("latitude"), 0.0)
            assertEquals(50.0, locs[2].getDouble("latitude"), 0.0)

            val manifest = JSONObject(zip.readEntry("manifest.json")!!)
            assertEquals(3, manifest.getInt("coordinateCount"))
            assertEquals(baseNow - 24 * 3600_000L, manifest.getLong("coordinateRangeStartUtc"))
            assertEquals(baseNow, manifest.getLong("coordinateRangeEndUtc"))
            assertTrue(manifest.getBoolean("includeRecentLocations"))
        }
    }

    @Test
    fun exportWithLocations_excludesOldAndFuturePoints() = runTest {
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 1.0, longitude = 2.0, recordedAtUtc = baseNow - 25 * 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 3.0, longitude = 4.0, recordedAtUtc = baseNow - 12 * 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 5.0, longitude = 6.0, recordedAtUtc = baseNow + 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = true)

        ZipFile(result.file).use { zip ->
            val locsRaw = zip.readEntry("locations.jsonl")!!
            val lines = locsRaw.trim().lines()
            assertEquals(1, lines.size)
            val loc = JSONObject(lines[0])
            assertEquals(3.0, loc.getDouble("latitude"), 0.0)

            val manifest = JSONObject(zip.readEntry("manifest.json")!!)
            assertEquals(1, manifest.getInt("coordinateCount"))
        }
    }

    @Test
    fun exportWithLocations_ordersAscending() = runTest {
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 30.0, longitude = 40.0, recordedAtUtc = baseNow - 12 * 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 10.0, longitude = 20.0, recordedAtUtc = baseNow - 18 * 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 50.0, longitude = 60.0, recordedAtUtc = baseNow - 1 * 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = true)

        ZipFile(result.file).use { zip ->
            val locsRaw = zip.readEntry("locations.jsonl")!!
            val lines = locsRaw.trim().lines()
            assertEquals(3, lines.size)
            val locs = lines.map { JSONObject(it) }
            assertTrue(locs[0].getLong("recordedAtUtc") < locs[1].getLong("recordedAtUtc"))
            assertTrue(locs[1].getLong("recordedAtUtc") < locs[2].getLong("recordedAtUtc"))
        }
    }

    // --- Sensitive data redacted ---

    @Test
    fun export_syncHistoryExcludesSensitiveFields() = runTest {
        dao.insertSyncBatch(
            MobileSyncBatchEntity(
                batchId = "secret-batch",
                entityType = "usage_events",
                rowCount = 10,
                startedAtUtc = 100L,
                finishedAtUtc = 200L,
                syncStatus = MobileSyncStatus.SYNCED,
                createdAtUtc = 300L,
                requestJson = "{\"token\":\"secret\"}",
                responseJson = "{\"data\":\"ok\"}",
                lastError = "auth failed"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val syncRaw = zip.readEntry("sync-history.json")!!
            val arr = JSONArray(syncRaw)
            assertEquals(1, arr.length())
            val row = arr.getJSONObject(0)
            assertEquals("usage_events", row.getString("entityType"))
            assertEquals(10, row.getInt("rowCount"))
            assertEquals(MobileSyncStatus.SYNCED, row.getString("syncStatus"))
            assertEquals(300L, row.getLong("createdAtUtc"))
            assertFalse(row.has("requestJson"))
            assertFalse(row.has("responseJson"))
            assertFalse(row.has("lastError"))
            assertFalse(row.has("batchId"))
        }
    }

    @Test
    fun export_redactsCredentialsInLogEntries() = runTest {
        structuredLogRepo.info("op", "token is eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.test.data")
        structuredLogRepo.info("op2", "password=supersecret")

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val logEntryNames = zip.entryNames().filter { it.startsWith("logs/") }
            assertTrue(logEntryNames.isNotEmpty())
            for (name in logEntryNames) {
                val content = zip.readEntry(name)!!
                val leaks = redactor.findCredentialLeaks(content)
                assertEquals(emptySet<String>(), leaks)
            }
        }
    }

    @Test
    fun export_allTextEntriesHaveNoCredentialLeaks() = runTest {
        dao.insertSyncBatch(
            MobileSyncBatchEntity(
                batchId = "b1", entityType = "t", rowCount = 1, createdAtUtc = baseNow,
                requestJson = "{\"Authorization\":\"Bearer secret\"}",
                responseJson = "{\"token\":\"abc\"}",
                lastError = "token expired"
            )
        )
        structuredLogRepo.info("op", "password=test")

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            for (name in zip.entryNames()) {
                val content = zip.readEntry(name)!!
                val leaks = redactor.findCredentialLeaks(content)
                assertEquals(emptySet<String>(), leaks)
            }
        }
    }

    // --- Settings whitelist ---

    @Test
    fun export_settingsOnlyContainsWhitelistedFields() = runTest {
        settingsStore.write(
            TrackingSettings.defaults().copy(
                profile = "test-profile",
                continuousCollectionEnabled = true,
                normalIntervalMillis = 60000L,
                verboseLoggingUntilUtcMillis = baseNow + 3600_000L
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val settings = JSONObject(zip.readEntry("settings.json")!!)
            assertEquals("test-profile", settings.getString("profile"))
            assertTrue(settings.getBoolean("continuousCollectionEnabled"))
            assertEquals(60000L, settings.getLong("normalIntervalMillis"))
            assertTrue(settings.getBoolean("verboseLoggingEnabled"))
            assertEquals(baseNow + 3600_000L, settings.getLong("verboseLoggingUntilUtcMillis"))
            assertTrue(settings.has("logRetentionDays"))
            assertTrue(settings.has("syncOnUnmeteredOnly"))
            assertTrue(settings.has("movementIntervalMillis"))
            assertTrue(settings.has("scheduleLowFrequencyIntervalMillis"))
            assertTrue(settings.has("scheduleRecoveryThresholdMeters"))
            assertTrue(settings.has("altitudeWaitTimeoutMillis"))
            assertFalse(settings.has("serverUrl"))
            assertFalse(settings.has("token"))
            assertFalse(settings.has("deviceId"))
            assertFalse(settings.has("batchId"))
            assertFalse(settings.has("lastError"))
        }
    }

    @Test
    fun export_settingsVerboseLoggingDisabledWhenExpired() = runTest {
        settingsStore.write(
            TrackingSettings.defaults().copy(
                verboseLoggingUntilUtcMillis = baseNow - 1000L
            )
        )

        val repo = createRepo(nowMillis = baseNow)
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val settings = JSONObject(zip.readEntry("settings.json")!!)
            assertFalse(settings.getBoolean("verboseLoggingEnabled"))
        }
    }

    // --- Status whitelist ---

    @Test
    fun export_statusOnlyContainsWhitelistedFields() = runTest {
        probeStore.save(
            ConnectionProbeResult(
                outcome = ConnectionProbeOutcome.Reachable,
                checkedAtUtcMillis = baseNow - 5000,
                serverIdentity = "should-not-appear",
                lastCompletedStage = ConnectionProbeStage.AuthenticatedStatus,
                latencyMillisByStage = emptyMap(),
                capabilities = ServerCapabilities(mobileItemResultsV1 = true, androidEmbedV1 = false),
                safeMessage = "should-not-appear-either"
            )
        )

        settingsStore.write(TrackingSettings.defaults().copy(continuousCollectionEnabled = true))
        val repo = createRepo(
            permissionSnapshot = PermissionStatusSnapshot(
                notificationGranted = true,
                preciseLocationGranted = false,
                backgroundLocationGranted = true,
                usageAccessGranted = false,
                activityRecognitionGranted = true,
                batteryOptimizationGranted = false
            ),
            serviceRunning = true
        )
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertTrue(status.getLong("generatedAtUtc") > 0)
            assertTrue(status.getBoolean("notificationGranted"))
            assertFalse(status.getBoolean("preciseLocationGranted"))
            assertTrue(status.getBoolean("backgroundLocationGranted"))
            assertFalse(status.getBoolean("usageAccessGranted"))
            assertTrue(status.getBoolean("activityRecognitionGranted"))
            assertFalse(status.getBoolean("batteryOptimizationGranted"))
            assertTrue(status.getBoolean("collectionIntentEnabled"))
            assertTrue(status.getBoolean("serviceRunning"))
            assertEquals("Reachable", status.getString("probeOutcome"))
            assertEquals(baseNow - 5000, status.getLong("probeCheckedAtUtc"))
            assertTrue(status.has("appUsagePendingCount"))
            assertTrue(status.has("usageEventsPendingCount"))
            assertTrue(status.has("aggregateRejectedCount"))
            assertFalse(status.has("safeMessage"))
            assertFalse(status.has("serverIdentity"))
            assertFalse(status.has("refreshToken"))
            assertFalse(status.has("password"))
            assertFalse(status.has("authorization"))
            assertFalse(status.has("token"))
            assertFalse(status.has("serverUrl"))
            assertFalse(status.has("logsPendingCount"))
        }
    }

    // --- Database-counts ---

    @Test
    fun export_databaseCountsExposesOnlyCounts() = runTest {
        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val dbCounts = JSONObject(zip.readEntry("database-counts.json")!!)
            assertTrue(dbCounts.has("appUsageRowCount"))
            assertTrue(dbCounts.has("mobileLogsRowCount"))
            assertTrue(dbCounts.has("mobileLocationPointsRowCount"))
            assertTrue(dbCounts.has("mobileForensicEventsRowCount"))
            assertEquals(11, dbCounts.length())
            assertFalse(dbCounts.has("rawJson"))
        }
    }

    // --- Low space / publish failure / success ---

    @Test
    fun export_lowSpace_throwsWithoutCreatingFiles() = runTest {
        structuredLogRepo.info("op", "msg")

        val repo = createRepo(availableBytes = 0L)
        try {
            repo.export(includeRecentLocations = false)
            fail("Expected insufficient storage failure")
        } catch (e: DiagnosticExportException) {
            assertEquals("INSUFFICIENT_STORAGE", e.code)
        }

        val exportDir = File(context.filesDir, "diagnostics/exports")
        if (exportDir.isDirectory) {
            val tmpFiles = exportDir.listFiles()?.filter { it.name.endsWith(".tmp") }.orEmpty()
            val finalFiles = exportDir.listFiles()?.filter { it.name.endsWith(".zip") }.orEmpty()
            assertEquals(0, tmpFiles.size)
            assertEquals(0, finalFiles.size)
        }
    }

    @Test
    fun export_publishFailure_removesTmp() = runTest {
        structuredLogRepo.info("op", "msg")

        var publishCalled = false
        val repo = createRepo(
            publish = { _, _ ->
                publishCalled = true
                throw IOException("simulated publish failure")
            }
        )
        try {
            repo.export(includeRecentLocations = false)
            fail("Expected publish failure")
        } catch (e: DiagnosticExportException) {
            assertEquals("EXPORT_FAILED", e.code)
        }

        assertTrue(publishCalled)
        val exportDir = File(context.filesDir, "diagnostics/exports")
        val tmpFiles = exportDir.listFiles()?.filter { it.name.endsWith(".tmp") }.orEmpty()
        val finalFiles = exportDir.listFiles()?.filter { it.name.endsWith(".zip") }.orEmpty()
        assertEquals(0, tmpFiles.size)
        assertEquals(0, finalFiles.size)
    }

    @Test
    fun export_success_createsFinalFileInTargetDir() = runTest {
        structuredLogRepo.info("op", "test")

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        val exportDir = File(context.filesDir, "diagnostics/exports")
        assertTrue(result.file.exists())
        assertEquals(exportDir, result.file.parentFile)
        assertTrue(result.file.name.startsWith("pim-diagnostics-"))
        assertTrue(result.file.name.endsWith(".zip"))
        assertEquals(0, result.coordinateCount)
        val tmpFiles = exportDir.listFiles()?.filter { it.name.endsWith(".tmp") }.orEmpty()
        assertEquals(0, tmpFiles.size)
    }

    @Test
    fun export_success_returnsCorrectCoordinateCount() = runTest {
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 1.0, longitude = 2.0, recordedAtUtc = baseNow - 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = true)
        assertEquals(1, result.coordinateCount)
    }

    // --- Clear diagnostics ---

    @Test
    fun clearDiagnostics_removesLogsAndDiagnosticRowsAndProbeAndExportFiles() = runTest {
        structuredLogRepo.info("op", "log-entry")
        dao.insertLogs(
            listOf(
                MobileLogEntity(
                    level = "INFO", message = "db", occurredAtUtc = baseNow,
                    source = "test", collectedAtUtc = baseNow, rawJson = "{}"
                )
            )
        )
        dao.insertDroppedLocationDiagnostic(
            MobileLocationDroppedDiagnosticEntity(
                recordedAtUtc = baseNow, provider = "gps", accuracyMeters = 10f,
                policyMode = "Active", reason = "test"
            )
        )
        dao.insertPolicyTransition(
            MobileLocationPolicyTransitionEntity(
                fromMode = "A", toMode = "B", reason = "test", occurredAtUtc = baseNow
            )
        )
        probeStore.save(
            ConnectionProbeResult(
                outcome = ConnectionProbeOutcome.Reachable,
                checkedAtUtcMillis = baseNow,
                lastCompletedStage = ConnectionProbeStage.Url,
                latencyMillisByStage = emptyMap(),
                capabilities = ServerCapabilities(true, false)
            )
        )

        val repo = createRepo()
        repo.export(includeRecentLocations = false)
        repo.clearDiagnostics()

        assertEquals(0, dao.diagnosticDatabaseCounts().mobileLogsRowCount)
        assertEquals(0, dao.diagnosticDatabaseCounts().mobileLocationDroppedDiagnosticsRowCount)
        assertEquals(0, dao.diagnosticDatabaseCounts().mobileLocationPolicyTransitionsRowCount)
        assertNull(probeStore.result.value)
        val exportDir = File(context.filesDir, "diagnostics/exports")
        val zips = exportDir.listFiles()?.filter { it.name.endsWith(".zip") }.orEmpty()
        assertEquals(0, zips.size)
    }

    @Test
    fun clearDiagnostics_preservesBusinessData() = runTest {
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 1.0, longitude = 2.0, recordedAtUtc = baseNow,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )
        dao.insertSyncBatch(
            MobileSyncBatchEntity(batchId = "b1", entityType = "t", rowCount = 1, createdAtUtc = baseNow)
        )
        dao.insertUsageEvents(
            listOf(
                MobileUsageEventEntity(
                    packageName = "com.test", eventType = 1, eventName = "test",
                    eventTimeUtc = baseNow, source = "test",
                    sourceWindowStartUtc = 0L, sourceWindowEndUtc = 0L,
                    collectedAtUtc = baseNow, rawJson = "{}"
                )
            )
        )

        val repo = createRepo()
        repo.clearDiagnostics()

        val counts = dao.diagnosticDatabaseCounts()
        assertTrue(counts.mobileLocationPointsRowCount > 0)
        assertTrue(counts.mobileSyncBatchesRowCount > 0)
        assertTrue(counts.mobileUsageEventsRowCount > 0)
    }

    @Test
    fun clearDiagnostics_preservesNonMatchingExportFiles() = runTest {
        val exportDir = File(context.filesDir, "diagnostics/exports")
        exportDir.mkdirs()
        File(exportDir, "other-file.txt").writeText("keep")
        val zipDir = File(exportDir, "pim-diagnostics-subdir")
        zipDir.mkdirs()

        val repo = createRepo()
        repo.clearDiagnostics()

        assertTrue(File(exportDir, "other-file.txt").exists())
        assertTrue(zipDir.exists())
    }

    @Test
    fun clearDiagnostics_structuredLogsCleared() = runTest {
        structuredLogRepo.info("op", "msg")
        assertTrue(structuredLogRepo.snapshotFiles().isNotEmpty())

        val repo = createRepo()
        repo.clearDiagnostics()

        assertTrue(structuredLogRepo.snapshotFiles().isEmpty())
    }

    @Test
    fun clearDiagnostics_probeClearFailure_continuesCleanup() = runTest {
        structuredLogRepo.info("op", "log-entry")

        val repo = createRepo()
        repo.export(includeRecentLocations = false)

        val failingRepo = createRepo(clearProbe = { false }, deleteExportFile = { it.delete() })
        try {
            failingRepo.clearDiagnostics()
            fail("Expected probe clear failure")
        } catch (e: DiagnosticExportException) {
            assertEquals("CLEAR_FAILED", e.code)
        }

        assertTrue(structuredLogRepo.snapshotFiles().isEmpty())
        val exportDir = File(context.filesDir, "diagnostics/exports")
        val zips = exportDir.listFiles()?.filter { it.name.endsWith(".zip") }.orEmpty()
        assertEquals(0, zips.size)
    }

    @Test
    fun clearDiagnostics_onlyDeletesMatchingZipAndTmpFiles() = runTest {
        val exportDir = File(context.filesDir, "diagnostics/exports")
        exportDir.mkdirs()
        File(exportDir, "pim-diagnostics-11111.zip").writeText("delete-me")
        File(exportDir, "pim-diagnostics-22222.zip.tmp").writeText("delete-me-too")
        File(exportDir, "other-file.txt").writeText("keep")
        val subDir = File(exportDir, "pim-diagnostics-subdir")
        subDir.mkdirs()

        val repo = createRepo()
        repo.clearDiagnostics()

        assertFalse(File(exportDir, "pim-diagnostics-11111.zip").exists())
        assertFalse(File(exportDir, "pim-diagnostics-22222.zip.tmp").exists())
        assertTrue(File(exportDir, "other-file.txt").exists())
        assertTrue(subDir.exists())
    }

    @Test
    fun clearDiagnostics_structuredLogFailureContinuesExportCleanupThenThrows() = runTest {
        val exportDir = File(context.filesDir, "diagnostics/exports")
        exportDir.mkdirs()
        File(exportDir, "pim-diagnostics-99999.zip").writeText("export\n")

        val failingLogRepo = StructuredLogRepository(
            context, settingsStore,
            nowMillis = { baseNow },
            deleteFile = { false }
        )
        failingLogRepo.info("op", "msg")
        assertTrue(failingLogRepo.snapshotFiles().isNotEmpty())

        val repo = createRepo(
            structuredLogRepository = failingLogRepo,
            clearProbe = { true },
            deleteExportFile = { it.delete() }
        )

        try {
            repo.clearDiagnostics()
            fail("Expected structured log cleanup failure")
        } catch (e: DiagnosticExportException) {
            assertEquals("CLEAR_FAILED", e.code)
        }

        assertTrue(failingLogRepo.snapshotFiles().isNotEmpty())
        assertFalse(File(exportDir, "pim-diagnostics-99999.zip").exists())
    }

    @Test
    fun clearDiagnostics_firstExportDeleteFailsStillDeletesLaterFilesThenThrows() = runTest {
        val exportDir = File(context.filesDir, "diagnostics/exports")
        exportDir.mkdirs()
        File(exportDir, "pim-diagnostics-11111.zip").writeText("first\n")
        File(exportDir, "pim-diagnostics-22222.zip").writeText("second\n")
        File(exportDir, "pim-diagnostics-33333.zip.tmp").writeText("third\n")
        val keepFile = File(exportDir, "other.txt")
        keepFile.writeText("keep\n")

        var callCount = 0
        val repo = createRepo(
            clearProbe = { true },
            deleteExportFile = { file ->
                callCount++
                if (file.name == "pim-diagnostics-11111.zip") {
                    false
                } else {
                    file.delete()
                }
            }
        )

        try {
            repo.clearDiagnostics()
            fail("Expected export delete failure")
        } catch (e: DiagnosticExportException) {
            assertEquals("CLEAR_FAILED", e.code)
        }

        assertEquals(3, callCount)
        assertTrue(File(exportDir, "pim-diagnostics-11111.zip").exists())
        assertFalse(File(exportDir, "pim-diagnostics-22222.zip").exists())
        assertFalse(File(exportDir, "pim-diagnostics-33333.zip.tmp").exists())
        assertTrue(keepFile.exists())
    }

    @Test
    fun clearDiagnostics_structuredLogClearThrowsContinuesCleanup() = runTest {
        val exportDir = File(context.filesDir, "diagnostics/exports")
        exportDir.mkdirs()
        File(exportDir, "pim-diagnostics-99999.zip").writeText("export\n")

        val throwingLogRepo = StructuredLogRepository(
            context, settingsStore,
            nowMillis = { baseNow },
            deleteFile = { throw IOException("simulated clear failure") }
        )
        throwingLogRepo.info("op", "msg")
        assertTrue(throwingLogRepo.snapshotFiles().isNotEmpty())

        val repo = createRepo(
            structuredLogRepository = throwingLogRepo,
            clearProbe = { true },
            deleteExportFile = { it.delete() }
        )

        try {
            repo.clearDiagnostics()
            fail("Expected CLEAR_FAILED from structured log clear exception")
        } catch (e: DiagnosticExportException) {
            assertEquals("CLEAR_FAILED", e.code)
        }

        assertTrue(throwingLogRepo.snapshotFiles().isNotEmpty())
        assertFalse(File(exportDir, "pim-diagnostics-99999.zip").exists())
    }

    @Test
    fun clearDiagnostics_exportFileDeleteThrowsContinuesWithRemaining() = runTest {
        val exportDir = File(context.filesDir, "diagnostics/exports")
        exportDir.mkdirs()
        File(exportDir, "pim-diagnostics-11111.zip").writeText("first\n")
        File(exportDir, "pim-diagnostics-22222.zip").writeText("second\n")
        File(exportDir, "pim-diagnostics-33333.zip.tmp").writeText("third\n")

        var callCount = 0
        val repo = createRepo(
            clearProbe = { true },
            deleteExportFile = { file ->
                callCount++
                if (file.name == "pim-diagnostics-11111.zip") {
                    throw IOException("simulated delete failure")
                } else {
                    file.delete()
                }
            }
        )

        try {
            repo.clearDiagnostics()
            fail("Expected CLEAR_FAILED from export file delete exception")
        } catch (e: DiagnosticExportException) {
            assertEquals("CLEAR_FAILED", e.code)
        }

        assertEquals(3, callCount)
        assertTrue(File(exportDir, "pim-diagnostics-11111.zip").exists())
        assertFalse(File(exportDir, "pim-diagnostics-22222.zip").exists())
        assertFalse(File(exportDir, "pim-diagnostics-33333.zip.tmp").exists())
    }

    // --- Entry names safe / non-duplicate ---

    @Test
    fun export_entryNamesAreSafe() = runTest {
        structuredLogRepo.info("op", "msg")

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            for (name in zip.entryNames()) {
                assertFalse(redactor.isUnsafeEntryName(name))
            }
        }
    }

    @Test
    fun export_entryNamesNoDuplicates() = runTest {
        structuredLogRepo.info("op", "msg")

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val nameList = zip.entryNameList()
            assertEquals(nameList.size, nameList.distinct().size)
        }
    }

    // --- Location fields ---

    @Test
    fun export_locationEntryHasExplicitThirteenFields() = runTest {
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 12.34, longitude = 56.78,
                altitudeMeters = 100.0, accuracyMeters = 10f,
                speedMetersPerSecond = 5f, bearingDegrees = 90f,
                provider = "gps", recordedAtUtc = baseNow - 3600_000L,
                source = "test", policyMode = "Active",
                scheduleLowFrequency = false, motionState = "STILL",
                syncStatus = "pending",
                collectedAtUtc = baseNow, rawJson = "{}"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = true)

        ZipFile(result.file).use { zip ->
            val loc = JSONObject(zip.readEntry("locations.jsonl")!!.trim())
            assertEquals(12.34, loc.getDouble("latitude"), 0.0)
            assertEquals(56.78, loc.getDouble("longitude"), 0.0)
            assertEquals(100.0, loc.getDouble("altitudeMeters"), 0.0)
            assertEquals(10.0, loc.getDouble("accuracyMeters"), 0.0)
            assertEquals(5.0, loc.getDouble("speedMetersPerSecond"), 0.0)
            assertEquals(90.0, loc.getDouble("bearingDegrees"), 0.0)
            assertEquals("gps", loc.getString("provider"))
            assertEquals("test", loc.getString("source"))
            assertEquals("Active", loc.getString("policyMode"))
            assertFalse(loc.getBoolean("scheduleLowFrequency"))
            assertEquals("STILL", loc.getString("motionState"))
            assertEquals("pending", loc.getString("syncStatus"))
            assertEquals(13, loc.length())
        }
    }

    @Test
    fun export_locationEntryFieldsAreRedacted() = runTest {
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 12.34, longitude = 56.78,
                provider = "Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.test.data",
                recordedAtUtc = baseNow - 3600_000L,
                source = "test", policyMode = "Active",
                scheduleLowFrequency = false, motionState = "STILL",
                syncStatus = "pending",
                collectedAtUtc = baseNow, rawJson = "{}"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = true)

        ZipFile(result.file).use { zip ->
            val locRaw = zip.readEntry("locations.jsonl")!!
            val loc = JSONObject(locRaw.trim())
            assertEquals(13, loc.length())
            assertEquals(12.34, loc.getDouble("latitude"), 0.0)
            assertEquals(56.78, loc.getDouble("longitude"), 0.0)
            assertFalse(
                locRaw.contains(
                    "Authorization: Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.test.data"
                )
            )
            assertFalse(locRaw.contains("eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.test.data"))
            val leaks = redactor.findCredentialLeaks(locRaw)
            assertEquals(emptySet<String>(), leaks)
        }
    }

    // --- Exception safety / dispatcher / cancellation ---

    @Test
    fun export_publishFailure_sanitizesTopLevelMessage_andPreservesCause() = runTest {
        structuredLogRepo.info("op", "msg")

        val repo = createRepo(
            publish = { _, _ ->
                throw IOException("Bearer eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.test.data")
            }
        )
        try {
            repo.export(includeRecentLocations = false)
            fail("Expected publish failure")
        } catch (e: DiagnosticExportException) {
            assertEquals("EXPORT_FAILED", e.code)
            assertEquals("Diagnostic export failed", e.message)
            assertNotNull(e.cause)
            assertTrue(e.cause!!.message!!.contains("Bearer"))
        }
    }

    @Test
    fun export_usesInjectedDispatcher() = runTest {
        structuredLogRepo.info("op", "msg")

        val publishThread = mutableListOf<String>()
        val testDispatcher = Executors.newSingleThreadExecutor { r ->
            Thread(r, "test-diag-pool").also { it.isDaemon = true }
        }.asCoroutineDispatcher()
        try {
            val repo = createRepo(
                dispatcher = testDispatcher,
                publish = { tmp, final ->
                    publishThread.add(Thread.currentThread().name)
                    Files.move(tmp.toPath(), final.toPath())
                }
            )
            repo.export(includeRecentLocations = false)
        } finally {
            testDispatcher.close()
        }

        assertEquals(1, publishThread.size)
        assertTrue(publishThread[0].contains("test-diag-pool"))
    }

    @Test
    fun export_cancellationException_rethrowsAndCleansTmp() = runTest {
        structuredLogRepo.info("op", "msg")

        val repo = createRepo(
            publish = { _, _ ->
                throw CancellationException("cancelled by test")
            }
        )
        try {
            repo.export(includeRecentLocations = false)
            fail("Expected cancellation")
        } catch (e: CancellationException) {
            assertEquals("cancelled by test", e.message)
        }

        val exportDir = File(context.filesDir, "diagnostics/exports")
        val tmpFiles = exportDir.listFiles()?.filter { it.name.endsWith(".tmp") }.orEmpty()
        assertEquals(0, tmpFiles.size)
    }

    // --- Pending counts ---

    @Test
    fun status_pendingCounts_fromDaos() = runTest {
        db.appUsageDao().insertAll(
            listOf(
                AppUsageEntity(
                    packageName = "com.test.a", startTime = baseNow, endTime = baseNow + 1,
                    durationMs = 1, lastTimeUsed = baseNow
                )
            )
        )
        dao.insertUsageEvents(
            listOf(
                MobileUsageEventEntity(
                    packageName = "com.test.e1", eventType = 1, eventName = "e1",
                    eventTimeUtc = baseNow, source = "test",
                    sourceWindowStartUtc = 0L, sourceWindowEndUtc = 0L,
                    collectedAtUtc = baseNow, rawJson = "{}"
                ),
                MobileUsageEventEntity(
                    packageName = "com.test.e2", eventType = 1, eventName = "e2",
                    eventTimeUtc = baseNow + 1, source = "test",
                    sourceWindowStartUtc = 0L, sourceWindowEndUtc = 0L,
                    collectedAtUtc = baseNow, rawJson = "{}"
                )
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertEquals(1, status.getInt("appUsagePendingCount"))
            assertEquals(2, status.getInt("usageEventsPendingCount"))
            assertFalse(status.has("logsPendingCount"))
        }
    }

    // --- Schedule and policy facts in status.json ---

    @Test
    fun status_containsScheduleAndPolicyFields() = runTest {
        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertTrue(status.has("scheduleFreshness"))
            assertTrue(status.has("scheduleLastSuccessAtUtc"))
            assertTrue(status.has("scheduleLastAttemptAtUtc"))
            assertTrue(status.has("scheduleLastError"))
            assertTrue(status.has("currentPolicyMode"))
            assertTrue(status.has("currentPolicyReason"))
            assertTrue(status.has("currentPolicyRequestIntervalMillis"))
            assertTrue(status.has("recentPolicyTransitions"))
            // WO-ANDROID-POLICY-TRANSITION-20260928 REQ-3：写入失败状态随导出给出。
            assertTrue(status.has("policyTransitionWriteFailureCount"))
            assertTrue(status.has("policyTransitionWriteLastFailureAtUtc"))
        }
    }

    @Test
    fun status_policyTransitionWriteFailureCarriesCountAndLastFailure() = runTest {
        val failedAt = 1_756_684_800_000L
        val repo = createRepo(
            policyTransitionWriteFailure = {
                PolicyTransitionWriteFailure(
                    consecutiveFailures = 2,
                    lastFailureAtUtcMillis = failedAt
                )
            }
        )
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertEquals(2, status.getInt("policyTransitionWriteFailureCount"))
            assertEquals(failedAt, status.getLong("policyTransitionWriteLastFailureAtUtc"))
        }
    }

    @Test
    fun status_policyTransitionWriteFailureComesFromTheRealStoreAfterAFailedWrite() = runTest {
        // AC-3.1 ②③：同一次运行里，真实 store 记一次失败 → 计数 = 1，导出字段跟着变。
        val prefs = context.getSharedPreferences("policy-transition-export", Context.MODE_PRIVATE)
            .also { it.edit().clear().commit() }
        val store = com.pim.app.location.PolicyTransitionWriteFailureStore(
            prefs,
            structuredLogRepo,
            { baseNow }
        )
        store.recordFailure(IllegalStateException("磁盘已满"))

        val repo = createRepo(policyTransitionWriteFailure = { store.state.value })
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertEquals(1, status.getInt("policyTransitionWriteFailureCount"))
            assertEquals(baseNow, status.getLong("policyTransitionWriteLastFailureAtUtc"))
        }
        // 成功一次后计数归零，导出字段同步归零（AC-3.2）。
        store.recordSuccess()
        val afterSuccess = createRepo(
            nowMillis = baseNow + 60_000L,
            policyTransitionWriteFailure = { store.state.value }
        ).export(includeRecentLocations = false)
        ZipFile(afterSuccess.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertEquals(0, status.getInt("policyTransitionWriteFailureCount"))
            assertTrue(status.isNull("policyTransitionWriteLastFailureAtUtc"))
        }
    }

    @Test
    fun status_policyTransitionWriteFailureDefaultsToZeroAndNull() = runTest {
        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertEquals(0, status.getInt("policyTransitionWriteFailureCount"))
            assertTrue(status.isNull("policyTransitionWriteLastFailureAtUtc"))
        }
    }

    @Test
    fun status_defaultNullValuesAreJsonNull() = runTest {
        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertEquals("Missing", status.getString("scheduleFreshness"))
            assertTrue(status.isNull("scheduleLastSuccessAtUtc"))
            assertTrue(status.isNull("scheduleLastAttemptAtUtc"))
            assertTrue(status.isNull("scheduleLastError"))
            assertEquals("Off", status.getString("currentPolicyMode"))
            assertTrue(status.isNull("currentPolicyReason"))
            assertTrue(status.isNull("currentPolicyRequestIntervalMillis"))
            val transitions = status.getJSONArray("recentPolicyTransitions")
            assertEquals(0, transitions.length())
        }
    }

    @Test
    fun status_scheduleErrorKindMapsToFixedChineseSummary() = runTest {
        for ((index, kind, expected) in listOf(
            Triple(1, ScheduleRefreshErrorKind.Authentication, "认证失败"),
            Triple(2, ScheduleRefreshErrorKind.Network, "网络错误"),
            Triple(3, ScheduleRefreshErrorKind.Server, "服务器错误"),
            Triple(4, ScheduleRefreshErrorKind.Cache, "缓存错误")
        )) {
            val repo = createRepo(
                nowMillis = baseNow + index * 1000L,
                scheduleSnapshot = {
                    ScheduleCacheSnapshot(
                        serverIdentity = "srv",
                        windows = emptyList(),
                        freshness = ScheduleCacheFreshness.Stale,
                        lastAttemptAtMillis = baseNow,
                        lastSuccessAtMillis = null,
                        lastError = "dummy error",
                        errorKind = kind
                    )
                }
            )
            val result = repo.export(includeRecentLocations = false)
            ZipFile(result.file).use { zip ->
                val status = JSONObject(zip.readEntry("status.json")!!)
                assertEquals(expected, status.getString("scheduleLastError"))
            }
        }
    }

    @Test
    fun status_scheduleNullErrorIsJsonNull() = runTest {
        val repo = createRepo(
            scheduleSnapshot = {
                ScheduleCacheSnapshot(
                    serverIdentity = "srv",
                    windows = emptyList(),
                    freshness = ScheduleCacheFreshness.Fresh,
                    lastAttemptAtMillis = baseNow,
                    lastSuccessAtMillis = baseNow,
                    lastError = null,
                    errorKind = null
                )
            }
        )
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertTrue(status.isNull("scheduleLastError"))
        }
    }

    @Test
    fun status_unknownErrorKindUsesGenericSummary() = runTest {
        val repo = createRepo(
            scheduleSnapshot = {
                ScheduleCacheSnapshot(
                    serverIdentity = "srv",
                    windows = emptyList(),
                    freshness = ScheduleCacheFreshness.Stale,
                    lastAttemptAtMillis = baseNow,
                    lastSuccessAtMillis = null,
                    lastError = "some raw error",
                    errorKind = null
                )
            }
        )
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertEquals("未知错误", status.getString("scheduleLastError"))
        }
    }

    @Test
    fun status_policyFieldsWithValues() = runTest {
        val repo = createRepo(
            runtimeSnapshot = {
                ForegroundLocationRuntimeState(
                    currentPolicyMode = "ScheduleLowFrequency",
                    currentPolicyReason = "当前日程时段，降低定位频率",
                    requestIntervalMillis = 300_000L
                )
            }
        )
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            assertEquals("ScheduleLowFrequency", status.getString("currentPolicyMode"))
            assertEquals("当前日程时段，降低定位频率", status.getString("currentPolicyReason"))
            assertEquals(300_000L, status.getLong("currentPolicyRequestIntervalMillis"))
        }
    }

    @Test
    fun status_recentPolicyTransitions_limitedTo20_noIdField() = runTest {
        for (i in 1..25) {
            dao.insertPolicyTransition(
                MobileLocationPolicyTransitionEntity(
                    fromMode = if (i % 2 == 0) "A" else null,
                    toMode = "B",
                    reason = "reason-$i",
                    occurredAtUtc = baseNow + i * 1000L
                )
            )
        }

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val status = JSONObject(zip.readEntry("status.json")!!)
            val arr = status.getJSONArray("recentPolicyTransitions")
            assertEquals(20, arr.length())
            assertEquals("reason-25", arr.getJSONObject(0).getString("reason"))
            assertTrue(arr.getJSONObject(0).isNull("fromMode"))
            assertEquals("A", arr.getJSONObject(1).getString("fromMode"))
            assertEquals("reason-6", arr.getJSONObject(19).getString("reason"))
            for (i in 0 until arr.length()) {
                val obj = arr.getJSONObject(i)
                assertFalse(obj.has("id"))
                assertTrue(obj.has("fromMode"))
                assertTrue(obj.has("toMode"))
                assertTrue(obj.has("reason"))
                assertTrue(obj.has("occurredAtUtc"))
            }
        }
    }

    @Test
    fun status_schedulePolicyFactsDontAddZipEntry() = runTest {
        val repo = createRepo()
        val result = repo.export(includeRecentLocations = false)

        ZipFile(result.file).use { zip ->
            val names = zip.entryNames()
            val logNames = names.filter { it.startsWith("logs/") }.toSet()
            assertEquals(coreEntries + logNames, names)
        }
    }

    // --- Concurrency: clear waits for in-flight export ---

    @Test
    fun clearDiagnostics_waitsForInFlightExport() = runTest {
        val publishEntered = CountDownLatch(1)
        val clearEnteredProbe = CountDownLatch(1)
        val publishComplete = CountDownLatch(1)

        val testDispatcher = Executors.newFixedThreadPool(2) { r ->
            Thread(r, "test-diag-pool").also { it.isDaemon = true }
        }.asCoroutineDispatcher()

        try {
            structuredLogRepo.info("op", "log-entry")
            val repo = createRepo(
                dispatcher = testDispatcher,
                publish = { tmp, final ->
                    publishEntered.countDown()
                    assertTrue(publishComplete.await(5, TimeUnit.SECONDS))
                    Files.move(tmp.toPath(), final.toPath())
                },
                clearProbe = {
                    clearEnteredProbe.countDown()
                    probeStore.clear()
                }
            )

            val exportJob = launch(testDispatcher) {
                repo.export(includeRecentLocations = false)
            }

            assertTrue(publishEntered.await(5, TimeUnit.SECONDS))

            val clearJob = launch(testDispatcher) {
                repo.clearDiagnostics()
            }

            assertFalse(clearEnteredProbe.await(500, TimeUnit.MILLISECONDS))

            publishComplete.countDown()

            exportJob.join()
            clearJob.join()

            assertEquals(0L, clearEnteredProbe.getCount())

            val exportDir = File(context.filesDir, "diagnostics/exports")
            val zips = exportDir.listFiles()?.filter { it.name.endsWith(".zip") }.orEmpty()
            assertEquals(0, zips.size)
            val tmps = exportDir.listFiles()?.filter { it.name.endsWith(".tmp") }.orEmpty()
            assertEquals(0, tmps.size)
        } finally {
            publishEntered.countDown()
            clearEnteredProbe.countDown()
            publishComplete.countDown()
            testDispatcher.close()
        }
    }

    // --- Manifest entries == actual ---

    @Test
    fun export_manifestEntriesReflectActualEntries() = runTest {
        dao.insertLocationPoint(
            MobileLocationPointEntity(
                latitude = 1.0, longitude = 2.0, recordedAtUtc = baseNow - 3600_000L,
                source = "test", collectedAtUtc = baseNow, rawJson = "{}"
            )
        )

        val repo = createRepo()
        val result = repo.export(includeRecentLocations = true)

        ZipFile(result.file).use { zip ->
            val actualNames = zip.entryNames()
            val manifest = JSONObject(zip.readEntry("manifest.json")!!)
            val entriesArr = manifest.getJSONArray("entries")
            val entriesList = (0 until entriesArr.length()).map { entriesArr.getString(it) }.toSet()
            assertTrue(entriesList.contains("locations.jsonl"))
            assertEquals(actualNames, entriesList)
        }
    }
}
