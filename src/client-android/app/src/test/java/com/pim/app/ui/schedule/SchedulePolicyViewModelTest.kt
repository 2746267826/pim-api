package com.pim.app.ui.schedule

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.pim.app.location.policy.ScheduleWindow
import com.pim.app.location.service.ForegroundLocationRuntimeState
import com.pim.app.schedule.ScheduleCacheFreshness
import com.pim.app.schedule.ScheduleCacheSnapshot
import com.pim.app.schedule.ScheduleCacheStore
import com.pim.app.schedule.ScheduleRefreshErrorKind
import com.pim.app.schedule.ScheduleWindowRepository
import com.pim.app.settings.TrackingSettings
import com.pim.app.settings.TrackingSettingsStore
import com.pim.core.auth.AuthSessionSnapshot
import com.pim.core.auth.AuthSessionStore
import com.pim.core.models.ApiResponse
import com.pim.core.models.EventResponse
import com.pim.core.network.ApiService
import com.pim.core.settings.ServerSettingsStore
import java.io.File
import java.io.IOException
import java.time.Instant
import java.time.LocalDate
import java.time.ZoneId
import java.time.ZoneOffset
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.test.setMain
import kotlinx.serialization.json.Json
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class SchedulePolicyViewModelTest {

    private lateinit var api: FakeApiService
    private lateinit var cacheStore: ScheduleCacheStore
    private lateinit var serverSettings: ServerSettingsStore
    private lateinit var trackingSettingsStore: TrackingSettingsStore
    private lateinit var repo: ScheduleWindowRepository
    private lateinit var cacheDir: File
    private val json = Json { ignoreUnknownKeys = true }

    private val mainDispatcher = UnconfinedTestDispatcher()

    @Before
    fun setUp() {
        Dispatchers.setMain(mainDispatcher)
        val context = ApplicationProvider.getApplicationContext<Context>()
        cacheDir = File(context.filesDir, "schedule-viewmodel-test-" + System.nanoTime())
        cacheDir.deleteRecursively()
        cacheDir.mkdirs()
        api = FakeApiService()
        cacheStore = ScheduleCacheStore(cacheDir, json)
        serverSettings = ServerSettingsStore(context, FakeAuthSessionStore())
        serverSettings.setBaseUrl("http://test-server:5858/api/v1/")
        trackingSettingsStore = TrackingSettingsStore(
            context.getSharedPreferences("tracking-test-" + System.nanoTime(), Context.MODE_PRIVATE)
        )
        repo = ScheduleWindowRepository(
            apiService = api,
            cacheStore = cacheStore,
            serverSettingsStore = serverSettings
        )
    }

    @After
    fun tearDown() {
        Dispatchers.resetMain()
        cacheDir.deleteRecursively()
    }

    // ─── Mapper: five states ──────────────────────────────────────────

    @Test
    fun `missing without error maps to Loading`() {
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(freshness = ScheduleCacheFreshness.Missing, lastError = null, lastSuccessAtMillis = null),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1000L,
            zoneId = ZoneOffset.UTC
        )
        assertTrue(state is SchedulePolicyUiState.Loading)
    }

    @Test
    fun `fresh with nonempty windows maps to Content`() {
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(
                freshness = ScheduleCacheFreshness.Fresh,
                windows = listOf(window(id = "e1", start = 1000L, end = 2000L)),
                lastSuccessAtMillis = 500L
            ),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1500L,
            zoneId = ZoneOffset.UTC
        )
        assertTrue(state is SchedulePolicyUiState.Content)
        val content = (state as SchedulePolicyUiState.Content).content
        assertEquals("e1", content.currentEvent?.id)
        assertNull(content.nextEvent)
    }

    @Test
    fun `fresh with empty windows maps to Empty`() {
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(freshness = ScheduleCacheFreshness.Fresh, windows = emptyList()),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1000L,
            zoneId = ZoneOffset.UTC
        )
        assertTrue(state is SchedulePolicyUiState.Empty)
    }

    @Test
    fun `stale with nonempty windows maps to StaleContent`() {
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(
                freshness = ScheduleCacheFreshness.Stale,
                windows = listOf(window(id = "e1")),
                lastSuccessAtMillis = 100L
            ),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = true,
            nowMillis = 1500L,
            zoneId = ZoneOffset.UTC
        )
        assertTrue(state is SchedulePolicyUiState.StaleContent)
        assertTrue((state as SchedulePolicyUiState.StaleContent).content.isRefreshing)
    }

    @Test
    fun `stale with empty windows maps to StaleContent`() {
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(
                freshness = ScheduleCacheFreshness.Stale,
                windows = emptyList(),
                lastSuccessAtMillis = 100L
            ),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1500L,
            zoneId = ZoneOffset.UTC
        )
        assertTrue("Stale with empty cache must still be StaleContent", state is SchedulePolicyUiState.StaleContent)
    }

    @Test
    fun `missing with error maps to Error`() {
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(
                freshness = ScheduleCacheFreshness.Missing,
                lastError = "网络不可用",
                errorKind = ScheduleRefreshErrorKind.Network,
                lastSuccessAtMillis = null
            ),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1000L,
            zoneId = ZoneOffset.UTC
        )
        assertTrue(state is SchedulePolicyUiState.Error)
        val err = state as SchedulePolicyUiState.Error
        assertEquals(ScheduleRefreshErrorKind.Network, err.errorKind)
        assertEquals("网络不可用", err.message)
    }

    // ─── Mapper: Empty/Error metadata ─────────────────────────────────

    @Test
    fun `Empty state carries ScheduleContentModel with empty grouped windows and null current next`() {
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(freshness = ScheduleCacheFreshness.Fresh, windows = emptyList()),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1000L,
            zoneId = ZoneOffset.UTC
        )
        assertTrue(state is SchedulePolicyUiState.Empty)
        val content = (state as SchedulePolicyUiState.Empty).content
        assertTrue(content.windowsByDate.isEmpty())
        assertNull(content.currentEvent)
        assertNull(content.nextEvent)
        assertFalse(content.isRefreshing)
    }

    @Test
    fun `Error state carries ScheduleContentModel with empty grouped windows and retains error info`() {
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(
                freshness = ScheduleCacheFreshness.Missing,
                lastError = "网络不可用",
                errorKind = ScheduleRefreshErrorKind.Network,
                lastSuccessAtMillis = 500L
            ),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = true,
            nowMillis = 1000L,
            zoneId = ZoneOffset.UTC
        )
        assertTrue(state is SchedulePolicyUiState.Error)
        val err = state as SchedulePolicyUiState.Error
        assertEquals(ScheduleRefreshErrorKind.Network, err.errorKind)
        assertEquals("网络不可用", err.message)
        assertTrue(err.content.windowsByDate.isEmpty())
        assertNull(err.content.currentEvent)
        assertNull(err.content.nextEvent)
        assertTrue(err.content.isRefreshing)
    }

    // ─── Mapper: current/next half-open boundaries ────────────────────

    @Test
    fun `current event is active at start inclusive`() {
        val w = window(id = "e1", start = 1000L, end = 2000L)
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(windows = listOf(w), freshness = ScheduleCacheFreshness.Fresh),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1000L,
            zoneId = ZoneOffset.UTC
        )
        assertEquals("e1", (state as SchedulePolicyUiState.Content).content.currentEvent?.id)
    }

    @Test
    fun `current event is inactive at end exclusive`() {
        val w = window(id = "e1", start = 1000L, end = 2000L)
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(windows = listOf(w), freshness = ScheduleCacheFreshness.Fresh),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 2000L,
            zoneId = ZoneOffset.UTC
        )
        assertNull((state as SchedulePolicyUiState.Content).content.currentEvent)
    }

    @Test
    fun `current event is active just before end`() {
        val w = window(id = "e1", start = 1000L, end = 2000L)
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(windows = listOf(w), freshness = ScheduleCacheFreshness.Fresh),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1999L,
            zoneId = ZoneOffset.UTC
        )
        assertEquals("e1", (state as SchedulePolicyUiState.Content).content.currentEvent?.id)
    }

    @Test
    fun `next event is earliest future start`() {
        val windows = listOf(
            window(id = "e1", start = 1000L, end = 2000L),
            window(id = "e2", start = 3000L, end = 4000L),
            window(id = "e3", start = 5000L, end = 6000L)
        )
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(windows = windows, freshness = ScheduleCacheFreshness.Fresh),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 2000L,
            zoneId = ZoneOffset.UTC
        )
        assertEquals("e2", (state as SchedulePolicyUiState.Content).content.nextEvent?.id)
    }

    // ─── Mapper: local-date grouping ──────────────────────────────────

    @Test
    fun `windows grouped by device local date`() {
        val windows = listOf(
            window(id = "d1", start = Instant.parse("2026-07-08T00:00:00Z").toEpochMilli()),
            window(id = "d2", start = Instant.parse("2026-07-09T00:00:00Z").toEpochMilli()),
            window(id = "d3", start = Instant.parse("2026-07-09T12:00:00Z").toEpochMilli())
        )
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(windows = windows, freshness = ScheduleCacheFreshness.Fresh),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1000L,
            zoneId = ZoneOffset.UTC
        )
        val byDate = (state as SchedulePolicyUiState.Content).content.windowsByDate
        assertEquals(setOf(LocalDate.of(2026, 7, 8), LocalDate.of(2026, 7, 9)), byDate.keys)
        assertEquals(1, byDate[LocalDate.of(2026, 7, 8)]?.size)
        assertEquals(2, byDate[LocalDate.of(2026, 7, 9)]?.size)
    }

    @Test
    fun `windows grouped across utc boundary`() {
        val windows = listOf(
            window(id = "before", start = Instant.parse("2026-07-08T14:00:00Z").toEpochMilli()),
            window(id = "after", start = Instant.parse("2026-07-08T22:00:00Z").toEpochMilli())
        )
        val tokyo = ZoneId.of("Asia/Tokyo")
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(windows = windows, freshness = ScheduleCacheFreshness.Fresh),
            runtimeState = ForegroundLocationRuntimeState(),
            settings = TrackingSettings.defaults(),
            refreshing = false,
            nowMillis = 1000L,
            zoneId = tokyo
        )
        val byDate = (state as SchedulePolicyUiState.Content).content.windowsByDate
        assertEquals(setOf(LocalDate.of(2026, 7, 8), LocalDate.of(2026, 7, 9)), byDate.keys)
        assertEquals("before", byDate[LocalDate.of(2026, 7, 8)]?.single()?.id)
        assertEquals("after", byDate[LocalDate.of(2026, 7, 9)]?.single()?.id)
    }

    // ─── Mapper: policy summary ───────────────────────────────────────

    @Test
    fun `policy summary maps from runtime state and settings`() {
        val runtime = ForegroundLocationRuntimeState(
            currentPolicyMode = "ScheduleLowFrequency",
            currentPolicyReason = "当前日程时段，降低定位频率",
            requestIntervalMillis = 900000L
        )
        val settings = TrackingSettings.defaults().copy(scheduleRecoveryThresholdMeters = 200.0)
        val state = SchedulePolicyMapper.stateFor(
            snapshot = snapshot(freshness = ScheduleCacheFreshness.Fresh, windows = listOf(window())),
            runtimeState = runtime,
            settings = settings,
            refreshing = false,
            nowMillis = 1500L,
            zoneId = ZoneOffset.UTC
        )
        val summary = (state as SchedulePolicyUiState.Content).content.policySummary
        assertEquals("ScheduleLowFrequency", summary.mode)
        assertEquals("当前日程时段，降低定位频率", summary.reason)
        assertEquals(900000L, summary.requestIntervalMillis)
        assertEquals(200.0, summary.recoveryThresholdMeters, 0.001)
    }

    // ─── ViewModel: initialisation and refresh behavior ───────────────

    @Test
    fun `init triggers refreshIfStale with force false`() = runTest {
        api.events = listOf(event(id = "init-event"))
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)

        val state = vm.state.first { it !is SchedulePolicyUiState.Loading }
        assertTrue("Expected Content after init refresh", state is SchedulePolicyUiState.Content)
        assertEquals(1, api.callCount)
    }

    @Test
    fun `empty api response leads to Empty state`() = runTest {
        api.events = emptyList()
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)

        val state = vm.state.first { it !is SchedulePolicyUiState.Loading }
        assertTrue("Expected Empty after empty API response", state is SchedulePolicyUiState.Empty)
    }

    @Test
    fun `fresh snapshot emits Content and windows are accessible`() = runTest {
        val now = System.currentTimeMillis()
        val currentStart = Instant.ofEpochMilli(now - 3600_000).toString()
        val currentEnd = Instant.ofEpochMilli(now + 3600_000).toString()
        val futureStart = Instant.ofEpochMilli(now + 7200_000).toString()
        val futureEnd = Instant.ofEpochMilli(now + 10800_000).toString()

        api.events = listOf(
            event(id = "current-event", start = currentStart, end = currentEnd),
            event(id = "future-event", start = futureStart, end = futureEnd)
        )
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)

        val state = vm.state.first { it is SchedulePolicyUiState.Content }
        val content = (state as SchedulePolicyUiState.Content).content
        assertEquals("current-event", content.currentEvent?.id)
        assertEquals("future-event", content.nextEvent?.id)
        assertEquals(2, content.windowsByDate.values.flatten().size)
    }

    @Test
    fun `stale cache with prior success produces StaleContent`() = runTest {
        val oldNow = System.currentTimeMillis() - 30 * 60 * 1000L
        val oldEvent = event(
            id = "cached",
            start = Instant.ofEpochMilli(oldNow - 3600_000).toString(),
            end = Instant.ofEpochMilli(oldNow + 3600_000).toString()
        )
        api.events = listOf(oldEvent)
        repo.refreshIfStale(force = true, nowMillis = oldNow)
        assertEquals(1, api.callCount)

        api.failNext = IOException("network error")
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)

        val state = vm.state.first { it is SchedulePolicyUiState.StaleContent }
        assertEquals("cached", (state as SchedulePolicyUiState.StaleContent).content.currentEvent?.id)
        assertFalse((state as SchedulePolicyUiState.StaleContent).content.windowsByDate.isEmpty())
    }

    @Test
    fun `network error without cache produces Error`() = runTest {
        api.failNext = IOException("network error")
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)

        val state = vm.state.first { it !is SchedulePolicyUiState.Loading }
        assertTrue("Expected Error after failed refresh without cache", state is SchedulePolicyUiState.Error)
        val err = state as SchedulePolicyUiState.Error
        assertEquals(ScheduleRefreshErrorKind.Network, err.errorKind)
    }

    @Test
    fun `refresh forces another API call`() = runTest {
        api.events = listOf(event(id = "first"))
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)

        vm.state.first { it !is SchedulePolicyUiState.Loading }
        assertEquals(1, api.callCount)

        api.events = listOf(event(id = "second"))
        vm.refresh()

        vm.state.first { it is SchedulePolicyUiState.Content }
        assertEquals(2, api.callCount)
    }

    @Test
    fun `retry forces another API call`() = runTest {
        api.events = listOf(event(id = "first"))
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)

        vm.state.first { it !is SchedulePolicyUiState.Loading }
        assertEquals(1, api.callCount)

        api.events = listOf(event(id = "second"))
        vm.retry()

        vm.state.first { it is SchedulePolicyUiState.Content }
        assertEquals(2, api.callCount)
    }

    @Test
    fun `overlapping refresh calls are suppressed`() = runTest {
        api.events = listOf(event(id = "first"))
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)

        vm.state.first { it !is SchedulePolicyUiState.Loading }
        assertEquals(1, api.callCount)

        val blocker = CompletableDeferred<Unit>()
        api.block = blocker

        vm.refresh()
        vm.refresh()
        blocker.complete(Unit)

        assertEquals(2, api.callCount)
    }

    @Test
    fun `refreshIfStale after server URL change fetches new server content`() = runTest {
        val now = System.currentTimeMillis()

        serverSettings.setBaseUrl("http://server-a:5858/api/v1/")
        api.events = listOf(
            event(
                id = "from-server-a",
                start = Instant.ofEpochMilli(now - 3600_000).toString(),
                end = Instant.ofEpochMilli(now + 3600_000).toString()
            )
        )
        val vm = SchedulePolicyViewModel(repo, trackingSettingsStore)
        vm.state.first { state ->
            state is SchedulePolicyUiState.Content &&
                state.content.windowsByDate.values.flatten().singleOrNull()?.id == "from-server-a"
        }
        assertEquals(1, api.callCount)

        serverSettings.setBaseUrl("http://server-b:5858/api/v1/")
        api.events = listOf(
            event(
                id = "from-server-b",
                start = Instant.ofEpochMilli(now - 3600_000).toString(),
                end = Instant.ofEpochMilli(now + 3600_000).toString()
            )
        )

        vm.refreshIfStale()
        val state = vm.state.first { candidate ->
            candidate is SchedulePolicyUiState.Content &&
                candidate.content.windowsByDate.values.flatten().singleOrNull()?.id == "from-server-b"
        }
        val content = (state as SchedulePolicyUiState.Content).content
        assertEquals("from-server-b", content.currentEvent?.id)
        assertEquals(2, api.callCount)
    }

    // ─── Helpers ──────────────────────────────────────────────────────

    private fun snapshot(
        freshness: ScheduleCacheFreshness = ScheduleCacheFreshness.Fresh,
        windows: List<ScheduleWindow> = emptyList(),
        lastAttemptAtMillis: Long? = null,
        lastSuccessAtMillis: Long? = null,
        lastError: String? = null,
        errorKind: ScheduleRefreshErrorKind? = null
    ): ScheduleCacheSnapshot = ScheduleCacheSnapshot(
        serverIdentity = "http://test-server:5858",
        windows = windows,
        freshness = freshness,
        lastAttemptAtMillis = lastAttemptAtMillis,
        lastSuccessAtMillis = lastSuccessAtMillis,
        lastError = lastError,
        errorKind = errorKind
    )

    private fun window(
        id: String = "e1",
        title: String = "事件",
        locationText: String = "",
        start: Long = 1000L,
        end: Long = 2000L
    ): ScheduleWindow = ScheduleWindow(
        id = id,
        title = title,
        locationText = locationText,
        startsAtMillis = start,
        endsAtMillis = end
    )

    private fun event(
        id: String = "evt1",
        title: String = "事件",
        location: String? = null,
        start: String = "2026-07-08T01:00:00Z",
        end: String = "2026-07-08T02:00:00Z"
    ): EventResponse = EventResponse(
        id = id, title = title, location = location,
        dtStart = start, dtEnd = end
    )

    class FakeApiService : ApiService {
        var events: List<EventResponse> = emptyList()
        var failNext: Throwable? = null
        var callCount = 0
        var block: CompletableDeferred<Unit>? = null
        var capturedStart: String? = null
        var capturedEnd: String? = null

        override suspend fun getEvents(start: String, end: String, page: Int?, pageSize: Int?): ApiResponse<com.pim.core.models.PagedResult<EventResponse>> {
            callCount++
            capturedStart = start
            capturedEnd = end
            block?.await()
            failNext?.let { t ->
                failNext = null
                throw t
            }
            return ApiResponse(code = 0, message = "ok", data = com.pim.core.models.PagedResult(items = events, page = 1, pageSize = 100, totalCount = events.size, totalPages = 1))
        }

        override suspend fun login(body: com.pim.core.models.LoginRequest) = error("not mocked")
        override suspend fun register(body: com.pim.core.models.RegisterRequest) = error("not mocked")
        override suspend fun refresh(body: com.pim.core.models.RefreshRequest) = error("not mocked")
        override suspend fun getCalendars() = error("not mocked")
        override suspend fun createCalendar(body: com.pim.core.models.CreateCalendarRequest) = error("not mocked")
        override suspend fun createEvent(body: com.pim.core.models.CreateEventRequest) = error("not mocked")
        override suspend fun updateEvent(id: String, body: com.pim.core.models.CreateEventRequest) = error("not mocked")
        override suspend fun deleteEvent(id: String) = error("not mocked")
        override suspend fun getTasks(inbox: Boolean?) = error("not mocked")
        override suspend fun createTask(body: com.pim.core.models.CreateTaskRequest) = error("not mocked")
        override suspend fun updateTask(id: String, body: com.pim.core.models.CreateTaskRequest) = error("not mocked")
        override suspend fun deleteTask(id: String) = error("not mocked")
        override suspend fun search(query: String, type: String?) = error("not mocked")
        override suspend fun importIcs(body: okhttp3.RequestBody) = error("not mocked")
        override suspend fun exportIcs(start: String, end: String) = error("not mocked")
        override suspend fun syncOutlook() = error("not mocked")
        override suspend fun uploadStats(batch: com.pim.core.models.UploadBatch) = error("not mocked")
        override suspend fun registerMobileDevice(body: com.pim.core.models.MobileDeviceRegisterRequest) = error("not mocked")
        override suspend fun getMobileGaps(body: com.pim.core.models.MobileGapRequest) = error("not mocked")
        override suspend fun uploadMobileUsage(body: com.pim.core.models.MobileUsageEventsUploadRequest) = error("not mocked")
        override suspend fun uploadMobileForensics(body: com.pim.core.models.MobileForensicsUploadRequest) = error("not mocked")
        override suspend fun uploadMobileLocation(body: com.pim.core.models.MobileLocationPointRequest) = error("not mocked")
        override suspend fun uploadMobileLocationsBatch(request: com.pim.core.models.MobileLocationPointsUploadRequest) = error("not used")
        override suspend fun getMobileSummary(date: String?, deviceId: String?) = error("not mocked")
        override suspend fun getMobileTimeline(date: String?, deviceId: String?, page: Int?, pageSize: Int?) = error("not mocked")
        override suspend fun getMobileQuality(date: String?, deviceId: String?, rangeStartUtc: String?, rangeEndUtc: String?) = error("not mocked")
        override suspend fun getMobileLocationHistory(rangeStartUtc: String?, rangeEndUtc: String?, deviceId: String?, maxAccuracyMeters: Double, includeRejected: Boolean, cursor: String?, pageSize: Int?) = error("not mocked")
        override suspend fun getMobileLocationOverview(rangeStartUtc: String, rangeEndUtc: String, deviceId: String?, maxAccuracyMeters: Double) = error("not mocked")
        override suspend fun getMobileLocationTracks(rangeStartUtc: String, rangeEndUtc: String, deviceId: String?, maxAccuracyMeters: Double) = error("not mocked")
        override suspend fun getMobileLocationSegmentPoints(segmentId: String, rangeStartUtc: String?, rangeEndUtc: String?, timezone: String?, deviceId: String?, maxAccuracyMeters: Double, includeRejected: Boolean, cursor: String?, pageSize: Int?) = error("not mocked")
        override suspend fun sendHeartbeat(body: com.pim.core.models.DaemonHeartbeatRequest) = error("not mocked")
        override suspend fun sendEndpointNotificationAction(deviceId: String, body: com.pim.core.models.EndpointNotificationActionRequestDto) = error("not mocked")
        override suspend fun getClientLatest() = com.pim.core.models.ClientShellLatestResponse()
    }

    class FakeAuthSessionStore : AuthSessionStore {
        private var snap = AuthSessionSnapshot(
            tokens = com.pim.core.auth.AuthTokens("a", "r", Long.MAX_VALUE),
            serverIdentity = null
        )
        override fun snapshot() = snap
        override fun save(accessToken: String, refreshToken: String, expiresAtUtcMillis: Long, serverIdentity: String): Boolean {
            snap = AuthSessionSnapshot(
                tokens = com.pim.core.auth.AuthTokens(accessToken, refreshToken, expiresAtUtcMillis),
                serverIdentity = serverIdentity
            )
            return true
        }
        override fun clear(): Boolean {
            snap = AuthSessionSnapshot(tokens = null, serverIdentity = null)
            return true
        }
    }
}
