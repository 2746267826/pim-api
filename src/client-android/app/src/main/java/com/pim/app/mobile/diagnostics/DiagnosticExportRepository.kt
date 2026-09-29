package com.pim.app.mobile.diagnostics

import android.content.Context
import android.os.Build
import android.os.StatFs
import androidx.room.withTransaction
import com.pim.app.data.AppDatabase
import com.pim.app.location.PolicyTransitionWriteFailure
import com.pim.app.location.PolicyTransitionWriteFailureSource
import com.pim.app.location.service.ForegroundLocationRuntimeState
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.permissions.PermissionStatusRepository
import com.pim.app.schedule.ScheduleCacheFreshness
import com.pim.app.schedule.ScheduleCacheSnapshot
import com.pim.app.schedule.ScheduleRefreshErrorKind
import com.pim.app.schedule.ScheduleWindowRepository
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.status.ConnectionProbeStore
import com.pim.app.status.PermissionStatusSnapshot
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedOutputStream
import java.io.File
import java.nio.file.AtomicMoveNotSupportedException
import java.nio.file.Files
import java.nio.file.StandardCopyOption
import java.util.zip.ZipEntry
import java.util.zip.ZipFile
import java.util.zip.ZipOutputStream
import javax.inject.Inject
import javax.inject.Singleton
import kotlin.coroutines.cancellation.CancellationException

interface DiagnosticOperations {
    suspend fun export(includeRecentLocations: Boolean): DiagnosticExportResult
    suspend fun clearDiagnostics()
}

data class DiagnosticExportResult(
    val file: File,
    val coordinateCount: Int
)

class DiagnosticExportException(
    val code: String,
    override val message: String,
    override val cause: Throwable? = null
) : Exception(message, cause)

