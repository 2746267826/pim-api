package com.pim.app.schedule

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.pim.core.auth.AuthSessionSnapshot
import com.pim.core.auth.AuthSessionStore
import com.pim.core.models.ApiResponse
import com.pim.core.models.EventResponse
import com.pim.core.network.ApiService
import com.pim.core.settings.PimServerEndpoints
import com.pim.core.settings.ServerSettingsStore
import java.io.File
import java.io.IOException
import java.time.Instant
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.ExperimentalCoroutinesApi
import kotlinx.coroutines.async
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.test.UnconfinedTestDispatcher
import kotlinx.coroutines.test.runTest
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.Json
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import retrofit2.HttpException
import retrofit2.Response

@OptIn(ExperimentalCoroutinesApi::class)
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class ScheduleWindowRepositoryTest {
    private lateinit var api: FakeApiService
    private lateinit var cacheStore: ScheduleCacheStore
    private lateinit var serverSettings: ServerSettingsStore
    private lateinit var repo: ScheduleWindowRepository
    private lateinit var cacheDir: File
    private val json = Json { ignoreUnknownKeys = true }

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        cacheDir = File(context.filesDir, "schedule-window-repo-test-" + System.nanoTime())
        cacheDir.deleteRecursively()
        cacheDir.mkdirs()

        api = FakeApiService()
        cacheStore = ScheduleCacheStore(cacheDir, json)
        serverSettings = ServerSettingsStore(
            context,
            FakeAuthSessionStore()
        )
        serverSettings.setBaseUrl("http://test-server:5858/api/v1/")

        repo = ScheduleWindowRepository(
            apiService = api,
            cacheStore = cacheStore,
            serverSettingsStore = serverSettings
        )
    }

    @After
    fun tearDown() {
        cacheDir.deleteRecursively()
    }

    @Test
    fun `mapEvents preserves events without location`() {
        val windows = ScheduleWindowRepository.mapEvents(
            listOf(
                EventResponse(id = "1", title = "有空地点的", location = "北京", dtStart = "2026-07-08T01:00:00Z", dtEnd = "2026-07-08T02:00:00Z"),
                EventResponse(id = "2", title = "无地点", location = null, dtStart = "2026-07-08T03:00:00Z", dtEnd = "2026-07-08T04:00:00Z"),
                EventResponse(id = "3", title = "空字符串地点", location = "", dtStart = "2026-07-08T05:00:00Z", dtEnd = "2026-07-08T06:00:00Z")
            )
        )
        assertEquals(3, windows.size)
        assertEquals("2", windows[1].id)
        assertEquals("", windows[1].locationText)
        assertEquals("3", windows[2].id)
        assertEquals("", windows[2].locationText)
    }

    @Test
    fun `mapEvents sorts by startsAtMillis`() {
        val windows = ScheduleWindowRepository.mapEvents(
            listOf(
                EventResponse(id = "later", title = "later", location = null, dtStart = "2026-07-08T05:00:00Z", dtEnd = "2026-07-08T06:00:00Z"),
                EventResponse(id = "earlier", title = "earlier", location = null, dtStart = "2026-07-08T01:00:00Z", dtEnd = "2026-07-08T02:00:00Z")
            )
        )
        assertEquals(listOf("earlier", "later"), windows.map { it.id })
    }

    @Test
    fun `refresh query range is exactly now-6h and now+7d`() = runTest {
        api.events = listOf(event(id = "e1"))
        val now = 1_000_000_000L
        repo.refreshIfStale(force = true, nowMillis = now)

        val start = Instant.ofEpochMilli(now - 6 * 3600 * 1000L).toString()
        val end = Instant.ofEpochMilli(now + 7 * 24 * 3600 * 1000L).toString()
        assertEquals(start, api.capturedStart)
        assertEquals(end, api.capturedEnd)
    }

    @Test
    fun `successful refresh populates snapshot with windows`() = runTest {
        api.events = listOf(
            event(id = "1", title = "会议", start = "2026-07-08T02:00:00Z", end = "2026-07-08T03:00:00Z")
        )
        val now = Instant.parse("2026-07-08T00:00:00Z").toEpochMilli()
        val result = repo.refreshIfStale(force = true, nowMillis = now)

        assertEquals(1, result.windows.size)
        assertEquals("会议", result.windows[0].title)
        assertEquals(ScheduleCacheFreshness.Fresh, result.freshness)
        assertEquals(now, result.lastAttemptAtMillis)
        assertEquals(now, result.lastSuccessAtMillis)
        assertNull(result.lastError)
        assertNull(result.errorKind)
    }

    @Test
    fun `successful empty list returns Fresh with empty windows`() = runTest {
        api.events = emptyList()
        val now = Instant.parse("2026-07-08T00:00:00Z").toEpochMilli()
        val result = repo.refreshIfStale(force = true, nowMillis = now)

        assertTrue(result.windows.isEmpty())
        assertEquals(ScheduleCacheFreshness.Fresh, result.freshness)
        assertEquals(now, result.lastSuccessAtMillis)
    }

    @Test
    fun `stale refresh keeps windows and lastSuccess from cache`() = runTest {
        api.events = listOf(event(id = "cached"))
        repo.refreshIfStale(force = true, nowMillis = 1_000L)

        api.failNext = IOException("simulated network failure")
        val result = repo.refreshIfStale(force = true, nowMillis = 2_000L)

        assertEquals(1, result.windows.size)
        assertEquals("cached", result.windows[0].id)
        assertEquals(ScheduleCacheFreshness.Stale, result.freshness)
        assertEquals(1_000L, result.lastSuccessAtMillis)
        assertNotNull(result.lastError)
        assertEquals(ScheduleRefreshErrorKind.Network, result.errorKind)
    }

    @Test
    fun `no cache failure returns Missing with fixed Chinese error`() = runTest {
        api.failNext = IOException("simulated network failure")
        val result = repo.refreshIfStale(force = true, nowMillis = 1_000L)

        assertTrue(result.windows.isEmpty())
        assertEquals(ScheduleCacheFreshness.Missing, result.freshness)
        assertEquals("网络不可用", result.lastError)
        assertEquals(ScheduleRefreshErrorKind.Network, result.errorKind)
    }

    @Test
    fun `freshness within 15 minutes returns Fresh without API call`() = runTest {
        api.events = listOf(event(id = "e1"))
        repo.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(1, api.callCount)

        val result = repo.refreshIfStale(nowMillis = 1_001L)
        assertEquals(ScheduleCacheFreshness.Fresh, result.freshness)
        assertEquals(1, api.callCount)
    }

    @Test
    fun `force bypasses freshness`() = runTest {
        api.events = listOf(event(id = "e1"))
        repo.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(1, api.callCount)

        api.events = listOf(event(id = "e2"))
        val result = repo.refreshIfStale(force = true, nowMillis = 2_000L)
        assertEquals(2, api.callCount)
        assertEquals("e2", result.windows[0].id)
    }

    @Test
    fun `throttle within 15 minutes blocks non-force refresh`() = runTest {
        api.failNext = IOException("first failure")
        repo.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(1, api.callCount)

        val result = repo.refreshIfStale(nowMillis = 1_001L)
        assertEquals(1, api.callCount)
        assertEquals(ScheduleRefreshErrorKind.Network, result.errorKind)
    }

    @Test
    fun `force bypasses throttle`() = runTest {
        api.failNext = IOException("first failure")
        repo.refreshIfStale(force = true, nowMillis = 1_000L)

        api.failNext = IOException("second failure")
        val result = repo.refreshIfStale(force = true, nowMillis = 1_001L)
        assertEquals(2, api.callCount)
        assertEquals(ScheduleRefreshErrorKind.Network, result.errorKind)
    }

    @Test
    fun `concurrent refresh only calls API once`() = runTest(UnconfinedTestDispatcher()) {
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        api.started = started
        api.block = release
        api.events = listOf(event(id = "e1"))

        val deferred1 = async { repo.refreshIfStale(force = true, nowMillis = 1_000L) }
        val deferred2 = async { repo.refreshIfStale(force = true, nowMillis = 1_000L) }

        withTimeout(5_000) { started.await() }
        assertEquals(1, api.callCount)
        release.complete(Unit)

        val r1 = withTimeout(5_000) { deferred1.await() }
        val r2 = withTimeout(5_000) { deferred2.await() }

        assertEquals(1, api.callCount)
        assertEquals(r1.windows[0].id, r2.windows[0].id)
    }

    @Test
    fun `concurrent mixed force and non-force share single request`() = runTest(UnconfinedTestDispatcher()) {
        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        api.started = started
        api.block = release
        api.events = listOf(event(id = "e1"))

        val deferred1 = async { repo.refreshIfStale(force = true, nowMillis = 1_000L) }
        val deferred2 = async { repo.refreshIfStale(nowMillis = 1_000L) }

        withTimeout(5_000) { started.await() }
        assertEquals(1, api.callCount)
        release.complete(Unit)

        val r1 = withTimeout(5_000) { deferred1.await() }
        val r2 = withTimeout(5_000) { deferred2.await() }

        assertEquals(1, api.callCount)
        assertEquals("e1", r1.windows[0].id)
        assertEquals("e1", r2.windows[0].id)
    }

    @Test
    fun `server identity switch clears old windows`() = runTest {
        api.events = listOf(event(id = "server-a"))
        serverSettings.setBaseUrl("http://server-a:5858/api/v1/")
        repo.refreshIfStale(force = true, nowMillis = 1_000L)

        serverSettings.setBaseUrl("http://server-b:5858/api/v1/")
        api.events = listOf(event(id = "server-b"))
        val result = repo.refreshIfStale(force = true, nowMillis = 2_000L)

        assertEquals(1, result.windows.size)
        assertEquals("server-b", result.windows[0].id)
    }

    @Test
    fun `server identity switch clears old windows before refresh`() = runTest {
        api.events = listOf(event(id = "server-a"))
        serverSettings.setBaseUrl("http://server-a:5858/api/v1/")
        repo.refreshIfStale(force = true, nowMillis = 1_000L)

        serverSettings.setBaseUrl("http://server-b:5858/api/v1/")
        api.failNext = IOException("no network for server-b")
        val result = repo.refreshIfStale(force = true, nowMillis = 2_000L)

        assertEquals("server-b identity", serverIdentity("http://server-b:5858/api/v1/"), result.serverIdentity)
        assertTrue("old server-a windows must not leak", result.windows.isEmpty())
        assertEquals(ScheduleCacheFreshness.Missing, result.freshness)
    }

    @Test
    fun `current snapshot guard clears old windows synchronously after server switch`() = runTest {
        api.events = listOf(event(id = "server-a"))
        serverSettings.setBaseUrl("http://server-a:5858/api/v1/")
        repo.refreshIfStale(force = true, nowMillis = 1_000L)

        serverSettings.setBaseUrl("http://server-b:5858/api/v1/")

        val guarded = repo.snapshotForCurrentServer()

        assertEquals("server-b identity", serverIdentity("http://server-b:5858/api/v1/"), guarded.serverIdentity)
        assertTrue("guarded snapshot must not expose old server windows", guarded.windows.isEmpty())
        assertEquals(ScheduleCacheFreshness.Missing, guarded.freshness)
        assertTrue("repository snapshot must also clear old windows", repo.snapshot.value.windows.isEmpty())
    }

    @Test
    fun `401 error maps to Authentication`() = runTest {
        api.failNext = HttpException(
            Response.error<Any>(401, "{}".toResponseBody("application/json".toMediaType()))
        )
        val result = repo.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(ScheduleRefreshErrorKind.Authentication, result.errorKind)
        assertEquals("登录状态已失效", result.lastError)
    }

    @Test
    fun `403 error maps to Authentication`() = runTest {
        api.failNext = HttpException(
            Response.error<Any>(403, "{}".toResponseBody("application/json".toMediaType()))
        )
        val result = repo.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(ScheduleRefreshErrorKind.Authentication, result.errorKind)
    }

    @Test
    fun `500 error maps to Server`() = runTest {
        api.failNext = HttpException(
            Response.error<Any>(500, "{}".toResponseBody("application/json".toMediaType()))
        )
        val result = repo.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(ScheduleRefreshErrorKind.Server, result.errorKind)
    }

    @Test
    fun `non-zero ApiResponse code maps to Server error`() = runTest {
        api.responseCode = 1
        api.responseMessage = "业务错误"
        val result = repo.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(ScheduleRefreshErrorKind.Server, result.errorKind)
        assertEquals("服务器暂时不可用", result.lastError)
    }

    @Test
    fun `SerializationException from API maps to Server not Cache`() = runTest {
        api.failNext = kotlinx.serialization.SerializationException("bad payload")
        val result = repo.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(ScheduleRefreshErrorKind.Server, result.errorKind)
        assertEquals("服务器返回数据格式错误", result.lastError)
    }

    @Test
    fun `CancellationException propagates without snapshot update`() = runTest {
        api.failNext = CancellationException("cancelled")
        try {
            repo.refreshIfStale(force = true, nowMillis = 1_000L)
        } catch (_: CancellationException) {
        }
        val snap = repo.snapshot.value
        assertEquals(ScheduleCacheFreshness.Missing, snap.freshness)
    }

    @Test
    fun `invalid server address returns Missing with Server errorKind`() = runTest {
        val context = ApplicationProvider.getApplicationContext<Context>()
        context.getSharedPreferences("pim_server_settings", Context.MODE_PRIVATE)
            .edit().remove("server_base_url").commit()
        val emptySettings = ServerSettingsStore(
            context,
            FakeAuthSessionStore()
        )
        val repo2 = ScheduleWindowRepository(
            apiService = api,
            cacheStore = cacheStore,
            serverSettingsStore = emptySettings
        )
        val result = repo2.refreshIfStale(force = true, nowMillis = 1_000L)
        assertEquals(ScheduleCacheFreshness.Missing, result.freshness)
        assertEquals(ScheduleRefreshErrorKind.Server, result.errorKind)
    }

    @Test
    fun `future lastSuccessAtMillis does not return Fresh`() = runTest {
        val identity = serverIdentity("http://test-server:5858/api/v1/")
        cacheStore.cacheFile(identity).writeText(
            """{"windows":[],"rangeStartMillis":0,"rangeEndMillis":0,"lastAttemptAtMillis":2000,"lastSuccessAtMillis":2000}"""
        )
        api.events = listOf(event(id = "e1"))
        val result = repo.refreshIfStale(nowMillis = 1_000L)
        assertEquals(1, api.callCount)
        assertEquals("e1", result.windows.singleOrNull()?.id)
    }

    @Test
    fun `future lastAttemptAtMillis does not throttle`() = runTest {
        val identity = serverIdentity("http://test-server:5858/api/v1/")
        cacheStore.cacheFile(identity).writeText(
            """{"windows":[],"rangeStartMillis":0,"rangeEndMillis":0,"lastAttemptAtMillis":2000}"""
        )
        api.events = listOf(event(id = "e1"))
        val result = repo.refreshIfStale(nowMillis = 1_000L)
        assertEquals(1, api.callCount)
        assertEquals("e1", result.windows.singleOrNull()?.id)
    }

    @Test
    fun `failed force refresh prevents next non-force Fresh`() = runTest {
        val identity = serverIdentity("http://test-server:5858/api/v1/")
        cacheStore.write(identity, ScheduleCacheDocument(
            windows = listOf(ScheduleCacheWindow("old", "old", "", 100L, 200L)),
            rangeStartMillis = 0L, rangeEndMillis = 1000L,
            lastAttemptAtMillis = 45_000L,
            lastSuccessAtMillis = 1_000L,
            lastError = "网络不可用",
            lastErrorKind = ScheduleRefreshErrorKind.Network.name
        ))

        api.events = listOf(event(id = "e2"))
        val result = repo.refreshIfStale(nowMillis = 500_000L)

        assertNotEquals("Fresh must not be returned after a failed refresh", ScheduleCacheFreshness.Fresh, result.freshness)
        assertNotNull("error must be visible", result.lastError)
    }

    @Test
    fun `throttle publishes snapshot`() = runTest {
        api.failNext = IOException("first failure")
        repo.refreshIfStale(force = true, nowMillis = 1_000L)

        val result = repo.refreshIfStale(nowMillis = 1_001L)

        val snap = repo.snapshot.value
        assertEquals(result.serverIdentity, snap.serverIdentity)
        assertEquals(result.freshness, snap.freshness)
        assertEquals(result.lastAttemptAtMillis, snap.lastAttemptAtMillis)
    }

    @Test
    fun `empty cached windows with lastSuccessAtMillis returns Stale after failure`() = runTest {
        api.events = emptyList()
        repo.refreshIfStale(force = true, nowMillis = 1_000L)

        api.failNext = IOException("failure")
        val result = repo.refreshIfStale(force = true, nowMillis = 2_000L)

        assertEquals(ScheduleCacheFreshness.Stale, result.freshness)
        assertTrue(result.windows.isEmpty())
        assertEquals(1_000L, result.lastSuccessAtMillis)
    }

    @Test
    fun `server identity switch resets all freshness fields`() = runTest {
        api.events = listOf(event(id = "a"))
        serverSettings.setBaseUrl("http://server-a:5858/api/v1/")
        repo.refreshIfStale(force = true, nowMillis = 1_000L)

        serverSettings.setBaseUrl("http://server-b:5858/api/v1/")
        api.failNext = IOException("no B")
        val result = repo.refreshIfStale(force = true, nowMillis = 2_000L)

        assertEquals(serverIdentity("http://server-b:5858/api/v1/"), result.serverIdentity)
        assertTrue("windows must be empty for new identity", result.windows.isEmpty())
        assertEquals(ScheduleCacheFreshness.Missing, result.freshness)
        assertNull("lastSuccessAtMillis must be null", result.lastSuccessAtMillis)
        assertNotNull("lastError set from failed API", result.lastError)
    }

    @Test
    fun `non-force corrupt cache with API success refreshes and repairs file`() = runTest {
        val identity = serverIdentity("http://test-server:5858/api/v1/")
        cacheStore.cacheFile(identity).writeText("{not-json")
        assertEquals(ScheduleCacheStore.CacheReadResult.Corrupt, cacheStore.readOutcome(identity))

        api.events = listOf(event(id = "repaired"))
        val result = repo.refreshIfStale(nowMillis = 1_000L)

        assertEquals(1, api.callCount)
        assertEquals(ScheduleCacheFreshness.Fresh, result.freshness)
        assertEquals("repaired", result.windows.single().id)
        assertNull(result.lastError)
        assertTrue(cacheStore.readOutcome(identity) is ScheduleCacheStore.CacheReadResult.Found)
    }

    @Test
    fun `non-force corrupt cache with API IOException exposes Cache Missing`() = runTest {
        val identity = serverIdentity("http://test-server:5858/api/v1/")
        cacheStore.cacheFile(identity).writeText("{not-json")
        assertEquals(ScheduleCacheStore.CacheReadResult.Corrupt, cacheStore.readOutcome(identity))

        api.failNext = IOException("network down")
        val result = repo.refreshIfStale(nowMillis = 1_000L)

        assertEquals(1, api.callCount)
        assertEquals(ScheduleCacheFreshness.Missing, result.freshness)
        assertEquals(ScheduleRefreshErrorKind.Cache, result.errorKind)
        assertEquals("本地日程缓存不可用", result.lastError)
        assertTrue(result.windows.isEmpty())
    }

    @Test
    fun `failed error metadata write keeps memory Network over older disk Fresh`() = runTest {
        val identity = serverIdentity("http://test-server:5858/api/v1/")
        val hybridDir = File(cacheDir, "hybrid")
        hybridDir.mkdirs()
        val hybridStore = ScheduleCacheStore(hybridDir, json)
        val hybridRepo = ScheduleWindowRepository(api, hybridStore, serverSettings)

        api.events = listOf(event(id = "seed"))
        hybridRepo.refreshIfStale(force = true, nowMillis = 10_000L)
        assertEquals(ScheduleCacheFreshness.Fresh, hybridRepo.snapshot.value.freshness)
        val callsAfterSeed = api.callCount

        // Make cacheDir a plain file so subsequent writes deterministically fail.
        hybridDir.listFiles()?.forEach { it.delete() }
        assertTrue(hybridDir.delete())
        hybridDir.writeText("blocked")

        api.failNext = IOException("network down")
        val networkFail = hybridRepo.refreshIfStale(force = true, nowMillis = 20_000L)
        assertEquals(ScheduleRefreshErrorKind.Network, networkFail.errorKind)
        assertEquals("网络不可用", networkFail.lastError)
        assertEquals(callsAfterSeed + 1, api.callCount)

        // Restore older Fresh success on disk while in-memory still holds Network failure.
        assertTrue(hybridDir.delete())
        hybridDir.mkdirs()
        hybridStore.write(
            identity,
            ScheduleCacheDocument(
                windows = listOf(ScheduleCacheWindow("disk-old", "disk-old", "", 100L, 200L)),
                rangeStartMillis = 0L,
                rangeEndMillis = 1_000L,
                lastAttemptAtMillis = 100L,
                lastSuccessAtMillis = 100L,
                lastError = null,
                lastErrorKind = null
            )
        )

        val next = hybridRepo.refreshIfStale(nowMillis = 20_001L)
        assertEquals(ScheduleRefreshErrorKind.Network, next.errorKind)
        assertEquals("网络不可用", next.lastError)
        assertNotEquals(ScheduleCacheFreshness.Fresh, next.freshness)
        assertEquals(callsAfterSeed + 1, api.callCount)
        assertEquals(ScheduleRefreshErrorKind.Network, hybridRepo.snapshot.value.errorKind)
        assertTrue(next.windows.none { it.id == "disk-old" })
    }

    @Test
    fun `API success with unwritable cache completes waiters once with Cache error`() = runTest(UnconfinedTestDispatcher()) {
        val unwritableRoot = File(cacheDir, "file-not-dir")
        unwritableRoot.writeText("x")
        val brokenStore = ScheduleCacheStore(unwritableRoot, json)
        val brokenRepo = ScheduleWindowRepository(api, brokenStore, serverSettings)

        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        api.started = started
        api.block = release
        api.events = listOf(event(id = "kept"))

        val d1 = async { brokenRepo.refreshIfStale(force = true, nowMillis = 1_000L) }
        val d2 = async { brokenRepo.refreshIfStale(force = true, nowMillis = 1_000L) }

        withTimeout(5_000) { started.await() }
        assertEquals(1, api.callCount)
        release.complete(Unit)

        val r1 = withTimeout(5_000) { d1.await() }
        val r2 = withTimeout(5_000) { d2.await() }

        assertEquals(1, api.callCount)
        assertEquals("kept", r1.windows.single().id)
        assertEquals("kept", r2.windows.single().id)
        assertEquals(ScheduleCacheFreshness.Fresh, r1.freshness)
        assertEquals(ScheduleRefreshErrorKind.Cache, r1.errorKind)
        assertEquals("本地日程缓存不可用", r1.lastError)
        assertEquals(r1.errorKind, r2.errorKind)
        assertEquals(r1.lastError, r2.lastError)
    }

    @Test
    fun `API IOException with unwritable cache completes waiters and keeps memory Network`() = runTest(UnconfinedTestDispatcher()) {
        val unwritableRoot = File(cacheDir, "file-not-dir-net")
        unwritableRoot.writeText("x")
        val brokenStore = ScheduleCacheStore(unwritableRoot, json)
        val brokenRepo = ScheduleWindowRepository(api, brokenStore, serverSettings)

        val started = CompletableDeferred<Unit>()
        val release = CompletableDeferred<Unit>()
        api.started = started
        api.block = release
        api.failNext = IOException("network down")

        val d1 = async { brokenRepo.refreshIfStale(force = true, nowMillis = 1_000L) }
        val d2 = async { brokenRepo.refreshIfStale(force = true, nowMillis = 1_000L) }

        withTimeout(5_000) { started.await() }
        assertEquals(1, api.callCount)
        release.complete(Unit)

        val r1 = withTimeout(5_000) { d1.await() }
        val r2 = withTimeout(5_000) { d2.await() }

        assertEquals(ScheduleRefreshErrorKind.Network, r1.errorKind)
        assertEquals("网络不可用", r1.lastError)
        assertEquals(r1.errorKind, r2.errorKind)
        assertEquals(r1.lastError, r2.lastError)
        assertEquals(ScheduleCacheFreshness.Missing, r1.freshness)

        val callsAfterFail = api.callCount
        val next = brokenRepo.refreshIfStale(nowMillis = 1_001L)
        assertEquals(callsAfterFail, api.callCount)
        assertEquals(ScheduleRefreshErrorKind.Network, next.errorKind)
        assertEquals("网络不可用", next.lastError)
    }

    @Test
    fun `cross identity refresh prefers latest identity after overlapping flights`() = runTest(UnconfinedTestDispatcher()) {
        val scripted = ScriptedApiService()
        val scriptedRepo = ScheduleWindowRepository(scripted, cacheStore, serverSettings)

        serverSettings.setBaseUrl("http://server-a:5858/api/v1/")
        val deferredA = async { scriptedRepo.refreshIfStale(force = true, nowMillis = 1_000L) }

        withTimeout(5_000) { scripted.startedA.await() }
        assertEquals(1, scripted.callCount)

        serverSettings.setBaseUrl("http://server-b:5858/api/v1/")
        val deferredB = async { scriptedRepo.refreshIfStale(force = true, nowMillis = 2_000L) }

        withTimeout(5_000) { scripted.startedB.await() }
        assertEquals(2, scripted.callCount)

        scripted.releaseB.complete(Unit)
        val resultB = withTimeout(5_000) { deferredB.await() }
        assertEquals("B", resultB.windows.single().id)
        assertEquals(serverIdentity("http://server-b:5858/api/v1/"), scriptedRepo.snapshot.value.serverIdentity)
        assertEquals("B", scriptedRepo.snapshot.value.windows.single().id)

        scripted.releaseA.complete(Unit)
        val resultA = withTimeout(5_000) { deferredA.await() }
        assertEquals("A", resultA.windows.single().id)

        assertEquals(serverIdentity("http://server-b:5858/api/v1/"), scriptedRepo.snapshot.value.serverIdentity)
        assertEquals("B", scriptedRepo.snapshot.value.windows.single().id)
    }

    @Test
    fun `snapshotForCurrentServer must not overwrite concurrent refresh result`() = runTest(UnconfinedTestDispatcher()) {
        val identityA = serverIdentity("http://server-a:5858/api/v1/")
        val identityB = serverIdentity("http://test-server:5858/api/v1/")

        serverSettings.setBaseUrl("http://server-a:5858/api/v1/")
        cacheStore.write(
            identityA,
            ScheduleCacheDocument(
                windows = listOf(ScheduleCacheWindow("a-win", "A", "", 100L, 200L)),
                rangeStartMillis = 0L, rangeEndMillis = 1000L,
                lastAttemptAtMillis = 50L, lastSuccessAtMillis = 50L,
                lastError = null, lastErrorKind = null
            )
        )
        api.events = listOf(event(id = "a-win"))
        repo.refreshIfStale(force = true, nowMillis = 100L)
        assertEquals(listOf("a-win"), repo.snapshot.value.windows.map { it.id })

        serverSettings.setBaseUrl("http://test-server:5858/api/v1/")

        val snapshotField = ScheduleWindowRepository::class.java.getDeclaredField("_snapshot")
        snapshotField.isAccessible = true
        val realFlow = snapshotField.get(repo) as MutableStateFlow<ScheduleCacheSnapshot>
        val readLatch = CountDownLatch(1)
        val releaseLatch = CountDownLatch(1)
        val interceptor = SnapshotInterceptor(realFlow, readLatch, releaseLatch)
        snapshotField.set(repo, interceptor)

        val executor = Executors.newSingleThreadExecutor()
        try {
            val future = executor.submit<ScheduleCacheSnapshot> {
                repo.snapshotForCurrentServer()
            }

            assertTrue(
                "readLatch timed out waiting for snapshot getter",
                readLatch.await(5, TimeUnit.SECONDS)
            )

            api.events = listOf(event(id = "b-win"))
            repo.refreshIfStale(force = true, nowMillis = 200L)

            assertEquals(identityB, realFlow.value.serverIdentity)
            assertTrue("realFlow must have B's windows after refresh", realFlow.value.windows.isNotEmpty())
            assertEquals(listOf("b-win"), realFlow.value.windows.map { it.id })

            releaseLatch.countDown()

            val result = future.get(5, TimeUnit.SECONDS)

            assertEquals(identityB, result.serverIdentity)
            assertTrue(
                "snapshotForCurrentServer must not discard windows from concurrent refresh",
                result.windows.isNotEmpty()
            )
            assertEquals(listOf("b-win"), result.windows.map { it.id })
            assertEquals(
                listOf("b-win"),
                interceptor.value.windows.map { it.id }
            )
        } finally {
            releaseLatch.countDown()
            executor.shutdownNow()
        }
    }

    private class SnapshotInterceptor(
        private val delegate: MutableStateFlow<ScheduleCacheSnapshot>,
        private val readLatch: CountDownLatch,
        private val releaseLatch: CountDownLatch
    ) : MutableStateFlow<ScheduleCacheSnapshot> by delegate {
        private val firstGetComplete = AtomicBoolean(false)
        @Volatile private var captured: ScheduleCacheSnapshot? = null

        override var value: ScheduleCacheSnapshot
            get() {
                if (firstGetComplete.compareAndSet(false, true)) {
                    captured = delegate.value
                    readLatch.countDown()
                    if (!releaseLatch.await(5, TimeUnit.SECONDS)) {
                        throw AssertionError("releaseLatch timed out in SnapshotInterceptor")
                    }
                    return captured!!
                }
                return delegate.value
            }
            set(v) {
                delegate.value = v
            }
    }

    private fun serverIdentity(url: String): String =
        PimServerEndpoints.from(url).apiBaseUrl.toString()

    private fun event(
        id: String = "e1",
        title: String = "事件",
        location: String? = null,
        start: String = "2026-07-08T01:00:00Z",
        end: String = "2026-07-08T02:00:00Z"
    ) = EventResponse(id = id, title = title, location = location, dtStart = start, dtEnd = end)

    class FakeApiService : ApiService {
        var events: List<EventResponse> = emptyList()
        var failNext: Throwable? = null
        var callCount = 0
        var block: CompletableDeferred<Unit>? = null
        var started: CompletableDeferred<Unit>? = null
        var capturedStart: String? = null
        var capturedEnd: String? = null
        var responseCode: Int? = null
        var responseMessage: String? = null

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
            val code = responseCode ?: 0
            val msg = responseMessage ?: "ok"
            return ApiResponse(code = code, message = msg, data = com.pim.core.models.PagedResult(items = events, page = 1, pageSize = 100, totalCount = events.size, totalPages = 1))
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

    class ScriptedApiService : ApiService {
        val startedA = CompletableDeferred<Unit>()
        val releaseA = CompletableDeferred<Unit>()
        val startedB = CompletableDeferred<Unit>()
        val releaseB = CompletableDeferred<Unit>()
        private val counter = AtomicInteger(0)
        val callCount: Int get() = counter.get()
        private val order = CopyOnWriteArrayList<String>()

        override suspend fun getEvents(start: String, end: String, page: Int?, pageSize: Int?): ApiResponse<com.pim.core.models.PagedResult<EventResponse>> {
            val n = counter.incrementAndGet()
            return if (n == 1) {
                order.add("A")
                startedA.complete(Unit)
                releaseA.await()
                ApiResponse(
                    code = 0,
                    message = "ok",
                    data = com.pim.core.models.PagedResult(
                        items = listOf(
                            EventResponse(
                                id = "A",
                                title = "A",
                                location = null,
                                dtStart = "2026-07-08T01:00:00Z",
                                dtEnd = "2026-07-08T02:00:00Z"
                            )
                        ),
                        page = 1, pageSize = 100, totalCount = 1, totalPages = 1
                    )
                )
            } else {
                order.add("B")
                startedB.complete(Unit)
                releaseB.await()
                ApiResponse(
                    code = 0,
                    message = "ok",
                    data = com.pim.core.models.PagedResult(
                        items = listOf(
                            EventResponse(
                                id = "B",
                                title = "B",
                                location = null,
                                dtStart = "2026-07-08T01:00:00Z",
                                dtEnd = "2026-07-08T02:00:00Z"
                            )
                        ),
                        page = 1, pageSize = 100, totalCount = 1, totalPages = 1
                    )
                )
            }
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
