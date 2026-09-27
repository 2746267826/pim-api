package com.pim.app.mobile.sync

import android.content.Context
import android.os.Build
import android.provider.Settings
import com.pim.app.data.AppDatabase
import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileLocationPointEntity
import com.pim.app.data.MobileSyncStatus
import com.pim.core.models.MobileLocationPointRequest
import com.pim.core.network.ApiService
import dagger.hilt.android.qualifiers.ApplicationContext
import java.security.MessageDigest
import java.time.Instant
import java.util.Locale
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException

data class LocationUploadBatchResult(
    val syncedIds: List<Long>,
    val failedIds: List<Long>,
    val errorMessage: String?,
    val retryableFailedIds: List<Long> = emptyList()
)

data class LocationUploadStatusUpdates(
    val syncedIds: List<Long>,
    val failedIds: List<Long>,
    val failedReason: String?,
    val shouldRetry: Boolean,
    val perItemErrors: Map<Long, String> = emptyMap(),
    val retryableFailedIds: List<Long> = emptyList()
)

object LocationUploadPlanner {
    fun planStatusUpdates(result: LocationUploadBatchResult): LocationUploadStatusUpdates {
        return LocationUploadStatusUpdates(
            syncedIds = result.syncedIds,
            failedIds = result.failedIds,
            failedReason = result.errorMessage,
            shouldRetry = result.retryableFailedIds.isNotEmpty(),
            retryableFailedIds = result.retryableFailedIds
        )
    }
}

