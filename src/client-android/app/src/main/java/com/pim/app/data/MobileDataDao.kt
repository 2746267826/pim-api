package com.pim.app.data

import androidx.room.Dao
import androidx.room.Insert
import androidx.room.OnConflictStrategy
import androidx.room.Query
import androidx.room.Upsert
import kotlinx.coroutines.flow.Flow

@Dao
interface MobileDataDao {
    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertUsageEvents(events: List<MobileUsageEventEntity>): List<Long>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertUsageSummaries(summaries: List<MobileUsageSummaryEntity>): List<Long>

    @Upsert
    suspend fun upsertAppMetadata(metadata: List<MobileAppMetadataEntity>)

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertLocationPoints(points: List<MobileLocationPointEntity>): List<Long>

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertLocationPoint(point: MobileLocationPointEntity): Long

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertDroppedLocationDiagnostic(diagnostic: MobileLocationDroppedDiagnosticEntity): Long

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertPolicyTransition(transition: MobileLocationPolicyTransitionEntity): Long

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertSyncBatch(batch: MobileSyncBatchEntity): Long

    @Insert(onConflict = OnConflictStrategy.REPLACE)
    suspend fun insertLogs(logs: List<MobileLogEntity>): List<Long>

    @Upsert
    suspend fun upsertDeviceProfile(profile: MobileDeviceProfileEntity)

    @Query(
        """
        SELECT * FROM mobile_usage_events
        WHERE sync_status = :syncStatus
        ORDER BY event_time_utc ASC
        LIMIT :limit
        """
    )
    suspend fun getUsageEventsBySyncStatus(
        syncStatus: String = MobileSyncStatus.PENDING,
        limit: Int = 500
    ): List<MobileUsageEventEntity>

    @Query(
        """
        SELECT * FROM mobile_usage_summaries
        WHERE sync_status = :syncStatus
        ORDER BY window_start_utc ASC
        LIMIT :limit
        """
    )
    suspend fun getUsageSummariesBySyncStatus(
        syncStatus: String = MobileSyncStatus.PENDING,
        limit: Int = 500
    ): List<MobileUsageSummaryEntity>

    @Query(
        """
        SELECT * FROM mobile_app_metadata
        WHERE sync_status = :syncStatus
        ORDER BY package_name ASC
        LIMIT :limit
        """
    )
    suspend fun getAppMetadataBySyncStatus(
        syncStatus: String = MobileSyncStatus.PENDING,
        limit: Int = 500
    ): List<MobileAppMetadataEntity>

    @Query(
        """
        SELECT * FROM mobile_location_points
        WHERE sync_status = :syncStatus
        ORDER BY recorded_at_utc ASC
        LIMIT :limit
        """
    )
    suspend fun getLocationPointsBySyncStatus(
        syncStatus: String = MobileSyncStatus.PENDING,
        limit: Int = 500
    ): List<MobileLocationPointEntity>

    @Query(
        """
        SELECT * FROM mobile_sync_batches
        WHERE sync_status = :syncStatus
        ORDER BY created_at_utc ASC
        LIMIT :limit
        """
    )
    suspend fun getSyncBatchesBySyncStatus(
        syncStatus: String = MobileSyncStatus.PENDING,
        limit: Int = 500
    ): List<MobileSyncBatchEntity>

    @Query(
        """
        SELECT * FROM mobile_logs
        WHERE sync_status = :syncStatus
        ORDER BY occurred_at_utc ASC
        LIMIT :limit
        """
    )
    suspend fun getLogsBySyncStatus(
        syncStatus: String = MobileSyncStatus.PENDING,
        limit: Int = 500
    ): List<MobileLogEntity>

    @Query(
        """
        SELECT * FROM mobile_device_profile
        WHERE sync_status = :syncStatus
        LIMIT 1
        """
    )
    suspend fun getDeviceProfileBySyncStatus(
        syncStatus: String = MobileSyncStatus.PENDING
    ): MobileDeviceProfileEntity?

