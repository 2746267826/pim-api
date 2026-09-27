package com.pim.app.location

import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import android.content.Context
import com.pim.app.data.AppDatabase
import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileSyncStatus
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.RawLocationFix
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 / AC-15.2 / AC-15.4。
 *
 * 客户端**只做精度过滤**：取消既有三条点级裁剪 —— ①常规流 2 秒去重 ②静止聚类丢弃
 * ③上传前抽稀。本条覆盖 ② 的入库侧：静止场景（点之间 ≤5 米 / ≤30 秒）下，
 * **每一条达标点都必须逐条入库**。
 *
 * 基线行为（反面对照）：`LocationQueueRepository.enqueueAccepted` 命中
 * `shouldClusterDrop` 即静默 `return -1`，不上传、不留记录 —— 正是 AC-15.3
 * 禁止的「命中即 return 且无记录」。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class LocationQueueRepositoryNoCullingTest {

    private lateinit var db: AppDatabase
    private lateinit var dao: MobileDataDao
    private lateinit var repository: LocationQueueRepository

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        dao = db.mobileDataDao()
        repository = LocationQueueRepository(dao)
    }

    @After
    fun tearDown() {
        db.close()
    }

    /** AC-15.1：静止 10 分钟、每秒一个点 → 全部逐条入库（基线会只剩零星几条）。 */
    @Test
    fun `静止场景下连续的达标点逐条入库不被聚类丢弃`() = runTest {
        val startMillis = 1_700_000_000_000L
        // 1 秒 1 次、共 600 次（10 分钟）；坐标固定 → 距离 0 米，必然命中 5 米/30 秒聚类。
        val accepted = (0 until 600).map { index ->
            acceptedAt(recordedAtMillis = startMillis + index * 1_000L)
        }

        val ids = accepted.map { repository.enqueueAccepted(it, rawJson = "{}", source = "auto") }

        assertEquals(
            "AC-15.1：静止场景下 600 条达标点必须逐条入库，不得被静止聚类吞掉",
            (0 until 600).toList(),
            ids.mapIndexed { index, value -> if (value > 0L) index else -1 }.filter { it >= 0 }
        )
        assertEquals(600, dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 1_000).size)
    }

    /** AC-15.3：不得存在「命中即 return 且无记录」的静默丢弃 —— 入库侧每条都要落库。 */
    @Test
    fun `同一坐标同一时刻的两个达标点也都入库`() = runTest {
        val identical = acceptedAt(recordedAtMillis = 1_700_000_000_000L)

        val first = repository.enqueueAccepted(identical, rawJson = "{}", source = "auto")
        val second = repository.enqueueAccepted(identical, rawJson = "{}", source = "auto")

        assertEquals("AC-15.3：不得静默丢弃与上一条完全相同的达标点", true, first > 0L)
        assertEquals(true, second > 0L)
        assertEquals(2, dao.getLocationPointsBySyncStatus(MobileSyncStatus.PENDING, 10).size)
    }

    /**
     * AC-15.2：入库路径上不再有聚类判定调用。
     *
     * 只看**可执行代码**（剥掉注释）：文档注释里会解释「取消了什么」，
     * 那不是调用点。
     */
    @Test
    fun `入库路径不再调用聚类丢弃判定`() {
        val code = stripComments(
            java.io.File("").canonicalFile.let { dir ->
                generateSequence(dir) { it.parentFile }
                    .map { it.resolve("src/main/java/com/pim/app/location/LocationQueueRepository.kt") }
                    .first { it.isFile }
            }.readText(Charsets.UTF_8)
        )

        listOf("shouldClusterDrop", "shouldClusterDropEntities", "compressAccepted").forEach { needle ->
            assertEquals(
                "AC-15.2：入库路径不得再调用 $needle（点级裁剪已取消）",
                false,
                code.contains(needle)
            )
        }
        assertEquals(
            "AC-15.2：客户端点级取舍只剩精度门一处；入库路径必须是纯插入",
            true,
            code.contains("dao.insertLocationPoint(")
        )
    }

    private fun acceptedAt(recordedAtMillis: Long) = QualityAcceptedLocation(
        fix = RawLocationFix(
            latitude = 31.230416,
            longitude = 121.473701,
            horizontalAccuracyMeters = 12f,
            altitudeMeters = 10.0,
            provider = "gps",
            recordedAtMillis = recordedAtMillis,
            policyMode = "PowerSavingNormal",
            scheduleLowFrequency = false,
            motionSignal = "Still"
        ),
        altitudeMeters = 10.0,
        acceptedAtMillis = recordedAtMillis,
        qualityFlags = emptySet()
    )
}

/** 剥掉行注释与块注释，只留可执行代码（源码谓词断言必须排除注释以免自我误判）。 */
internal fun stripComments(source: String): String =
    source
        .replace(Regex("""/\*.*?\*/""", RegexOption.DOT_MATCHES_ALL), "")
        .lines()
        .joinToString("\n") { line -> line.substringBefore("//") }
