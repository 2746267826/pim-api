package com.pim.app.status

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.data.AppDatabase
import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileLocationPolicyTransitionEntity
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 **REQ-4 / REQ-5 / REQ-6** 的数据口径（真实 Room）。
 *
 * 这里用固定时钟（注入 `nowMillis`）而不是 `System.currentTimeMillis()`：
 * - REQ-4：`已持续 = now − 最新一条记录时间`，且「上次切换」与「已持续」取自**同一条**记录；
 * - REQ-5：30 天窗口、时间倒序、窗口计数（AC-5.3 / AC-5.4）；
 * - REQ-6 的窗口边界与 `ForensicRetention`（30 天、严格小于）保持一致。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class PolicyTransitionHistoryRepositoryTest {

    private lateinit var db: AppDatabase
    private lateinit var dao: MobileDataDao
    private val day = 24L * 60L * 60L * 1000L
    private val now = 1_800_000_000_000L

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

    private fun repository() = PolicyTransitionHistoryRepository(dao) { now }

    private suspend fun insert(
        occurredAtUtc: Long,
        from: String? = null,
        to: String = "PowerSavingNormal",
        reason: String = "默认省电档"
    ): Long = dao.insertPolicyTransition(
        MobileLocationPolicyTransitionEntity(
            fromMode = from,
            toMode = to,
            reason = reason,
            occurredAtUtc = occurredAtUtc
        )
    )

    @Test
    fun `空库时最新记录与已持续都为空`() = runTest {
        val state = repository().observeCurrent().first()
        assertNull(state.latest)
        assertNull("无记录时「已持续」必须为 null（界面显示「未知」）", state.currentDurationMillis)
    }

    @Test
    fun `已持续取自最新一条记录的时刻`() = runTest {
        insert(now - 3 * day, reason = "老的")
        insert(now - 90 * 60_000L, from = "PowerSavingNormal", to = "MotionObservation", reason = "新的")

        val state = repository().observeCurrent().first()

        assertEquals("新的", state.latest?.reason)
        assertEquals(90L * 60_000L, state.currentDurationMillis)
        // 「上次切换」行与「已持续」用的是同一条记录的时间（AC-4.2）。
        assertEquals(state.latest!!.occurredAtMillis, now - state.currentDurationMillis!!)
    }

    @Test
    fun `同毫秒并列时取 id 更大的那条`() = runTest {
        val millis = now - 1_000L
        insert(millis, reason = "先写")
        insert(millis, reason = "后写")

        assertEquals("后写", repository().observeCurrent().first().latest?.reason)
    }

    @Test
    fun `三十天窗口只统计窗口内记录且时间倒序`() = runTest {
        insert(now - 31 * day, reason = "窗口外")
        insert(now - 30 * day, reason = "恰好 30 天")
        insert(now - 2 * day, reason = "窗口内旧")
        insert(now - 1 * day, reason = "窗口内新")
        val repository = repository()

        val rows = repository.observeWindow().first()
        val count = repository.observeWindowCount().first()

        assertEquals(listOf("窗口内新", "窗口内旧", "恰好 30 天"), rows.map { it.reason })
        assertEquals("窗口内条数必须与实际可见条数一致（AC-5.4）", 3, count)
    }

    @Test
    fun `窗口条数按默认二十条分页展示时分母不变`() = runTest {
        repeat(25) { index -> insert(now - index * 60_000L - 1_000L, reason = "第 ${index + 1} 条") }
        val repository = repository()

        val all = repository.observeWindow().first()
        val count = repository.observeWindowCount().first()

        assertEquals(25, all.size)
        assertEquals(25, count)
        assertEquals("仅显示最近 20 条时也要能看到 25 这个分母", 20, all.take(20).size)
    }
}