@Singleton
class LocationUploadCoordinator @Inject constructor(
    @ApplicationContext private val context: Context,
    private val database: AppDatabase,
    private val api: ApiService
) {
    private val dao: MobileDataDao = database.mobileDataDao()

    suspend fun uploadPending(limit: Int = DEFAULT_LIMIT): LocationUploadStatusUpdates {
        val rows = pendingRows(limit)
        if (rows.isEmpty()) {
            return LocationUploadPlanner.planStatusUpdates(
                LocationUploadBatchResult(emptyList(), emptyList(), null)
            )
        }

        val synced = mutableListOf<Long>()
        val retryableFailed = mutableListOf<Long>()
        val permanentFailed = mutableListOf<Long>()
        val perItemErrors = linkedMapOf<Long, String>()
        var lastError: String? = null
        val deviceId = deviceId()

        for (row in rows) {
            val request = row.toRequest(deviceId)
            if (request == null) {
                permanentFailed += row.id
                perItemErrors[row.id] = "missing-horizontal-accuracy"
                lastError = lastError ?: "missing-horizontal-accuracy"
                continue
            }

            try {
                val response = api.uploadMobileLocation(request)
                if (response.code == 0 && response.data != null) {
                    synced += row.id
                } else {
                    val msg = response.message.ifBlank { "location upload failed" }
                    permanentFailed += row.id
                    perItemErrors[row.id] = msg
                    lastError = lastError ?: msg
                }
            } catch (ex: Exception) {
                if (ex is CancellationException) throw ex
                val outcome = MobileSyncErrorClassifier.classify(ex)
                when (outcome) {
                    MobileSyncOutcome.RETRY -> {
                        retryableFailed += row.id
                        val msg = ex.message ?: ex::class.java.simpleName
                        perItemErrors[row.id] = msg
                        lastError = lastError ?: msg
                    }
                    MobileSyncOutcome.BLOCKED -> {
                        permanentFailed += row.id
                        val msg = ex.message ?: ex::class.java.simpleName
                        perItemErrors[row.id] = msg
                        lastError = lastError ?: msg
                    }
                    else -> {
                        permanentFailed += row.id
                        val msg = ex.message ?: ex::class.java.simpleName
                        perItemErrors[row.id] = msg
                        lastError = lastError ?: msg
                    }
                }
            }
        }

        val allFailed = retryableFailed + permanentFailed
        val updates = LocationUploadPlanner.planStatusUpdates(
            LocationUploadBatchResult(synced, allFailed, lastError, retryableFailed)
        )
        val fullUpdates = updates.copy(perItemErrors = perItemErrors, retryableFailedIds = retryableFailed)
        applyStatusUpdates(fullUpdates)
        return fullUpdates
    }

    private suspend fun pendingRows(limit: Int): List<MobileLocationPointEntity> {
        // WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 / AC-15.2：客户端只做精度过滤。
        // 这里原本对 pendingRows 调 Douglas-Peucker（ε = 8.0 米，
        // TrajectoryCompressor.DOUGLAS_EPSILON_METERS）抽稀，被抽掉的点既不进上传、
        // 也不留任何记录 —— 属 AC-15.3 禁止的静默丢弃路径。抽稀已整体取消：
        // 达标的点逐条进入上传队列，滤波与舍弃交给服务端（A9 / A11）。
        //
        // 上传口径不变（本工单范围外）：仍是逐条 HTTP 请求、每次同步最多取 DEFAULT_LIMIT 条。
        val pending = dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, limit)
        if (pending.size >= limit) return pending

        val failed = dao.getLocationPointsBySyncStatus(MobileSyncStatus.FAILED, limit - pending.size)
        return pending + failed
    }

    private suspend fun applyStatusUpdates(updates: LocationUploadStatusUpdates) {
        applyLocationStatusUpdates(dao, updates)
    }

    private fun MobileLocationPointEntity.toRequest(deviceId: String): MobileLocationPointRequest? {
        val accuracy = accuracyMeters ?: return null
        return MobileLocationPointRequest(
            deviceId = deviceId,
            recordedAtUtc = Instant.ofEpochMilli(recordedAtUtc).toString(),
            latitude = latitude,
            longitude = longitude,
            horizontalAccuracyMeters = accuracy.toDouble(),
            provider = provider ?: "unknown",
            sourceKind = source,
            altitudeMeters = altitudeMeters,
            speedMetersPerSecond = speedMetersPerSecond?.toDouble(),
            bearingDegrees = bearingDegrees?.toDouble(),
            isAutoSubmitted = source != "manual",
            rawJson = rawJson
        )
    }

    private fun deviceId(): String {
        val androidId = Settings.Secure.getString(context.contentResolver, Settings.Secure.ANDROID_ID)
        val seed = androidId ?: Build.FINGERPRINT ?: "android-device"
        return "android-${sha256(seed).take(16)}"
    }

    private fun sha256(value: String): String {
        val bytes = MessageDigest.getInstance("SHA-256").digest(value.toByteArray())
        return bytes.joinToString("") { "%02x".format(Locale.US, it) }
    }

    private companion object {
        const val DEFAULT_LIMIT = 100
    }
}

internal fun LocationUploadStatusUpdates.retryableFirstError(): String? {
    return retryableFailedIds.firstOrNull()?.let { perItemErrors[it] }
}

internal suspend fun applyLocationStatusUpdates(
    dao: MobileDataDao,
    updates: LocationUploadStatusUpdates
) {
    if (updates.syncedIds.isNotEmpty()) {
        dao.deleteLocationPointByIds(updates.syncedIds)
    }
    val retryableSet = updates.retryableFailedIds.toSet()
    val permanentIds = updates.failedIds.filter { it !in retryableSet && it !in updates.syncedIds.toSet() }
    val retryableIds = updates.failedIds.filter { it in retryableSet && it !in updates.syncedIds.toSet() }
    if (permanentIds.isNotEmpty()) {
        permanentIds.forEach { id ->
            dao.updateLocationPointSyncStatus(
                ids = listOf(id),
                syncStatus = MobileSyncStatus.REJECTED,
                lastError = updates.perItemErrors[id] ?: updates.failedReason ?: "permanent-failure"
            )
        }
    }
    if (retryableIds.isNotEmpty()) {
        retryableIds.forEach { id ->
            dao.updateLocationPointSyncStatus(
                ids = listOf(id),
                syncStatus = MobileSyncStatus.PENDING,
                lastError = updates.perItemErrors[id] ?: updates.failedReason ?: "transient-failure"
            )
        }
    }
}
