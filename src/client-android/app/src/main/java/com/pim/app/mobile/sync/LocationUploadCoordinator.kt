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
import java.io.IOException
import java.security.MessageDigest
import java.time.Instant
import java.util.Locale
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import retrofit2.HttpException

/** 单批点数上限：服务端 `MobileLocationService.MaxBatchPoints = 1000`，客户端留一倍余量。 */
internal const val LOCATION_UPLOAD_BATCH_LIMIT = 500

/** 单轮同步最多跑几批（500 × 10 = 5000 点/轮），防止一次同步无限占用工作线程。 */
internal const val MAX_LOCATION_BATCHES_PER_RUN = 10

/** 请求被服务端拒绝时按半拆分重试，最多拆这么多层（500 → 250 → … → 31）。 */
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
    val retryableFailedIds: List<Long> = emptyList(),
    /**
     * 被服务端拒绝、且拆到最小仍是拒绝的行 —— 写回 `FAILED` 状态（仍会重试，但排到队列尾部，
     * 不再占住队头把后续点全挡住）。
     */
    val deferredFailedIds: List<Long> = emptyList(),
    /** 本轮排空因批次数上限而停止，且队列仍有待传 —— 调用方据此提示"仍在追平"。 */
    val truncated: Boolean = false
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
 * - 服务端**逐条**返回结果（`itemResults` 条数 == 请求条数、顺序与请求一致，见服务端
 *   `MobileLocationService.SubmitBatchAttemptAsync` 的逐点 append），客户端按请求顺序对齐本地行：
 *   `accepted` 删除本地行、`skipped`（natural-key 重复，服务端已有该点）同样视为已送达并删除、
 *   `rejected` 落 `REJECTED` 状态并保留本地记录。
 * - **不丢点**：唯一删除本地行的路径是「服务端已确认送达（accepted / skipped）」；
 *   其余任何失败都保留本地行，只改变重传时机。
 * - **不会卡死**：请求级拒绝（4xx / 请求体无法序列化）按半拆分隔离坏行；拆到最小仍被拒的行
 *   写回 `FAILED`（排到队尾继续重试，不占队头）；只有网络/服务端级故障才整批留原位并结束本轮。
 * - 单轮由 [uploadPendingUntilDrained] 排空（上限 [MAX_LOCATION_BATCHES_PER_RUN] 批）。
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
            val (request, rejectReason) = row.toRequestOrReason(deviceId)
            if (request == null) {
                // 缺精度列或数值不合法（NaN/∞ 会让整个请求体无法序列化）：本地判永久失败、保留记录留痕。
                outcome.rejected(row.id, rejectReason)
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
     * 循环上传直到队列排空（或达到 [maxBatches] 批量上限 / 出现网络级失败）。
     *
     * 断网期间积压的点在恢复联网后由本轮同步一次追平，不必等每个 15 分钟周期只吃一批。
     * 返回值里的 [LocationUploadStatusUpdates.truncated] 说明本轮是否因为批次数上限提前收手。
     */
    suspend fun uploadPendingUntilDrained(
        limit: Int = LOCATION_UPLOAD_BATCH_LIMIT,
        maxBatches: Int = MAX_LOCATION_BATCHES_PER_RUN
    ): LocationUploadStatusUpdates {
        val merged = RowOutcome()
        var batches = 0
        while (batches < maxBatches) {
            currentCoroutineContext().ensureActive()
            batches++
            val updates = uploadPending(limit)
            val retryableSet = updates.retryableFailedIds.toSet()
            val deferredSet = updates.deferredFailedIds.toSet()

            merged.syncedIds += updates.syncedIds
            merged.retryableIds += updates.retryableFailedIds
            merged.deferredIds += updates.deferredFailedIds
            merged.rejectedIds += updates.failedIds.filter {
                it !in retryableSet && it !in deferredSet
            }
            merged.perItemErrors.putAll(updates.perItemErrors)
            if (merged.lastError == null) {
                merged.lastError = updates.failedReason
            }

            val nothingLeft = updates.syncedIds.isEmpty() && updates.failedIds.isEmpty()
            if (nothingLeft) break
            // 网络/服务端级故障：本轮到此为止，交给 WorkManager 按既有节奏重试。
            if (updates.shouldRetry) break
        }

        // 跑满上限时再看一眼队列：还有待传就是「被截断」，调用方据此提示仍在追平。
        val truncated = batches >= maxBatches && hasPendingRows()
        return merged.toStatusUpdates().copy(truncated = truncated)
    }

    /**
     * 提交一个分片。
     *
     * @param depth 已拆分层数（见 [MAX_LOCATION_BATCH_SPLIT_DEPTH]）。
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
            val message = ex.toCauseChainMessage().ifBlank { ex::class.java.simpleName }
            val splittable = isSplittableRequestRejection(ex)
            if (splittable && chunk.size > 1 && depth < MAX_LOCATION_BATCH_SPLIT_DEPTH) {
                // 服务端拒绝了这个请求体（含请求体无法序列化）：按半拆分，把坏行隔离到最小分片。
                val mid = chunk.size / 2
                uploadChunk(chunk.subList(0, mid), outcome, depth + 1)
                uploadChunk(chunk.subList(mid, chunk.size), outcome, depth + 1)
                return
            }
            if (splittable) {
                // 已拆到最小仍被拒：写回 FAILED（继续重试但排到队尾），不占住队头把后面的点全挡住。
                outcome.deferred(chunk.map { it.first.id }, "batch-item-rejected: $message")
            } else {
                // 网络/服务端级故障：整批留在原位等待下一轮，并结束本轮排空。
                outcome.retryable(chunk.map { it.first.id }, "batch-request-failed: $message")
            }
            return
        }

        val items = response.data?.itemResults
        if (response.code != 0 || items == null) {
            // HTTP 200 但业务码非 0（当前服务端不产生该形态，防御性分支）：退到队尾重试。
            val message = response.message.ifBlank { "batch request rejected" }
            outcome.deferred(chunk.map { it.first.id }, "batch-request-rejected: $message")
            return
        }
        if (items.size != chunk.size) {
            // 逐条结果与请求条数对不上：不猜、不删，整批退到队尾等下一轮重发。
            outcome.deferred(chunk.map { it.first.id }, BATCH_RESULT_MISMATCH)
            return
        }
        if (items.any { it.entityType != EXPECTED_ITEM_ENTITY_TYPE }) {
            // 服务端掺入非定位点条目（如批次汇总项）会让条数恰好相等却整体错位：
            // 那时按下标对齐就会「删掉没上传的点、给已上传的点打上拒绝」，属静默丢点。整批不删。
            outcome.deferred(chunk.map { it.first.id }, BATCH_ENTITY_MISMATCH)
            return
        }

        items.forEachIndexed { index, item ->
            val rowId = chunk[index].first.id
            when (item.outcome.lowercase(Locale.US)) {
                OUTCOME_ACCEPTED -> outcome.accepted(rowId)
                // 服务端已存在同一自然键（设备+时刻+经纬度）= 该点已送达，删本地行。
                OUTCOME_SKIPPED -> outcome.accepted(rowId)
                OUTCOME_REJECTED -> outcome.rejected(rowId, describe(item))
                else -> outcome.deferred(listOf(rowId), "unknown-outcome: ${describe(item)}")
            }
        }
    }

    /**
     * 是否能通过「拆小请求体」解决 —— 只有服务端拒绝了这个请求（4xx）或请求体本身无法序列化时成立。
     *
     * 网络/鉴权/限流类失败拆了也一样失败，只会放大请求数（断网时把 500 条拆成 16 次无效请求），
     * 因此这里明确排除。异常可能被包装（如动态代理的 `UndeclaredThrowableException`、Retrofit 的
     * 包装层），所以沿 cause 链取第一个可识别的类型再判定。
     */
    private fun isSplittableRequestRejection(ex: Throwable): Boolean {
        var current: Throwable? = ex
        var depth = 0
        while (current != null && depth < MAX_CAUSE_CHAIN_DEPTH) {
            when (current) {
                is HttpException -> return current.code() in 400..499 &&
                    current.code() !in NON_SPLITTABLE_STATUSES
                is IOException -> return false
                else -> Unit
            }
            current = current.cause
            depth++
        }
        return true
    }

    private suspend fun hasPendingRows(): Boolean =
        dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 1).isNotEmpty() ||
            dao.getLocationPointsBySyncStatus(MobileSyncStatus.FAILED, 1).isNotEmpty()

    private suspend fun pendingRows(limit: Int): List<MobileLocationPointEntity> {
        // WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 / AC-15.2：客户端只做精度过滤。
        // 这里原本对 pendingRows 调 Douglas-Peucker（ε = 8.0 米，
        // TrajectoryCompressor.DOUGLAS_EPSILON_METERS）抽稀，被抽掉的点既不进上传、
        // 也不留任何记录 —— 属 AC-15.3 禁止的静默丢弃路径。抽稀已整体取消：
        // 达标的点逐条进入上传队列，滤波与舍弃交给服务端（A9 / A11）。
        //
        // 上传口径（批量补传改造）：一次同步取 LOCATION_UPLOAD_BATCH_LIMIT 条走批量端点，
        // 并由 uploadPendingUntilDrained 排空 —— 队列进度不再受「每轮 100 条」限制。
        // PENDING 优先、不足才补 FAILED：被延迟到队尾的行（deferred）因此不会挡住新点。
        val pending = dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, limit)
        if (pending.size >= limit) return pending

        val failed = dao.getLocationPointsBySyncStatus(MobileSyncStatus.FAILED, limit - pending.size)
        return pending + failed
    }

    private suspend fun applyStatusUpdates(updates: LocationUploadStatusUpdates) {
        applyLocationStatusUpdates(dao, updates)
    }

    /**
     * 组装请求体；数值不合法（NaN / ∞ 会让整个请求体的 JSON 序列化失败）时返回拒绝原因。
     */
    private fun MobileLocationPointEntity.toRequestOrReason(
        deviceId: String
    ): Pair<MobileLocationPointRequest?, String> {
        val accuracy = accuracyMeters ?: return null to MISSING_ACCURACY
        val values = listOf(
            accuracy.toDouble(), latitude, longitude,
            altitudeMeters, speedMetersPerSecond?.toDouble(), bearingDegrees?.toDouble()
        )
        if (values.any { it != null && !it.isFinite() }) {
            return null to INVALID_NUMERIC
        }
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
        ) to ""
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
        val deferredIds = mutableListOf<Long>()
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

        fun deferred(ids: Collection<Long>, reason: String) {
            deferredIds += ids
            ids.forEach { perItemErrors[it] = reason }
            if (lastError == null) lastError = reason
        }

        fun toStatusUpdates(): LocationUploadStatusUpdates = LocationUploadStatusUpdates(
            syncedIds = syncedIds,
            failedIds = rejectedIds + retryableIds + deferredIds,
            failedReason = lastError,
            shouldRetry = retryableIds.isNotEmpty(),
            perItemErrors = perItemErrors,
            retryableFailedIds = retryableIds,
            deferredFailedIds = deferredIds
        )
    }

    private companion object {
        const val MISSING_ACCURACY = "missing-horizontal-accuracy"
        const val INVALID_NUMERIC = "invalid-numeric-values"
        const val BATCH_RESULT_MISMATCH = "batch-item-result-mismatch"
        const val BATCH_ENTITY_MISMATCH = "batch-item-entity-mismatch"

        /** 服务端批量定位点结果的条目类型（`MobileLocationService.Item(...)`）。 */
        const val EXPECTED_ITEM_ENTITY_TYPE = "location-point"
        const val OUTCOME_ACCEPTED = "accepted"
        const val OUTCOME_SKIPPED = "skipped"
        const val OUTCOME_REJECTED = "rejected"

        /** 拆小也没用的 4xx：鉴权（401/403）、超时、限流。 */
        val NON_SPLITTABLE_STATUSES = setOf(401, 403, 408, 429)

        /** 沿 cause 链判定的最大深度（防异常自引用死循环）。 */
        const val MAX_CAUSE_CHAIN_DEPTH = 8

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
        // 唯一的删除路径：服务端已确认送达（accepted / skipped）。
        dao.deleteLocationPointByIds(updates.syncedIds)
    }
    val syncedSet = updates.syncedIds.toSet()
    val retryableSet = updates.retryableFailedIds.toSet()
    val deferredSet = updates.deferredFailedIds.toSet()

    val permanentIds = updates.failedIds.filter {
        it !in retryableSet && it !in deferredSet && it !in syncedSet
    }
    val retryableIds = updates.failedIds.filter { it in retryableSet && it !in syncedSet }
    val deferredIds = updates.failedIds.filter { it in deferredSet && it !in syncedSet }

    updateLocationStatusGrouped(dao, permanentIds, MobileSyncStatus.REJECTED, updates, "permanent-failure")
    updateLocationStatusGrouped(dao, retryableIds, MobileSyncStatus.PENDING, updates, "transient-failure")
    updateLocationStatusGrouped(dao, deferredIds, MobileSyncStatus.FAILED, updates, "deferred-failure")
}

/**
 * 按「状态 + 原因」分组批量落库：一批 500 条同因失败只写一次本地事务，而不是逐行 500 次。
 */
private suspend fun updateLocationStatusGrouped(
    dao: MobileDataDao,
    ids: List<Long>,
    syncStatus: String,
    updates: LocationUploadStatusUpdates,
    fallbackReason: String
) {
    if (ids.isEmpty()) return
    ids.groupBy { updates.perItemErrors[it] ?: updates.failedReason ?: fallbackReason }
        .forEach { (reason, group) ->
            dao.updateLocationPointSyncStatus(
                ids = group,
                syncStatus = syncStatus,
                lastError = reason
            )
        }
}
