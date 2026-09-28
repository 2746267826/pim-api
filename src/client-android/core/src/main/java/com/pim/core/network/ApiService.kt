package com.pim.core.network

import com.pim.core.models.*
import okhttp3.RequestBody
import retrofit2.Response
import retrofit2.http.*

interface ApiService {
    // Auth
    @POST("auth/login")
    suspend fun login(@Body request: LoginRequest): ApiResponse<AuthResponse>

    @POST("auth/register")
    suspend fun register(@Body request: RegisterRequest): ApiResponse<AuthResponse>

    @POST("auth/refresh")
    suspend fun refresh(@Body request: RefreshRequest): Response<ApiResponse<AuthResponse>>

    // Calendars
    @GET("calendar/calendars")
    suspend fun getCalendars(): ApiResponse<List<CalendarResponse>>

    @POST("calendar/calendars")
    suspend fun createCalendar(@Body request: CreateCalendarRequest): ApiResponse<CalendarResponse>

    // Events — PIM-024: server now always returns PagedResult; keep legacy list fallback via wrapper handling in repository.
    // New clients should request page/pageSize. Absolute path not needed here as calendar is under /api/v1.
    @GET("calendar/events")
    suspend fun getEvents(
        @Query("start") start: String,
        @Query("end") end: String,
        @Query("page") page: Int? = null,
        @Query("pageSize") pageSize: Int? = null
    ): ApiResponse<PagedResult<EventResponse>>

    @POST("calendar/events")
    suspend fun createEvent(@Body request: CreateEventRequest): ApiResponse<EventResponse>

    @PUT("calendar/events/{id}")
    suspend fun updateEvent(
        @Path("id") id: String,
        @Body request: CreateEventRequest
    ): ApiResponse<EventResponse>

    @DELETE("calendar/events/{id}")
    suspend fun deleteEvent(@Path("id") id: String): ApiResponse<String>

    // Tasks
    @GET("calendar/tasks")
    suspend fun getTasks(
        @Query("inbox") inbox: Boolean? = null
    ): ApiResponse<List<TaskResponse>>

    @POST("calendar/tasks")
    suspend fun createTask(@Body request: CreateTaskRequest): ApiResponse<TaskResponse>

    @PUT("calendar/tasks/{id}")
    suspend fun updateTask(
        @Path("id") id: String,
        @Body request: CreateTaskRequest
    ): ApiResponse<TaskResponse>

    @DELETE("calendar/tasks/{id}")
    suspend fun deleteTask(@Path("id") id: String): ApiResponse<String>

    // Search
    @GET("search")
    suspend fun search(
        @Query("q") query: String,
        @Query("type") type: String? = null
    ): ApiResponse<List<SearchResult>>

    // ICS
    @POST("calendar/import-ics")
    suspend fun importIcs(@Body body: RequestBody): ApiResponse<Int>

    @GET("calendar/export-ics")
    suspend fun exportIcs(
        @Query("start") start: String,
        @Query("end") end: String
    ): ApiResponse<String>

    // Outlook
    @POST("calendar/outlook/sync")
    suspend fun syncOutlook(): ApiResponse<String>

    // Stats
    @POST("stats/upload")
    suspend fun uploadStats(@Body batch: UploadBatch): ApiResponse<Int>

    // Mobile
    @POST("mobile/devices/register")
    suspend fun registerMobileDevice(@Body request: MobileDeviceRegisterRequest): ApiResponse<MobileDeviceDto>

    @POST("mobile/sync/gaps")
    suspend fun getMobileGaps(@Body request: MobileGapRequest): ApiResponse<MobileGapResponse>

    @POST("mobile/usage/events")
    suspend fun uploadMobileUsage(@Body request: MobileUsageEventsUploadRequest): ApiResponse<MobileIngestResponse>

    /** 阶段一取证事件上报（REQ-5）：与使用记录走独立通道。 */
    @POST("mobile/forensics/events")
    suspend fun uploadMobileForensics(
        @Body request: MobileForensicsUploadRequest
    ): ApiResponse<MobileForensicsIngestResponse>

    @POST("mobile/location/points")
    suspend fun uploadMobileLocation(@Body request: MobileLocationPointRequest): ApiResponse<MobileLocationPointDto>

