package com.pim.app.mobile.sync

import android.content.Context
import android.os.Build
import android.provider.Settings
import com.pim.app.data.AppDatabase
import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileLocationPointEntity
import com.pim.app.data.MobileSyncStatus
import com.pim.core.models.MobileIngestItemResult
import com.pim.core.models.MobileLocationPointRequest
import com.pim.core.models.MobileLocationPointsUploadRequest
import com.pim.core.network.ApiService
import com.pim.core.util.toCauseChainMessage
import dagger.hilt.android.qualifiers.ApplicationContext
import java.security.MessageDigest
import java.time.Instant
import java.util.Locale
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException
import retrofit2.HttpException

/** 单批点数上限：服务端 `MobileLocationService.MaxBatchPoints = 1000`，客户端留一倍余量。 */
internal const val LOCATION_UPLOAD_BATCH_LIMIT = 500

/** 单轮同步最多跑几批（500 × 10 = 5000 点/轮），防止一次同步无限占用工作线程。 */
internal const val MAX_LOCATION_BATCHES_PER_RUN = 10

/** 请求体过大（服务端/中间层拒绝）时按半拆分重试，最多拆这么多层。 */
internal const val MAX_LOCATION_BATCH_SPLIT_DEPTH = 4

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

/**
 * 定位点上传协调器。
 *
 * **上传协议（批量补传）**：
 * - 走服务端批量端点 `POST mobile/location/points/batch`（单批 ≤ [LOCATION_UPLOAD_BATCH_LIMIT] 条），
 *   不再逐条 HTTP 请求 —— 积压时请求数从「点数」降到「批数」。
 * - 服务端**逐条**返回结果，客户端以**请求顺序**对齐本地行：`accepted` 删除本地行、
 *   `skipped`（natural-key 重复，服务端已有该点）同样视为已送达并删除、`rejected` 落
 *   `REJECTED` 状态并保留本地记录。
 * - 请求级失败（网络异常、业务错误码、逐条结果条数与请求条数不符）一律**不删本地行**、
 *   保持可重传状态，口径是「不丢点、允许积压」。
 * - 单轮同步由 [uploadPendingUntilDrained] 循环到队列排空（上限 [MAX_LOCATION_BATCHES_PER_RUN] 批），
 *   出现可重试失败即停止本轮，交给 WorkManager 按既有节奏重试。
 */
