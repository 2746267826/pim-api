package com.pim.app.mobile.sync

import android.content.Context
import android.content.SharedPreferences
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.TestPimApp
import com.pim.app.data.AppDatabase
import com.pim.app.data.ForensicEventDao
import com.pim.app.forensics.AndroidHeartbeatSnapshotReader
import com.pim.app.forensics.ForensicContext
import com.pim.app.forensics.ForensicContextSource
import com.pim.app.forensics.ForensicEventTypes
import com.pim.app.forensics.ForensicLedger
import com.pim.app.forensics.ForensicSentinelStore
import com.pim.app.forensics.ForensicUploadCoordinator
import com.pim.app.forensics.HeartbeatSnapshot
import com.pim.app.forensics.WakeHeartbeatRecorder
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.mobile.usage.AppMetadataCollector
import com.pim.app.mobile.usage.UsageAccessChecker
import com.pim.app.mobile.usage.UsageEventCollector
import com.pim.app.settings.TrackingSettingsStore
import com.pim.core.auth.SecurePreferencesFactory
import com.pim.core.auth.TokenManager
import com.pim.core.models.ApiResponse
import com.pim.core.network.ApiService
import com.pim.core.settings.ServerSettingsStore
import java.lang.reflect.Proxy
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * 缺陷 #345 / REQ-3：**周期同步**这条唤醒路径也必须写一条心搏。
 *
 * 阶段一只在 `PimApp.onCreate()` 里调用了启动取证，因此真机上「一周只有 2 条心搏」——
 * 周期同步跑了 35 次/24h，却一条心搏都没记。这里断言的就是那次缺失的写入：
 * 跑一次真正的同步编排（`syncOnOpen()`），台账里就必须多出一条心搏。
 *
 * 这里走的是「服务器地址未配置」分支——它是真机日志里同样会出现的最短真实路径，
 * 且心搏发生在任何提前返回之前，因此正好证明"同步被跳过，但唤醒仍被记录"。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = TestPimApp::class)
class MobileSyncPeriodicHeartbeatTest {

