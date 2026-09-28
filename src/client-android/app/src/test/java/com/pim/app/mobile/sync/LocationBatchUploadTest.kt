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
import okhttp3.ResponseBody.Companion.toResponseBody
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config
import retrofit2.HttpException
import retrofit2.Response

/**
 * 定位点批量补传（走服务端 `mobile/location/points/batch`）。
 *
 * 覆盖四条口径：
 * 1. 一次请求携带多条点，服务端**逐条**结果按请求顺序对齐本地行（断言到「哪一行」，不只断言条数）；
 * 2. `accepted` / `skipped`（服务端已有同一自然键）都算已送达并删除本地行，
 *    `rejected` 才落 REJECTED 并保留记录；
 * 3. 请求级失败（服务端拒绝、请求体无法序列化、条数不符）**一律不删本地行**，
 *    且必须能继续推进 —— 拆小请求体失败后退到队尾（FAILED），绝不占住队头把整条管道卡死；
 * 4. 单轮同步循环到队列排空，有批次数上限，网络级失败立即停止本轮，被截断时会明确标记。
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

    /**
     * 被拒结果必须落到**正确那一行**：只断言条数是不够的 —— 若下标错位，
     * 本地会删掉真正被拒的点、同时给已送达的点打上「永久失败」，属于静默丢点。
     */
    @Test
    fun `服务端拒绝的点落 REJECTED 且落到的确实是那一行`() = runTest {
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
        val row = rejected.single()
        assertEquals("被拒的必须是 seed 的第 2 行（按下标错位就会误删/误标）", RECORDED_BASE + 1_000L, row.recordedAtUtc)
        assertEquals(31.0 + 0.00001, row.latitude, 1e-9)
        assertTrue(row.lastError!!.contains("unusable-accuracy"))
    }

    @Test
    fun `整批网络异常时全部保持待传不丢点`() = runTest {
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

    // --- 请求级失败：拆小隔离 + 队尾退避，不允许卡住整条管道 ---

    /** 服务端拒绝请求体（真实形态是 400，不是 413）：按半拆分隔离，拆到最小仍被拒就退到队尾。 */
    @Test
    fun `服务端拒绝请求体时按半拆分且最终退到队尾而不是卡住队头`() = runTest {
        seed(count = 8)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { throw httpException(400) }

        val updates = coordinator(api).uploadPending()

        assertTrue("必须真的尝试过拆分（8 → 4/4 → 2/2/2/2 → 1…）", sizes.size > 1)
        assertEquals("一条都不能删", 8, failedCount() + pendingCount())
        assertEquals("被拒的行退到队尾（FAILED），不占队头", 0, pendingCount())
        assertEquals(8, failedCount())
        assertEquals(8, updates.deferredFailedIds.size)
        assertEquals(false, updates.shouldRetry)
        assertTrue(updates.perItemErrors.values.all { it.contains("batch-item-rejected") })
    }

    /** 队尾退避的意义：坏行不能再挡住后面正常的新点。 */
    @Test
    fun `退到队尾的坏行不再阻塞新点上传`() = runTest {
        seed(count = 3)
        val poisonedIds = dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 100).map { it.id }.toSet()
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { throw httpException(400) }
        coordinator(api).uploadPending()  // 3 条坏行 → 全部退到队尾（FAILED）

        // 再来一批新点（PENDING）；只取 5 条待传时应当全是新点，而不是回头看队尾的坏行
        seed(count = 6, startLat = 35.0)
        val newSizes = mutableListOf<Int>()
        val okApi = batchApi(newSizes) { points -> success(points.map { item("accepted") }) }
        coordinator(okApi).uploadPending(limit = 5)

        assertEquals("新点应被优先上传（坏行已退到队尾）", listOf(5), newSizes)
        assertEquals("坏行仍在本地（不丢点）", 3, failedCount())
        val remaining = dao.getLocationPointsBySyncStatus(MobileSyncStatus.FAILED, 100)
        assertEquals(poisonedIds, remaining.map { it.id }.toSet())
    }

    /** 鉴权类失败拆了也没用：不拆分、整批留原位、结束本轮排空。 */
    @Test
    fun `鉴权失败不拆分且整批留原位`() = runTest {
        seed(count = 3)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { throw httpException(401) }

        val updates = coordinator(api).uploadPending()

        assertEquals("401 不拆分（拆了也一样失败）", listOf(3), sizes)
        assertEquals(0, failedCount())
        assertEquals(3, pendingCount())
        assertEquals(true, updates.shouldRetry)
    }

    @Test
    fun `逐条结果条数与请求条数不符时整批退到队尾不删行`() = runTest {
        seed(count = 3)
        val api = batchApi { points -> success(points.take(2).map { item("accepted") }) }

        val updates = coordinator(api).uploadPending()

        assertEquals(0, updates.syncedIds.size)
        assertEquals(3, updates.failedIds.size)
        assertTrue(updates.perItemErrors.values.all { it == "batch-item-result-mismatch" })
        assertEquals("对账不上时宁可重发也不能误删", 0, pendingCount())
        assertEquals("留在本地等下一轮", 3, failedCount())
    }

    /** 条数相同但掺了非定位点条目（如批次汇总项）同样会整体错位 —— 必须整批不删。 */
    @Test
    fun `逐条结果里出现非定位点条目时整批退到队尾不删行`() = runTest {
        seed(count = 3)
        val api = batchApi { points ->
            success(
                points.mapIndexed { index, _ ->
                    if (index == 2) item("accepted", entityType = "batch-summary") else item("accepted")
                }
            )
        }

        val updates = coordinator(api).uploadPending()

        assertEquals(0, updates.syncedIds.size)
        assertEquals(3, updates.failedIds.size)
        assertTrue(updates.perItemErrors.values.all { it == "batch-item-entity-mismatch" })
        assertEquals(0, pendingCount())
        assertEquals("一条都不能误删", 3, failedCount())
    }

    @Test
    fun `业务错误码响应整批退到队尾不删行`() = runTest {
        seed(count = 2)
        val api = batchApi {
            ApiResponse<MobileLocationPointsUploadResult>(code = 5000, message = "server busy", data = null)
        }

        val updates = coordinator(api).uploadPending()

        assertEquals(0, updates.syncedIds.size)
        assertEquals(0, pendingCount())
        assertEquals(2, failedCount())
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
        assertFalse("队列真的清空了，不应标成截断", updates.truncated)
    }

    @Test
    fun `排空循环遇到网络级失败立即停止本轮`() = runTest {
        seed(count = 1200)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { throw IOException("network down") }

        val updates = coordinator(api).uploadPendingUntilDrained()

        assertEquals("失败后不再硬打后续批次", 1, sizes.size)
        assertEquals(true, updates.shouldRetry)
        assertEquals("一条都不能少", 1200, pendingCount())
    }

    @Test
    fun `排空循环受单轮批次数上限约束且被截断时明确标记`() = runTest {
        seed(count = 1500)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { points -> success(points.map { item("accepted") }) }

        val updates = coordinator(api).uploadPendingUntilDrained(maxBatches = 2)

        assertEquals(listOf(500, 500), sizes)
        assertEquals("剩余积压留待下一个同步周期", 500, pendingCount())
        assertTrue("跑满上限且队列仍有待传 → 必须标记截断", updates.truncated)
    }

    @Test
    fun `一批里 mixed 结果各归其位`() = runTest {
        seed(count = 4)
        val api = batchApi { points ->
            success(
                listOf(
                    item("accepted"),
                    item(outcome = "rejected", code = "unusable-accuracy"),
                    item(outcome = "weird-outcome", code = "wat"),
                    item("accepted")
                ).also { assertEquals(points.size, it.size) }
            )
        }

        val updates = coordinator(api).uploadPending()

        assertEquals(2, updates.syncedIds.size)
        assertTrue("未知 outcome 属可重试但需退避，不是网络级失败", updates.retryableFailedIds.isEmpty())
        assertEquals(1, rejectedCount())
        assertEquals(1, failedCount())
        assertEquals("未知 outcome 退到队尾而不是永久失败", 0, pendingCount())
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

    /** 非有限数值（NaN / ∞）会让**整个请求体**无法序列化 —— 必须在本地就拦下来，否则它会毒死整批。 */
    @Test
    fun `数值不合法的行本地判永久失败且不进入请求`() = runTest {
        dao.insertLocationPoint(point(latitude = Double.POSITIVE_INFINITY, rawJson = "bad-inf"))
        seed(count = 1, startLat = 33.0)
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { points -> success(points.map { item("accepted") }) }

        coordinator(api).uploadPending()

        assertEquals("坏行不进请求体", listOf(1), sizes)
        val rejected = dao.getLocationPointsBySyncStatus(MobileSyncStatus.REJECTED, 100)
        assertEquals(1, rejected.size)
        assertEquals("invalid-numeric-values", rejected.single().lastError)
    }

    @Test
    fun `队列为空时不发请求`() = runTest {
        val sizes = mutableListOf<Int>()
        val api = batchApi(sizes) { error("队列为空时不应发起请求") }

        val updates = coordinator(api).uploadPendingUntilDrained()

        assertEquals(emptyList<Int>(), sizes)
        assertEquals(0, updates.syncedIds.size)
        assertEquals(0, updates.failedIds.size)
        assertFalse(updates.truncated)
    }

    // --- helpers ---

    private suspend fun seed(count: Int, accuracy: Float? = 12f, startLat: Double = 31.0) {
        (0 until count).forEach { index ->
            dao.insertLocationPoint(
                point(
                    latitude = startLat + index * 0.00001,
                    accuracy = accuracy,
                    recordedAtUtc = RECORDED_BASE + index * 1_000L,
                    rawJson = "row-$startLat-$index"
                )
            )
        }
    }

    private fun point(
        latitude: Double,
        accuracy: Float? = 12f,
        recordedAtUtc: Long = RECORDED_BASE,
        rawJson: String = "row"
    ) = MobileLocationPointEntity(
        latitude = latitude,
        longitude = 121.0,
        accuracyMeters = accuracy,
        recordedAtUtc = recordedAtUtc,
        source = "auto",
        collectedAtUtc = recordedAtUtc,
        rawJson = rawJson
    )

    private suspend fun pendingCount(): Int =
        dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 5_000).size

    private suspend fun failedCount(): Int =
        dao.getLocationPointsBySyncStatus(MobileSyncStatus.FAILED, 5_000).size

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
        message: String = "Accepted.",
        entityType: String = "location-point"
    ) = MobileIngestItemResult(
        clientItemKey = "key-$outcome-${code}",
        entityType = entityType,
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

    /** 本地自建 HttpException，避免依赖其它测试文件的夹具。 */
    private fun httpException(code: Int): HttpException =
        HttpException(Response.error<Any>(code, "{}".toResponseBody()))

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

    private companion object {
        const val RECORDED_BASE = 1_700_000_000_000L
    }
}
