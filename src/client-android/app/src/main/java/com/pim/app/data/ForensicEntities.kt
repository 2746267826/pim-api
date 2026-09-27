package com.pim.app.data

import androidx.room.ColumnInfo
import androidx.room.Dao
import androidx.room.Entity
import androidx.room.Index
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.PrimaryKey
import androidx.room.Query
import kotlinx.coroutines.flow.Flow

/**
 * 阶段一取证事件（REQ-1 ~ REQ-6）：进程退出原因、强停/重启、存活心跳及其上下文。
 *
 * - 本地**不设条数上限**（R4-P3），只按 30 天时间清理兜底（AC-6.2）。
 * - [clientItemKey] 是幂等键：同一秒内重复唤醒只产生一条心搏（AC-3.3），
 *   重复上传服务端也会按同一键跳过（AC-5.2）。
 * - 负载只含结构化诊断字段，**不含经纬度、令牌、口令、账号信息**（AC-27.1）。
 */
@Entity(
    tableName = "mobile_forensic_events",
    indices = [
        Index(value = ["occurred_at_utc"]),
        Index(value = ["client_item_key"], unique = true),
        Index(value = ["sync_status"])
    ]
)
data class ForensicEventEntity(
    @PrimaryKey(autoGenerate = true) val id: Long = 0,
    @ColumnInfo(name = "event_type") val eventType: String,
    @ColumnInfo(name = "occurred_at_utc") val occurredAtUtc: Long,
    @ColumnInfo(name = "client_item_key") val clientItemKey: String,
    @ColumnInfo(name = "payload_json") val payloadJson: String,
    @ColumnInfo(name = "sync_status") val syncStatus: String = MobileSyncStatus.PENDING,
    @ColumnInfo(name = "last_error") val lastError: String? = null,
    @ColumnInfo(name = "created_at_utc") val createdAtUtc: Long = System.currentTimeMillis()
)

/** 按「本地日 + 原因」聚合的定位丢弃统计（REQ-9）：明细留本地，统计上报服务端。 */
data class DroppedReasonDailyCount(
    @ColumnInfo(name = "local_date") val localDate: String,
    @ColumnInfo(name = "reason") val reason: String,
    @ColumnInfo(name = "count") val count: Int
)

@Dao
interface ForensicEventDao {
    @Insert(onConflict = OnConflictStrategy.IGNORE)
    suspend fun insertIgnore(event: ForensicEventEntity): Long

    @Query("SELECT COUNT(*) FROM mobile_forensic_events")
    suspend fun totalCount(): Int

    @Query("SELECT COUNT(*) FROM mobile_forensic_events WHERE sync_status = :syncStatus")
    fun observeCountBySyncStatus(syncStatus: String = MobileSyncStatus.PENDING): Flow<Int>

    @Query("SELECT COUNT(*) FROM mobile_forensic_events WHERE sync_status = :syncStatus")
    suspend fun countBySyncStatus(syncStatus: String = MobileSyncStatus.PENDING): Int

    @Query(
        """
        SELECT * FROM mobile_forensic_events
        WHERE sync_status = :syncStatus
        ORDER BY occurred_at_utc ASC
        LIMIT :limit
        """
    )
    suspend fun pendingEvents(
        syncStatus: String = MobileSyncStatus.PENDING,
        limit: Int = 500
    ): List<ForensicEventEntity>

    @Query(
        """
        SELECT * FROM mobile_forensic_events
        WHERE client_item_key IN (:keys)
        """
    )
    suspend fun findByKeys(keys: List<String>): List<ForensicEventEntity>

    @Query(
        """
        UPDATE mobile_forensic_events
        SET sync_status = :syncStatus, last_error = :lastError
        WHERE client_item_key IN (:keys)
        """
    )
    suspend fun updateSyncStatusByKeys(
        keys: List<String>,
        syncStatus: String,
        lastError: String? = null
    )

    @Query(
        """
        SELECT * FROM mobile_forensic_events
        WHERE occurred_at_utc >= :fromUtc AND occurred_at_utc < :toUtc
        ORDER BY occurred_at_utc ASC
        """
    )
    suspend fun eventsInRange(fromUtc: Long, toUtc: Long): List<ForensicEventEntity>

    @Query(
        """
        SELECT * FROM mobile_forensic_events
        WHERE event_type = :eventType
        ORDER BY occurred_at_utc DESC
        LIMIT :limit
        """
    )
    suspend fun recentByType(eventType: String, limit: Int): List<ForensicEventEntity>

    /**
     * 时间清理（AC-6.2）：超过 30 天的条目**不论是否已上传**都移除。
     * 30 天内未上传的条目不在删除范围内，因此清理后设备端待上传计数与条数保持一致。
     */
    @Query("DELETE FROM mobile_forensic_events WHERE occurred_at_utc < :cutoffUtc")
    suspend fun deleteOlderThan(cutoffUtc: Long): Int

    @Query("DELETE FROM mobile_forensic_events")
    suspend fun deleteAll()

    @Query("SELECT COUNT(*) FROM mobile_forensic_events WHERE event_type = :eventType")
    suspend fun countByType(eventType: String): Int

    /**
     * 按「事件类型 + 时间窗 + 结果」计数（REQ-9 / AC-9.1）。
     *
     * 走 SQL 而不是「读最近 N 条再过滤」：高速档（2.5 秒一拍）下跳过记录可达约
     * 34560 条/天，用固定条数上限会把 24 小时内的已执行记录挤出窗口，
     * 让状态页把「其实冲了很多次」显示成 0 次。
     */
    @Query(
        """
        SELECT COUNT(*) FROM mobile_forensic_events
        WHERE event_type = :eventType
          AND occurred_at_utc >= :fromUtc
          AND payload_json LIKE :payloadLike
        """
    )
    suspend fun countByTypeInWindowWithPayloadLike(
        eventType: String,
        fromUtc: Long,
        payloadLike: String
    ): Int

