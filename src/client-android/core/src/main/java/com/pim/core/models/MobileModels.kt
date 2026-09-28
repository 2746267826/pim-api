package com.pim.core.models

import kotlinx.serialization.Serializable

@Serializable
data class MobileDeviceRegisterRequest(
    val deviceId: String,
    val androidIdHash: String? = null,
    val displayName: String,
    val manufacturer: String,
    val brand: String,
    val model: String,
    val androidVersion: String,
    val sdkInt: Int,
    val appVersion: String,
    val metadataJson: String
)

@Serializable
data class MobileDeviceDto(
    val id: String,
    val deviceId: String,
    val androidIdHash: String? = null,
    val displayName: String,
    val manufacturer: String,
    val brand: String,
    val model: String,
    val androidVersion: String,
    val sdkInt: Int,
    val appVersion: String,
    val metadataJson: String,
    val firstSeenAt: String,
    val lastSeenAt: String,
    val lastHeartbeatAt: String? = null,
    val lastSyncAt: String? = null,
    val isActive: Boolean
)

@Serializable
data class MobileGapRequest(
    val deviceId: String,
    val rangeStartUtc: String,
    val rangeEndUtc: String,
    val capabilityJson: String
)

@Serializable
data class MobileGapResponse(
    val maxBackfillStartUtc: String,
    val windows: List<MobileGapWindowDto> = emptyList()
)

@Serializable
data class MobileGapWindowDto(
    val windowStartUtc: String,
    val windowEndUtc: String,
    val reason: String,
    val sourcePreference: String
)

@Serializable
data class MobileUsageEventsUploadRequest(
    val deviceId: String,
    val clientBatchId: String,
    val sourceWindowStartUtc: String,
    val sourceWindowEndUtc: String,
    val apps: List<MobileAppMetadataDto>,
    val events: List<MobileUsageEventDto>,
    val fallbackSummaries: List<MobileUsageSummaryDto>
)

@Serializable
data class MobileAppMetadataDto(
    val packageName: String,
    val displayName: String,
    val versionName: String? = null,
    val versionCode: Long,
    val isSystemApp: Boolean,
    val categoryName: String? = null,
    val installerPackageName: String? = null,
    val firstInstallTimeUtc: String,
    val lastUpdateTimeUtc: String,
    val rawJson: String,
    val collectedAtUtc: String? = null,
    val clientItemKey: String? = null
)

@Serializable
data class MobileUsageEventDto(
    val packageName: String,
    val eventType: String,
    val eventTimestampUtc: String,
    val className: String? = null,
    val collectedAtUtc: String,
    val rawJson: String,
    val clientItemKey: String? = null
)

@Serializable
data class MobileUsageSummaryDto(
    val packageName: String,
    val windowStartUtc: String,
    val windowEndUtc: String,
    val totalTimeForegroundMs: Long,
    val lastTimeUsedUtc: String,
    val sourceKind: String,
    val rawJson: String,
    val clientItemKey: String? = null
)

@Serializable
data class MobileIngestItemResult(
    val clientItemKey: String,
    val entityType: String,
    val outcome: String,
    val code: String,
    val message: String
)

@Serializable
data class MobileIngestResponse(
    val batchId: String,
    val acceptedCount: Int = 0,
    val skippedCount: Int = 0,
    val rejectedCount: Int = 0,
    val failedCount: Int = 0,
    val itemResults: List<MobileIngestItemResult> = emptyList()
)

@Serializable
data class MobileLocationPointRequest(
    val deviceId: String,
    val recordedAtUtc: String,
    val latitude: Double,
    val longitude: Double,
    val horizontalAccuracyMeters: Double,
    val provider: String,
    val sourceKind: String,
    val altitudeMeters: Double? = null,
    val verticalAccuracyMeters: Double? = null,
    val speedMetersPerSecond: Double? = null,
    val speedAccuracyMetersPerSecond: Double? = null,
    val bearingDegrees: Double? = null,
    val bearingAccuracyDegrees: Double? = null,
    val isAutoSubmitted: Boolean,
    val rawJson: String
)