    private lateinit var context: Context
    private lateinit var db: AppDatabase

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
    }

    @After
    fun tearDown() {
        db.close()
    }

    /**
     * 只实现同步链路会碰到的接口；其余方法一律抛出，避免用"意外调用"掩盖装配错误。
     * `sendHeartbeat` 返回一个空的成功响应（本测试不关心服务端心跳）。
     */
    private fun fakeApi(): ApiService = Proxy.newProxyInstance(
        ApiService::class.java.classLoader,
        arrayOf(ApiService::class.java)
    ) { _, method, _ ->
        when (method.name) {
            "sendHeartbeat" -> ApiResponse<Any>(code = 0, message = "ok", data = null)
            "toString" -> "StubApiService"
            "hashCode" -> System.identityHashCode(this)
            "equals" -> false
            else -> error("Unexpected API call in test: ${method.name}")
        }
    } as ApiService

    private fun coordinator(
        heartbeatRecorder: WakeHeartbeatRecorder? = null
    ): MobileSyncCoordinator {
        val api = fakeApi()
        val trackPrefs = context.getSharedPreferences("heartbeat-sync-test", Context.MODE_PRIVATE)
        trackPrefs.edit().clear().commit()
        val trackSettings = TrackingSettingsStore(trackPrefs)
        val logs = StructuredLogRepository(context, trackSettings) { System.currentTimeMillis() }
        val tokenManager = TokenManager(TestSecurePreferencesFactory(trackPrefs))

        return MobileSyncCoordinator(
            context = context,
            api = api,
            tokenManager = tokenManager,
            usageAccessChecker = UsageAccessChecker(context),
            usageEventCollector = UsageEventCollector(context, UsageAccessChecker(context)),
            appMetadataCollector = AppMetadataCollector(context),
            database = db,
            logs = logs,
            heartbeatReporter = MobileHeartbeatReporter(context, api),
            serverSettingsStore = ServerSettingsStore(context, tokenManager),
            locationUploadCoordinator = LocationUploadCoordinator(context, db, api),
            forensicUploadCoordinator = ForensicUploadCoordinator(
                context = context,
                api = api,
                dao = db.forensicEventDao(),
                logs = logs,
                sentinelStore = ForensicSentinelStore(context)
            ),
            wakeHeartbeatRecorder = heartbeatRecorder ?: WakeHeartbeatRecorder(
                ledger = ForensicLedger(db.forensicEventDao(), logs),
                contextReader = FixedForensicContextSource(ForensicContext()),
                snapshotReader = AndroidHeartbeatSnapshotReader(context),
                logs = logs
            ),
            syncScheduler = { MobileSyncScheduler(context, trackSettings) }
        )
    }

    private suspend fun heartbeats() =
        db.forensicEventDao().recentByType(ForensicEventTypes.HEARTBEAT, 10)

    @Test
    fun `issue345 a periodic sync wake writes exactly one heartbeat to the ledger`() = runTest {
        assertEquals(0, heartbeats().size)

        coordinator().syncOnOpen()

        val rows = heartbeats()
        assertEquals(1, rows.size)
        assertEquals(ForensicEventTypes.HEARTBEAT, rows.single().eventType)
    }

    @Test
    fun `issue345 the sync-wake heartbeat carries the REQ-3 fields`() = runTest {
        val trackPrefs = context.getSharedPreferences("heartbeat-sync-test", Context.MODE_PRIVATE)
        val logs = StructuredLogRepository(context, TrackingSettingsStore(trackPrefs)) { FIXED_NOW }
        val recorder = WakeHeartbeatRecorder(
            ledger = ForensicLedger(db.forensicEventDao(), logs),
            contextReader = FixedForensicContextSource(ForensicContext()),
            snapshotReader = snapshotSource(context),
            logs = logs,
            nowUtcMillis = { FIXED_NOW },
            bootElapsedMillis = { FIXED_BOOT_ELAPSED },
            serviceRunning = { false }
        )

        assertTrue(recorder.record())

        // 解析 JSON 并断言**取值与类型**，而不是只查子串：
        // 只查 "contains" 的话，字段恒为 null 也照样能过（独立 review 的 Minor 发现）。
        val json = org.json.JSONObject(heartbeats().single().payloadJson)

        // 时刻：由台账列承载，必须等于注入的时钟。
        assertEquals(FIXED_NOW, heartbeats().single().occurredAtUtc)
        // 开机时长：必须是注入值（Long），不是 null。
        assertEquals(FIXED_BOOT_ELAPSED, json.getLong("bootElapsedMs"))
        // 距上次心搏间隔：本进程首跳应为显式 null（而不是缺字段或被猜成 0）。
        assertTrue(json.has("sinceLastHeartbeatMs"))
        assertTrue(json.isNull("sinceLastHeartbeatMs"))
        // 待机桶：桶号与中文标签都必须存在；标签不得落到"未知"以外的猜测值。
        assertTrue(json.has("standbyBucket"))
        assertTrue(json.getString("standbyBucketLabel").isNotBlank())
        // 三个布尔/可空诊断字段：键必须存在（值为 null 或布尔，取决于设备读数是否可用）。
        assertTrue(json.has("ignoringBatteryOptimizations"))
        assertTrue(json.has("dozeMode"))
        assertTrue(json.has("powerSaveMode"))
        // 前台采集服务：注入 false，必须如实写 false。
        assertFalse(json.getBoolean("foregroundServiceRunning"))
    }

    @Test
    fun `AC-3_3 two sync wakes within the same second still produce one heartbeat`() = runTest {
        // 注入固定时钟：AC-3.3 说的是"同一秒内多次唤醒"，不是"两次调用之间真实流逝了多久"。
        // 两次唤醒刻意取同一秒里不同的毫秒（+10ms），这样断言的是秒级幂等键本身，
        // 而不是"两次调用恰好拿到同一个毫秒值"——否则把幂等键的粒度改成毫秒也照样能过。
        val trackPrefs = context.getSharedPreferences("heartbeat-sync-test", Context.MODE_PRIVATE)
        val logs = StructuredLogRepository(context, TrackingSettingsStore(trackPrefs)) { FIXED_NOW }
        var now = FIXED_NOW
        val recorder = WakeHeartbeatRecorder(
            ledger = ForensicLedger(db.forensicEventDao(), logs),
            contextReader = FixedForensicContextSource(ForensicContext()),
            snapshotReader = snapshotSource(context),
            logs = logs,
            nowUtcMillis = { now },
            bootElapsedMillis = { FIXED_BOOT_ELAPSED },
            serviceRunning = { false }
        )
        val sync = coordinator(recorder)

        sync.syncOnOpen()
        now += 10L
        sync.syncOnOpen()

        assertEquals(1, heartbeats().size)
    }

    @Test
    fun `AC-3_3 a failing ledger write does not break the sync run`() = runTest {
        // 让"写台账"确定性地失败：包一层 DAO，每次插入都抛，并**计数**插入尝试。
        // 计数是关键：只断言 phase=="server-missing" 的话，即使协调器根本没调用写心搏
        // 也照样能过（独立 review 的 Minor 发现），因此必须证明"确实尝试写、且失败了"。
        val trackPrefs = context.getSharedPreferences("heartbeat-sync-test", Context.MODE_PRIVATE)
        val logs = StructuredLogRepository(context, TrackingSettingsStore(trackPrefs)) { FIXED_NOW }
        val insertAttempts = java.util.concurrent.atomic.AtomicInteger(0)
        val failingDao = FailingInsertForensicDao(db.forensicEventDao(), insertAttempts)
        val recorder = WakeHeartbeatRecorder(
            ledger = ForensicLedger(failingDao, logs),
            contextReader = FixedForensicContextSource(ForensicContext()),
            snapshotReader = snapshotSource(context),
            logs = logs,
            nowUtcMillis = { FIXED_NOW },
            bootElapsedMillis = { FIXED_BOOT_ELAPSED },
            serviceRunning = { false }
        )

        // 同步跑完（服务器未配置 → 有序结束），过程中协调器确实尝试写过心搏且失败了。
        val state = coordinator(recorder).syncOnOpen()

        assertEquals("server-missing", state.phase)
        assertTrue(
            "协调器必须真的尝试写过心搏（否则本测试是空过的）",
            insertAttempts.get() >= 1
        )
        assertEquals("写入失败不得落任何心搏行", 0, heartbeats().size)
    }

    @Test
    fun `issue345 a wake skipped by the sync lock is still recorded`() = runTest {
        // 两次唤醒同时到达：第二次因乐观锁直接返回"同步正在进行中"。
        // 它同样是真实唤醒，心搏不能因为"同步没跑起来"而丢失——否则又会出现
        // 「同步批次有、心搏没有」的口径缺口（正是 #345 的成因）。
        val trackPrefs = context.getSharedPreferences("heartbeat-sync-test", Context.MODE_PRIVATE)
        val logs = StructuredLogRepository(context, TrackingSettingsStore(trackPrefs)) { FIXED_NOW }
        // 两次唤醒时刻不同（不同秒），确保去重不会把它们合成一条。
        var now = FIXED_NOW
        val recorder = WakeHeartbeatRecorder(
            ledger = ForensicLedger(db.forensicEventDao(), logs),
            contextReader = FixedForensicContextSource(ForensicContext()),
            snapshotReader = snapshotSource(context),
            logs = logs,
            nowUtcMillis = { now },
            bootElapsedMillis = { FIXED_BOOT_ELAPSED },
            serviceRunning = { false }
        )
        val sync = coordinator(recorder)

        // 手工占住互斥锁，模拟"已有同步在跑"。
        val lockField = MobileSyncCoordinator::class.java.getDeclaredField("syncMutex")
        lockField.isAccessible = true
        val mutex = lockField.get(sync) as kotlinx.coroutines.sync.Mutex
        mutex.lock()

        try {
            now += 60_000L
            val state = sync.syncOnOpen()
            assertEquals("同步正在进行中。", state.progressText)
        } finally {
            mutex.unlock()
        }

        // 被锁挡下的那次唤醒仍然留下了心搏。
        assertEquals(1, heartbeats().size)
    }

    @Test
    fun `REQ-3 the second wake in the same process carries a real time-since-last-heartbeat`() = runTest {
        // 「距上次心搏间隔」必须来自上一次成功写入的基线，且不得为负。
        val trackPrefs = context.getSharedPreferences("heartbeat-sync-test", Context.MODE_PRIVATE)
        val logs = StructuredLogRepository(context, TrackingSettingsStore(trackPrefs)) { FIXED_NOW }
        var now = FIXED_NOW
        var boot = FIXED_BOOT_ELAPSED
        val recorder = WakeHeartbeatRecorder(
            ledger = ForensicLedger(db.forensicEventDao(), logs),
            contextReader = FixedForensicContextSource(ForensicContext()),
            snapshotReader = snapshotSource(context),
            logs = logs,
            nowUtcMillis = { now },
            bootElapsedMillis = { boot },
            serviceRunning = { false }
        )

        // 第一条：本进程的第一跳没有基线，间隔为 null（阶段一既有行为）。
        assertTrue(recorder.record())
        // 第二条：比第一跳晚 15 分钟（一个周期同步周期），间隔必须是 900000ms。
        // 壁钟与开机时长同步推进，避免被秒级去重键拦下（那正是 AC-3.3 的另一条路径）。
        now += 15 * 60_000L
        boot += 15 * 60_000L
        assertTrue(recorder.record())

        val sorted = heartbeats().sortedBy { it.occurredAtUtc }
        val first = org.json.JSONObject(sorted[0].payloadJson)
        val second = org.json.JSONObject(sorted[1].payloadJson)
        assertTrue("首跳应为 null", first.isNull("sinceLastHeartbeatMs"))
        assertEquals(15 * 60_000L, second.getLong("sinceLastHeartbeatMs"))
    }

    /**
     * 并发唤醒下「距上次心搏间隔」仍然可信。
     *
     * 这条测试来自独立 review 的 Important 发现：心搏入口改成单例后，启动取证与周期同步
     * 可能同时进来，而"读基线 → 造负载 → 写台账 → 更新基线"不是原子的。
     * 这里用真实并发（不是顺序调用）：两个不同秒的唤醒同时在飞，完成顺序被**刻意反转**
     * ——后发生的唤醒（boot 更大）先完成，先发生的后完成。
     * 正确实现下，每一跳的间隔都等于它与"紧邻的上一跳"之差，且永不为负。
     */
    @Test
    fun `concurrent wakes never produce a wrong or negative time-since-last-heartbeat`() = runTest {
        val trackPrefs = context.getSharedPreferences("heartbeat-sync-test", Context.MODE_PRIVATE)
        val logs = StructuredLogRepository(context, TrackingSettingsStore(trackPrefs)) { FIXED_NOW }
        // 用一个闸门让两次唤醒真正交错：第一次进入后卡住，第二次先跑完。
        val firstInside = CompletableDeferred<Unit>()
        val releaseFirst = CompletableDeferred<Unit>()
        val recorder = WakeHeartbeatRecorder(
            ledger = ForensicLedger(db.forensicEventDao(), logs),
            contextReader = FixedForensicContextSource(ForensicContext()),
            snapshotReader = SlowFirstSnapshotReader(
                delegate = AndroidHeartbeatSnapshotReader(context),
                onFirstCall = {
                    firstInside.complete(Unit)
                },
                waitBeforeReturning = { releaseFirst.await() }
            ),
            logs = logs,
            nowUtcMillis = { FIXED_NOW },
            bootElapsedMillis = { FIXED_BOOT_ELAPSED },
            serviceRunning = { false }
        )

        val earlier = async { recorder.record(FIXED_NOW, FIXED_BOOT_ELAPSED) }
        firstInside.await()                       // earlier 已进入临界区
        val later = async { recorder.record(FIXED_NOW + 600_000L, FIXED_BOOT_ELAPSED + 600_000L) }
        releaseFirst.complete(Unit)               // 放行 earlier
        earlier.await()
        later.await()

        val sorted = heartbeats().sortedBy { it.occurredAtUtc }
        assertEquals(2, sorted.size)
        val intervals = sorted.map {
            org.json.JSONObject(it.payloadJson).opt("sinceLastHeartbeatMs")
        }
        // 首跳无基线；第二跳的间隔必须是两跳之差，且不得为负。
        assertTrue("首跳应为 null，实际 ${intervals[0]}", intervals[0] == null || intervals[0] == org.json.JSONObject.NULL)
        assertEquals(600_000L, org.json.JSONObject(sorted[1].payloadJson).getLong("sinceLastHeartbeatMs"))
    }

    private companion object {
        /** AC-3.3 的同一秒内：毫秒只在同一秒里抖动。 */
        const val FIXED_NOW = 1_790_300_000_000L
        const val FIXED_BOOT_ELAPSED = 82_000L
    }
}

