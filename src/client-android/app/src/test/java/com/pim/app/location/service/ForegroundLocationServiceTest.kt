package com.pim.app.location.service

import android.Manifest
import android.app.Application
import android.app.Notification
import android.app.NotificationManager
import android.content.Context
import android.content.Intent
import android.location.LocationManager
import android.os.Looper
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import androidx.work.testing.WorkManagerTestInitHelper
import com.google.android.gms.location.Priority
import com.pim.app.TestPimApp
import com.pim.app.data.AppDatabase
import com.pim.app.location.LocationSnapshot
import com.pim.app.location.PolicyTransitionRecorder
import com.pim.app.location.PolicyTransitionWriteFailureStore
import com.pim.app.location.passive.PassiveLocationCoordinator
import com.pim.app.location.passive.PassiveLocationLedger
import com.pim.app.location.passive.PassiveLocationSource
import com.pim.app.location.sprint.LocationSprintController
import com.pim.app.location.sprint.LocationSprintRuntime
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.location.acquisition.AcquisitionPhase
import com.pim.app.location.acquisition.AcquisitionContext
import com.pim.app.location.acquisition.LocationAcquisitionCoordinator
import com.pim.app.location.acquisition.LocationAcquisitionOperations
import com.pim.app.location.acquisition.LocationAcquisitionRunner
import com.pim.app.location.acquisition.LocationAcquisitionState
import com.pim.app.location.acquisition.LocationUpdateRequest
import com.pim.app.location.acquisition.LocationEngineCompletion
import com.pim.app.location.acquisition.LocationEngineRequest
import com.pim.app.location.acquisition.LocationEngineResult
import com.pim.app.location.acquisition.LocationPrerequisiteChecker
import com.pim.app.location.acquisition.LocationPrerequisiteResult
import com.pim.app.location.acquisition.SessionStartResult
import com.pim.app.location.acquisition.TriggerType
import com.pim.app.location.motion.MotionSignalRepository
import com.pim.app.location.policy.LocationPolicyMode
import com.pim.app.location.policy.PolicyDecision
import com.pim.app.location.policy.ScheduleWindow
import com.pim.app.mobile.sync.MobileSyncScheduler
import com.pim.app.notifications.LocationNotificationRenderer
import com.pim.app.notifications.LocationNotificationState
import com.pim.app.schedule.ScheduleCacheDocument
import com.pim.app.schedule.ScheduleCacheFreshness
import com.pim.app.schedule.ScheduleCacheSnapshot
import com.pim.app.schedule.ScheduleCacheStore
import com.pim.app.schedule.ScheduleCacheWindow
import com.pim.app.schedule.ScheduleWindowRepository
import com.pim.app.settings.TrackingSettings
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.status.QueueStatusRepository
import com.pim.app.status.QueueStatusSnapshot
import com.pim.app.testing.InMemorySharedPreferences
import com.pim.core.auth.AuthSessionSnapshot
import com.pim.core.auth.AuthSessionStore
import com.pim.core.models.ApiResponse
import com.pim.core.models.EventResponse
import com.pim.core.network.ApiService
import com.pim.core.settings.PimServerEndpoints
import com.pim.core.settings.ServerSettingsStore
import java.io.IOException
import java.lang.reflect.Proxy
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.Robolectric
import org.robolectric.RobolectricTestRunner
import org.robolectric.Shadows.shadowOf
import org.robolectric.annotation.Config
import org.robolectric.annotation.LooperMode

@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = TestPimApp::class)
class ForegroundLocationServiceTest {
    private val cacheDirs = mutableListOf<java.io.File>()
    private val databases = mutableListOf<AppDatabase>()

    @org.junit.After
    fun cleanUp() {
        cacheDirs.forEach { it.deleteRecursively() }
        cacheDirs.clear()
        databases.forEach { it.close() }
        databases.clear()
    }

    private fun emptyQueueStatusRepo(
        locations: MutableStateFlow<Int> = MutableStateFlow(0)
    ): QueueStatusRepository {
        return QueueStatusRepository(
            locations = locations,
            usageEvents = MutableStateFlow(0),
            usageSummaries = MutableStateFlow(0),
            appMetadata = MutableStateFlow(0),
            deviceProfiles = MutableStateFlow(0),
            syncBatches = MutableStateFlow(0)
        )
    }

    private fun newHarness(
        configure: CoordinatorHarness.() -> Unit = {}
    ): CoordinatorHarness {
        return CoordinatorHarness().apply(configure)
    }

    private fun buildService(
        harness: CoordinatorHarness = newHarness(),
        queueStatusRepository: QueueStatusRepository = emptyQueueStatusRepo(),
        trackingStore: TrackingSettingsStore? = null
    ): ForegroundLocationService {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val service = Robolectric.buildService(ForegroundLocationService::class.java).get()
        service.locationAcquisitionCoordinator = harness.coordinator
        service.queueStatusRepository = queueStatusRepository
        service.motionSignalRepository = MotionSignalRepository(context)
        service.scheduleWindowRepository = minimalScheduleRepository(context)
        if (trackingStore != null) {
            service.trackingSettingsStore = trackingStore
        } else {
            service.trackingSettingsStore = trackingStore("fg_default_", enabled = false)
        }
        service.attachSprintAndPassiveDependencies(harness, context, service.trackingSettingsStore)
        attachPolicyTransitionDependencies(service)
        return service
    }

    /**
     * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-1 / REQ-3：给服务装上**非空**的写入依赖。
     *
     * 缺陷版本这里是「可空字段 + 静默跳过」，测试不装也能过；现在写入依赖是必填的，
     * 未装时生产路径会以「装配错误」显式失败（AC-1.5，见 `PolicyTransitionRecordingWiringTest`）。
     * 本文件的用例关心的是去重 / 取消 / 协程语义，因此默认装一个记录用的测试替身；
     * **真实接线**（走生产装配 + 真 DAO）由 `PolicyTransitionRecordingWiringTest` 守住。
     */
    private fun attachPolicyTransitionDependencies(
        service: ForegroundLocationService,
        recorder: PolicyTransitionRecorder = PolicyTransitionRecorder { _, _ -> 0L },
        now: () -> Long = System::currentTimeMillis
    ): PolicyTransitionWriteFailureStore {
        val store = newPolicyTransitionWriteFailureStore(now)
        service.policyTransitionRecorder = recorder
        service.policyTransitionWriteFailures = store
        return store
    }

    private fun newPolicyTransitionWriteFailureStore(
        now: () -> Long = System::currentTimeMillis
    ): PolicyTransitionWriteFailureStore {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val prefs = context.getSharedPreferences(
            "policy_transition_failure_fixture_" + System.nanoTime(),
            Context.MODE_PRIVATE
        ).also { it.edit().clear().commit() }
        val logs = StructuredLogRepository(
            context,
            TrackingSettingsStore(InMemorySharedPreferences())
        ) { now() }
        return PolicyTransitionWriteFailureStore(prefs, logs, now)
    }

    /**
     * WO-ANDROID-GATE-20260926：装配冲刺与被动定位的运行时依赖。
     *
     * 被动计数台账用 Robolectric 下的真实（内存）`AppDatabase`，避免用假件
     * 掩盖 Room 层的装配错误。
     */
    private fun ForegroundLocationService.attachSprintAndPassiveDependencies(
        harness: CoordinatorHarness,
        context: Application,
        settingsStore: TrackingSettingsStore
    ) {
        val database = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        databases += database
        val operations = RecordingAcquisitionOperations()
        val sprintController = LocationSprintController(
            runner = harness.runner,
            ledger = harness.sprintLedger,
            trackingSettingsStore = settingsStore
        )
        locationSprintRuntime = LocationSprintRuntime(
            controller = sprintController,
            operations = operations
        )
        val passiveLogs = StructuredLogRepository(context, settingsStore) { System.currentTimeMillis() }
        passiveLocationCoordinator = PassiveLocationCoordinator(
            source = PassiveLocationSource(context, settingsStore, passiveLogs),
            operations = operations,
            ledger = PassiveLocationLedger(database.forensicEventDao(), passiveLogs),
            logs = passiveLogs
        )
    }

    /** 记录冲刺/被动点的入库与丢弃，供运行时接线断言用（不落库，避免测试互相干扰）。 */
    class RecordingAcquisitionOperations : com.pim.app.location.acquisition.LocationAcquisitionOperations {
        val enqueued = java.util.Collections.synchronizedList(
            mutableListOf<Triple<com.pim.app.location.quality.QualityAcceptedLocation, String, String>>()
        )
        val dropped = java.util.Collections.synchronizedList(
            mutableListOf<Pair<com.pim.app.location.quality.RawLocationFix, String>>()
        )
        val syncRequests = java.util.concurrent.atomic.AtomicInteger(0)

        override suspend fun enqueueAccepted(
            accepted: com.pim.app.location.quality.QualityAcceptedLocation,
            rawJson: String,
            source: String
        ) {
            enqueued += Triple(accepted, rawJson, source)
        }

        override suspend fun recordDropped(
            fix: com.pim.app.location.quality.RawLocationFix,
            reason: String
        ) {
            dropped += fix to reason
        }

        override fun scheduleSync() {
            syncRequests.incrementAndGet()
        }
    }

    private fun minimalScheduleRepository(context: Application): ScheduleWindowRepository {
        val cacheDir = java.io.File(context.filesDir, "fg-min-sched-" + System.nanoTime()).also {
            it.mkdirs()
            cacheDirs.add(it)
        }
        val authStore = object : AuthSessionStore {
            override fun snapshot() = AuthSessionSnapshot(null, null)
            override fun save(
                accessToken: String,
                refreshToken: String,
                expiresAtUtcMillis: Long,
                serverIdentity: String
            ) = true
            override fun clear() = true
        }
        val serverSettings = ServerSettingsStore(context, authStore).also {
            runCatching { it.setBaseUrl("http://127.0.0.1:5858/api/v1/") }
        }
        val api = object : ApiService {
            override suspend fun getEvents(start: String, end: String, page: Int?, pageSize: Int?): ApiResponse<com.pim.core.models.PagedResult<EventResponse>> =
                ApiResponse(code = 0, message = "ok", data = com.pim.core.models.PagedResult(items = emptyList(), page = 1, pageSize = 100, totalCount = 0, totalPages = 0))
            override suspend fun login(request: com.pim.core.models.LoginRequest) = error("not mocked")
            override suspend fun register(request: com.pim.core.models.RegisterRequest) = error("not mocked")
            override suspend fun refresh(request: com.pim.core.models.RefreshRequest) = error("not mocked")
            override suspend fun getCalendars() = error("not mocked")
            override suspend fun createCalendar(request: com.pim.core.models.CreateCalendarRequest) = error("not mocked")
            override suspend fun createEvent(request: com.pim.core.models.CreateEventRequest) = error("not mocked")
            override suspend fun updateEvent(id: String, request: com.pim.core.models.CreateEventRequest) = error("not mocked")
            override suspend fun deleteEvent(id: String) = error("not mocked")
            override suspend fun getTasks(inbox: Boolean?) = error("not mocked")
            override suspend fun createTask(request: com.pim.core.models.CreateTaskRequest) = error("not mocked")
            override suspend fun updateTask(id: String, request: com.pim.core.models.CreateTaskRequest) = error("not mocked")
            override suspend fun deleteTask(id: String) = error("not mocked")
            override suspend fun search(query: String, type: String?) = error("not mocked")
            override suspend fun importIcs(body: okhttp3.RequestBody) = error("not mocked")
            override suspend fun exportIcs(start: String, end: String) = error("not mocked")
            override suspend fun syncOutlook() = error("not mocked")
            override suspend fun uploadStats(batch: com.pim.core.models.UploadBatch) = error("not mocked")
            override suspend fun registerMobileDevice(request: com.pim.core.models.MobileDeviceRegisterRequest) = error("not mocked")
            override suspend fun getMobileGaps(request: com.pim.core.models.MobileGapRequest) = error("not mocked")
            override suspend fun uploadMobileUsage(request: com.pim.core.models.MobileUsageEventsUploadRequest) = error("not mocked")
            override suspend fun uploadMobileForensics(request: com.pim.core.models.MobileForensicsUploadRequest) = error("not mocked")
            override suspend fun uploadMobileLocation(request: com.pim.core.models.MobileLocationPointRequest) = error("not mocked")
            override suspend fun uploadMobileLocationsBatch(request: com.pim.core.models.MobileLocationPointsUploadRequest) = error("not used")
            override suspend fun getMobileSummary(date: String?, deviceId: String?) = error("not mocked")
            override suspend fun getMobileTimeline(date: String?, deviceId: String?, page: Int?, pageSize: Int?) = error("not mocked")
            override suspend fun getMobileQuality(date: String?, deviceId: String?, rangeStartUtc: String?, rangeEndUtc: String?) = error("not mocked")
            override suspend fun getMobileLocationHistory(rangeStartUtc: String?, rangeEndUtc: String?, deviceId: String?, maxAccuracyMeters: Double, includeRejected: Boolean, cursor: String?, pageSize: Int?) = error("not mocked")
            override suspend fun getMobileLocationOverview(rangeStartUtc: String, rangeEndUtc: String, deviceId: String?, maxAccuracyMeters: Double) = error("not mocked")
            override suspend fun getMobileLocationTracks(rangeStartUtc: String, rangeEndUtc: String, deviceId: String?, maxAccuracyMeters: Double) = error("not mocked")
            override suspend fun getMobileLocationSegmentPoints(segmentId: String, rangeStartUtc: String?, rangeEndUtc: String?, timezone: String?, deviceId: String?, maxAccuracyMeters: Double, includeRejected: Boolean, cursor: String?, pageSize: Int?) = error("not mocked")
            override suspend fun sendHeartbeat(request: com.pim.core.models.DaemonHeartbeatRequest) = error("not mocked")
            override suspend fun sendEndpointNotificationAction(deviceId: String, request: com.pim.core.models.EndpointNotificationActionRequestDto) = error("not mocked")
            override suspend fun getClientLatest() = com.pim.core.models.ClientShellLatestResponse()
        }
        return ScheduleWindowRepository(
            api,
            ScheduleCacheStore(cacheDir, Json { ignoreUnknownKeys = true }),
            serverSettings
        )
    }