    /**
     * 批量补传通道（客户端积压时用）：一次请求上传多个定位点，逐条返回结果。
     *
     * 与单点端点共用同一套幂等语义（同一 设备+时刻+经纬度 只保留一行）。
     */
    @POST("mobile/location/points/batch")
    suspend fun uploadMobileLocationsBatch(
        @Body request: MobileLocationPointsUploadRequest
    ): ApiResponse<MobileLocationPointsUploadResult>

    @GET("mobile/summary")
    suspend fun getMobileSummary(
        @Query("date") date: String? = null,
        @Query("deviceId") deviceId: String? = null
    ): ApiResponse<MobileUsageSummaryResponse>

    @GET("mobile/timeline")
    suspend fun getMobileTimeline(
        @Query("date") date: String? = null,
        @Query("deviceId") deviceId: String? = null,
        @Query("page") page: Int? = null,
        @Query("pageSize") pageSize: Int? = null
    ): ApiResponse<MobileTimelineResponse>

    @GET("mobile/quality")
    suspend fun getMobileQuality(
        @Query("date") date: String? = null,
        @Query("deviceId") deviceId: String? = null,
        @Query("rangeStartUtc") rangeStartUtc: String? = null,
        @Query("rangeEndUtc") rangeEndUtc: String? = null
    ): ApiResponse<MobileQualityResponse>

    @GET("mobile/location/history")
    suspend fun getMobileLocationHistory(
        @Query("rangeStartUtc") rangeStartUtc: String? = null,
        @Query("rangeEndUtc") rangeEndUtc: String? = null,
        @Query("deviceId") deviceId: String? = null,
        @Query("maxAccuracyMeters") maxAccuracyMeters: Double = 50.0,
        @Query("includeRejected") includeRejected: Boolean = false,
        @Query("cursor") cursor: String? = null,
        @Query("pageSize") pageSize: Int? = null
    ): ApiResponse<MobileLocationHistoryResponse>

    @GET("mobile/location/analytics/overview")
    suspend fun getMobileLocationOverview(
        @Query("rangeStartUtc") rangeStartUtc: String,
        @Query("rangeEndUtc") rangeEndUtc: String,
        @Query("deviceId") deviceId: String? = null,
        @Query("maxAccuracyMeters") maxAccuracyMeters: Double = 50.0
    ): ApiResponse<MobileLocationAnalyticsOverviewResponse>

    @GET("mobile/location/analytics/tracks")
    suspend fun getMobileLocationTracks(
        @Query("rangeStartUtc") rangeStartUtc: String,
        @Query("rangeEndUtc") rangeEndUtc: String,
        @Query("deviceId") deviceId: String? = null,
        @Query("maxAccuracyMeters") maxAccuracyMeters: Double = 50.0
    ): ApiResponse<List<MobileLocationTrackDto>>

    @GET("mobile/location/analytics/segments/{segmentId}/points")
    suspend fun getMobileLocationSegmentPoints(
        @Path("segmentId") segmentId: String,
        @Query("rangeStartUtc") rangeStartUtc: String? = null,
        @Query("rangeEndUtc") rangeEndUtc: String? = null,
        @Query("timezone") timezone: String? = null,
        @Query("deviceId") deviceId: String? = null,
        @Query("maxAccuracyMeters") maxAccuracyMeters: Double = 50.0,
        @Query("includeRejected") includeRejected: Boolean = false,
        @Query("cursor") cursor: String? = null,
        @Query("pageSize") pageSize: Int? = null
    ): ApiResponse<MobileLocationSegmentPointPageDto>

    // Daemon
    @POST("daemon/heartbeat")
    suspend fun sendHeartbeat(@Body request: DaemonHeartbeatRequest): ApiResponse<DaemonHeartbeatDto>

    // Endpoint shell
    @POST("endpoints/{deviceId}/notification-actions")
    suspend fun sendEndpointNotificationAction(
        @Path("deviceId") deviceId: String,
        @Body request: EndpointNotificationActionRequestDto
    ): ApiResponse<EndpointNotificationActionResponseDto>

    @GET("/api/client/shell/latest")
    suspend fun getClientLatest(): ClientShellLatestResponse
}