@Singleton
class LocationUploadCoordinator @Inject constructor(
    @ApplicationContext private val context: Context,
    private val database: AppDatabase,
    private val api: ApiService
) {
    private val dao: MobileDataDao = database.mobileDataDao()

    /** 单批上限，便于测试与调用方复用同一口径。 */
    val batchLimit: Int = LOCATION_UPLOAD_BATCH_LIMIT

    /**
     * 上传**一批**待传定位点。
     *
     * @param limit 本批最多取多少条（默认 [LOCATION_UPLOAD_BATCH_LIMIT]）。
     */
    suspend fun uploadPending(limit: Int = LOCATION_UPLOAD_BATCH_LIMIT): LocationUploadStatusUpdates {
        val rows = pendingRows(limit)
        if (rows.isEmpty()) {
            return LocationUploadPlanner.planStatusUpdates(
                LocationUploadBatchResult(emptyList(), emptyList(), null)
            )
        }

        val deviceId = deviceId()
        val outcome = RowOutcome()
        val chunk = ArrayList<Pair<MobileLocationPointEntity, MobileLocationPointRequest>>(rows.size)
        for (row in rows) {
            val request = row.toRequest(deviceId)
            if (request == null) {
                // 缺精度列的行没有可提交的内容：本地判永久失败，保留记录留痕。
                outcome.rejected(row.id, MISSING_ACCURACY)
            } else {
                chunk += row to request
            }
        }

        if (chunk.isNotEmpty()) {
            uploadChunk(chunk, outcome, depth = 0)
        }

        val updates = outcome.toStatusUpdates()
        applyStatusUpdates(updates)
        return updates
    }

    /**
     * 循环上传直到队列排空（或达到 [maxBatches] 批量上限 / 出现可重试失败）。
     *
     * 断网期间积压的点在恢复联网后由本轮同步一次追平，不必等每个 15 分钟周期只吃一批。
     */
    suspend fun uploadPendingUntilDrained(
        limit: Int = LOCATION_UPLOAD_BATCH_LIMIT,
        maxBatches: Int = MAX_LOCATION_BATCHES_PER_RUN
    ): LocationUploadStatusUpdates {
        val merged = RowOutcome()
        var batches = 0
        while (batches < maxBatches) {
            batches++
            val updates = uploadPending(limit)
            val retryableSet = updates.retryableFailedIds.toSet()

            merged.syncedIds += updates.syncedIds
            merged.retryableIds += updates.retryableFailedIds
            merged.rejectedIds += updates.failedIds.filter { it !in retryableSet }
            merged.perItemErrors.putAll(updates.perItemErrors)
            if (merged.lastError == null) {
                merged.lastError = updates.failedReason
            }

            val nothingLeft = updates.syncedIds.isEmpty() && updates.failedIds.isEmpty()
            if (nothingLeft) break
            // 出现可重试失败（网络/服务端故障）：停止本轮，交给 WorkManager 按既有节奏重试。
            if (updates.shouldRetry) break
        }
        return merged.toStatusUpdates()
    }

    /**
     * 提交一个分片。
     *
     * @param depth 已拆分层数（请求体过大时按半拆分重试，见 [MAX_LOCATION_BATCH_SPLIT_DEPTH]）。
     */
    private suspend fun uploadChunk(
        chunk: List<Pair<MobileLocationPointEntity, MobileLocationPointRequest>>,
        outcome: RowOutcome,
        depth: Int
    ) {
        val response = try {
            api.uploadMobileLocationsBatch(
                MobileLocationPointsUploadRequest(chunk.map { it.second })
            )
        } catch (ex: Exception) {
            if (ex is CancellationException) throw ex
            // 请求体过大：按半拆分重试，避免整批永久卡在队列里（不删任何本地行）。
            if (ex is HttpException && ex.code() == REQUEST_TOO_LARGE_STATUS &&
                chunk.size > 1 && depth < MAX_LOCATION_BATCH_SPLIT_DEPTH
            ) {
                val mid = chunk.size / 2
                uploadChunk(chunk.subList(0, mid), outcome, depth + 1)
                uploadChunk(chunk.subList(mid, chunk.size), outcome, depth + 1)
                return
            }
            val message = ex.toCauseChainMessage().ifBlank { ex::class.java.simpleName }
            outcome.retryable(chunk.map { it.first.id }, "batch-request-failed: $message")
            return
        }

        val items = response.data?.itemResults
        if (response.code != 0 || items == null) {
            val message = response.message.ifBlank { "batch request rejected" }
            outcome.retryable(chunk.map { it.first.id }, "batch-request-rejected: $message")
            return
        }
        if (items.size != chunk.size) {
            // 逐条结果与请求条数对不上：不猜、不删，整批留待下轮重发。
            outcome.retryable(chunk.map { it.first.id }, BATCH_RESULT_MISMATCH)
            return
        }

        items.forEachIndexed { index, item ->
            val rowId = chunk[index].first.id
            when (item.outcome.lowercase(Locale.US)) {
                OUTCOME_ACCEPTED -> outcome.accepted(rowId)
                // 服务端已存在同一自然键（设备+时刻+经纬度）= 该点已送达，删本地行。
                OUTCOME_SKIPPED -> outcome.accepted(rowId)
                OUTCOME_REJECTED -> outcome.rejected(rowId, describe(item))
                else -> outcome.retryable(listOf(rowId), "unknown-outcome: ${describe(item)}")
            }
        }
    }

    private suspend fun pendingRows(limit: Int): List<MobileLocationPointEntity> {
        // WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 / AC-15.2：客户端只做精度过滤。
        // 这里原本对 pendingRows 调 Douglas-Peucker（ε = 8.0 米，
        // TrajectoryCompressor.DOUGLAS_EPSILON_METERS）抽稀，被抽掉的点既不进上传、
        // 也不留任何记录 —— 属 AC-15.3 禁止的静默丢弃路径。抽稀已整体取消：
        // 达标的点逐条进入上传队列，滤波与舍弃交给服务端（A9 / A11）。
        //
        // 上传口径（批量补传改造）：一次同步取 LOCATION_UPLOAD_BATCH_LIMIT 条走批量端点，
        // 并由 uploadPendingUntilDrained 循环到排空 —— 队列进度不再受「每轮 100 条」限制。
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

    /** 一批（或一次拆分后的子批）的逐条结果累加器。 */
    private class RowOutcome {
        val syncedIds = mutableListOf<Long>()
        val retryableIds = mutableListOf<Long>()
        val rejectedIds = mutableListOf<Long>()
        val perItemErrors = linkedMapOf<Long, String>()
        var lastError: String? = null

        fun accepted(id: Long) {
            syncedIds += id
        }

        fun rejected(id: Long, reason: String) {
            rejectedIds += id
            perItemErrors[id] = reason
            if (lastError == null) lastError = reason
        }

        fun retryable(ids: Collection<Long>, reason: String) {
            retryableIds += ids
            ids.forEach { perItemErrors[it] = reason }
            if (lastError == null) lastError = reason
        }

        fun toStatusUpdates(): LocationUploadStatusUpdates = LocationUploadStatusUpdates(
            syncedIds = syncedIds,
            failedIds = rejectedIds + retryableIds,
            failedReason = lastError,
            shouldRetry = retryableIds.isNotEmpty(),
            perItemErrors = perItemErrors,
            retryableFailedIds = retryableIds
        )
    }

    private companion object {
        const val MISSING_ACCURACY = "missing-horizontal-accuracy"
        const val BATCH_RESULT_MISMATCH = "batch-item-result-mismatch"
        const val REQUEST_TOO_LARGE_STATUS = 413
        const val OUTCOME_ACCEPTED = "accepted"
        const val OUTCOME_SKIPPED = "skipped"
        const val OUTCOME_REJECTED = "rejected"

        fun describe(item: MobileIngestItemResult): String {
            val code = item.code.ifBlank { "rejected" }
            return if (item.message.isBlank()) code else "$code: ${item.message}"
        }
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
