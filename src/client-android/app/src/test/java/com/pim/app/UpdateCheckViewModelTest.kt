package com.pim.app

import android.Manifest
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import androidx.work.testing.WorkManagerTestInitHelper
import com.pim.app.data.AppDatabase
import com.pim.app.location.service.ForegroundLocationController
import com.pim.app.mobile.sync.MobileSyncScheduler
import com.pim.app.permissions.PermissionStatusRepository
import com.pim.app.mobile.usage.UsageAccessChecker
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.status.ConnectionProbeService
import com.pim.app.status.ConnectionProbeStore
import com.pim.app.status.ProbeTokenSource
import com.pim.app.ui.settings.SettingsViewModel
import com.pim.app.ui.settings.WebViewSiteDataCleaner
import com.pim.app.schedule.ScheduleCacheStore
import com.pim.app.mobile.diagnostics.DiagnosticOperations
import com.pim.app.mobile.diagnostics.DiagnosticExportResult
import com.pim.app.recovery.RunningStateRestorer
import com.pim.core.auth.ServerBoundLoginCoordinator
import com.pim.core.auth.ServerBoundLoginTransport
import com.pim.core.auth.TokenManager
import com.pim.core.network.ApiService
import com.pim.core.settings.ServerSettingsStore
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.resetMain
import kotlinx.coroutines.test.setMain
import kotlinx.serialization.json.Json
import okhttp3.OkHttpClient
import org.junit.After
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import java.io.File

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = TestPimApp::class)
class UpdateCheckViewModelTest {
    private val mainDispatcher = UnconfinedTestDispatcher()
    private lateinit var context: Context