/** 测试用的内存 SharedPreferences 工厂（TokenManager 只依赖这个接口）。 */
private class TestSecurePreferencesFactory(
    private val preferences: SharedPreferences
) : SecurePreferencesFactory {
    override fun open(): SharedPreferences = preferences
}

/**
 * 只让 `insertIgnore` 失败的 DAO 代理（其余方法原样转发给真实 DAO）。
 * 用动态代理而不是"关掉数据库"，是为了让"写入失败"确定性地发生；
 * [attempts] 记录插入尝试次数，用来证明调用方**确实尝试过**写心搏。
 */
private fun FailingInsertForensicDao(
    delegate: ForensicEventDao,
    attempts: java.util.concurrent.atomic.AtomicInteger
): ForensicEventDao =
    Proxy.newProxyInstance(
        ForensicEventDao::class.java.classLoader,
        arrayOf(ForensicEventDao::class.java)
    ) { _, method, args ->
        when (method.name) {
            "insertIgnore" -> {
                attempts.incrementAndGet()
                throw android.database.sqlite.SQLiteException("disk I/O error (injected)")
            }
            "toString" -> "FailingInsertForensicDao"
            "hashCode" -> System.identityHashCode(delegate)
            "equals" -> false
            else -> method.invoke(delegate, *(args ?: emptyArray()))
        }
    } as ForensicEventDao