@Singleton
class DiagnosticExportRepository internal constructor(
    @ApplicationContext private val context: Context,
    private val db: AppDatabase,
    private val structuredLogRepository: StructuredLogRepository,
    private val trackingSettingsStore: TrackingSettingsStore,
    private val redactor: DiagnosticRedactor,
    private val connectionProbeStore: ConnectionProbeStore,
    private val permissionSnapshot: () -> PermissionStatusSnapshot,
    private val serviceRunning: () -> Boolean,
    private val dispatcher: CoroutineDispatcher = Dispatchers.IO,
    private val clearProbe: () -> Boolean = { connectionProbeStore.clear() },
    private val nowMillis: () -> Long = System::currentTimeMillis,
    private val availableBytes: () -> Long = {
        File(context.filesDir, "diagnostics/exports").mkdirs()
        StatFs(File(context.filesDir, "diagnostics/exports").path).availableBytes
    },
    private val publish: (tmp: File, final: File) -> Unit = { tmp, final ->
        try {
            Files.move(tmp.toPath(), final.toPath(), StandardCopyOption.ATOMIC_MOVE)
        } catch (e: AtomicMoveNotSupportedException) {
            Files.move(tmp.toPath(), final.toPath())
        } catch (e: UnsupportedOperationException) {
            Files.move(tmp.toPath(), final.toPath())
        }
    },
    private val deleteExportFile: (File) -> Boolean = { it.delete() },
    private val mutex: Mutex = Mutex(),
    private val scheduleSnapshot: () -> ScheduleCacheSnapshot = {
        ScheduleCacheSnapshot(
            serverIdentity = "",
            windows = emptyList(),
            freshness = ScheduleCacheFreshness.Missing,
            lastAttemptAtMillis = null,
            lastSuccessAtMillis = null,
            lastError = null,
            errorKind = null
        )
    },
    private val runtimeSnapshot: () -> ForegroundLocationRuntimeState = {
        ForegroundLocationRuntimeState()
    },
    /**
     * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-3：策略切换写入失败状态
     * （连续失败次数 + 最近失败时间），随诊断导出一起给出（AC-3.1 ③）。
     */
    private val policyTransitionWriteFailure: () -> PolicyTransitionWriteFailure = {
        PolicyTransitionWriteFailure.None
    }
) : DiagnosticOperations {
    @Inject constructor(
        @ApplicationContext context: Context,
        db: AppDatabase,
        structuredLogRepository: StructuredLogRepository,
        trackingSettingsStore: TrackingSettingsStore,
        redactor: DiagnosticRedactor,
        connectionProbeStore: ConnectionProbeStore,
        permissionStatusRepository: PermissionStatusRepository,
        scheduleWindowRepository: ScheduleWindowRepository,
        policyTransitionWriteFailureSource: PolicyTransitionWriteFailureSource
    ) : this(
        context = context,
        db = db,
        structuredLogRepository = structuredLogRepository,
        trackingSettingsStore = trackingSettingsStore,
        redactor = redactor,
        connectionProbeStore = connectionProbeStore,
        permissionSnapshot = { permissionStatusRepository.snapshot() },
        serviceRunning = { com.pim.app.location.service.ForegroundLocationService.isRunning() },
        scheduleSnapshot = { scheduleWindowRepository.snapshot.value },
        runtimeSnapshot = { com.pim.app.location.service.ForegroundLocationService.runtimeState.value },
        policyTransitionWriteFailure = { policyTransitionWriteFailureSource.state.value }
    )

    override suspend fun export(includeRecentLocations: Boolean): DiagnosticExportResult = withContext(dispatcher) {
        mutex.withLock {
            val dao = db.mobileDataDao()
            val exportNow = nowMillis()
            val exportDir = File(context.filesDir, "diagnostics/exports")
            exportDir.mkdirs()
            val tmpFile = File(exportDir, "pim-diagnostics-$exportNow.zip.tmp")
            val finalFile = File(exportDir, "pim-diagnostics-$exportNow.zip")

            try {
                val entries = mutableMapOf<String, String>()

                entries["status.json"] = buildStatus(exportNow, dao).toString()
                entries["settings.json"] = buildSettings().toString()
                entries["database-counts.json"] = buildDatabaseCounts(dao).toString()
                entries["sync-history.json"] = buildSyncHistory(dao).toString()
                // REQ-9 / AC-9.1：丢弃原因明细进诊断包（时刻 / 原因 / 准确度 / provider / 策略档）。
                // 明细不设条数上限（R4-P3），条数由 30 天时间清理决定，因此清理后导出包只会变小（AC-9.3）。
                entries["dropped-locations.jsonl"] = buildDroppedLocationDetails().toString()
                // REQ-1 ~ REQ-4：进程退出原因台账与存活心跳，供需求方在真机上核对死因。
                entries["forensics.jsonl"] = buildForensicEvents(exportNow).toString()

                val logEntryNames = mutableListOf<String>()
                val logSnapshots = structuredLogRepository.snapshotFiles()
                for (snapshot in logSnapshots) {
                    val entryName = "logs/${snapshot.fileName}"
                    require(isValidLogName(entryName)) { "Invalid log entry name: $entryName" }
                    require(!redactor.isUnsafeEntryName(entryName)) { "Unsafe entry name: $entryName" }
                    val redactedLines = snapshot.content.lines()
                        .filter { it.isNotBlank() }
                        .joinToString("\n") { line -> redactor.redactJsonLine(line) }
                    entries[entryName] = redactedLines
                    logEntryNames.add(entryName)
                }

                var coordinateCount = 0
                if (includeRecentLocations) {
                    val from = exportNow - 24L * 3600_000L
                    val to = exportNow
                    val locations = dao.diagnosticLocations(from, to)
                    coordinateCount = locations.size
                    val lines = locations.joinToString("\n") { loc -> locationToJson(loc).toString() }
                    entries["locations.jsonl"] = lines
                }

                entries["manifest.json"] = buildManifest(
                    includeRecentLocations = includeRecentLocations,
                    exportNow = exportNow,
                    coordinateCount = coordinateCount,
                    logEntryNames = logEntryNames
                ).toString()

                for ((name, content) in entries.toList()) {
                    val redacted = content.lines().joinToString("\n") { line ->
                        redactor.redactJsonLine(line)
                    }
                    entries[name] = redacted
                }

                val expectedNames = buildExpectedEntrySet(
                    validatedLogEntryNames = logEntryNames,
                    includeRecentLocations = includeRecentLocations
                )
                for ((name, content) in entries) {
                    require(!redactor.isUnsafeEntryName(name)) { "Unsafe entry name: $name" }
                    require(name in expectedNames) { "Unexpected entry: $name" }
                    val leaks = redactor.findCredentialLeaks(content)
                    require(leaks.isEmpty()) { "Credential leak in $name: $leaks" }
                }

                val estimatedBytes = entries.entries.sumOf { (name, content) ->
                    name.toByteArray(Charsets.UTF_8).size.toLong() +
                        content.toByteArray(Charsets.UTF_8).size.toLong()
                } + 1_048_576L

                val avail = availableBytes()
                if (estimatedBytes > avail) {
                    throw DiagnosticExportException(
                        "INSUFFICIENT_STORAGE",
                        "Not enough storage space for export"
                    )
                }

                BufferedOutputStream(tmpFile.outputStream()).use { bos ->
                    ZipOutputStream(bos).use { zos ->
                        for ((name, content) in entries) {
                            zos.putNextEntry(ZipEntry(name))
                            zos.write(content.toByteArray(Charsets.UTF_8))
                            zos.closeEntry()
                        }
                    }
                }

                ZipFile(tmpFile).use { zip ->
                    val actualNames = mutableSetOf<String>()
                    for (entry in zip.entries().asSequence()) {
                        val name = entry.name
                        require(!actualNames.contains(name)) { "Duplicate entry: $name" }
                        actualNames.add(name)
                        require(!redactor.isUnsafeEntryName(name)) { "Unsafe entry name after write: $name" }
                        require(name in expectedNames) { "Unexpected entry after write: $name" }
                        val content = zip.getInputStream(entry).bufferedReader(Charsets.UTF_8).readText()
                        val leaks = redactor.findCredentialLeaks(content)
                        require(leaks.isEmpty()) { "Credential leak after write in $name: $leaks" }
                    }
                    require(actualNames == expectedNames) {
                        "ZIP entry mismatch: expected $expectedNames, got $actualNames"
                    }
                }

                publish(tmpFile, finalFile)
                DiagnosticExportResult(finalFile, coordinateCount)
            } catch (e: CancellationException) {
                tmpFile.delete()
                throw e
            } catch (e: DiagnosticExportException) {
                tmpFile.delete()
                throw e
            } catch (e: Exception) {
                tmpFile.delete()
                throw DiagnosticExportException(
                    "EXPORT_FAILED",
                    "Diagnostic export failed",
                    e
                )
            }
        }
    }

    override suspend fun clearDiagnostics() = withContext(dispatcher) {
        mutex.withLock {
            var probeFailed = false
            try {
                if (!clearProbe()) {
                    probeFailed = true
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                probeFailed = true
            }

            var logClearFailed = false
            try {
                logClearFailed = !structuredLogRepository.clear()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                logClearFailed = true
            }

            var roomClearFailed = false
            try {
                val dao = db.mobileDataDao()
                db.withTransaction {
                    dao.deleteAllMobileLogs()
                    dao.deleteAllMobileLocationDroppedDiagnostics()
                    dao.deleteAllMobileLocationPolicyTransitions()
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                roomClearFailed = true
            }

            val exportDir = File(context.filesDir, "diagnostics/exports")
            var anyDeleteFailed = false
            if (exportDir.isDirectory) {
                exportDir.listFiles()?.forEach { file ->
                    if (file.isFile && EXPORT_FILE_PATTERN.matches(file.name)) {
                        try {
                            if (!deleteExportFile(file)) {
                                anyDeleteFailed = true
                            }
                        } catch (e: CancellationException) {
                            throw e
                        } catch (e: Exception) {
                            anyDeleteFailed = true
                        }
                    }
                }
            }
            if (probeFailed || logClearFailed || roomClearFailed || anyDeleteFailed) {
                throw DiagnosticExportException(
                    "CLEAR_FAILED",
                    "Failed to clear diagnostics"
                )
            }
        }
    }

    private fun buildManifest(
        includeRecentLocations: Boolean,
        exportNow: Long,
        coordinateCount: Int,
        logEntryNames: List<String>
    ): JSONObject {
        val pInfo = runCatching {
            context.packageManager.getPackageInfo(context.packageName, 0)
        }.getOrNull()
        val versionName = pInfo?.versionName ?: "unknown"
        val versionCode: Long = if (pInfo != null) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
                pInfo.longVersionCode
            } else {
                @Suppress("DEPRECATION")
                pInfo.versionCode.toLong()
            }
        } else 0L

        val entries = buildExpectedEntrySet(logEntryNames, includeRecentLocations)
            .toList().sorted()

        val result = JSONObject()
            .put("formatVersion", 1)
            .put("createdAtUtc", exportNow)
            .put("appVersionName", versionName)
            .put("appVersionCode", versionCode)
            .put("includeRecentLocations", includeRecentLocations)
            .put("coordinateCount", coordinateCount)

        if (includeRecentLocations) {
            val from = exportNow - 24L * 3600_000L
            val to = exportNow
            result.put("coordinateRangeStartUtc", from)
            result.put("coordinateRangeEndUtc", to)
        } else {
            result.put("coordinateRangeStartUtc", JSONObject.NULL)
            result.put("coordinateRangeEndUtc", JSONObject.NULL)
        }

        result.put("entries", JSONArray(entries))
        return result
    }

    companion object {
        private val LOG_NAME_PATTERN = Regex("""^logs/mobile-\d{4}-\d{2}-\d{2}\.jsonl$""")
        private val EXPORT_FILE_PATTERN = Regex("""^pim-diagnostics-\d+\.zip(?:\.tmp)?$""")
        private val CORE_ENTRIES = setOf(
            "manifest.json", "status.json", "settings.json",
            "database-counts.json", "sync-history.json",
            // REQ-9 / REQ-1~REQ-4：取证两件套随包导出（丢弃明细 + 死因台账）。
            "dropped-locations.jsonl", "forensics.jsonl"
        )

        private fun buildExpectedEntrySet(
            validatedLogEntryNames: List<String>,
            includeRecentLocations: Boolean
        ): Set<String> {
            val result = CORE_ENTRIES.toMutableSet()
            result.addAll(validatedLogEntryNames)
            if (includeRecentLocations) {
                result.add("locations.jsonl")
            }
            return result
        }

        internal fun isValidLogName(name: String): Boolean =
            LOG_NAME_PATTERN.matches(name)
    }

    private suspend fun buildStatus(
        exportNow: Long,
        dao: com.pim.app.data.MobileDataDao
    ): JSONObject {
        val snap = permissionSnapshot()

        val settings = trackingSettingsStore.read()

        val probeResult = connectionProbeStore.result.value

        val status = JSONObject()
            .put("generatedAtUtc", exportNow)
            .put("notificationGranted", snap.notificationGranted)
            .put("preciseLocationGranted", snap.preciseLocationGranted)
            .put("backgroundLocationGranted", snap.backgroundLocationGranted)
            .put("usageAccessGranted", snap.usageAccessGranted)
            .put("activityRecognitionGranted", snap.activityRecognitionGranted)
            .put("batteryOptimizationGranted", snap.batteryOptimizationGranted)
            .put("collectionIntentEnabled", settings.continuousCollectionEnabled)
            .put("serviceRunning", serviceRunning())
            .put("probeOutcome", probeResult?.outcome?.name ?: JSONObject.NULL)
            .put("probeCheckedAtUtc", probeResult?.checkedAtUtcMillis ?: JSONObject.NULL)

        status.put("appUsagePendingCount", db.appUsageDao().unsyncedCount().first())
        status.put("usageEventsPendingCount", dao.pendingUsageEventCount().first())
        status.put("usageSummariesPendingCount", dao.pendingUsageSummaryCount().first())
        status.put("appMetadataPendingCount", dao.pendingAppMetadataCount().first())
        status.put("locationPointsPendingCount", dao.pendingLocationPointCount().first())
        status.put("syncBatchesPendingCount", dao.pendingSyncBatchCount().first())
        status.put("deviceProfilePendingCount", dao.pendingDeviceProfileCount().first())
        status.put("aggregateRejectedCount", dao.aggregateRejectedCount().first())

        val sched = scheduleSnapshot()
        status.put("scheduleFreshness", sched.freshness.name)
        status.put("scheduleLastSuccessAtUtc", sched.lastSuccessAtMillis ?: JSONObject.NULL)
        status.put("scheduleLastAttemptAtUtc", sched.lastAttemptAtMillis ?: JSONObject.NULL)
        status.put("scheduleLastError", mapErrorKindToSummary(sched.lastError, sched.errorKind))

        val rt = runtimeSnapshot()
        status.put("currentPolicyMode", rt.currentPolicyMode)
        status.put("currentPolicyReason", rt.currentPolicyReason ?: JSONObject.NULL)
        status.put("currentPolicyRequestIntervalMillis", rt.requestIntervalMillis ?: JSONObject.NULL)

        // REQ-3：写入失败可见化——导出新增「连续失败次数 + 最近一次失败时间」（AC-3.1 ③）。
        val writeFailure = policyTransitionWriteFailure()
        status.put("policyTransitionWriteFailureCount", writeFailure.consecutiveFailures)
        status.put(
            "policyTransitionWriteLastFailureAtUtc",
            writeFailure.lastFailureAtUtcMillis ?: JSONObject.NULL
        )

        val transitions = dao.recentPolicyTransitions(limit = 20).first()
        val transitionsArr = JSONArray()
        for (t in transitions) {
            transitionsArr.put(
                JSONObject()
                    .put("fromMode", t.fromMode ?: JSONObject.NULL)
                    .put("toMode", t.toMode)
                    .put("reason", t.reason)
                    .put("occurredAtUtc", t.occurredAtUtc)
            )
        }
        status.put("recentPolicyTransitions", transitionsArr)

        return status
    }

    private fun mapErrorKindToSummary(
        lastError: String?,
        errorKind: ScheduleRefreshErrorKind?
    ): Any {
        if (lastError == null) return JSONObject.NULL
        return when (errorKind) {
            ScheduleRefreshErrorKind.Authentication -> "认证失败"
            ScheduleRefreshErrorKind.Network -> "网络错误"
            ScheduleRefreshErrorKind.Server -> "服务器错误"
            ScheduleRefreshErrorKind.Cache -> "缓存错误"
            null -> "未知错误"
        }
    }

    /**
     * 丢弃原因明细（REQ-9 / AC-9.1）：时刻 / 原因 / 准确度 / provider / 策略档。
     * 不设条数上限，条数由 30 天时间清理决定（AC-9.3）。
     */
    private suspend fun buildDroppedLocationDetails(): String {
        val rows = db.mobileDataDao().diagnosticDroppedDetailRows()
        return rows.joinToString("\n") { row ->
            JSONObject()
                .put("recordedAtUtc", row.recordedAtUtc)
                .put("reason", row.reason)
                .put("accuracyMeters", row.accuracyMeters?.toDouble() ?: JSONObject.NULL)
                .put("provider", row.provider ?: JSONObject.NULL)
                .put("policyMode", row.policyMode)
                .toString()
        }
    }

    /**
     * 进程退出原因台账与存活心跳（REQ-1 ~ REQ-4）。
     * 负载本身已在写入时脱敏（不含经纬度/令牌/账号），这里原样带出以便核对死因。
     */
    private suspend fun buildForensicEvents(exportNow: Long): String {
        val rows = db.forensicEventDao().eventsInRange(0L, exportNow)
        return rows.joinToString("\n") { row ->
            JSONObject()
                .put("eventType", row.eventType)
                .put("occurredAtUtc", row.occurredAtUtc)
                .put("clientItemKey", row.clientItemKey)
                .put("syncStatus", row.syncStatus)
                .put("payload", payloadOf(row.payloadJson))
                .toString()
        }
    }

    private fun payloadOf(json: String): Any = try {
        JSONObject(json)
    } catch (_: Exception) {
        json
    }

    private fun buildSettings(): JSONObject {
        val s = trackingSettingsStore.read()
        val verboseEnabled = s.verboseLoggingUntilUtcMillis != null &&
            s.verboseLoggingUntilUtcMillis > nowMillis()
        return JSONObject()
            .put("profile", s.profile)
            .put("continuousCollectionEnabled", s.continuousCollectionEnabled)
            .put("normalIntervalMillis", s.normalIntervalMillis)
            .put("scheduleLowFrequencyIntervalMillis", s.scheduleLowFrequencyIntervalMillis)
            .put("movementIntervalMillis", s.movementIntervalMillis)
            .put("scheduleRecoveryThresholdMeters", s.scheduleRecoveryThresholdMeters)
            .put("altitudeWaitTimeoutMillis", s.altitudeWaitTimeoutMillis)
            .put("syncOnUnmeteredOnly", s.syncOnUnmeteredOnly)
            .put("logRetentionDays", s.logRetentionDays)
            .put("verboseLoggingEnabled", verboseEnabled)
            .put(
                "verboseLoggingUntilUtcMillis",
                s.verboseLoggingUntilUtcMillis ?: JSONObject.NULL
            )
    }

    private suspend fun buildDatabaseCounts(
        dao: com.pim.app.data.MobileDataDao
    ): JSONObject {
        val c = dao.diagnosticDatabaseCounts()
        return JSONObject()
            .put("appUsageRowCount", c.appUsageRowCount)
            .put("mobileUsageEventsRowCount", c.mobileUsageEventsRowCount)
            .put("mobileUsageSummariesRowCount", c.mobileUsageSummariesRowCount)
            .put("mobileAppMetadataRowCount", c.mobileAppMetadataRowCount)
            .put("mobileLocationPointsRowCount", c.mobileLocationPointsRowCount)
            .put("mobileLocationDroppedDiagnosticsRowCount", c.mobileLocationDroppedDiagnosticsRowCount)
            .put("mobileLocationPolicyTransitionsRowCount", c.mobileLocationPolicyTransitionsRowCount)
            .put("mobileSyncBatchesRowCount", c.mobileSyncBatchesRowCount)
            .put("mobileLogsRowCount", c.mobileLogsRowCount)
            .put("mobileDeviceProfileRowCount", c.mobileDeviceProfileRowCount)
            .put("mobileForensicEventsRowCount", c.mobileForensicEventsRowCount)
    }

    private suspend fun buildSyncHistory(
        dao: com.pim.app.data.MobileDataDao
    ): JSONArray {
        val rows = dao.diagnosticSyncHistory(limit = 100)
        val arr = JSONArray()
        for (row in rows) {
            val obj = JSONObject()
                .put("entityType", row.entityType)
                .put("rowCount", row.rowCount)
                .put("startedAtUtc", row.startedAtUtc ?: JSONObject.NULL)
                .put("finishedAtUtc", row.finishedAtUtc ?: JSONObject.NULL)
                .put("syncStatus", row.syncStatus)
                .put("createdAtUtc", row.createdAtUtc)
            arr.put(obj)
        }
        return arr
    }

    private fun locationToJson(loc: com.pim.app.data.DiagnosticLocationRow): JSONObject {
        return JSONObject()
            .put("latitude", loc.latitude)
            .put("longitude", loc.longitude)
            .put("altitudeMeters", loc.altitudeMeters ?: JSONObject.NULL)
            .put("accuracyMeters", loc.accuracyMeters?.toDouble() ?: JSONObject.NULL)
            .put("speedMetersPerSecond", loc.speedMetersPerSecond?.toDouble() ?: JSONObject.NULL)
            .put("bearingDegrees", loc.bearingDegrees?.toDouble() ?: JSONObject.NULL)
            .put("provider", loc.provider ?: JSONObject.NULL)
            .put("recordedAtUtc", loc.recordedAtUtc)
            .put("source", loc.source)
            .put("policyMode", loc.policyMode)
            .put("scheduleLowFrequency", loc.scheduleLowFrequency)
            .put("motionState", loc.motionState ?: JSONObject.NULL)
            .put("syncStatus", loc.syncStatus)
    }
}