    @Query("SELECT COUNT(*) FROM mobile_usage_events WHERE sync_status != :syncedStatus AND sync_status != :rejectedStatus")
    fun pendingUsageEventCount(
        syncedStatus: String = MobileSyncStatus.SYNCED,
        rejectedStatus: String = MobileSyncStatus.REJECTED
    ): Flow<Int>

    @Query("SELECT COUNT(*) FROM mobile_usage_summaries WHERE sync_status != :syncedStatus AND sync_status != :rejectedStatus")
    fun pendingUsageSummaryCount(
        syncedStatus: String = MobileSyncStatus.SYNCED,
        rejectedStatus: String = MobileSyncStatus.REJECTED
    ): Flow<Int>

    @Query("SELECT COUNT(*) FROM mobile_app_metadata WHERE sync_status != :syncedStatus AND sync_status != :rejectedStatus")
    fun pendingAppMetadataCount(
        syncedStatus: String = MobileSyncStatus.SYNCED,
        rejectedStatus: String = MobileSyncStatus.REJECTED
    ): Flow<Int>

    /**
     * 时间窗内是否有定位点（AC-9.2 的空态判定：「无定位点、无心跳、无台账记录」才算「暂无」）。
     *
     * 只看**时间窗**、不看 sync_status：已上传的点已被删除，因此「有采集数据」应以
     * 仍然留存的行为准；这里用于「最近 24 小时是否采集过」的粗判，不用于计数展示。
     */
    @Query("SELECT COUNT(*) FROM mobile_location_points WHERE recorded_at_utc >= :fromUtc")
    suspend fun countLocationPointsSince(fromUtc: Long): Int

    @Query("SELECT COUNT(*) FROM mobile_location_points WHERE sync_status != :syncedStatus AND sync_status != :rejectedStatus")
    fun pendingLocationPointCount(
        syncedStatus: String = MobileSyncStatus.SYNCED,
        rejectedStatus: String = MobileSyncStatus.REJECTED
    ): Flow<Int>

    @Query("SELECT COUNT(*) FROM mobile_sync_batches WHERE sync_status != :syncedStatus AND sync_status != :rejectedStatus")
    fun pendingSyncBatchCount(
        syncedStatus: String = MobileSyncStatus.SYNCED,
        rejectedStatus: String = MobileSyncStatus.REJECTED
    ): Flow<Int>

    @Query("SELECT COUNT(*) FROM mobile_logs WHERE sync_status != :syncedStatus")
    fun pendingLogCount(syncedStatus: String = MobileSyncStatus.SYNCED): Flow<Int>

    @Query("SELECT COUNT(*) FROM mobile_device_profile WHERE sync_status != :syncedStatus AND sync_status != :rejectedStatus")
    fun pendingDeviceProfileCount(
        syncedStatus: String = MobileSyncStatus.SYNCED,
        rejectedStatus: String = MobileSyncStatus.REJECTED
    ): Flow<Int>

    @Query(
        """
        SELECT (
            COALESCE((SELECT COUNT(*) FROM mobile_usage_events WHERE sync_status = :rejected), 0) +
            COALESCE((SELECT COUNT(*) FROM mobile_usage_summaries WHERE sync_status = :rejected), 0) +
            COALESCE((SELECT COUNT(*) FROM mobile_app_metadata WHERE sync_status = :rejected), 0) +
            COALESCE((SELECT COUNT(*) FROM mobile_location_points WHERE sync_status = :rejected), 0) +
            COALESCE((SELECT COUNT(*) FROM mobile_sync_batches WHERE sync_status = :rejected), 0) +
            COALESCE((SELECT COUNT(*) FROM mobile_device_profile WHERE sync_status = :rejected), 0)
        )
        """
    )
    fun aggregateRejectedCount(rejected: String = MobileSyncStatus.REJECTED): Flow<Int>

    @Query("DELETE FROM mobile_usage_events WHERE id IN (:ids)")
    suspend fun deleteUsageEventByIds(ids: List<Long>)