/**
 * 批量定位点上传（积压补传）：一次请求携带多个点。
 *
 * 服务端单批上限 1000 条（`MobileLocationService.MaxBatchPoints`），超限整批被拒。
 */
@Serializable
data class MobileLocationPointsUploadRequest(
    val points: List<MobileLocationPointRequest>
)

/**
 * 批量上传结果：**逐条**返回，客户端据此把「已接受 / 重复 / 被拒」映射回本地队列。
 *
 * 服务端按请求顺序逐条产出 `itemResults`（条数 == 请求点数），客户端以顺序对齐本地行。
 */
@Serializable
data class MobileLocationPointsUploadResult(
    val acceptedCount: Int = 0,
    val skippedCount: Int = 0,
    val rejectedCount: Int = 0,
    val itemResults: List<MobileIngestItemResult> = emptyList()
)

@Serializable
data class MobileLocationPointDto(
    val id: String,
    val deviceId: String,
    val recordedAtUtc: String,
    val submittedAtUtc: String,
    val latitude: Double,
    val longitude: Double,
    val horizontalAccuracyMeters: Double,
    val provider: String,
    val sourceKind: String,
    val altitudeMeters: Double? = null,
    val verticalAccuracyMeters: Double? = null,
    val speedMetersPerSecond: Double? = null,
    val speedAccuracyMetersPerSecond: Double? = null,
    val bearingDegrees: Double? = null,
    val bearingAccuracyDegrees: Double? = null,
    val isAutoSubmitted: Boolean,
    val quality: String,
    val rawJson: String
)

@Serializable
data class MobileAppUsageSummaryDto(
    val packageName: String,
    val displayName: String,
    val categoryName: String? = null,
    val foregroundSeconds: Long,
    val sessionCount: Int,
    val launchCount: Int,
    val lastUsedAt: String? = null,
    val source: String,
    val share: Double
)

@Serializable
data class MobileSyncBatchSummaryDto(
    val id: String,
    val deviceId: String,
    val clientBatchId: String,
    val sourceWindowStartUtc: String,
    val sourceWindowEndUtc: String,
    val submittedAtUtc: String,
    val status: String,
    val acceptedEventCount: Int,
    val skippedEventCount: Int,
    val acceptedLocationCount: Int,
    val rejectedLocationCount: Int,
    val errorMessage: String? = null
)

@Serializable
data class MobileUsageSummaryResponse(
    val date: String,
    val deviceId: String? = null,
    val generatedAt: String,
    val totalForegroundSeconds: Long,
    val fallbackForegroundSeconds: Long,
    val appSwitchCount: Int,
    val appsUsed: Int,
    val completeness: Double,
    val lastSyncAt: String? = null,
    val appRanking: List<MobileAppUsageSummaryDto> = emptyList(),
    val syncBatches: List<MobileSyncBatchSummaryDto> = emptyList(),
    val qualityIssueCount: Int
)

@Serializable
data class MobileTimelineItemDto(
    val id: String,
    val kind: String,
    val deviceId: String,
    val packageName: String,
    val displayName: String,
    val start: String,
    val end: String? = null,
    val durationSeconds: Long,
    val source: String,
    val confidence: Double,
    val reason: String
)

@Serializable
data class MobileTimelineResponse(
    val date: String,
    val deviceId: String? = null,
    val generatedAt: String,
    val sessions: List<MobileTimelineItemDto> = emptyList(),
    val fallbackSummaries: List<MobileTimelineItemDto> = emptyList(),
    val items: List<MobileTimelineItemDto> = emptyList(),
    // #330：单日会话可达数千条，服务端分页返回；hasMore/truncated 为 true 时
    // sessions/items 只是当前页，需继续翻页才能读到全天数据。
    val page: Int = 1,
    val pageSize: Int = 5000,
    val totalCount: Int = 0,
    val sessionTotalCount: Int = 0,
    val fallbackTotalCount: Int = 0,
    val hasMore: Boolean = false,
    val truncated: Boolean = false
)