    @Before
    fun setUp() {
        Dispatchers.setMain(mainDispatcher)
        context = ApplicationProvider.getApplicationContext()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)
    }

    @After
    fun tearDown() {
        Dispatchers.resetMain()
    }

    @Test
    fun `hasUpdate only compares N`() {
        val vm = createViewModel()
        assertTrue(vm.isNewer("2026.08.9", "2026.08.10"))
        assertFalse(vm.isNewer("2026.08.10", "2026.08.9"))
        assertFalse(vm.isNewer("2026.08.12+android.1", "2026.08.12-pr.5"))
    }

    private fun createViewModel(): SettingsViewModel {
        val serverPrefs = context.getSharedPreferences("uc_server", Context.MODE_PRIVATE)
        val authPrefs = context.getSharedPreferences("uc_auth", Context.MODE_PRIVATE)
        serverPrefs.edit().clear().commit()
        authPrefs.edit().clear().commit()
        val tokenManager = TokenManager(context)
        val serverSettings = ServerSettingsStore(context, tokenManager)
        val trackingSettings = TrackingSettingsStore(context.getSharedPreferences("uc_tracking", Context.MODE_PRIVATE))
        val coordinator = ServerBoundLoginCoordinator(serverSettings, tokenManager, ServerBoundLoginTransport { _, _ -> error("not used") })
        val probeStore = ConnectionProbeStore(context.getSharedPreferences("uc_probe", Context.MODE_PRIVATE), Json { ignoreUnknownKeys = true })
        val probeService = ConnectionProbeService(OkHttpClient(), OkHttpClient(), ProbeTokenSource { null })
        val scheduler = MobileSyncScheduler(context, trackingSettings)
        val permissionRepo = PermissionStatusRepository(context, UsageAccessChecker(context))
        val controller = ForegroundLocationController(context)
        val diagOps = object : DiagnosticOperations {
            override suspend fun export(includeRecentLocations: Boolean): DiagnosticExportResult = DiagnosticExportResult(File(context.cacheDir, "dummy"), 0)
            override suspend fun clearDiagnostics() {}
        }
        val restorer = RunningStateRestorer(trackingSettings, permissionRepo, com.pim.app.mobile.logs.StructuredLogRepository(context, trackingSettings) { 0L }, {}, { scheduler.ensurePeriodic() }, { false }, { controller.start() })
        val cleaner = object : WebViewSiteDataCleaner { override fun clearOrigin(origin: String) {} }
        val cacheStore = ScheduleCacheStore(Json { ignoreUnknownKeys = true }, File(context.filesDir, "uc-cache-${System.nanoTime()}"))
        val fakeApi = object : ApiService {
            override suspend fun login(request: com.pim.core.models.LoginRequest) = error("not used")
            override suspend fun register(request: com.pim.core.models.RegisterRequest) = error("not used")
            override suspend fun refresh(request: com.pim.core.models.RefreshRequest) = error("not used")
            override suspend fun getCalendars() = error("not used")
            override suspend fun createCalendar(request: com.pim.core.models.CreateCalendarRequest) = error("not used")
            override suspend fun getEvents(start: String, end: String, page: Int?, pageSize: Int?) = error("not used")
            override suspend fun createEvent(request: com.pim.core.models.CreateEventRequest) = error("not used")
            override suspend fun updateEvent(id: String, request: com.pim.core.models.CreateEventRequest) = error("not used")
            override suspend fun deleteEvent(id: String) = error("not used")
            override suspend fun getTasks(inbox: Boolean?) = error("not used")
            override suspend fun createTask(request: com.pim.core.models.CreateTaskRequest) = error("not used")
            override suspend fun updateTask(id: String, request: com.pim.core.models.CreateTaskRequest) = error("not used")
            override suspend fun deleteTask(id: String) = error("not used")
            override suspend fun search(query: String, type: String?) = error("not used")
            override suspend fun importIcs(body: okhttp3.RequestBody) = error("not used")
            override suspend fun exportIcs(start: String, end: String) = error("not used")
            override suspend fun syncOutlook() = error("not used")
            override suspend fun uploadStats(batch: com.pim.core.models.UploadBatch) = error("not used")
            override suspend fun registerMobileDevice(request: com.pim.core.models.MobileDeviceRegisterRequest) = error("not used")
            override suspend fun getMobileGaps(request: com.pim.core.models.MobileGapRequest) = error("not used")
            override suspend fun uploadMobileUsage(request: com.pim.core.models.MobileUsageEventsUploadRequest) = error("not used")
            override suspend fun uploadMobileForensics(request: com.pim.core.models.MobileForensicsUploadRequest) = error("not used")
            override suspend fun uploadMobileLocation(request: com.pim.core.models.MobileLocationPointRequest) = error("not used")
            override suspend fun uploadMobileLocationsBatch(request: com.pim.core.models.MobileLocationPointsUploadRequest) = error("not used")
            override suspend fun getMobileSummary(date: String?, deviceId: String?) = error("not used")
            override suspend fun getMobileTimeline(date: String?, deviceId: String?, page: Int?, pageSize: Int?) = error("not used")
            override suspend fun getMobileQuality(date: String?, deviceId: String?, rangeStartUtc: String?, rangeEndUtc: String?) = error("not used")
            override suspend fun getMobileLocationHistory(rangeStartUtc: String?, rangeEndUtc: String?, deviceId: String?, maxAccuracyMeters: Double, includeRejected: Boolean, cursor: String?, pageSize: Int?) = error("not used")
            override suspend fun getMobileLocationOverview(rangeStartUtc: String, rangeEndUtc: String, deviceId: String?, maxAccuracyMeters: Double) = error("not used")
            override suspend fun getMobileLocationTracks(rangeStartUtc: String, rangeEndUtc: String, deviceId: String?, maxAccuracyMeters: Double) = error("not used")
            override suspend fun getMobileLocationSegmentPoints(segmentId: String, rangeStartUtc: String?, rangeEndUtc: String?, timezone: String?, deviceId: String?, maxAccuracyMeters: Double, includeRejected: Boolean, cursor: String?, pageSize: Int?) = error("not used")
            override suspend fun sendHeartbeat(request: com.pim.core.models.DaemonHeartbeatRequest) = error("not used")
            override suspend fun sendEndpointNotificationAction(deviceId: String, request: com.pim.core.models.EndpointNotificationActionRequestDto) = error("not used")
            override suspend fun getClientLatest() = com.pim.core.models.ClientShellLatestResponse(androidVersion = null, androidUrl = null, windowsVersion = null, windowsUrl = null, checkedAt = null, error = null)
        }
        return SettingsViewModel(
            serverSettingsStore = serverSettings,
            tokenManager = tokenManager,
            serverBoundLoginCoordinator = coordinator,
            trackingSettingsStore = trackingSettings,
            foregroundLocationController = controller,
            permissionStatusRepository = permissionRepo,
            connectionProbeService = probeService,
            connectionProbeStore = probeStore,
            mobileSyncScheduler = scheduler,
            diagnosticOperations = diagOps,
            runningStateRestorer = restorer,
            webViewSiteDataCleaner = cleaner,
            scheduleCacheStore = cacheStore,
            api = fakeApi,
            policyTransitionHistorySource = object : com.pim.app.status.PolicyTransitionHistorySource {
                override fun observeCurrent() = kotlinx.coroutines.flow.flowOf(com.pim.app.status.PolicyTransitionState())
                override fun observeWindow(limit: Int) =
                    kotlinx.coroutines.flow.flowOf(emptyList<com.pim.app.status.PolicyTransitionSnapshot>())
                override fun observeWindowCount() = kotlinx.coroutines.flow.flowOf(0)
            },
            policyTransitionWriteFailureSource = object : com.pim.app.location.PolicyTransitionWriteFailureSource {
                override val state =
                    kotlinx.coroutines.flow.MutableStateFlow(com.pim.app.location.PolicyTransitionWriteFailure.None)
            },
            appContext = context
        )
    }
}