    @Query("DELETE FROM mobile_usage_summaries WHERE id IN (:ids)")
    suspend fun deleteUsageSummaryByIds(ids: List<Long>)

    @Query("DELETE FROM mobile_app_metadata WHERE package_name IN (:packageNames)")
    suspend fun deleteAppMetadataByPackageNames(packageNames: List<String>)

    @Query("DELETE FROM mobile_location_points WHERE id IN (:ids)")
    suspend fun deleteLocationPointByIds(ids: List<Long>)

    @Query("SELECT * FROM mobile_logs ORDER BY occurred_at_utc DESC LIMIT :limit")
    fun recentLogs(limit: Int = 6): Flow<List<MobileLogEntity>>

    @Query("SELECT * FROM mobile_location_dropped_diagnostics ORDER BY recorded_at_utc DESC LIMIT :limit")
    fun recentDroppedLocationDiagnostics(limit: Int = 20): Flow<List<MobileLocationDroppedDiagnosticEntity>>

    @Query("SELECT * FROM mobile_location_policy_transitions ORDER BY occurred_at_utc DESC LIMIT :limit")
    fun recentPolicyTransitions(limit: Int = 20): Flow<List<MobileLocationPolicyTransitionEntity>>

    @Query(
        """
        UPDATE mobile_usage_events
        SET sync_status = :syncStatus,
            last_error = :lastError,
            updated_at_utc = :updatedAtUtc
        WHERE id IN (:ids)
        """
    )
    suspend fun updateUsageEventSyncStatus(
        ids: List<Long>,
        syncStatus: String,
        lastError: String? = null,
        updatedAtUtc: Long = System.currentTimeMillis()
    )

    @Query(
        """
        UPDATE mobile_usage_summaries
        SET sync_status = :syncStatus,
            last_error = :lastError,
            updated_at_utc = :updatedAtUtc
        WHERE id IN (:ids)
        """
    )
    suspend fun updateUsageSummarySyncStatus(
        ids: List<Long>,
        syncStatus: String,
        lastError: String? = null,
        updatedAtUtc: Long = System.currentTimeMillis()
    )

    @Query(
        """
        UPDATE mobile_app_metadata
        SET sync_status = :syncStatus,
            last_error = :lastError,
            updated_at_utc = :updatedAtUtc
        WHERE package_name IN (:packageNames)
        """
    )
    suspend fun updateAppMetadataSyncStatus(
        packageNames: List<String>,
        syncStatus: String,
        lastError: String? = null,
        updatedAtUtc: Long = System.currentTimeMillis()
    )

    @Query(
        """
        UPDATE mobile_location_points
        SET sync_status = :syncStatus,
            last_error = :lastError,
            updated_at_utc = :updatedAtUtc
        WHERE id IN (:ids)
        """
    )
    suspend fun updateLocationPointSyncStatus(
        ids: List<Long>,
        syncStatus: String,
        lastError: String? = null,
        updatedAtUtc: Long = System.currentTimeMillis()
    )

    @Query(
        """
        UPDATE mobile_sync_batches
        SET sync_status = :syncStatus,
            last_error = :lastError,
            updated_at_utc = :updatedAtUtc
        WHERE batch_id IN (:batchIds)
        """
    )
    suspend fun updateSyncBatchSyncStatus(
        batchIds: List<String>,
        syncStatus: String,
        lastError: String? = null,
        updatedAtUtc: Long = System.currentTimeMillis()
    )

    @Query(
        """
        UPDATE mobile_logs
        SET sync_status = :syncStatus,
            last_error = :lastError,
            updated_at_utc = :updatedAtUtc
        WHERE id IN (:ids)
        """
    )
    suspend fun updateLogSyncStatus(
        ids: List<Long>,
        syncStatus: String,
        lastError: String? = null,
        updatedAtUtc: Long = System.currentTimeMillis()
    )

