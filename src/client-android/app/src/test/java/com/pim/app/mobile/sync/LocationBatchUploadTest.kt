package com.pim.app.mobile.sync

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.data.AppDatabase
import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileLocationPointEntity
import com.pim.app.data.MobileSyncStatus
import com.pim.core.models.ApiResponse
import com.pim.core.models.MobileIngestItemResult
import com.pim.core.models.MobileLocationPointRequest
import com.pim.core.models.MobileLocationPointsUploadRequest
import com.pim.core.models.MobileLocationPointsUploadResult
import com.pim.core.network.ApiService
import java.io.IOException
import java.lang.reflect.Proxy
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * 定位点批量补传（走服务端 `mobile/location/points/batch`）。
 *
 * 覆盖四条口径：
 * 1. 一次请求携带多条点，服务端**逐条**结果按请求顺序对齐本地行；
 * 2. `accepted` / `skipped`（服务端已有同一自然键）都算已送达并删除本地行，
 *    `rejected` 才落 REJECTED 并保留记录；
 * 3. 请求级失败（网络异常、业务错误码、逐条结果条数不符、请求体过大）
 *    **一律不删本地行**——口径是「不丢点、允许积压」；
 * 4. 单轮同步循环到队列排空，但有批次数上限，且出现可重试失败立即停止本轮。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class LocationBatchUploadTest {

    private lateinit var db: AppDatabase
    private lateinit var dao: MobileDataDao

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        dao = db.mobileDataDao()
    }

    @After
    fun tearDown() {
        db.close()
    }

    @Test
    fun `批量请求一次携带多点且逐条结果对齐本地行`() = runTest {
        seed(count = 3)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { points -> success(points.map { item("accepted") }) }

        val updates = coordinator(api).uploadPending()

        assertEquals("多点应合并成一次请求", listOf(3), sizes)
        assertEquals(3, updates.syncedIds.size)
        assertEquals(0, updates.failedIds.size)
        assertEquals("已送达的点本地删除", 0, pendingCount())
        assertEquals(0, rejectedCount())
    }

    /** 服务端已有同一自然键（设备+时刻+经纬度）时返回 skipped/duplicate —— 该点已送达，不能再重发。 */
    @Test
    fun `服务端标注重复的点同样视为已送达并删除本地行`() = runTest {
        seed(count = 3)
        val api = batchApi { points ->
            success(points.map { item(outcome = "skipped", code = "duplicate") })
        }

        val updates = coordinator(api).uploadPending()

        assertEquals(3, updates.syncedIds.size)
        assertEquals(0, pendingCount())
        assertEquals(0, rejectedCount())
    }

    @Test
    fun `服务端拒绝的点落 REJECTED 并保留本地记录`() = runTest {
        seed(count = 3)
        val api = batchApi { points ->
            success(
                points.mapIndexed { index, _ ->
                    if (index == 1) {
                        item(outcome = "rejected", code = "unusable-accuracy", message = "not usable")
                    } else {
                        item("accepted")
                    }
                }
            )
        }

        val updates = coordinator(api).uploadPending()

        assertEquals(2, updates.syncedIds.size)
        assertEquals(1, updates.failedIds.size)
        assertEquals(0, pendingCount())
        val rejected = dao.getLocationPointsBySyncStatus(MobileSyncStatus.REJECTED, 100)
        assertEquals(1, rejected.size)
        assertTrue(rejected.single().lastError!!.contains("unusable-accuracy"))
    }

    @Test
    fun `整批请求异常时全部保持待传不丢点`() = runTest {
        seed(count = 3)
        val api = batchApi { throw IOException("network down") }

        val updates = coordinator(api).uploadPending()

        assertEquals(0, updates.syncedIds.size)
        assertEquals(3, updates.failedIds.size)
        assertEquals(true, updates.shouldRetry)
        assertTrue(updates.perItemErrors.values.all { it.contains("batch-request-failed") })
        assertEquals("网络失败的点必须留队重传", 3, pendingCount())
        assertEquals(0, rejectedCount())
    }

    @Test
    fun `逐条结果条数与请求条数不符时整批保持待传`() = runTest {
        seed(count = 3)
        val api = batchApi { points -> success(points.take(2).map { item("accepted") }) }

        val updates = coordinator(api).uploadPending()

        assertEquals(0, updates.syncedIds.size)
        assertEquals(3, updates.failedIds.size)
        assertTrue(updates.perItemErrors.values.all { it == "batch-item-result-mismatch" })
        assertEquals("对账不上时宁可重发也不能误删", 3, pendingCount())
    }

    @Test
    fun `业务错误码响应整批保持待传`() = runTest {
        seed(count = 2)
        val api = batchApi {
            ApiResponse<MobileLocationPointsUploadResult>(code = 5000, message = "server busy", data = null)
        }

        val updates = coordinator(api).uploadPending()

        assertEquals(0, updates.syncedIds.size)
        assertEquals(2, pendingCount())
        assertTrue(updates.perItemErrors.values.all { it.contains("batch-request-rejected") })
    }

    @Test
    fun `排空循环把积压一次传完并按批切分`() = runTest {
        seed(count = 1200)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { points -> success(points.map { item("accepted") }) }

        val updates = coordinator(api).uploadPendingUntilDrained()

        assertEquals("500 + 500 + 200 三批吃完全部积压", listOf(500, 500, 200), sizes)
        assertEquals(1200, updates.syncedIds.size)
        assertEquals(0, pendingCount())
    }

    @Test
    fun `排空循环遇到可重试失败立即停止本轮`() = runTest {
        seed(count = 1200)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { throw IOException("network down") }

        val updates = coordinator(api).uploadPendingUntilDrained()

        assertEquals("失败后不再硬打后续批次", 1, sizes.size)
        assertEquals(true, updates.shouldRetry)
        assertEquals("一条都不能少", 1200, pendingCount())
    }

    @Test
    fun `排空循环受单轮批次数上限约束`() = runTest {
        seed(count = 1500)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { points -> success(points.map { item("accepted") }) }

        coordinator(api).uploadPendingUntilDrained(maxBatches = 2)

        assertEquals(listOf(500, 500), sizes)
        assertEquals("剩余积压留待下一个同步周期", 500, pendingCount())
    }

    /** 请求体过大（413）时按半拆分重试，而不是让整批永久卡在队列里。 */
    @Test
    fun `请求体过大时按半拆分重试且不丢点`() = runTest {
        seed(count = 100)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { points ->
            if (points.size > 50) throw FakeHttpException(413)
            success(points.map { item("accepted") })
        }

        coordinator(api).uploadPending()

        assertEquals("先试 100（被拒）→ 拆成 50 + 50", listOf(100, 50, 50), sizes)
        assertEquals(0, pendingCount())
    }

    @Test
    fun `缺精度的行不进入请求且本地判永久失败`() = runTest {
        seed(count = 1, accuracy = null)
        seed(count = 1, accuracy = 12f, startLat = 32.0)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { points -> success(points.map { item("accepted") }) }

        val updates = coordinator(api).uploadPending()

        assertEquals("只有精度达标的行进入请求", listOf(1), sizes)
        assertEquals(1, updates.syncedIds.size)
        val rejected = dao.getLocationPointsBySyncStatus(MobileSyncStatus.REJECTED, 100)
        assertEquals(1, rejected.size)
        assertEquals("missing-horizontal-accuracy", rejected.single().lastError)
    }

    @Test
    fun `队列为空时不发请求`() = runTest {
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { error("队列为空时不应发起请求") }

        val updates = coordinator(api).uploadPendingUntilDrained()

        assertEquals(emptyList<Int>(), sizes)
        assertEquals(0, updates.syncedIds.size)
        assertEquals(0, updates.failedIds.size)
    }

    // --- helpers ---

    private suspend fun seed(count: Int, accuracy: Float? = 12f, startLat: Double = 31.0) {
        val base = 1_700_000_000_000L
        (0 until count).forEach { index ->
            dao.insertLocationPoint(
                MobileLocationPointEntity(
                    latitude = startLat + index * 0.00001,
                    longitude = 121.0,
                    accuracyMeters = accuracy,
                    recordedAtUtc = base + index * 1_000L,
                    source = "auto",
                    collectedAtUtc = base + index * 1_000L,
                    rawJson = "row-$startLat-$index"
                )
            )
        }
    }

    private suspend fun pendingCount(): Int =
        dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 5_000).size

    private suspend fun rejectedCount(): Int =
        dao.getLocationPointsBySyncStatus(MobileSyncStatus.REJECTED, 5_000).size

    private fun coordinator(api: ApiService) = LocationUploadCoordinator(
        ApplicationProvider.getApplicationContext(),
        db,
        api
    )

    private fun item(
        outcome: String,
        code: String = outcome,
        message: String = "Accepted."
    ) = MobileIngestItemResult(
        clientItemKey = "key-$outcome-${code}",
        entityType = "location",
        outcome = outcome,
        code = code,
        message = message
    )

    private fun success(items: List<MobileIngestItemResult>): ApiResponse<MobileLocationPointsUploadResult> =
        ApiResponse(
            code = 0,
            message = "ok",
            data = MobileLocationPointsUploadResult(
                acceptedCount = items.count { it.outcome == "accepted" },
                skippedCount = items.count { it.outcome == "skipped" },
                rejectedCount = items.count { it.outcome == "rejected" },
                itemResults = items
            )
        )

    /** 记录每次请求的批大小，并按 [handler] 生成响应（handler 可抛异常模拟失败）。 */
    private fun batchApi(
        sizes: MutableList<Int> = mutableListOf(),
        handler: (List<MobileLocationPointRequest>) -> ApiResponse<MobileLocationPointsUploadResult>
    ): ApiService = Proxy.newProxyInstance(
        ApiService::class.java.classLoader,
        arrayOf(ApiService::class.java)
    ) { _, method, args ->
        when (method.name) {
            "uploadMobileLocationsBatch" -> {
                val request = args!![0] as MobileLocationPointsUploadRequest
                sizes += request.points.size
                handler(request.points)
            }
            "toString" -> "BatchApi"
            "hashCode" -> System.identityHashCode(this)
            "equals" -> false
            else -> error("Unexpected API call in test: ${method.name}")
        }
    } as ApiService
}