/** 固定的取证上下文：本测试只关心"唤醒是否落一条心搏"，不关心上下文读数。 */
private class FixedForensicContextSource(
    private val value: ForensicContext
) : ForensicContextSource {
    override fun read(): ForensicContext = value
}

/** 把真实快照读取器适配成 [HeartbeatSnapshotSource]（生产构造函数接受的就是它）。 */
private fun snapshotSource(context: Context): com.pim.app.forensics.HeartbeatSnapshotSource {
    val reader = AndroidHeartbeatSnapshotReader(context)
    return com.pim.app.forensics.HeartbeatSnapshotSource { now, last, running, ctx ->
        reader.read(
            nowElapsedMillis = now,
            lastHeartbeatElapsedMillis = last,
            foregroundServiceRunning = running,
            forensicContext = ctx
        )
    }
}

/**
 * 可控闸门的快照读取器：`read()` 是 [WakeHeartbeatRecorder.record] 临界区里的第一件事，
 * 在这里卡住第一次调用，就能让第二次唤醒真正并发地插进来。
 */
private class SlowFirstSnapshotReader(
    private val delegate: AndroidHeartbeatSnapshotReader,
    private val onFirstCall: suspend () -> Unit,
    private val waitBeforeReturning: suspend () -> Unit
) : com.pim.app.forensics.HeartbeatSnapshotSource {
    private val calls = java.util.concurrent.atomic.AtomicInteger(0)

    override suspend fun read(
        nowElapsedMillis: Long,
        lastHeartbeatElapsedMillis: Long?,
        foregroundServiceRunning: Boolean,
        forensicContext: ForensicContext
    ): HeartbeatSnapshot {
        if (calls.incrementAndGet() == 1) {
            onFirstCall()
            waitBeforeReturning()
        }
        return delegate.read(
            nowElapsedMillis = nowElapsedMillis,
            lastHeartbeatElapsedMillis = lastHeartbeatElapsedMillis,
            foregroundServiceRunning = foregroundServiceRunning,
            forensicContext = forensicContext
        )
    }
}