    private fun invokeStartAutomaticLoop(service: ForegroundLocationService) {
        ForegroundLocationService::class.java
            .getDeclaredMethod("startAutomaticLoop")
            .apply { isAccessible = true }
            .invoke(service)
    }

    private fun invokeObserveQueueStatus(service: ForegroundLocationService) {
        ForegroundLocationService::class.java
            .getDeclaredMethod("observeQueueStatus")
            .apply { isAccessible = true }
            .invoke(service)
    }

    private fun grantCollectionPrerequisites(context: Application = ApplicationProvider.getApplicationContext()) {
        shadowOf(context).grantPermissions(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_COARSE_LOCATION,
            Manifest.permission.ACCESS_BACKGROUND_LOCATION,
            Manifest.permission.ACTIVITY_RECOGNITION
        )
        val lm = context.getSystemService(Context.LOCATION_SERVICE) as LocationManager
        val shadowLm = shadowOf(lm)
        shadowLm.setProviderEnabled(LocationManager.GPS_PROVIDER, true)
        shadowLm.setProviderEnabled(LocationManager.NETWORK_PROVIDER, true)
    }

    private fun trackingStore(name: String, enabled: Boolean): TrackingSettingsStore {
        val prefs = ApplicationProvider.getApplicationContext<Application>()
            .getSharedPreferences(name + System.nanoTime(), Context.MODE_PRIVATE)
            .also { it.edit().clear().commit() }
        return TrackingSettingsStore(prefs).also {
            it.setContinuousCollectionEnabled(enabled)
        }
    }

    private fun acceptedSnapshot(timeMillis: Long = System.currentTimeMillis()): LocationSnapshot {
        return LocationSnapshot(
            latitude = 31.2304,
            longitude = 121.4737,
            horizontalAccuracyMeters = 5f,
            provider = "gps",
            source = "test",
            altitudeMeters = 12.0,
            speedMetersPerSecond = null,
            bearingDegrees = null,
            timeMillis = timeMillis
        )
    }

    @Test
    fun runtimeStateDefaultApiStateMatchesScheduleWaitingText() {
        assertEquals("等待日程数据", ForegroundLocationRuntimeState().apiState)
    }

    @Test
    fun resolveRequestIntervalPreservedBelowSixtySeconds() {
        // ForegroundLocationService.requestLocationUpdates silently clamps to 60s,
        // overriding legal 30s and 45s intervals from presets.
        // The fix should expose a companion method that returns the interval unchanged.
        assertEquals(30_000L, ForegroundLocationService.resolveRequestInterval(30_000L))
        assertEquals(45_000L, ForegroundLocationService.resolveRequestInterval(45_000L))
        assertEquals(60_000L, ForegroundLocationService.resolveRequestInterval(60_000L))
        assertEquals(120_000L, ForegroundLocationService.resolveRequestInterval(120_000L))
    }

    @Test
    fun resolveLocationPriorityIsHighAccuracyForEveryMode() {
        LocationPolicyMode.entries.forEach { mode ->
            assertEquals(
                "mode $mode must use HIGH_ACCURACY",
                com.google.android.gms.location.Priority.PRIORITY_HIGH_ACCURACY,
                ForegroundLocationService.resolveLocationPriority(mode)
            )
        }
    }

    @Test
    fun resolveLocationPriorityCoversEveryPolicyMode() {
        LocationPolicyMode.entries.forEach { mode ->
            // Must not throw; exhaustive when on all enum values.
            ForegroundLocationService.resolveLocationPriority(mode)
        }
    }

    @Test
    fun permissionDenialMustNotOverwritePersistedCollectionIntent() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        shadowOf(context).denyPermissions(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_BACKGROUND_LOCATION
        )
        val prefs = context.getSharedPreferences("fg_perm_test", android.content.Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)
        store.setContinuousCollectionEnabled(true)
        assertTrue(store.read().continuousCollectionEnabled)

        val service = buildService()
        service.trackingSettingsStore = store
        service.onStartCommand(null, 0, 1)

