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
import java.lang.reflect.Proxy
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 / AC-15.2 / AC-15.3。
 *
 * **上传前抽稀必须真的停用**：基线 `LocationUploadCoordinator.pendingRows` 对
 * 待传行调 Douglas-Peucker（ε = 8.0 米），被抽掉的点既不进上传、也不留记录。
 * 本测试给出**同一场景停用前后的条数对照**：一条直线轨迹上的达标点，
 * 抽稀只会保留首尾（中间全部命中 ε），停用后必须逐条进入上传队列。
 *
 * 注：同文件的 `compressForUpload` 是死代码（全仓无调用者），真正生效的只有
 * `pendingRows` 这一处 —— 本测试走真实的 `uploadPending()`，因此只认可它。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class LocationUploadNoDecimationTest {

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

    /** AC-15.1：直线轨迹（相邻约 1 米，全部远小于 ε=8 米）100 条必须逐条上传。 */
    @Test
    fun `直线轨迹上的达标点全部进入上传队列不被抽稀`() = runTest {
        val requested = mutableListOf<MobileLocationPointRequest>()
        seedStraightLine(count = 100, metersPerStep = 1.0)

        coordinator(requested).uploadPending(limit = 500)

        assertEquals(
            "AC-15.1：上传前抽稀已取消，100 条达标点必须逐条进入上传队列（停用前只剩首尾 2 条）",
            100,
            requested.size
        )
        assertEquals(
            "AC-15.1：上传成功后队列清空，无残留",
            0,
            dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 500).size
        )
    }

    /** AC-15.2 / AC-15.3：逐条核对每条待传行都真的被请求，无抽稀造成的点数缺口。 */
    @Test
    fun `每条待上传点都被真正请求不留点数缺口`() = runTest {
        val requested = mutableListOf<MobileLocationPointRequest>()
        seedStraightLine(count = 50, metersPerStep = 0.5)

        coordinator(requested).uploadPending(limit = 500)

        // rawJson 承载行序号，用于逐条核对「哪一条真的被请求过」。
        val requestedRows = requested.map { it.rawJson }.toSet()
        assertEquals("AC-15.3：每条待上传点都必须被真正请求过", 50, requested.size)
        assertEquals(50, requestedRows.size)
        assertEquals(
            "AC-15.3：被请求的行必须与入库的行一一对应",
            (0 until 50).map { "row-$it" }.toSet(),
            requestedRows
        )
    }

    /**
     * 上传协议（批量补传）：单次同步走批量端点，一批最多 [LOCATION_UPLOAD_BATCH_LIMIT] 条。
     *
     * 旧口径（WO-ANDROID-GATE-20260926 AC-15.5 记录）：逐条 HTTP、每次同步最多 100 条。
     * 本改造后同样 150 条只发 **1 次** 请求即可传完。
     */
    @Test
    fun `单次同步走批量端点且 150 条一次请求传完`() = runTest {
        val requested = mutableListOf<MobileLocationPointRequest>()
        val requestSizes = mutableListOf<Int>()
        seedStraightLine(count = 150, metersPerStep = 1.0)

        coordinator(requested, requestSizes).uploadPending()

        assertEquals("批量上传：150 条应只发 1 次请求", 1, requestSizes.size)
        assertEquals(listOf(150), requestSizes)
        assertEquals(150, requested.size)
        assertEquals(
            "上传成功后队列清空",
            0,
            dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 500).size
        )
    }

    /** 单批不超过 [LOCATION_UPLOAD_BATCH_LIMIT] 条；超出的留在队列，不丢点。 */
    @Test
    fun `单批上限 500 条超出的留待下一批`() = runTest {
        val requested = mutableListOf<MobileLocationPointRequest>()
        val requestSizes = mutableListOf<Int>()
        seedStraightLine(count = 700, metersPerStep = 1.0)

        coordinator(requested, requestSizes).uploadPending()

        assertEquals(listOf(500), requestSizes)
        assertEquals(500, requested.size)
        assertEquals(
            "剩余 200 条留在队列等待下一批（不丢点、允许积压）",
            200,
            dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 500).size
        )
    }

    private suspend fun seedStraightLine(count: Int, metersPerStep: Double) {
        val recordedBase = 1_700_000_000_000L
        val degreesPerStep = metersPerStep / 111_320.0
        (0 until count).forEach { index ->
            dao.insertLocationPoint(
                MobileLocationPointEntity(
                    latitude = 31.0 + index * degreesPerStep,
                    longitude = 121.0,
                    accuracyMeters = 12f,
                    recordedAtUtc = recordedBase + index * 1_000L,
                    source = "auto",
                    collectedAtUtc = recordedBase + index * 1_000L,
                    rawJson = "row-$index"
                )
            )
        }
    }

    private fun coordinator(
        sink: MutableList<MobileLocationPointRequest> = mutableListOf(),
        requestSizes: MutableList<Int> = mutableListOf()
    ) = LocationUploadCoordinator(
        ApplicationProvider.getApplicationContext(),
        db,
        recordingApi(sink, requestSizes)
    )

    /**
     * 只实现上传链路会碰到的接口，其余方法一律抛出，避免用「意外调用」掩盖装配错误。
     */
    private fun recordingApi(
        sink: MutableList<MobileLocationPointRequest>,
        requestSizes: MutableList<Int>
    ): ApiService =
        Proxy.newProxyInstance(
            ApiService::class.java.classLoader,
            arrayOf(ApiService::class.java)
        ) { _, method, args ->
            when (method.name) {
                "uploadMobileLocationsBatch" -> {
                    val request = args!![0] as MobileLocationPointsUploadRequest
                    requestSizes += request.points.size
                    sink += request.points
                    val items = request.points.mapIndexed { index, _ ->
                        MobileIngestItemResult(
                            clientItemKey = "row-$index",
                            entityType = "location-point",
                            outcome = "accepted",
                            code = "accepted",
                            message = "Accepted."
                        )
                    }
                    ApiResponse<Any>(
                        code = 0,
                        message = "ok",
                        data = MobileLocationPointsUploadResult(
                            acceptedCount = items.size,
                            skippedCount = 0,
                            rejectedCount = 0,
                            itemResults = items
                        )
                    )
                }
                "toString" -> "RecordingApiService"
                "hashCode" -> System.identityHashCode(this)
                "equals" -> false
                else -> error("Unexpected API call in test: ${method.name}")
            }
        } as ApiService
}