    @Query(
        """
        UPDATE mobile_device_profile
        SET sync_status = :syncStatus,
            last_error = :lastError,
            updated_at_utc = :updatedAtUtc
        WHERE profile_id = :profileId
        """
    )
    suspend fun updateDeviceProfileSyncStatus(
        profileId: String = "default",
        syncStatus: String,
        lastError: String? = null,
        updatedAtUtc: Long = System.currentTimeMillis()
    )

    @Query(
        """
        SELECT
            COALESCE((SELECT COUNT(*) FROM app_usage), 0) AS appUsageRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_usage_events), 0) AS mobileUsageEventsRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_usage_summaries), 0) AS mobileUsageSummariesRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_app_metadata), 0) AS mobileAppMetadataRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_location_points), 0) AS mobileLocationPointsRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_location_dropped_diagnostics), 0) AS mobileLocationDroppedDiagnosticsRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_location_policy_transitions), 0) AS mobileLocationPolicyTransitionsRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_sync_batches), 0) AS mobileSyncBatchesRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_logs), 0) AS mobileLogsRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_device_profile), 0) AS mobileDeviceProfileRowCount,
            COALESCE((SELECT COUNT(*) FROM mobile_forensic_events), 0) AS mobileForensicEventsRowCount
        """
    )
    suspend fun diagnosticDatabaseCounts(): DiagnosticDatabaseCounts

    @Query(
        """
        SELECT entity_type AS entityType, row_count AS rowCount,
               started_at_utc AS startedAtUtc, finished_at_utc AS finishedAtUtc,
               sync_status AS syncStatus, created_at_utc AS createdAtUtc
        FROM mobile_sync_batches
        ORDER BY created_at_utc DESC
        LIMIT :limit
        """
    )
    suspend fun diagnosticSyncHistory(limit: Int = 100): List<DiagnosticSyncHistoryRow>

    @Query(
        """
        SELECT latitude, longitude, altitude_meters AS altitudeMeters,
               accuracy_meters AS accuracyMeters, speed_meters_per_second AS speedMetersPerSecond,
               bearing_degrees AS bearingDegrees, provider, recorded_at_utc AS recordedAtUtc,
               source, policy_mode AS policyMode, schedule_low_frequency AS scheduleLowFrequency,
               motion_state AS motionState, sync_status AS syncStatus
        FROM mobile_location_points
        WHERE recorded_at_utc >= :from AND recorded_at_utc <= :to
        ORDER BY recorded_at_utc ASC
        """
    )
    suspend fun diagnosticLocations(from: Long, to: Long): List<DiagnosticLocationRow>

    /**
     * 丢弃原因明细（REQ-9 / AC-9.1）：时刻 / 原因 / 准确度 / provider / 策略档。
     * 不设条数上限，条数由 30 天时间清理决定（AC-9.3）。
     */
    @Query(
        """
        SELECT recorded_at_utc AS recordedAtUtc, reason, accuracy_meters AS accuracyMeters,
               provider, policy_mode AS policyMode
        FROM mobile_location_dropped_diagnostics
        ORDER BY recorded_at_utc ASC
        """
    )
    suspend fun diagnosticDroppedDetailRows(): List<DiagnosticDroppedDetailRow>

    @Query("DELETE FROM mobile_logs")
    suspend fun deleteAllMobileLogs(): Int

    @Query("DELETE FROM mobile_location_dropped_diagnostics")
    suspend fun deleteAllMobileLocationDroppedDiagnostics(): Int

    @Query("DELETE FROM mobile_location_policy_transitions")
    suspend fun deleteAllMobileLocationPolicyTransitions(): Int

    /**
     * 丢弃原因明细的时间清理（REQ-9 / AC-9.3）：超过 30 天的明细移除，
     * **不设条数上限**（上限只由时间决定，R4-P3）。
     */
    @Query("DELETE FROM mobile_location_dropped_diagnostics WHERE recorded_at_utc < :cutoffUtc")
    suspend fun deleteDroppedDiagnosticsOlderThan(cutoffUtc: Long): Int

    @Query("SELECT COUNT(*) FROM mobile_location_dropped_diagnostics")
    suspend fun droppedDiagnosticCount(): Int
}