        assertTrue(store.read().continuousCollectionEnabled)
    }

    @Test
    fun prerequisiteFailureMustNotEnableDisabledCollectionIntent() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        shadowOf(context).denyPermissions(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_BACKGROUND_LOCATION
        )
        val prefs = context.getSharedPreferences("fg_perm_disabled_test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)
        store.setContinuousCollectionEnabled(false)

        val service = buildService()
        service.trackingSettingsStore = store
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_COLLECTION),
            0,
            2
        )

        assertFalse(store.read().continuousCollectionEnabled)
    }

    private fun findNotification(nm: NotificationManager): Notification? {
        return nm.activeNotifications
            .firstOrNull { it.id == LocationNotificationRenderer.NOTIFICATION_ID }
            ?.notification
    }

    private fun pausedState() = LocationNotificationState(
        mode = LocationPolicyMode.Off,
        nextExpectedLocationText = "暂停",
        lastAcceptedLocationText = "无",
        lastAccuracyText = "无",
        pendingUploadTotal = 0,
        apiState = "等待采集",
        lastDroppedReason = null
    )

    @Test
    fun pauseLeavesDismissibleResumeNotification() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val prefs = context.getSharedPreferences("fg_pause_test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)
        store.setContinuousCollectionEnabled(true)

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        val service = buildService()
        service.trackingSettingsStore = store
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_PAUSE_COLLECTION),
            0, 1
        )

        assertFalse(store.read().continuousCollectionEnabled)

        val n = findNotification(nm)
        assertNotNull(n)
        assertTrue((n!!.flags and Notification.FLAG_ONGOING_EVENT) == 0)
        assertEquals("恢复", n.actions!![0].title.toString())

        service.onDestroy()
        val n2 = findNotification(nm)
        assertNotNull("暂停通知在 onDestroy 后应保留", n2)
        assertTrue("暂停通知在 onDestroy 后仍应是非 ongoing", (n2!!.flags and Notification.FLAG_ONGOING_EVENT) == 0)
    }

    @Test
    fun explicitStopCancelsPausedNotification() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val prefs = context.getSharedPreferences("fg_stop_test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)
        store.setContinuousCollectionEnabled(true)

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.notify(
            LocationNotificationRenderer.NOTIFICATION_ID,
            LocationNotificationRenderer.build(context, pausedState())
        )

        assertTrue(nm.activeNotifications.any { it.id == LocationNotificationRenderer.NOTIFICATION_ID })

        val service = buildService()
        service.trackingSettingsStore = store
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_STOP_COLLECTION),
            0, 1
        )

        assertFalse(store.read().continuousCollectionEnabled)
        assertFalse(nm.activeNotifications.any { it.id == LocationNotificationRenderer.NOTIFICATION_ID })
    }

    @Test
    fun pauseStopsOnlyItsOwnStartId() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val prefs = context.getSharedPreferences("fg_pause_stopself_test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)

        val service = buildService()
        service.trackingSettingsStore = store
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_PAUSE_COLLECTION),
            0, 41
        )

        assertEquals(41, shadowOf(service).stopSelfId)
    }

    @Test
    fun explicitStopStopsOnlyItsOwnStartId() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val prefs = context.getSharedPreferences("fg_stop_stopself_test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)

        val service = buildService()
        service.trackingSettingsStore = store
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_STOP_COLLECTION),
            0, 42
        )

        assertEquals(42, shadowOf(service).stopSelfId)
    }

    @Test
    fun resumeAttemptClearsPausedLifecycleFlag() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        shadowOf(context).denyPermissions(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_BACKGROUND_LOCATION
        )
        val prefs = context.getSharedPreferences("fg_resume_clear_flag_test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)

        val service = buildService()
        service.trackingSettingsStore = store

        // PAUSE sets isPausing = true
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_PAUSE_COLLECTION),
            0, 1
        )

        // Call onStartCommand with null intent — should reset isPausing
        service.onStartCommand(null, 0, 1)

        // Reflectively read isPausing
        val isPausingField = ForegroundLocationService::class.java.getDeclaredField("isPausing")
        isPausingField.isAccessible = true
        assertFalse(isPausingField.getBoolean(service))
    }

    @Test
    fun resumePermissionDenialPreservesRequestedCollectionIntent() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        shadowOf(context).denyPermissions(
            Manifest.permission.ACCESS_FINE_LOCATION,
            Manifest.permission.ACCESS_BACKGROUND_LOCATION
        )
        val prefs = context.getSharedPreferences("fg_resume_perm_deny_test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)
        // initial: continuousCollectionEnabled == false (default)
        assertFalse(store.read().continuousCollectionEnabled)

        val service = buildService()
        service.trackingSettingsStore = store
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_RESUME_COLLECTION),
            0, 55
        )

        assertTrue(store.read().continuousCollectionEnabled)
        assertEquals(55, shadowOf(service).stopSelfId)
    }

    @Test
    fun syncWhilePausedPreservesResumeNotificationAndShowsResult() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)

        val prefs = context.getSharedPreferences("fg_sync_paused_test", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)
        store.setContinuousCollectionEnabled(true)

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        // service1: PAUSE then onDestroy
        val service1 = buildService()
        service1.trackingSettingsStore = store
        service1.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_PAUSE_COLLECTION),
            0, 1
        )

        var n = findNotification(nm)
        assertNotNull(n)
        assertTrue((n!!.flags and Notification.FLAG_ONGOING_EVENT) == 0)
        assertEquals("恢复", n.actions!![0].title.toString())

        service1.onDestroy()

        val nAfterDestroy = findNotification(nm)
        assertNotNull("暂停通知在 onDestroy 后应保留", nAfterDestroy)
        assertTrue("暂停通知在 onDestroy 后仍应是非 ongoing",
            (nAfterDestroy!!.flags and Notification.FLAG_ONGOING_EVENT) == 0)

        // service2: simulate service rebuild, inject same store + scheduler
        val scheduler = MobileSyncScheduler(context, store)
        val service2 = buildService()
        service2.trackingSettingsStore = store
        service2.mobileSyncScheduler = scheduler

        service2.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
            0, 73
        )

        // idle main looper to let the coroutine complete
        shadowOf(Looper.getMainLooper()).idle()

        n = findNotification(nm)
        assertNotNull("sync 后暂停通知仍应存在", n)
        assertTrue("sync 后通知仍应是非 ongoing",
            (n!!.flags and Notification.FLAG_ONGOING_EVENT) == 0)
        assertEquals("恢复", n.actions!![0].title.toString())
        val syncMsg = n!!.extras?.getCharSequence(android.app.Notification.EXTRA_TEXT)?.toString() ?: ""
        assertTrue(
            "collapsedText 应包含同步状态: $syncMsg",
            syncMsg.contains("同步请求已提交")
        )
        // 手动专用实例的 sync 收尾必须使用无条件 stopSelf()（不记录 startId）
        assertEquals(0, shadowOf(service2).stopSelfId)

        service2.onDestroy()
        val nFinal = findNotification(nm)
        assertNotNull("onDestroy 后暂停通知仍应保留", nFinal)
    }

    // Separate thread in this test queues Dispatchers.Main.immediate so the
    // first notification can be checked before the coroutine completes.
    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun syncFromNewServiceAfterPauseMustNotShowPowerSavingTransientState() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)

        val prefs = context.getSharedPreferences("fg_transient_free_sync", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)
        store.setContinuousCollectionEnabled(true)

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        val service1 = buildService()
        service1.trackingSettingsStore = store
        service1.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_PAUSE_COLLECTION),
            0, 1
        )
        service1.onDestroy()

        val nBefore = findNotification(nm)
        assertNotNull("暂停通知应存在", nBefore)

        val scheduler = MobileSyncScheduler(context, store)
        val service2 = buildService()
        service2.trackingSettingsStore = store
        service2.mobileSyncScheduler = scheduler

        val executor = Executors.newSingleThreadExecutor()
        try {
            executor.submit {
                service2.onStartCommand(
                    Intent(context, ForegroundLocationService::class.java)
                        .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
                    0, 74
                )
            }.get(5, TimeUnit.SECONDS)
        } finally {
            executor.shutdownNow()
        }

        val n = findNotification(nm)
        assertNotNull("同步协程执行前通知应存在", n)
        assertEquals(
            "第一 action 应为恢复（保持暂停状态）",
            "恢复",
            n!!.actions!![0].title.toString()
        )
        assertTrue((n.flags and Notification.FLAG_ONGOING_EVENT) == 0)
        val collapsed = n.extras?.getCharSequence(android.app.Notification.EXTRA_TEXT)?.toString() ?: ""
        assertTrue("collapsedText 应包含已暂停: $collapsed", collapsed.contains("已暂停"))
        assertFalse("collapsedText 不应包含省电档: $collapsed", collapsed.contains("省电档"))
        assertTrue("collapsedText 应包含同步中状态: $collapsed", collapsed.contains("同步中"))

        shadowOf(Looper.getMainLooper()).idle()
        val completed = findNotification(nm)
        assertNotNull("同步完成后通知应存在", completed)
        val completedText = completed!!.extras
            ?.getCharSequence(android.app.Notification.EXTRA_TEXT)
            ?.toString()
            .orEmpty()
        assertTrue(completedText.contains("同步请求已提交"))

        service2.onDestroy()
    }

    @Test
    fun syncAfterPauseOnSameInstancePreservesResumeNotification() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)

        val prefs = context.getSharedPreferences("fg_same_instance_pause_sync", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)
        store.setContinuousCollectionEnabled(true)

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        val service = buildService()
        service.trackingSettingsStore = store
        // PAUSE on this instance
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_PAUSE_COLLECTION),
            0, 41
        )

        // The PAUSE stops the service, so a fresh service would normally be needed.
        // But for the regression test we simulate: paused notification is active.
        // Send SYNC to the same service instance
        val scheduler = MobileSyncScheduler(context, store)
        service.mobileSyncScheduler = scheduler

        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
            0, 42
        )

        shadowOf(Looper.getMainLooper()).idle()

        val n = findNotification(nm)
        assertNotNull("PAUSE+SYNC 后暂停通知应存在", n)
        assertTrue("通知应为非 ongoing", (n!!.flags and Notification.FLAG_ONGOING_EVENT) == 0)
        assertEquals("恢复", n.actions!![0].title.toString())
        val collapsed = n.extras?.getCharSequence(android.app.Notification.EXTRA_TEXT)?.toString() ?: ""
        assertTrue("应包含已暂停: $collapsed", collapsed.contains("已暂停"))
        assertTrue("应包含同步结果: $collapsed", collapsed.contains("同步请求已提交"))

        service.onDestroy()
    }

    @Test
    fun nonPauseOnDestroyExplicitlyCancelsNotification() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val prefs = context.getSharedPreferences("fg_ondestroy_explicit_cancel", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        val service = buildService()
        service.trackingSettingsStore = store

        // STOP_COLLECTION sets isPausing = false
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_STOP_COLLECTION),
            0, 1
        )

        // Simulate late coroutine re-posting (e.g. refreshScheduleWindows completing after STOP)
        val lateState = LocationNotificationState(
            mode = LocationPolicyMode.ScheduleLowFrequency,
            nextExpectedLocationText = "3 分钟后",
            lastAcceptedLocationText = "12:00",
            lastAccuracyText = "10m",
            pendingUploadTotal = 1,
            apiState = "正常",
            lastDroppedReason = null
        )
        nm.notify(
            LocationNotificationRenderer.NOTIFICATION_ID,
            LocationNotificationRenderer.build(context, lateState)
        )
        assertTrue(
            "晚到协程重发后通知应可见",
            nm.activeNotifications.any { it.id == LocationNotificationRenderer.NOTIFICATION_ID }
        )

        // onDestroy for !isPausing should remove all notifications
        service.onDestroy()

        val n = findNotification(nm)
        assertNull("onDestroy 后应无通知残留", n)
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun cancelledScheduleRefreshDoesNotPublishFailureNotification() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        val apiService = object : ApiService {
            override suspend fun getEvents(start: String, end: String, page: Int?, pageSize: Int?): ApiResponse<com.pim.core.models.PagedResult<EventResponse>> {
                throw CancellationException("test cancellation")
            }
            override suspend fun login(request: com.pim.core.models.LoginRequest) = error("not mocked")
            override suspend fun register(request: com.pim.core.models.RegisterRequest) = error("not mocked")
            override suspend fun refresh(request: com.pim.core.models.RefreshRequest) = error("not mocked")
            override suspend fun getCalendars() = error("not mocked")
            override suspend fun createCalendar(request: com.pim.core.models.CreateCalendarRequest) = error("not mocked")
            override suspend fun createEvent(request: com.pim.core.models.CreateEventRequest) = error("not mocked")
            override suspend fun updateEvent(id: String, request: com.pim.core.models.CreateEventRequest) = error("not mocked")
            override suspend fun deleteEvent(id: String) = error("not mocked")
            override suspend fun getTasks(inbox: Boolean?) = error("not mocked")
            override suspend fun createTask(request: com.pim.core.models.CreateTaskRequest) = error("not mocked")
            override suspend fun updateTask(id: String, request: com.pim.core.models.CreateTaskRequest) = error("not mocked")
            override suspend fun deleteTask(id: String) = error("not mocked")
            override suspend fun search(query: String, type: String?) = error("not mocked")
            override suspend fun importIcs(body: okhttp3.RequestBody) = error("not mocked")
            override suspend fun exportIcs(start: String, end: String) = error("not mocked")
            override suspend fun syncOutlook() = error("not mocked")
            override suspend fun uploadStats(batch: com.pim.core.models.UploadBatch) = error("not mocked")
            override suspend fun registerMobileDevice(request: com.pim.core.models.MobileDeviceRegisterRequest) = error("not mocked")
            override suspend fun getMobileGaps(request: com.pim.core.models.MobileGapRequest) = error("not mocked")
            override suspend fun uploadMobileUsage(request: com.pim.core.models.MobileUsageEventsUploadRequest) = error("not mocked")
            override suspend fun uploadMobileForensics(request: com.pim.core.models.MobileForensicsUploadRequest) = error("not mocked")
            override suspend fun uploadMobileLocation(request: com.pim.core.models.MobileLocationPointRequest) = error("not mocked")
            override suspend fun uploadMobileLocationsBatch(request: com.pim.core.models.MobileLocationPointsUploadRequest) = error("not used")
            override suspend fun getMobileSummary(date: String?, deviceId: String?) = error("not mocked")
            override suspend fun getMobileTimeline(date: String?, deviceId: String?, page: Int?, pageSize: Int?) = error("not mocked")
            override suspend fun getMobileQuality(date: String?, deviceId: String?, rangeStartUtc: String?, rangeEndUtc: String?) = error("not mocked")
            override suspend fun getMobileLocationHistory(rangeStartUtc: String?, rangeEndUtc: String?, deviceId: String?, maxAccuracyMeters: Double, includeRejected: Boolean, cursor: String?, pageSize: Int?) = error("not mocked")
            override suspend fun getMobileLocationOverview(rangeStartUtc: String, rangeEndUtc: String, deviceId: String?, maxAccuracyMeters: Double) = error("not mocked")
            override suspend fun getMobileLocationTracks(rangeStartUtc: String, rangeEndUtc: String, deviceId: String?, maxAccuracyMeters: Double) = error("not mocked")
            override suspend fun getMobileLocationSegmentPoints(segmentId: String, rangeStartUtc: String?, rangeEndUtc: String?, timezone: String?, deviceId: String?, maxAccuracyMeters: Double, includeRejected: Boolean, cursor: String?, pageSize: Int?) = error("not mocked")
            override suspend fun sendHeartbeat(request: com.pim.core.models.DaemonHeartbeatRequest) = error("not mocked")
            override suspend fun sendEndpointNotificationAction(deviceId: String, request: com.pim.core.models.EndpointNotificationActionRequestDto) = error("not mocked")
            override suspend fun getClientLatest() = com.pim.core.models.ClientShellLatestResponse()
        }

        val service = buildService()
        val testCacheDir = java.io.File(context.filesDir, "fg-test-cache-" + java.lang.System.nanoTime())
        testCacheDir.mkdirs()
        cacheDirs.add(testCacheDir)
        val testCacheStore = com.pim.app.schedule.ScheduleCacheStore(testCacheDir, kotlinx.serialization.json.Json { ignoreUnknownKeys = true })
        val testAuthStore = object : com.pim.core.auth.AuthSessionStore {
            override fun snapshot() = com.pim.core.auth.AuthSessionSnapshot(null, null)
            override fun save(accessToken: String, refreshToken: String, expiresAtUtcMillis: Long, serverIdentity: String) = true
            override fun clear() = true
        }
        val testServerSettings = com.pim.core.settings.ServerSettingsStore(context, testAuthStore)
        kotlin.runCatching { testServerSettings.setBaseUrl("http://127.0.0.1:5858/api/v1/") }
        service.scheduleWindowRepository = ScheduleWindowRepository(apiService, testCacheStore, testServerSettings)

        val refresh = ForegroundLocationService::class.java
            .getDeclaredMethod("refreshScheduleWindows", Boolean::class.javaPrimitiveType)
            .apply { isAccessible = true }
        refresh.invoke(service, false)

        repeat(20) {
            Thread.sleep(25)
            shadowOf(Looper.getMainLooper()).idle()
        }

        assertNull(
            "取消日程刷新不应被当作 API 失败并重发通知",
            findNotification(nm)
        )
        service.onDestroy()
    }

    @Test
    fun syncAfterStopDoesNotLeaveNotification() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)

        val prefs = context.getSharedPreferences("fg_stop_then_sync", Context.MODE_PRIVATE)
        prefs.edit().clear().commit()
        val store = TrackingSettingsStore(prefs)

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)
        assertFalse("不应有残留通知", nm.activeNotifications.any { it.id == LocationNotificationRenderer.NOTIFICATION_ID })

        val service = buildService()
        service.trackingSettingsStore = store

        // STOP_COLLECTION (no prior PAUSE — collection was never enabled)
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_STOP_COLLECTION),
            0, 1
        )

        assertFalse("STOP 后不应有通知", nm.activeNotifications.any { it.id == LocationNotificationRenderer.NOTIFICATION_ID })

        // Now SYNC — should NOT restore a notification
        val scheduler = MobileSyncScheduler(context, store)
        service.mobileSyncScheduler = scheduler

        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
            0, 2
        )

        shadowOf(Looper.getMainLooper()).idle()

        assertFalse("STOP+SYNC 后不应有通知", nm.activeNotifications.any { it.id == LocationNotificationRenderer.NOTIFICATION_ID })

        service.onDestroy()
    }

    @Test
    fun policyTransitionDeduperRecordsModeIntervalReasonChangesOnce() {
        val deduper = PolicyTransitionDeduper()
        val base = PolicyDecision(
            mode = LocationPolicyMode.PowerSavingNormal,
            requestIntervalMillis = 180_000L,
            nextExpectedLocationAtMillis = 1_000L,
            reason = "默认省电档",
            scheduleLowFrequency = false
        )

        val first = deduper.note(base)
        assertNotNull(first)
        assertNull(first!!.fromMode)
        assertEquals(LocationPolicyMode.PowerSavingNormal, first.decision.mode)

        assertNull("完全相同 decision 不应再写", deduper.note(base))

        val modeChanged = deduper.note(
            base.copy(mode = LocationPolicyMode.ScheduleLowFrequency, scheduleLowFrequency = true, reason = "日程低频")
        )
        assertNotNull(modeChanged)
        assertEquals(LocationPolicyMode.PowerSavingNormal, modeChanged!!.fromMode)
        assertEquals(LocationPolicyMode.ScheduleLowFrequency, modeChanged.decision.mode)

        val intervalChanged = deduper.note(
            modeChanged.decision.copy(requestIntervalMillis = 300_000L)
        )
        assertNotNull(intervalChanged)
        assertEquals(LocationPolicyMode.ScheduleLowFrequency, intervalChanged!!.fromMode)
        assertEquals(300_000L, intervalChanged.decision.requestIntervalMillis)

        val reasonChanged = deduper.note(
            intervalChanged.decision.copy(reason = "原因变更")
        )
        assertNotNull(reasonChanged)
        assertEquals("原因变更", reasonChanged!!.decision.reason)

        assertNull(
            deduper.note(reasonChanged.decision.copy(nextExpectedLocationAtMillis = 9_999L))
        )
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun stoppingCollectionCancelsScheduleRefreshAndSnapshotJobs() {
        val fixture = ScheduleServiceFixture()
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        fixture.api.started = started
        fixture.api.block = release

        val service = fixture.createService()
        invokeRefresh(service)
        idleUntil {
            started.isCompleted &&
                isScheduleRefreshJobActive(service) &&
                isSnapshotCollectorActive(service)
        }

        invokeStopCollection(service)

        assertTrue("停止采集后应取消日程刷新协程", readScheduleRefreshJob(service)!!.isCancelled)
        assertTrue("停止采集后应取消日程快照收集协程", readSnapshotCollectorJob(service)!!.isCancelled)

        release.complete(Unit)
        service.onDestroy()
        fixture.cleanup()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun refreshScheduleWindowsUsesRepositorySnapshotAndRuntimeFields() {
        val fixture = ScheduleServiceFixture()
        val now = System.currentTimeMillis()
        val startIso = Instant.ofEpochMilli(now - 60_000L).toString()
        val endIso = Instant.ofEpochMilli(now + 3_600_000L).toString()
        fixture.api.events = listOf(
            EventResponse(
                id = "evt-1",
                title = "会议",
                location = "办公室",
                dtStart = startIso,
                dtEnd = endIso
            )
        )

        val service = fixture.createService()
        invokeRefresh(service)
        idleUntil {
            ForegroundLocationService.runtimeState.value.scheduleFreshness == ScheduleCacheFreshness.Fresh
        }

        assertEquals(1, fixture.api.callCount)
        assertTrue("应走 refreshIfStale，不得调用 loadWindows 自定义范围", fixture.api.capturedStart != null)
        val runtime = ForegroundLocationService.runtimeState.value
        assertEquals(ScheduleCacheFreshness.Fresh, runtime.scheduleFreshness)
        assertNotNull(runtime.scheduleLastSuccessAtMillis)
        assertNotNull(runtime.scheduleLastAttemptAtMillis)
        assertNull(runtime.scheduleLastError)
        assertEquals("正常", runtime.apiState)
        assertEquals(
            listOf("evt-1"),
            readScheduleWindows(service).map { it.id }
        )
        service.onDestroy()
        fixture.cleanup()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun refreshFailureKeepsStaleWindowsAndCollectionIntent() {
        val fixture = ScheduleServiceFixture()
        val identity = PimServerEndpoints.from(fixture.serverSettings.getBaseUrl()).apiBaseUrl.toString()
        val oldWindow = ScheduleCacheWindow(
            id = "cached",
            title = "旧会议",
            locationText = "A",
            startsAtMillis = 1_000L,
            endsAtMillis = 2_000L
        )
        fixture.cacheStore.write(
            identity,
            ScheduleCacheDocument(
                windows = listOf(oldWindow),
                rangeStartMillis = 0L,
                rangeEndMillis = 10_000L,
                lastAttemptAtMillis = 100L,
                lastSuccessAtMillis = 100L,
                lastError = null,
                lastErrorKind = null
            )
        )
        fixture.store.setContinuousCollectionEnabled(true)
        assertTrue(fixture.store.read().continuousCollectionEnabled)

        fixture.api.failNext = IOException("network down")
        val service = fixture.createService()
        invokeRefresh(service)
        idleUntil {
            ForegroundLocationService.runtimeState.value.scheduleLastError != null
        }

        val runtime = ForegroundLocationService.runtimeState.value
        assertEquals(ScheduleCacheFreshness.Stale, runtime.scheduleFreshness)
        assertEquals("网络不可用", runtime.scheduleLastError)
        assertEquals("日程缓存可能过期", runtime.apiState)
        assertEquals(listOf("cached"), readScheduleWindows(service).map { it.id })
        assertTrue(
            "刷新失败不得关闭 continuousCollectionEnabled",
            fixture.store.read().continuousCollectionEnabled
        )
        service.onDestroy()
        fixture.cleanup()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun serverIdentityChangeClearsOldWindowsBeforeNewSnapshot() {
        val fixture = ScheduleServiceFixture()
        val oldIdentity = PimServerEndpoints.from(fixture.serverSettings.getBaseUrl()).apiBaseUrl.toString()
        fixture.cacheStore.write(
            oldIdentity,
            ScheduleCacheDocument(
                windows = listOf(
                    ScheduleCacheWindow("old-server", "旧服", "", 1_000L, 2_000L)
                ),
                rangeStartMillis = 0L,
                rangeEndMillis = 10_000L,
                lastAttemptAtMillis = 50L,
                lastSuccessAtMillis = 50L,
                lastError = null,
                lastErrorKind = null
            )
        )

        // Seed service memory with old windows via first successful refresh.
        fixture.api.events = listOf(
            EventResponse(
                id = "old-server",
                title = "旧服",
                location = null,
                dtStart = Instant.ofEpochMilli(1_000L).toString(),
                dtEnd = Instant.ofEpochMilli(2_000L).toString()
            )
        )
        val service = fixture.createService()
        invokeRefresh(service)
        idleUntil { readScheduleWindows(service).any { it.id == "old-server" } }
        // Repository may publish before the Main refresh job resumes; wait until it finishes
        // so the next refresh is not skipped by scheduleRefreshJob?.isActive.
        idleUntil { !isScheduleRefreshJobActive(service) }

        // Switch server; next refresh must clear old windows before applying new ones.
        fixture.serverSettings.setBaseUrl("http://other-server:5858/api/v1/")
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        fixture.api.started = started
        fixture.api.block = release
        fixture.api.events = listOf(
            EventResponse(
                id = "new-server",
                title = "新服",
                location = null,
                dtStart = Instant.ofEpochMilli(3_000L).toString(),
                dtEnd = Instant.ofEpochMilli(4_000L).toString()
            )
        )

        invokeRefresh(service)
        // PAUSED looper: drain Main so the refresh job can reach Dispatchers.IO/API.
        idleUntil(timeoutMillis = 5_000L) { started.isCompleted }
        idleUntil(timeoutMillis = 5_000L) {
            readScheduleWindows(service).isEmpty()
        }
        assertTrue(
            "server identity 变化后应先清空旧窗口",
            readScheduleWindows(service).isEmpty()
        )
        release.complete(Unit)
        idleUntil { readScheduleWindows(service).any { it.id == "new-server" } }
        assertEquals(listOf("new-server"), readScheduleWindows(service).map { it.id })
        service.onDestroy()
        fixture.cleanup()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun applyDecisionDedupesTransitionsAndPublishesRuntimePolicyFields() {
        val recorded = CopyOnWriteArrayList<Pair<LocationPolicyMode?, PolicyDecision>>()
        val service = buildService()
        // WO-ANDROID-POLICY-TRANSITION-20260928 AC-2.4：原反射注入用例迁移到此注入方式
        // （直接给非空依赖装测试替身，不再 `getDeclaredField("policyTransitionWriter")`）。
        service.policyTransitionRecorder = PolicyTransitionRecorder { from, decision ->
            recorded += from to decision
            0L
        }

        val d1 = PolicyDecision(
            mode = LocationPolicyMode.PowerSavingNormal,
            requestIntervalMillis = 180_000L,
            nextExpectedLocationAtMillis = 1_000L,
            reason = "默认省电档",
            scheduleLowFrequency = false
        )
        val d2 = d1.copy(mode = LocationPolicyMode.ScheduleLowFrequency, reason = "日程低频", scheduleLowFrequency = true)
        val d3 = d2.copy(requestIntervalMillis = 300_000L)
        val d4 = d3.copy(reason = "原因变更")

        invokeApplyDecision(service, d1)
        invokeApplyDecision(service, d1)
        invokeApplyDecision(service, d2)
        invokeApplyDecision(service, d3)
        invokeApplyDecision(service, d4)
        invokeApplyDecision(service, d4.copy(nextExpectedLocationAtMillis = 99_000L))
        // 写入成功路径会记录「连续失败归零」，其中有一次 IO 派发（不在主线程落盘），
        // 因此这里等写入协程真正跑完，而不是只 idle 一次主 looper。
        idleUntil { recorded.size == 4 }

        assertEquals(4, recorded.size)
        assertNull(recorded[0].first)
        assertEquals(LocationPolicyMode.PowerSavingNormal, recorded[0].second.mode)
        assertEquals(LocationPolicyMode.PowerSavingNormal, recorded[1].first)
        assertEquals(LocationPolicyMode.ScheduleLowFrequency, recorded[1].second.mode)
        assertEquals(LocationPolicyMode.ScheduleLowFrequency, recorded[2].first)
        assertEquals(300_000L, recorded[2].second.requestIntervalMillis)
        assertEquals("原因变更", recorded[3].second.reason)

        val runtime = ForegroundLocationService.runtimeState.value
        assertEquals(LocationPolicyMode.ScheduleLowFrequency.name, runtime.currentPolicyMode)
        assertEquals("原因变更", runtime.currentPolicyReason)
        assertEquals(300_000L, runtime.requestIntervalMillis)

        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun cancelledScheduleRefreshDoesNotMarkApiFailureOrClearCollection() {
        val fixture = ScheduleServiceFixture()
        fixture.store.setContinuousCollectionEnabled(true)
        fixture.api.failNext = CancellationException("test cancellation")

        val nm = fixture.context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        val service = fixture.createService()
        invokeRefresh(service)
        repeat(10) {
            shadowOf(Looper.getMainLooper()).idle()
        }

        val runtime = ForegroundLocationService.runtimeState.value
        assertNotEquals("API 无法连接", runtime.apiState)
        assertFalse(
            (runtime.apiState ?: "").contains("API 失败") ||
                (runtime.apiState ?: "").contains("API 无法连接")
        )
        assertTrue(fixture.store.read().continuousCollectionEnabled)
        service.onDestroy()
        fixture.cleanup()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun emptySuccessfulScheduleFollowedByFailureShowsStaleCacheState() {
        val fixture = ScheduleServiceFixture()
        val identity = PimServerEndpoints.from(fixture.serverSettings.getBaseUrl()).apiBaseUrl.toString()
        fixture.cacheStore.write(
            identity,
            ScheduleCacheDocument(
                windows = emptyList(),
                rangeStartMillis = 0L,
                rangeEndMillis = 10_000L,
                lastAttemptAtMillis = 100L,
                lastSuccessAtMillis = 100L,
                lastError = null,
                lastErrorKind = null
            )
        )
        fixture.api.failNext = IOException("network down")

        val service = fixture.createService()
        invokeRefresh(service)
        idleUntil { ForegroundLocationService.runtimeState.value.scheduleLastError != null }

        assertEquals(ScheduleCacheFreshness.Stale, ForegroundLocationService.runtimeState.value.scheduleFreshness)
        assertEquals("日程缓存可能过期", ForegroundLocationService.runtimeState.value.apiState)
        service.onDestroy()
        fixture.cleanup()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun destroyingServicePreservesPublishedScheduleFacts() {
        val fixture = ScheduleServiceFixture()
        val now = System.currentTimeMillis()
        fixture.api.events = listOf(
            EventResponse(
                id = "persisted-fact",
                title = "事实",
                location = null,
                dtStart = Instant.ofEpochMilli(now - 1_000L).toString(),
                dtEnd = Instant.ofEpochMilli(now + 60_000L).toString()
            )
        )

        val service = fixture.createService()
        invokeRefresh(service)
        idleUntil { ForegroundLocationService.runtimeState.value.scheduleFreshness == ScheduleCacheFreshness.Fresh }
        val before = ForegroundLocationService.runtimeState.value

        service.onDestroy()

        val after = ForegroundLocationService.runtimeState.value
        assertEquals(false, after.isRunning)
        assertEquals(before.scheduleFreshness, after.scheduleFreshness)
        assertEquals(before.scheduleLastSuccessAtMillis, after.scheduleLastSuccessAtMillis)
        assertEquals(before.scheduleLastAttemptAtMillis, after.scheduleLastAttemptAtMillis)
        fixture.cleanup()
    }

    @Test
    fun serviceUsesRepositorySnapshotInsteadOfSecondScheduleList() {
        val source = serviceSource()
        assertFalse("service must not maintain a second schedule list", source.contains("private var scheduleWindows"))
        assertTrue(
            "location policy must read repository snapshot",
            source.contains("scheduleWindowRepository.snapshotForCurrentServer()") ||
                source.contains("scheduleWindowRepository.snapshot.value.windows")
        )
    }

    @Test
    fun serviceDoesNotOwnFusedLocationProviderCallback() {
        val source = serviceSource()
        assertFalse(source.contains("FusedLocationProviderClient"))
        assertFalse(source.contains("LocationCallback"))
        assertFalse(source.contains("LocationRequest"))
        assertFalse(source.contains("LocationResult"))
        assertFalse(source.contains("LocationServices"))
        assertFalse(source.contains("requestLocationUpdates"))
        assertFalse(source.contains("AltitudeWaitCoordinator"))
        assertFalse(source.contains("LocationQualityGate"))
        assertFalse(source.contains("queueAccepted"))
        assertFalse(source.contains("recordDropped"))
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun automaticLoopStartsImmediatelyWithHighAccuracyPriority() {
        val harness = newHarness()
        val store = trackingStore("fg_auto_priority_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)

        invokeStartAutomaticLoop(service)
        idleUntil { harness.runner.acquireCount.get() >= 1 }

        assertEquals(1, harness.runner.acquireCount.get())
        assertEquals(Priority.PRIORITY_HIGH_ACCURACY, harness.runner.lastRequest!!.priority)
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun automaticLoopRegistersPersistentStreamAndRecordsIntervalFixes() {
        val harness = newHarness()
        val store = trackingStore("fg_auto_terminal_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        invokeApplyDecision(
            service,
            PolicyDecision(
                mode = LocationPolicyMode.PowerSavingNormal,
                requestIntervalMillis = 1_000L,
                nextExpectedLocationAtMillis = 1_000L,
                reason = "测试间隔",
                scheduleLowFrequency = false
            )
        )

        invokeStartAutomaticLoop(service)
        // 预热一次性采集（HIGH_ACCURACY）
        idleUntil { harness.runner.acquireCount.get() == 1 }
        assertEquals(
            Priority.PRIORITY_HIGH_ACCURACY,
            harness.runner.lastRequest!!.priority
        )
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        harness.runner.waitForStreamStart()
        assertEquals(1_000L, harness.runner.lastStreamRequest!!.intervalMillis)

        // 常驻流逐点入库，无需等待任何"下一轮"
        val fix = acceptedSnapshot()
        harness.runner.emitStreamCandidate(fix)
        idleUntil { harness.coordinator.streamState.value.latestFix != null }
        assertEquals(fix.latitude, harness.coordinator.streamState.value.latestFix!!.latitude, 0.0)
        assertTrue(harness.coordinator.isAutomaticStreamActive())
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun automaticLoopRegistersStreamWhileManualOneShotIsInFlight() {
        val harness = newHarness()
        val store = trackingStore("fg_auto_busy_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)

        // Occupy the coordinator with a manual one-shot before the automatic loop starts.
        val manualStarted = harness.coordinator.startManualSession() as SessionStartResult.Started
        idleUntil {
            harness.coordinator.state.value.phase == AcquisitionPhase.Acquiring &&
                harness.runner.acquireCount.get() >= 1
        }

        invokeStartAutomaticLoop(service)
        // The manual session and the automatic stream must not block each other:
        // the loop starts the warm-up acquisition and registers the stream even
        // while the manual one-shot is in flight.
        shadowOf(Looper.getMainLooper()).idleFor(1_000L, TimeUnit.MILLISECONDS)
        idleUntil { harness.runner.acquireCount.get() >= 2 }
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        harness.runner.waitForStreamStart()
        assertTrue(harness.coordinator.isAutomaticStreamActive())
        assertEquals(TriggerType.MANUAL, harness.coordinator.state.value.triggerType)

        // Complete the manual one-shot (session index 0); the stream stays
        // active and independent.
        harness.runner.completeAt(
            index = 0,
            result = LocationEngineResult(
                sessionId = manualStarted.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        idleUntil {
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.TimedOut,
                AcquisitionPhase.Failed,
                AcquisitionPhase.Completed,
                AcquisitionPhase.Cancelled
            )
        }
        assertTrue(harness.coordinator.isAutomaticStreamActive())
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun stoppingCollectionStopsAutomaticStreamButKeepsManualSession() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_disable_manual_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)

        val manual = harness.coordinator.startManualSession() as SessionStartResult.Started
        idleUntil { harness.coordinator.state.value.sessionId == manual.sessionId }
        harness.coordinator.startAutomaticStream(
            AcquisitionContext(
                policyMode = "PowerSavingNormal",
                scheduleLowFrequency = false,
                motionSignal = "Still",
                requestIntervalMillis = 60_000L
            )
        )
        idleUntil { harness.coordinator.isAutomaticStreamActive() }

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_STOP_COLLECTION),
            0,
            12
        )
        assertFalse(
            "stopping collection must stop the automatic stream",
            harness.coordinator.isAutomaticStreamActive()
        )
        assertEquals(
            "the manual one-shot must be untouched by collection stop",
            manual.sessionId,
            harness.coordinator.state.value.sessionId
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun manualOnlyKeepsForegroundUntilCompleted() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_manual_only_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0,
            21
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        assertFalse(shadowOf(service).isForegroundStopped)
        assertNotNull(findNotification(nm))

        val sessionId = harness.runner.lastRequest!!.sessionId
        harness.runner.emitCandidate(acceptedSnapshot())
        // WO-ANDROID-GATE-20260926 D6：手动会话不再「首个达标点即结束」，必须等满 30 秒。
        // 这里显式让一次性采集截止，等价于窗口跑满。
        // D6：手动会话等满 30 秒才终结 —— 显式让一次性采集截止。
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        idleUntil {
            harness.coordinator.state.value.sessionId == sessionId &&
                harness.coordinator.state.value.phase == AcquisitionPhase.Completed
        }
        idleUntil { shadowOf(service).isStoppedBySelf }
        assertTrue(
            "manual-only session must stop the service itself, not merely remove foreground",
            shadowOf(service).isStoppedBySelf
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun enabledCollectionManualActionInitializesAutomaticRuntime() {
        grantCollectionPrerequisites()
        val locations = MutableStateFlow(4)
        val harness = newHarness()
        val store = trackingStore("fg_manual_enabled_", enabled = true)
        val service = buildService(
            harness = harness,
            queueStatusRepository = emptyQueueStatusRepo(locations),
            trackingStore = store
        )

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0,
            22
        )

        idleUntil { harness.runner.acquireCount.get() >= 1 }
        idleUntil { ForegroundLocationService.runtimeState.value.pendingUploadTotal == 4 }
        val policyEngineField = ForegroundLocationService::class.java
            .getDeclaredField("policyEngine")
            .apply { isAccessible = true }
        val automaticLoopJobField = ForegroundLocationService::class.java
            .getDeclaredField("automaticLoopJob")
            .apply { isAccessible = true }

        assertNotNull(policyEngineField.get(service))
        assertTrue((automaticLoopJobField.get(service) as Job).isActive)
        assertEquals(TriggerType.MANUAL, harness.coordinator.state.value.triggerType)
        service.onDestroy()
    }

    @Test
    fun `rejected manual session does not start foreground and preserves reason`() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val harness = CoordinatorHarness(
            prerequisiteResult = LocationPrerequisiteResult.Blocked("缺少精确定位权限")
        )
        val service = buildService(harness = harness)
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 91
        )

        assertEquals(91, shadowOf(service).stopSelfId)
        assertFalse(
            "no 7101 notification for rejected manual session",
            nm.activeNotifications.any { it.id == LocationNotificationRenderer.NOTIFICATION_ID }
        )
        assertEquals("缺少精确定位权限", harness.coordinator.state.value.errorReason)
        assertEquals(AcquisitionPhase.Idle, harness.coordinator.state.value.phase)
        assertNull(harness.coordinator.state.value.sessionId)
        service.onDestroy()
    }

    @Test
    fun cancelLocationSessionForwardsNullableSessionId() {
        val harness = newHarness()
        val service = buildService(harness = harness)
        val context = ApplicationProvider.getApplicationContext<Application>()

        val started = harness.coordinator.startManualSession() as SessionStartResult.Started
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_CANCEL_LOCATION_SESSION)
                .putExtra(ForegroundLocationController.EXTRA_SESSION_ID, started.sessionId),
            0,
            31
        )
        assertEquals(AcquisitionPhase.Cancelled, harness.coordinator.state.value.phase)

        // A missing extra must never be forwarded to the coordinator as a
        // wildcard cancellation: the current session stays untouched.
        val started2 = harness.coordinator.startManualSession() as SessionStartResult.Started
        assertEquals(started2.sessionId, harness.coordinator.state.value.sessionId)
        val result = service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_CANCEL_LOCATION_SESSION),
            0,
            32
        )
        assertEquals(android.app.Service.START_STICKY, result)
        assertEquals(started2.sessionId, harness.coordinator.state.value.sessionId)
        assertTrue(
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun manualOnlyCancelReleasesForegroundTeardownWaiter() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_manual_cancel_release_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0,
            61
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        assertFalse(shadowOf(service).isForegroundStopped)
        assertNotNull(findNotification(nm))

        val sessionId = harness.runner.lastRequest!!.sessionId
        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_CANCEL_LOCATION_SESSION)
                .putExtra(ForegroundLocationController.EXTRA_SESSION_ID, sessionId),
            0,
            62
        )
        idleUntil { shadowOf(service).isStoppedBySelf }
        assertTrue(
            "cancelled manual session must stop the service (not merely remove foreground)",
            shadowOf(service).isStoppedBySelf
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun secondManualStartWhileAcquiringReplacesFirstSession() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_double_start_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 81
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        assertFalse(shadowOf(service).isForegroundStopped)
        assertNotNull(findNotification(nm))
        val firstSessionId = harness.coordinator.state.value.sessionId
        assertNotNull(firstSessionId)

        // A rapid double-start while the first one-shot is still acquiring must
        // replace it (restart semantics): the old session is cancelled and a new
        // session id is active, with the foreground notification preserved.
        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 82
        )
        shadowOf(Looper.getMainLooper()).idle()

        assertNotEquals(firstSessionId, harness.coordinator.state.value.sessionId)
        assertTrue(
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertFalse(shadowOf(service).isForegroundStopped)
        assertNotNull(findNotification(nm))

        // The replacement session's terminal waiter retires the manual-only
        // service when the session completes.
        val replacementSessionId = harness.coordinator.state.value.sessionId
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = replacementSessionId!!,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        idleUntil { shadowOf(service).isStoppedBySelf }
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun staleCancelOnFreshInstanceIsFailClosed() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_cancel_fresh_", enabled = false)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        // Instance A runs a manual-only session to completion; its
        // terminal waiter stops the instance.
        val serviceA = buildService(harness = harness, trackingStore = store)
        serviceA.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 71
        )
        idleUntil(timeoutMillis = 15_000L) { harness.runner.acquireCount.get() >= 1 }
        harness.runner.emitCandidate(acceptedSnapshot())
        // D6：手动会话等满 30 秒；显式让一次性采集截止以进入 Completed。
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        idleUntil(timeoutMillis = 15_000L) {
            harness.coordinator.state.value.phase == AcquisitionPhase.Completed
        }
        idleUntil(timeoutMillis = 15_000L) { shadowOf(serviceA).isStoppedBySelf }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)
        serviceA.onDestroy()

        // A fresh instance handles the stale cancel: a completed session is no
        // longer cancellable, so the cancel must fail closed (START_STICKY,
        // no teardown). A null-intent restart self-cleans via the default
        // startCollection branch.
        val serviceB = buildService(harness = harness, trackingStore = store)
        val result = serviceB.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_CANCEL_LOCATION_SESSION)
                .putExtra(ForegroundLocationController.EXTRA_SESSION_ID, sessionId),
            0, 72
        )
        shadowOf(Looper.getMainLooper()).idle()

        assertEquals(
            "a completed manual session is not cancelled by a stale cancel",
            AcquisitionPhase.Completed,
            harness.coordinator.state.value.phase
        )
        assertEquals(android.app.Service.START_STICKY, result)
        assertFalse(
            "stale cancel must not stop the waiter-less instance",
            shadowOf(serviceB).isStoppedBySelf
        )
        serviceB.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun manualSyncFinallyStopsUnconditionallyDespiteNewerStartId() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)
        val store = trackingStore("fg_sync_unconditional_", enabled = false)
        val service = buildService(trackingStore = store)
        service.mobileSyncScheduler = MobileSyncScheduler(context, store)

        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
            0, 73
        )
        // A newer sync start id must not keep the manual-only service alive:
        // the finally path must terminate with unconditional stopSelf().
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
            0, 74
        )
        shadowOf(Looper.getMainLooper()).idle()

        assertTrue(shadowOf(service).isStoppedBySelf)
        assertEquals(
            "manual-only sync termination must use unconditional stopSelf(), not stopSelf(startId)",
            0,
            shadowOf(service).stopSelfId
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun automaticLoopReRegistersStreamAfterMotionWaitTimeout() {
        val harness = newHarness()
        val store = trackingStore("fg_auto_cancel_progress_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        invokeApplyDecision(
            service,
            PolicyDecision(
                mode = LocationPolicyMode.PowerSavingNormal,
                requestIntervalMillis = 1_000L,
                nextExpectedLocationAtMillis = 1_000L,
                reason = "测试间隔",
                scheduleLowFrequency = false
            )
        )

        invokeStartAutomaticLoop(service)
        idleUntil { harness.runner.acquireCount.get() == 1 }
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        harness.runner.waitForStreamStart()
        assertEquals(1, harness.runner.streamCount.get())
        assertTrue(harness.coordinator.isAutomaticStreamActive())

        // 决策未变化时，运动等待超时（30s）后循环重算，流保持注册不重建
        shadowOf(Looper.getMainLooper()).idleFor(31_000L, TimeUnit.MILLISECONDS)
        shadowOf(Looper.getMainLooper()).idle()
        assertEquals(1, harness.runner.streamCount.get())
        assertTrue(harness.coordinator.isAutomaticStreamActive())

        // 决策变化（间隔改变）触发流重注册
        invokeApplyDecision(
            service,
            PolicyDecision(
                mode = LocationPolicyMode.MotionObservation,
                requestIntervalMillis = 30_000L,
                nextExpectedLocationAtMillis = 30_000L,
                reason = "检测到运动状态：步行",
                scheduleLowFrequency = false
            )
        )
        // 循环还挂在 30s 运动等待上：先推进超时，下一轮才看到决策变化
        shadowOf(Looper.getMainLooper()).idleFor(31_000L, TimeUnit.MILLISECONDS)
        idleUntil { harness.runner.streamCount.get() >= 2 }
        assertEquals(30_000L, harness.runner.lastStreamRequest!!.intervalMillis)
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun cancelWithoutSessionIdDuringCancellablePhaseIsFailClosed() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_cancel_null_cancellable_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 71
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)
        assertTrue(
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertNotNull(findNotification(nm))

        // A cancel intent without EXTRA_SESSION_ID must fail closed during a
        // cancellable phase: no wildcard cancellation, no stop, no foreground
        // teardown, no 7101 removal.
        val result = service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_CANCEL_LOCATION_SESSION),
            0, 72
        )
        shadowOf(Looper.getMainLooper()).idle()

        assertEquals(sessionId, harness.coordinator.state.value.sessionId)
        assertTrue(
            "session must remain active and cancellable",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertEquals(android.app.Service.START_STICKY, result)
        assertFalse(
            "missing-id cancel must not stop the service",
            shadowOf(service).isStoppedBySelf
        )
        assertFalse(
            "missing-id cancel must not remove the foreground state",
            shadowOf(service).isForegroundStopped
        )
        assertNotNull(
            "missing-id cancel must keep the 7101 notification",
            findNotification(nm)
        )
        service.onDestroy()
    }

    @Test
    fun onDestroyPreservesCompletedManualResult() {
        val harness = newHarness()
        val service = buildService(harness = harness)
        harness.forceState(
            LocationAcquisitionState(
                sessionId = "manual-await",
                triggerType = TriggerType.MANUAL,
                phase = AcquisitionPhase.Completed
            )
        )
        service.onDestroy()
        assertEquals(AcquisitionPhase.Completed, harness.coordinator.state.value.phase)
        assertEquals("manual-await", harness.coordinator.state.value.sessionId)
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun onDestroyDuringOwnedCompletedWindowPreservesResult() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_await_window_destroy_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 1
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)

        // Advance the coordinator to Completed WITHOUT idling the
        // paused main looper: the terminal waiter's resumption is queued but
        // has not run yet, which is exactly the deterministic window in which
        // an unexpected onDestroy must not cancel the owned result.
        harness.runner.emitCandidate(acceptedSnapshot())
        // D6：手动会话等满 30 秒才终结 —— 显式让一次性采集截止。
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = sessionId!!,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        while (harness.coordinator.state.value.phase != AcquisitionPhase.Completed) {
            if (System.nanoTime() > deadline) {
                throw AssertionError("manual result did not reach Completed")
            }
            Thread.yield()
        }

        service.onDestroy()

        assertEquals(sessionId, harness.coordinator.state.value.sessionId)
        assertEquals(
            "owned Completed manual result must survive an unexpected onDestroy",
            AcquisitionPhase.Completed,
            harness.coordinator.state.value.phase
        )
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun onDestroyCancelsOwnedManualSessionOnUnexpectedDestruction() {
        val harness = newHarness()
        val store = trackingStore("fg_owned_destroy_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        // Unexpected service destruction (no explicit PAUSE/STOP_COLLECTION teardown)
        // must still cancel the manual session this instance actually started.
        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 1
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)

        service.onDestroy()
        assertEquals(sessionId, harness.coordinator.state.value.sessionId)
        assertEquals(AcquisitionPhase.Cancelled, harness.coordinator.state.value.phase)
    }

    @Test
    fun onDestroyPreservesExternalManualSession() {
        val harness = newHarness()
        val service = buildService(harness = harness)
        // The active manual session was started by another instance, the UI
        // controller or an unrelated path, not by this instance: unexpected
        // destruction must preserve it.
        harness.forceState(
            LocationAcquisitionState(
                sessionId = "external-manual",
                triggerType = TriggerType.MANUAL,
                phase = AcquisitionPhase.Evaluating
            )
        )
        service.onDestroy()
        assertEquals("external-manual", harness.coordinator.state.value.sessionId)
        assertTrue(
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun explicitCollectionStopPreservesActiveManualSessionAcrossTeardown() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_stop_manual_preserve_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)

        val manual = harness.coordinator.startManualSession() as SessionStartResult.Started
        idleUntil { harness.coordinator.state.value.sessionId == manual.sessionId }

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_STOP_COLLECTION),
            0,
            51
        )
        // Production teardown runs onDestroy() after the explicit stop.
        service.onDestroy()

        assertEquals(manual.sessionId, harness.coordinator.state.value.sessionId)
        assertEquals(TriggerType.MANUAL, harness.coordinator.state.value.triggerType)
        assertTrue(
            "explicit collection stop must not cancel an active manual session",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun explicitPausePreservesActiveManualSessionAcrossTeardown() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_pause_manual_preserve_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)

        val manual = harness.coordinator.startManualSession() as SessionStartResult.Started
        idleUntil { harness.coordinator.state.value.sessionId == manual.sessionId }

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_PAUSE_COLLECTION),
            0,
            53
        )
        // Production teardown runs onDestroy() after the explicit pause.
        service.onDestroy()

        assertEquals(manual.sessionId, harness.coordinator.state.value.sessionId)
        assertEquals(TriggerType.MANUAL, harness.coordinator.state.value.triggerType)
        assertTrue(
            "explicit pause must not cancel an active manual session",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun stopThenManualSessionUnexpectedDestroyCancelsNewManualSession() {
        val harness = newHarness()
        val store = trackingStore("fg_stop_then_manual_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_STOP_COLLECTION),
            0,
            1
        )

        // A new manual session on the same instance clears the explicit-teardown
        // flags; unexpected destruction must then cancel this new session.
        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0,
            2
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val manualSessionId = harness.coordinator.state.value.sessionId
        assertNotNull(manualSessionId)

        service.onDestroy()

        assertEquals(manualSessionId, harness.coordinator.state.value.sessionId)
        assertEquals(AcquisitionPhase.Cancelled, harness.coordinator.state.value.phase)
        assertNull(
            "unexpected destruction after STOP+manual session must remove the notification",
            findNotification(nm)
        )
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun pauseThenManualSessionUnexpectedDestroyCancelsNewManualSessionAndPausedNotification() {
        val harness = newHarness()
        val store = trackingStore("fg_pause_then_manual_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_PAUSE_COLLECTION),
            0,
            1
        )
        assertNotNull("pause must leave its paused notification", findNotification(nm))

        // A fresh manual session must clear the paused lifecycle flags so a later
        // unexpected destruction cancels the session and removes the notification.
        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0,
            2
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val manualSessionId = harness.coordinator.state.value.sessionId
        assertNotNull(manualSessionId)

        service.onDestroy()

        assertEquals(manualSessionId, harness.coordinator.state.value.sessionId)
        assertEquals(AcquisitionPhase.Cancelled, harness.coordinator.state.value.phase)
        assertNull(
            "paused notification must not outlive unexpected destruction after a new manual session",
            findNotification(nm)
        )
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun cancelWithWrongSessionIdPreservesActiveManualSessionAndForeground() {
        val harness = newHarness()
        val store = trackingStore("fg_cancel_wrong_id_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 61
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)
        assertNotNull(findNotification(nm))

        // A stale/wrong session id must fail closed: the coordinator refuses the
        // cancel, so the service must keep the active session, its 7101
        // notification and its foreground state untouched, and must not induce
        // an onDestroy() that would cancel the valid session.
        val result = service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_CANCEL_LOCATION_SESSION)
                .putExtra(ForegroundLocationController.EXTRA_SESSION_ID, "wrong-or-stale-id"),
            0, 62
        )
        shadowOf(Looper.getMainLooper()).idle()

        assertEquals(sessionId, harness.coordinator.state.value.sessionId)
        assertTrue(
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertEquals(android.app.Service.START_STICKY, result)
        assertFalse(
            "failed cancel must not stop the service",
            shadowOf(service).isStoppedBySelf
        )
        assertFalse(
            "failed cancel must not remove the foreground state",
            shadowOf(service).isForegroundStopped
        )
        assertNotNull(
            "failed cancel must keep the 7101 notification",
            findNotification(nm)
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun cancelWithoutSessionIdWhileSessionNonCancellablePreservesSessionAndForeground() {
        val harness = newHarness()
        val store = trackingStore("fg_cancel_missing_id_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 63
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)
        // A cancel intent without a session id must fail closed and leave the
        // active session, the 7101 notification and the foreground state alone.
        val result = service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_CANCEL_LOCATION_SESSION),
            0, 64
        )
        shadowOf(Looper.getMainLooper()).idle()

        assertEquals(sessionId, harness.coordinator.state.value.sessionId)
        assertTrue(
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertEquals(android.app.Service.START_STICKY, result)
        assertFalse(
            "failed cancel must not stop the service",
            shadowOf(service).isStoppedBySelf
        )
        assertFalse(
            "failed cancel must not remove the foreground state",
            shadowOf(service).isForegroundStopped
        )
        assertNotNull(
            "failed cancel must keep the 7101 notification",
            findNotification(nm)
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun secondInstanceManualStartReplacesSessionAndRetiresForegroundWhenDone() {
        val harness = newHarness()
        val store = trackingStore("fg_busy_waiter_", enabled = false)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        val serviceA = buildService(harness = harness, trackingStore = store)
        serviceA.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 81
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)

        // A second manual-only instance arriving while a one-shot is in flight
        // triggers the restart semantics: the coordinator cancels the old
        // session and starts a replacement owned by the new instance. The new
        // instance stays foreground until the replacement completes, then
        // retires its foreground and stops itself.
        val serviceB = buildService(harness = harness, trackingStore = store)
        serviceB.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 82
        )
        shadowOf(Looper.getMainLooper()).idle()
        idleUntil { harness.runner.acquireCount.get() >= 2 }
        assertNotEquals(sessionId, harness.coordinator.state.value.sessionId)

        assertFalse(
            "replacement start must not stop the new instance while the session is active",
            shadowOf(serviceB).isStoppedBySelf
        )
        assertFalse(
            "replacement start must keep the new instance foreground",
            shadowOf(serviceB).isForegroundStopped
        )
        assertNotNull(
            "replacement start must keep the foreground notification",
            findNotification(nm)
        )

        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        idleUntil { shadowOf(serviceB).isStoppedBySelf }

        assertTrue(
            "busy instance must retire its foreground when the adopted session completes",
            shadowOf(serviceB).isForegroundStopped
        )
        serviceA.onDestroy()
        serviceB.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun busyManualStartRetiresOnReplacementWithoutTouchingReplacement() {
        val harness = newHarness()
        val store = trackingStore("fg_busy_waiter_replace_", enabled = false)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        val serviceA = buildService(harness = harness, trackingStore = store)
        serviceA.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 83
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)

        val serviceB = buildService(harness = harness, trackingStore = store)
        serviceB.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 84
        )
        shadowOf(Looper.getMainLooper()).idle()
        assertFalse(
            "busy start must not stop the new instance while the session is active",
            shadowOf(serviceB).isStoppedBySelf
        )

        // The adopted session ends while a replacement starts. The busy
        // instance must retire its own service (not wait forever) but must
        // not cancel, stop or remove the replacement session's foreground.
        harness.forceState(
            LocationAcquisitionState(
                sessionId = sessionId,
                triggerType = TriggerType.MANUAL,
                phase = AcquisitionPhase.Idle
            )
        )
        val replacement = harness.coordinator.startManualSession() as SessionStartResult.Started
        assertNotEquals(sessionId, replacement.sessionId)
        idleUntil { shadowOf(serviceB).isStoppedBySelf }
        assertTrue(
            "busy instance must retire itself once its observed session is replaced",
            shadowOf(serviceB).isStoppedBySelf
        )

        assertEquals(replacement.sessionId, harness.coordinator.state.value.sessionId)
        assertTrue(
            "replacement session must stay active",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertFalse(
            "retiring instance must not remove the foreground state",
            shadowOf(serviceB).isForegroundStopped
        )
        assertNotNull(
            "replacement session must keep the 7101 notification",
            findNotification(nm)
        )
        serviceA.onDestroy()
        serviceB.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun startedManualSessionWaiterRetiresOnReplacementWithoutTouchingReplacement() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_started_waiter_replace_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 85
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val startedId = harness.coordinator.state.value.sessionId
        assertNotNull(startedId)
        assertFalse(shadowOf(service).isStoppedBySelf)
        assertNotNull(findNotification(nm))

        // Advance the coordinator to Completed WITHOUT idling the
        // paused main looper: the Started waiter's terminal observation is
        // queued but has not resumed yet, which is exactly the deterministic
        // window in which a replaceAwaitingManual start switches the session id
        // before the old waiter ever sees the old terminal state.
        harness.runner.emitCandidate(acceptedSnapshot())
        // D6：手动会话等满 30 秒才终结 —— 显式让一次性采集截止。
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = startedId!!,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        while (harness.coordinator.state.value.phase != AcquisitionPhase.Completed) {
            if (System.nanoTime() > deadline) {
                throw AssertionError("manual result did not reach Completed")
            }
            Thread.yield()
        }

        // The old Started session is replaced while its waiter is still
        // suspended: the waiter must self-retire (never wait forever) and must
        // not cancel, stop or remove the replacement session's foreground.
        val replacement = harness.coordinator.startManualSession() as SessionStartResult.Started
        assertNotEquals(startedId, replacement.sessionId)
        idleUntil { shadowOf(service).isStoppedBySelf }

        assertTrue(
            "Started waiter must retire itself once its observed session is replaced",
            shadowOf(service).isStoppedBySelf
        )
        assertEquals(replacement.sessionId, harness.coordinator.state.value.sessionId)
        assertTrue(
            "replacement session must stay active",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertFalse(
            "retiring instance must not remove the foreground state",
            shadowOf(service).isForegroundStopped
        )
        assertNotNull(
            "replacement session must keep the 7101 notification",
            findNotification(nm)
        )
        val ownedField = ForegroundLocationService::class.java
            .getDeclaredField("ownedManualSessionId")
            .apply { isAccessible = true }
        assertNull(
            "old instance must no longer own the replaced session",
            ownedField.get(service)
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun sameInstanceManualReplacementMustNotRetireServiceWhileReplacementOwned() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_same_instance_replace_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)
        val nm = ApplicationProvider.getApplicationContext<Application>()
            .getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 85
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val startedId = harness.coordinator.state.value.sessionId
        assertNotNull(startedId)
        assertFalse(shadowOf(service).isStoppedBySelf)
        assertNotNull(findNotification(nm))

        // Advance the coordinator to Completed WITHOUT idling the
        // paused main looper: the Started waiter's terminal continuation is
        // queued but has not executed yet, which is exactly the deterministic
        // window in which a second start through the SAME instance switches
        // ownership before the old waiter ever resumes.
        harness.runner.emitCandidate(acceptedSnapshot())
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = startedId!!,
                bestLocation = acceptedSnapshot(),
                completion = LocationEngineCompletion.TimedOut
            )
        )
        val deadline = System.nanoTime() + TimeUnit.SECONDS.toNanos(5)
        while (harness.coordinator.state.value.phase != AcquisitionPhase.Completed) {
            if (System.nanoTime() > deadline) {
                throw AssertionError("manual result did not reach Completed")
            }
            Thread.yield()
        }

        // Second ACTION_START_MANUAL_SESSION through the same service instance:
        // the instance now owns the replacement, so the old Started waiter must
        // NOT retire the service or drop ownership of the replacement.
        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 86
        )
        val replacementId = harness.coordinator.state.value.sessionId
        assertNotEquals(startedId, replacementId)
        shadowOf(Looper.getMainLooper()).idle()
        idleUntil {
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        }

        assertFalse(
            "the old Started waiter must not stop the service while the same instance owns the replacement",
            shadowOf(service).isStoppedBySelf
        )
        val ownedField = ForegroundLocationService::class.java
            .getDeclaredField("ownedManualSessionId")
            .apply { isAccessible = true }
        assertEquals(
            "ownership must remain on the replacement session started by the same instance",
            replacementId,
            ownedField.get(service)
        )
        assertEquals(replacementId, harness.coordinator.state.value.sessionId)
        assertTrue(
            "replacement session must stay active",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertFalse(
            "replacement session must keep the foreground state",
            shadowOf(service).isForegroundStopped
        )
        assertNotNull(
            "replacement session must keep the 7101 notification",
            findNotification(nm)
        )
        service.onDestroy()
    }

    fun sameInstanceWaiterDoesNotRetireServiceWhenReplacementIsStarted() {
        grantCollectionPrerequisites()
        val harness = newHarness()
        val store = trackingStore("fg_same_inst_replace_", enabled = false)
        val service = buildService(harness = harness, trackingStore = store)

        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 85
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val firstId = harness.coordinator.state.value.sessionId
        assertNotNull(firstId)

        // 第二次手动触发：替换语义下旧会话被取消、新会话接替。旧会话的
        // waiter（观察 firstId）看到 sessionId 变化后必须自我退役，且不得
        // 停止服务或清除新会话的 owner。
        service.onStartCommand(
            Intent(ApplicationProvider.getApplicationContext(), ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 86
        )
        shadowOf(Looper.getMainLooper()).idle()
        val replacementId = harness.coordinator.state.value.sessionId
        assertNotEquals(firstId, replacementId)

        shadowOf(Looper.getMainLooper()).idle()
        assertFalse(
            "the old waiter must not stop the service while the same instance owns the replacement",
            shadowOf(service).isStoppedBySelf
        )
        val ownedField = ForegroundLocationService::class.java
            .getDeclaredField("ownedManualSessionId")
            .apply { isAccessible = true }
        assertEquals(
            "ownership must move to the replacement session started by the same instance",
            replacementId,
            ownedField.get(service)
        )
        assertEquals(replacementId, harness.coordinator.state.value.sessionId)
        assertTrue(
            "replacement session must stay active",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        assertFalse(
            "replacement session must keep the foreground state",
            shadowOf(service).isForegroundStopped
        )

        // 新会话终结后，由它的 waiter 退役服务
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = replacementId!!,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        idleUntil { shadowOf(service).isStoppedBySelf }
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun manualSyncTeardownPreservesExternalActiveManualSession() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)
        val harness = newHarness()
        val store = trackingStore("fg_sync_preserve_ext_", enabled = false)

        val serviceA = buildService(harness = harness, trackingStore = store)
        serviceA.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 91
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)

        // A manual sync on a different instance must not cancel the manual
        // session the other instance is running, even though the sync tears
        // its own service instance down afterwards.
        val serviceB = buildService(harness = harness, trackingStore = store)
        serviceB.mobileSyncScheduler = MobileSyncScheduler(context, store)
        serviceB.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
            0, 92
        )
        shadowOf(Looper.getMainLooper()).idle()
        assertTrue(shadowOf(serviceB).isStoppedBySelf)
        serviceB.onDestroy()

        assertEquals(sessionId, harness.coordinator.state.value.sessionId)
        assertEquals(TriggerType.MANUAL, harness.coordinator.state.value.triggerType)
        assertTrue(
            "sync teardown must not cancel another instance's manual session",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        serviceA.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun sameInstanceManualSyncMustNotRetireOwnedActiveManualSession() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)
        val harness = newHarness()
        val store = trackingStore("fg_sync_preserve_owned_", enabled = false)

        val service = buildService(harness = harness, trackingStore = store)
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_START_MANUAL_SESSION),
            0, 91
        )
        idleUntil { harness.runner.acquireCount.get() >= 1 }
        val sessionId = harness.coordinator.state.value.sessionId
        assertNotNull(sessionId)
        assertFalse(shadowOf(service).isStoppedBySelf)

        // A manual sync on the SAME instance must not retire the service nor
        // cancel the active manual session this instance owns and is running,
        // even though the sync's manual-only teardown would normally stop the
        // instance (which would then destroy the owned session via onDestroy).
        service.mobileSyncScheduler = MobileSyncScheduler(context, store)
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
            0, 92
        )
        shadowOf(Looper.getMainLooper()).idle()

        assertFalse(
            "sync teardown must not stop the service while the same instance owns an active manual session",
            shadowOf(service).isStoppedBySelf
        )
        assertEquals(sessionId, harness.coordinator.state.value.sessionId)
        assertEquals(TriggerType.MANUAL, harness.coordinator.state.value.triggerType)
        assertTrue(
            "same-instance sync must not cancel the owned manual session",
            harness.coordinator.state.value.phase in setOf(
                AcquisitionPhase.Preparing,
                AcquisitionPhase.Acquiring,
                AcquisitionPhase.Evaluating
            )
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun syncOnlyInstanceDestroyPreservesExternalAutomaticSessionAndNotification() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        WorkManagerTestInitHelper.initializeTestWorkManager(context)
        val harness = newHarness()
        val store = trackingStore("fg_sync_only_destroy_", enabled = true)
        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(LocationNotificationRenderer.NOTIFICATION_ID)

        // An automatic session is running and owned elsewhere (e.g. by another
        // service instance's automatic loop).
        harness.coordinator.startAutomaticStream(
            AcquisitionContext(
                policyMode = "PowerSavingNormal",
                scheduleLowFrequency = false,
                motionSignal = "Static",
                requestIntervalMillis = 60_000L
            )
        )
        idleUntil { harness.coordinator.isAutomaticStreamActive() }

        val service = buildService(harness = harness, trackingStore = store)
        service.mobileSyncScheduler = MobileSyncScheduler(context, store)
        service.onStartCommand(
            Intent(context, ForegroundLocationService::class.java)
                .setAction(ForegroundLocationController.ACTION_SYNC_NOW),
            0, 101
        )
        shadowOf(Looper.getMainLooper()).idle()
        assertNotNull(
            "sync-only instance posts its own foreground notification",
            findNotification(nm)
        )
        // An unexpected destruction of the sync-only instance must neither
        // cancel the automatic session started by another instance nor remove
        // its 7101 notification itself. (Robolectric's shadow removes a
        // foreground service's notification at onDestroy like the system
        // tears down an FGS, so the assertion targets the service's own
        // foreground state: the old code called stopForeground(REMOVE) here.)
        service.onDestroy()

        assertTrue(
            "external automatic stream must survive the sync-only teardown",
            harness.coordinator.isAutomaticStreamActive()
        )
        assertEquals(
            "external automatic stream must keep its registered interval",
            60_000L,
            harness.coordinator.streamState.value.requestIntervalMillis
        )
        assertFalse(
            "sync-only onDestroy must not remove the foreground (shared 7101) itself",
            shadowOf(service).isForegroundStopped
        )
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun queueStatusRepositoryDecreasesPendingUploadTotal() {
        val locations = MutableStateFlow(5)
        val queueRepo = emptyQueueStatusRepo(locations)
        val harness = newHarness()
        val store = trackingStore("fg_queue_total_", enabled = true)

        val service = buildService(
            harness = harness,
            queueStatusRepository = queueRepo,
            trackingStore = store
        )
        invokeObserveQueueStatus(service)
        idleUntil { ForegroundLocationService.runtimeState.value.pendingUploadTotal == 5 }
        assertEquals(5, ForegroundLocationService.runtimeState.value.pendingUploadTotal)

        locations.value = 2
        idleUntil { ForegroundLocationService.runtimeState.value.pendingUploadTotal == 2 }
        assertEquals(2, ForegroundLocationService.runtimeState.value.pendingUploadTotal)
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun highSpeedFixReRegistersStreamAtDenseInterval() {
        val harness = newHarness()
        val store = trackingStore("fg_hs_dense_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        invokeInitializeAutomaticRuntime(service)
        invokeStartAutomaticLoop(service)
        idleUntil { harness.runner.acquireCount.get() == 1 }
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        harness.runner.waitForStreamStart()
        assertEquals(180_000L, harness.runner.lastStreamRequest!!.intervalMillis)

        harness.runner.emitStreamCandidate(
            acceptedSnapshot().copy(speedMetersPerSecond = 9f)
        )
        idleUntil { harness.coordinator.streamState.value.requestIntervalMillis == 2_500L }

        assertEquals(2_500L, harness.runner.lastStreamRequest!!.intervalMillis)
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun sustainedHighSpeedFixesActivateRuntimeStateAndNotificationCopy() {
        val context = ApplicationProvider.getApplicationContext<Application>()
        val harness = newHarness()
        val store = trackingStore("fg_hs_active_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        invokeInitializeAutomaticRuntime(service)
        invokeStartAutomaticLoop(service)
        idleUntil { harness.runner.acquireCount.get() == 1 }
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        harness.runner.waitForStreamStart()

        val base = System.currentTimeMillis()
        val fast = acceptedSnapshot(timeMillis = base).copy(speedMetersPerSecond = 9f)
        harness.runner.emitStreamCandidate(fast)
        idleUntil { harness.coordinator.streamState.value.requestIntervalMillis == 2_500L }
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 4_000L))
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 8_000L))
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 12_000L))

        idleUntil { ForegroundLocationService.runtimeState.value.highSpeedActive }
        assertTrue(ForegroundLocationService.runtimeState.value.highSpeedActive)

        // 高速档持续期间：新 fix 刷新已记录时长
        shadowOf(Looper.getMainLooper()).idleFor(5_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 17_000L))
        idleUntil {
            ForegroundLocationService.runtimeState.value.highSpeedElapsedSeconds >= 5L
        }
        assertTrue(ForegroundLocationService.runtimeState.value.highSpeedElapsedSeconds >= 5L)

        val nm = context.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val notification = findNotification(nm)
        assertNotNull("foreground notification must be present while collecting", notification)
        val collapsed = notification!!.extras
            .getCharSequence(Notification.EXTRA_TEXT).toString()
        assertTrue(
            "collapsed text must show high-speed copy but was: $collapsed",
            collapsed.contains("高速轨迹记录中")
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun slowFixesForSixtySecondsFallBackFromHighSpeed() {
        val harness = newHarness()
        val store = trackingStore("fg_hs_fall_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        invokeInitializeAutomaticRuntime(service)
        invokeStartAutomaticLoop(service)
        idleUntil { harness.runner.acquireCount.get() == 1 }
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        harness.runner.waitForStreamStart()

        val base = System.currentTimeMillis()
        val fast = acceptedSnapshot(timeMillis = base).copy(speedMetersPerSecond = 9f)
        harness.runner.emitStreamCandidate(fast)
        idleUntil { harness.coordinator.streamState.value.requestIntervalMillis == 2_500L }
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 4_000L))
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 8_000L))
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 12_000L))
        idleUntil { ForegroundLocationService.runtimeState.value.highSpeedActive }

        // 等红灯/停车：低速样本持续 60s 后回落
        val slow = acceptedSnapshot(timeMillis = base + 20_000L).copy(speedMetersPerSecond = 0.1f)
        harness.runner.emitStreamCandidate(slow)
        shadowOf(Looper.getMainLooper()).idleFor(20_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(slow.copy(timeMillis = base + 40_000L))
        shadowOf(Looper.getMainLooper()).idleFor(20_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(slow.copy(timeMillis = base + 60_000L))
        shadowOf(Looper.getMainLooper()).idleFor(20_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(slow.copy(timeMillis = base + 80_000L))

        idleUntil { !ForegroundLocationService.runtimeState.value.highSpeedActive }
        assertFalse(ForegroundLocationService.runtimeState.value.highSpeedActive)
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun stoppingCollectionResetsHighSpeedRuntimeState() {
        val harness = newHarness()
        val store = trackingStore("fg_hs_stop_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        invokeInitializeAutomaticRuntime(service)
        invokeStartAutomaticLoop(service)
        idleUntil { harness.runner.acquireCount.get() == 1 }
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        harness.runner.waitForStreamStart()

        val base = System.currentTimeMillis()
        val fast = acceptedSnapshot(timeMillis = base).copy(speedMetersPerSecond = 9f)
        harness.runner.emitStreamCandidate(fast)
        idleUntil { harness.coordinator.streamState.value.requestIntervalMillis == 2_500L }
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 4_000L))
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 8_000L))
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 12_000L))
        idleUntil { ForegroundLocationService.runtimeState.value.highSpeedActive }

        invokeStopCollection(service)

        assertFalse(
            "paused collection must not keep high-speed state",
            ForegroundLocationService.runtimeState.value.highSpeedActive
        )
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun gpsLossWithoutNewFixesFallsBackFromHighSpeed() {
        val harness = newHarness()
        val store = trackingStore("fg_hs_gpsloss_", enabled = true)
        val service = buildService(harness = harness, trackingStore = store)
        invokeInitializeAutomaticRuntime(service)
        invokeStartAutomaticLoop(service)
        idleUntil { harness.runner.acquireCount.get() == 1 }
        harness.runner.completeCurrent(
            LocationEngineResult(
                sessionId = harness.runner.lastRequest!!.sessionId,
                bestLocation = null,
                completion = LocationEngineCompletion.TimedOut
            )
        )
        harness.runner.waitForStreamStart()

        val base = System.currentTimeMillis()
        val fast = acceptedSnapshot(timeMillis = base).copy(speedMetersPerSecond = 9f)
        harness.runner.emitStreamCandidate(fast)
        idleUntil { harness.coordinator.streamState.value.requestIntervalMillis == 2_500L }
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 4_000L))
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 8_000L))
        shadowOf(Looper.getMainLooper()).idleFor(4_000L, TimeUnit.MILLISECONDS)
        harness.runner.emitStreamCandidate(fast.copy(timeMillis = base + 12_000L))
        idleUntil { ForegroundLocationService.runtimeState.value.highSpeedActive }

        // GPS 失锁：不再有新 fix，仅 30s 兜底重算按 null 观察速度，
        // 60s（含首个 30s 兜底）后回落
        shadowOf(Looper.getMainLooper()).idleFor(30_000L, TimeUnit.MILLISECONDS)
        shadowOf(Looper.getMainLooper()).idleFor(30_000L, TimeUnit.MILLISECONDS)
        shadowOf(Looper.getMainLooper()).idleFor(30_000L, TimeUnit.MILLISECONDS)

        idleUntil { !ForegroundLocationService.runtimeState.value.highSpeedActive }
        assertFalse(ForegroundLocationService.runtimeState.value.highSpeedActive)
        service.onDestroy()
    }

    private fun invokeInitializeAutomaticRuntime(service: ForegroundLocationService) {
        ForegroundLocationService::class.java
            .getDeclaredMethod("initializeAutomaticRuntime", TrackingSettings::class.java)
            .apply { isAccessible = true }
            .invoke(service, service.trackingSettingsStore.read())
    }

    @Test
    fun automaticLoopSourceRegistersPersistentStreamAndReactsToMotionChanges() {
        val source = serviceSource()
        assertTrue(source.contains("startAutomaticLoop()"))
        assertTrue(source.contains("locationAcquisitionCoordinator.startAutomaticStream("))
        assertTrue(source.contains("locationAcquisitionCoordinator.updateAutomaticStream("))
        assertTrue(source.contains("locationAcquisitionCoordinator.stopAutomaticStream()"))
        assertTrue(source.contains("isAutomaticStreamActive()"))
        // WO-ANDROID-GATE-20260926：循环等待改为「上限 30 秒、可提前到冲刺周期边界」。
        // 断言**意图**（30 秒上限常量 + 用 withTimeoutOrNull 施加）而不是字面量，
        // 否则「只缩短等待」的对齐改动会被误判成回归。
        assertTrue(source.contains("withTimeoutOrNull(loopWaitMillis("))
        assertTrue(source.contains("LOOP_WAIT_CAP_MILLIS = 30_000L"))
        assertTrue(source.contains("requestIntervalMillis = decision.requestIntervalMillis"))
        assertFalse(
            "automatic loop must not insert an initial delay before the first round",
            source.contains("delay(currentDecision.requestIntervalMillis")
        )
    }

    private fun serviceSource(): String {
        val relativePath = "app/src/main/java/com/pim/app/location/service/ForegroundLocationService.kt"
        return sequenceOf(
            java.io.File(relativePath),
            java.io.File(relativePath.removePrefix("app/")),
            java.io.File("..", relativePath)
        ).firstOrNull { it.isFile }?.readText()
            ?: error("source not found for $relativePath (cwd=${java.io.File(".").absolutePath})")
    }

    @Test
    fun freshSnapshotWithCacheErrorIsNotReportedAsNormal() {
        val service = buildService()
        val method = ForegroundLocationService::class.java
            .getDeclaredMethod("scheduleApiStateText", ScheduleCacheSnapshot::class.java)
            .apply { isAccessible = true }
        val text = method.invoke(
            service,
            ScheduleCacheSnapshot(
                serverIdentity = "https://server.example",
                windows = emptyList(),
                freshness = ScheduleCacheFreshness.Fresh,
                lastAttemptAtMillis = 2L,
                lastSuccessAtMillis = 2L,
                lastError = "本地日程缓存不可用",
                errorKind = com.pim.app.schedule.ScheduleRefreshErrorKind.Cache
            )
        ) as String

        assertEquals("日程缓存异常", text)
        service.onDestroy()
    }

    @Test
    fun staleSnapshotWithoutErrorIsNotNormal() {
        val service = buildService()
        val method = ForegroundLocationService::class.java
            .getDeclaredMethod("scheduleApiStateText", ScheduleCacheSnapshot::class.java)
            .apply { isAccessible = true }
        val text = method.invoke(
            service,
            ScheduleCacheSnapshot(
                serverIdentity = "https://server.example",
                windows = emptyList(),
                freshness = ScheduleCacheFreshness.Stale,
                lastAttemptAtMillis = 100L,
                lastSuccessAtMillis = 100L,
                lastError = null,
                errorKind = null
            )
        ) as String

        assertEquals("日程缓存可能过期", text)
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun policyTransitionWriterDoesNotSwallowCancellation() {
        val service = buildService()
        service.policyTransitionRecorder = PolicyTransitionRecorder { _, _ ->
            throw CancellationException("cancel transition write")
        }

        invokeApplyDecision(
            service,
            PolicyDecision(
                mode = LocationPolicyMode.MotionObservation,
                requestIntervalMillis = 30_000L,
                nextExpectedLocationAtMillis = 1_000L,
                reason = "测试运动",
                scheduleLowFrequency = false
            )
        )
        shadowOf(Looper.getMainLooper()).idle()

        val field = ForegroundLocationService::class.java
            .getDeclaredField("policyTransitionWriteJob")
            .apply { isAccessible = true }
        assertTrue("取消必须传递到策略写入协程", (field.get(service) as Job).isCancelled)
        service.onDestroy()
    }

    @Test
    @LooperMode(LooperMode.Mode.PAUSED)
    fun policyTransitionWritesDoNotDropRapidChanges() {
        val service = buildService()
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        val recorded = CopyOnWriteArrayList<String>()
        var first = true
        service.policyTransitionRecorder = PolicyTransitionRecorder { _, decision ->
            if (first) {
                first = false
                started.complete(Unit)
                release.await()
            }
            recorded += decision.reason
            0L
        }

        val firstDecision = PolicyDecision(
            mode = LocationPolicyMode.PowerSavingNormal,
            requestIntervalMillis = 180_000L,
            nextExpectedLocationAtMillis = 1_000L,
            reason = "第一条",
            scheduleLowFrequency = false
        )
        val secondDecision = firstDecision.copy(
            mode = LocationPolicyMode.MotionObservation,
            requestIntervalMillis = 30_000L,
            reason = "第二条"
        )
        invokeApplyDecision(service, firstDecision)
        runBlocking { withTimeout(5_000) { started.await() } }
        invokeApplyDecision(service, secondDecision)
        release.complete(Unit)
        idleUntil { recorded.size == 2 }

        assertEquals(listOf("第一条", "第二条"), recorded.toList())
        service.onDestroy()
    }

    private fun invokeRefresh(service: ForegroundLocationService) {
        ForegroundLocationService::class.java
            .getDeclaredMethod("refreshScheduleWindows", Boolean::class.javaPrimitiveType)
            .apply { isAccessible = true }
            .invoke(service, false)
    }

    private fun invokeApplyDecision(service: ForegroundLocationService, decision: PolicyDecision) {
        ForegroundLocationService::class.java
            .getDeclaredMethod(
                "applyDecision",
                PolicyDecision::class.java,
                Boolean::class.javaPrimitiveType
            )
            .apply { isAccessible = true }
            .invoke(service, decision, true)
    }

    private fun readScheduleWindows(service: ForegroundLocationService): List<ScheduleWindow> {
        return service.scheduleWindowRepository.snapshot.value.windows
    }

    private fun isScheduleRefreshJobActive(service: ForegroundLocationService): Boolean {
        return readScheduleRefreshJob(service)?.isActive == true
    }

    private fun isSnapshotCollectorActive(service: ForegroundLocationService): Boolean {
        return readSnapshotCollectorJob(service)?.isActive == true
    }

    private fun readScheduleRefreshJob(service: ForegroundLocationService): Job? {
        val field = ForegroundLocationService::class.java.getDeclaredField("scheduleRefreshJob")
        field.isAccessible = true
        return field.get(service) as Job?
    }

    private fun readSnapshotCollectorJob(service: ForegroundLocationService): Job? {
        val field = ForegroundLocationService::class.java.getDeclaredField("snapshotCollectJob")
        field.isAccessible = true
        return field.get(service) as Job?
    }

    private fun invokeStopCollection(service: ForegroundLocationService) {
        ForegroundLocationService::class.java
            .getDeclaredMethod("stopCollection")
            .apply { isAccessible = true }
            .invoke(service)
    }

    private fun idleUntil(timeoutMillis: Long = 5_000L, predicate: () -> Boolean) {
        val deadline = System.nanoTime() + TimeUnit.MILLISECONDS.toNanos(timeoutMillis)
        while (!predicate()) {
            shadowOf(Looper.getMainLooper()).idle()
            if (System.nanoTime() > deadline) {
                throw AssertionError("condition not met within ${timeoutMillis}ms")
            }
            Thread.yield()
        }
        shadowOf(Looper.getMainLooper()).idle()
    }

    private inner class ScheduleServiceFixture {
        val context: Application = ApplicationProvider.getApplicationContext()
        val prefs = context.getSharedPreferences(
            "fg_schedule_fixture_" + System.nanoTime(),
            Context.MODE_PRIVATE
        ).also { it.edit().clear().commit() }
        val store = TrackingSettingsStore(prefs)
        val cacheDir = java.io.File(context.filesDir, "fg-schedule-cache-" + System.nanoTime()).also {
            it.mkdirs()
        }
        val cacheStore = ScheduleCacheStore(cacheDir, Json { ignoreUnknownKeys = true })
        val authStore = object : AuthSessionStore {
            override fun snapshot() = AuthSessionSnapshot(null, null)
            override fun save(
                accessToken: String,
                refreshToken: String,
                expiresAtUtcMillis: Long,
                serverIdentity: String
            ) = true
            override fun clear() = true
        }
        val serverSettings = ServerSettingsStore(context, authStore).also {
            it.setBaseUrl("http://test-server:5858/api/v1/")
        }
        val api = GatedApiService()
        val repository = ScheduleWindowRepository(api, cacheStore, serverSettings)

        fun createService(): ForegroundLocationService {
            val service = Robolectric.buildService(ForegroundLocationService::class.java).get()
            val harness = newHarness()
            service.locationAcquisitionCoordinator = harness.coordinator
            service.queueStatusRepository = emptyQueueStatusRepo()
            service.motionSignalRepository = MotionSignalRepository(context)
            service.trackingSettingsStore = store
            service.scheduleWindowRepository = repository
            service.attachSprintAndPassiveDependencies(harness, context, store)
            attachPolicyTransitionDependencies(service)
            return service
        }

        fun cleanup() {
            cacheDir.deleteRecursively()
        }
    }


    class CoordinatorHarness(
        var prerequisiteResult: LocationPrerequisiteResult = LocationPrerequisiteResult.Ready
    ) {
        val runner = ControllableRunner()
        /** 冲刺台账假件（本文件只关心接线，台账落库由 LocationSprintLedgerTest 守）。 */
        val sprintLedger = RecordingSprintLedgerPort()
        val trackingSettingsStore = TrackingSettingsStore(
            InMemorySharedPreferences()
        )
        val coordinator = LocationAcquisitionCoordinator(
            runner = runner,
            prerequisiteChecker = object : LocationPrerequisiteChecker {
                override fun check(triggerType: TriggerType): LocationPrerequisiteResult =
                    prerequisiteResult
            },
            operations = object : LocationAcquisitionOperations {
                override suspend fun enqueueAccepted(
                    accepted: com.pim.app.location.quality.QualityAcceptedLocation,
                    rawJson: String,
                    source: String
                ) = Unit

                override suspend fun recordDropped(
                    fix: com.pim.app.location.quality.RawLocationFix,
                    reason: String
                ) = Unit

                override fun scheduleSync() = Unit
            },
            json = Json { ignoreUnknownKeys = true },
            trackingSettingsStore = trackingSettingsStore
        )
        private val stateField = LocationAcquisitionCoordinator::class.java
            .getDeclaredField("_state")
            .apply { isAccessible = true }

        @Suppress("UNCHECKED_CAST")
        private val mutableState: MutableStateFlow<LocationAcquisitionState>
            get() = stateField.get(coordinator) as MutableStateFlow<LocationAcquisitionState>

        fun forceState(state: LocationAcquisitionState) {
            mutableState.value = state
        }
    }

    class ControllableRunner : LocationAcquisitionRunner, com.pim.app.location.sprint.SprintUpdateSource {
        data class Session(
            val request: LocationEngineRequest,
            val onCandidate: suspend (LocationSnapshot) -> Unit,
            val result: CompletableDeferred<LocationEngineResult> = CompletableDeferred()
        )

        data class StreamSession(
            val request: LocationUpdateRequest,
            val onCandidate: suspend (LocationSnapshot) -> Unit
        )

        private val sessions = CopyOnWriteArrayList<Session>()
        private val streams = CopyOnWriteArrayList<StreamSession>()
        val acquireCount = AtomicInteger(0)
        val streamCount = AtomicInteger(0)
        val lastRequest: LocationEngineRequest?
            get() = sessions.lastOrNull()?.request
        val lastStreamRequest: LocationUpdateRequest?
            get() = streams.lastOrNull()?.request
        val streamStart = CompletableDeferred<Unit>()
        private val streamHold = CompletableDeferred<Unit>()

        // ── 冲刺的独立注册（AC-5.6）──
        private val sprintStreams = CopyOnWriteArrayList<StreamSession>()
        val sprintStreamCount = AtomicInteger(0)
        val lastSprintStreamRequest: LocationUpdateRequest?
            get() = sprintStreams.lastOrNull()?.request
        private val sprintHold = CompletableDeferred<Unit>()

        override suspend fun acquire(
            request: LocationEngineRequest,
            onCandidate: suspend (LocationSnapshot) -> Unit,
            onAvailabilityChanged: suspend (Boolean) -> Unit
        ): LocationEngineResult {
            val session = Session(request, onCandidate)
            sessions += session
            acquireCount.incrementAndGet()
            return session.result.await()
        }

        /** 主流常驻注册（AC-5.6：冲刺不得混进这里）。 */
        override suspend fun stream(
            request: LocationUpdateRequest,
            onCandidate: suspend (LocationSnapshot) -> Unit
        ) {
            streams += StreamSession(request, onCandidate)
            streamCount.incrementAndGet()
            streamStart.complete(Unit)
            streamHold.await()
        }

        /**
         * 冲刺的**独立**注册（AC-5.6）。
         *
         * 单独记账（[sprintStreams] / [sprintStreamCount]）：若与主流共用一份记录，
         * 冲刺的 1 秒窗口会污染「主流注册间隔」断言 —— 那正是工单要防的耦合。
         */
        override suspend fun streamSprintWindow(
            request: LocationUpdateRequest,
            onSnapshot: suspend (LocationSnapshot) -> Unit
        ) {
            sprintStreams += StreamSession(request, onSnapshot)
            sprintStreamCount.incrementAndGet()
            sprintHold.await()
        }

        fun emitSprintCandidate(snapshot: LocationSnapshot) {
            val session = sprintStreams.lastOrNull() ?: return
            runBlocking { session.onCandidate(snapshot) }
        }

        fun waitForStreamStart() {
            runBlocking { streamStart.await() }
        }

        fun emitCandidate(snapshot: LocationSnapshot) {
            val session = sessions.lastOrNull() ?: error("no active acquire session")
            runBlocking { session.onCandidate(snapshot) }
        }

        fun emitStreamCandidate(snapshot: LocationSnapshot) {
            val session = streams.lastOrNull() ?: error("no active stream session")
            runBlocking { session.onCandidate(snapshot) }
        }

        fun completeCurrent(result: LocationEngineResult) {
            val session = sessions.lastOrNull() ?: error("no active acquire session")
            session.result.complete(result)
        }

        fun completeAt(index: Int, result: LocationEngineResult) {
            val session = sessions.getOrNull(index) ?: error("no acquire session at index $index")
            session.result.complete(result)
        }
    }

    private class GatedApiService : ApiService {
        var events: List<EventResponse> = emptyList()
        var failNext: Throwable? = null
        var callCount = 0
        var block: CompletableDeferred<Unit>? = null
        var started: CompletableDeferred<Unit>? = null
        var capturedStart: String? = null
        var capturedEnd: String? = null

        override suspend fun getEvents(start: String, end: String, page: Int?, pageSize: Int?): ApiResponse<com.pim.core.models.PagedResult<EventResponse>> {
            callCount++
            capturedStart = start
            capturedEnd = end
            started?.complete(Unit)
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
}

/** 冲刺台账假件：本文件只关心 service 的接线，台账落库由 LocationSprintLedgerTest 守。 */
class RecordingSprintLedgerPort : com.pim.app.location.sprint.SprintLedgerPort {
    val records = java.util.Collections.synchronizedList(mutableListOf<Pair<String, String?>>())

    val executedCount: Int
        get() = records.count { it.first == com.pim.app.location.sprint.SprintOutcome.EXECUTED }

    override suspend fun recordExecuted(
        result: com.pim.app.location.sprint.SprintWindowResult
    ): Boolean {
        records += com.pim.app.location.sprint.SprintOutcome.EXECUTED to null
        return true
    }

    override suspend fun recordSkipped(occurredAtUtcMillis: Long, reason: String): Boolean {
        records += com.pim.app.location.sprint.SprintOutcome.SKIPPED to reason
        return true
    }
}