    /**
     * 取最近一条**已执行**冲刺记录的载荷（REQ-9：状态页显示最近一次窗口跨度）。
     *
     * 走 SQL 而不是「读最近 N 条再过滤」：高速档每 2.5 秒写一条**跳过**记录
     * （约 34560 条/天），固定条数上限会把已执行记录挤出扫描范围，
     * 让状态页那一行凭空消失（独立 review round 3 的 I-3）。
     */
    @Query(
        """
        SELECT payload_json FROM mobile_forensic_events
        WHERE event_type = :eventType AND payload_json LIKE :payloadLike
        ORDER BY occurred_at_utc DESC
        LIMIT 1
        """
    )
    suspend fun latestPayloadByTypeLike(
        eventType: String,
        payloadLike: String
    ): String?

    /** 时间窗内是否存在任何该类型事件（AC-9.2 的「有采集数据」判定用）。 */
    @Query(
        """
        SELECT COUNT(*) FROM mobile_forensic_events
        WHERE event_type = :eventType AND occurred_at_utc >= :fromUtc
        """
    )
    suspend fun countByTypeInWindow(eventType: String, fromUtc: Long): Int

    /** 进程退出原因台账最近若干条（REQ-1 / REQ-8「最近死因」）。 */
    @Query(
        """
        SELECT * FROM mobile_forensic_events
        WHERE event_type IN (:eventTypes)
        ORDER BY occurred_at_utc DESC
        LIMIT :limit
        """
    )
    suspend fun recentByTypes(eventTypes: List<String>, limit: Int): List<ForensicEventEntity>

    @Query(
        """
        SELECT reason AS reason, COUNT(*) AS count
        FROM mobile_location_dropped_diagnostics
        WHERE recorded_at_utc >= :fromUtc
        GROUP BY reason
        ORDER BY count DESC
        """
    )
    suspend fun droppedReasonCounts(fromUtc: Long): List<DroppedReasonCountOnly>

    /**
     * 丢弃明细的**键集分页**（REQ-9 / AC-9.2）。
     *
     * 统计走这里逐页扫描而不是「一次扫描 + 条数上限」：本地不设条数上限（R4-P3），
     * 明细分多少页都不影响最终计数，因此服务端统计不会被任何固定行数截断。
     */
    @Query(
        """
        SELECT id AS id, reason AS reason, recorded_at_utc AS recordedAtUtc
        FROM mobile_location_dropped_diagnostics
        WHERE recorded_at_utc >= :fromUtc AND id > :afterId
        ORDER BY id ASC
        LIMIT :pageSize
        """
    )
    suspend fun droppedDiagnosticPage(
        fromUtc: Long,
        afterId: Long,
        pageSize: Int
    ): List<DroppedReasonRow>

    /**
     * 按**设备本地日**聚合丢弃原因（REQ-9 / AC-9.2）。本地日由调用方给出的日界换算成 UTC 毫秒区间，
     * 避免把设备时区直接写进 SQL。
     */
    @Query(
        """
        SELECT reason AS reason, COUNT(*) AS count
        FROM mobile_location_dropped_diagnostics
        WHERE recorded_at_utc >= :fromUtc AND recorded_at_utc < :toUtc
        GROUP BY reason
        ORDER BY count DESC
        """
    )
    suspend fun droppedReasonCountsInWindow(fromUtc: Long, toUtc: Long): List<DroppedReasonCountOnly>

    /** 丢弃明细（REQ-9 / AC-9.1）：时刻 / 原因 / 准确度 / provider / 策略档，**不设条数上限**（上限由调用方按时间窗给）。 */
    @Query(
        """
        SELECT recorded_at_utc, provider, accuracy_meters, policy_mode, reason
        FROM mobile_location_dropped_diagnostics
        ORDER BY recorded_at_utc DESC
        LIMIT :limit
        """
    )
    suspend fun droppedDiagnosticsForExport(limit: Int): List<DroppedDiagnosticExportRow>

    @Query(
        """
        SELECT recorded_at_utc, provider, accuracy_meters, policy_mode, reason
        FROM mobile_location_dropped_diagnostics
        WHERE recorded_at_utc >= :fromUtc
        ORDER BY recorded_at_utc DESC
        LIMIT :limit
        """
    )
    suspend fun recentDroppedDiagnosticsInWindow(
        fromUtc: Long,
        limit: Int
    ): List<DroppedDiagnosticExportRow>

}

/** 按原因聚合的丢弃计数。 */
data class DroppedReasonCountOnly(
    @ColumnInfo(name = "reason") val reason: String,
    @ColumnInfo(name = "count") val count: Int
)

/** 统计扫描用的最小投影（自增 id + 原因 + 时刻）。 */
data class DroppedReasonRow(
    @ColumnInfo(name = "id") val id: Long,
    @ColumnInfo(name = "reason") val reason: String,
    @ColumnInfo(name = "recordedAtUtc") val recordedAtUtc: Long
)

/** 丢弃明细的导出行（REQ-9 / AC-9.1）。 */
data class DroppedDiagnosticExportRow(
    @ColumnInfo(name = "recorded_at_utc") val recordedAtUtc: Long,
    @ColumnInfo(name = "provider") val provider: String?,
    @ColumnInfo(name = "accuracy_meters") val accuracyMeters: Float?,
    @ColumnInfo(name = "policy_mode") val policyMode: String,
    @ColumnInfo(name = "reason") val reason: String
)