@Serializable
data class MobileLocationHistoryResponse(
    val start: String? = null,
    val end: String? = null,
    val deviceId: String? = null,
    val maxAccuracyMeters: Double,
    val points: List<MobileLocationPointDto> = emptyList()
)

@Serializable
data class MobileQualityResponse(
    val overallStatus: String,
    val label: String,
    val message: String,
    val checkedAt: String,
    val components: List<MobileQualityComponentDto> = emptyList(),
    val issues: List<MobileQualityIssueDto> = emptyList(),
    val nextSteps: List<String> = emptyList()
)

@Serializable
data class MobileQualityComponentDto(
    val key: String,
    val name: String,
    val status: String,
    val message: String,
    val checkedAt: String,
    val details: Map<String, String> = emptyMap()
)

@Serializable
data class MobileQualityIssueDto(
    val code: String,
    val severity: String,
    val componentKey: String,
    val message: String,
    val nextStep: String? = null
)

@Serializable
data class MobileAnalyticsRangeDto(
    val rangeStartUtc: String,
    val rangeEndUtc: String,
    val timezone: String,
    val localStartDate: String,
    val localEndDate: String
)

@Serializable
data class MobileGeoBoundsDto(
    val minLatitude: Double,
    val minLongitude: Double,
    val maxLatitude: Double,
    val maxLongitude: Double
)

@Serializable
data class MobileLocationAnalyticsOverviewResponse(
    val range: MobileAnalyticsRangeDto,
    val generatedAt: String,
    val pointCount: Int,
    val usablePointCount: Int,
    val rejectedPointCount: Int,
    val activeSpanSeconds: Long,
    val distanceMeters: Double,
    val stayCount: Int,
    val longestStaySeconds: Long,
    val averageAccuracyMeters: Double,
    val qualityIssueCount: Int,
    val qualityFlags: List<String> = emptyList()
)

@Serializable
data class MobileLocationPathPointDto(
    val id: String,
    val recordedAtUtc: String,
    val latitude: Double,
    val longitude: Double,
    val horizontalAccuracyMeters: Double,
    val quality: String
)

@Serializable
data class MobileLocationSegmentDto(
    val id: String,
    val trackId: String,
    val deviceId: String,
    val kind: String,
    val startUtc: String,
    val endUtc: String,
    val localStart: String,
    val localEnd: String,
    val durationSeconds: Long,
    val distanceMeters: Double,
    val pointCount: Int,
    val averageSpeedMetersPerSecond: Double,
    val averageAccuracyMeters: Double,
    val maxAccuracyMeters: Double,
    val quality: String,
    val qualityFlags: List<String> = emptyList(),
    val bounds: MobileGeoBoundsDto? = null,
    val path: List<MobileLocationPathPointDto> = emptyList()
)

@Serializable
data class MobileLocationTrackDto(
    val id: String,
    val deviceId: String,
    val startUtc: String,
    val endUtc: String,
    val distanceMeters: Double,
    val durationSeconds: Long,
    val pointCount: Int,
    val segmentCount: Int,
    val bounds: MobileGeoBoundsDto? = null,
    val qualityFlags: List<String> = emptyList(),
    val segments: List<MobileLocationSegmentDto> = emptyList()
)

@Serializable
data class MobileLocationSegmentPointPageDto(
    val items: List<MobileLocationPointDto> = emptyList(),
    val nextCursor: String? = null,
    val hasMore: Boolean
)

@Serializable
data class ClientShellLatestResponse(
    val androidVersion: String? = null,
    val androidUrl: String? = null,
    val windowsVersion: String? = null,
    val windowsUrl: String? = null,
    val shellWindowsVersion: String? = null,
    val shellWindowsUrl: String? = null,
    val shellAndroidVersion: String? = null,
    val shellAndroidUrl: String? = null,
    val checkedAt: String? = null,
    val error: String? = null
)
