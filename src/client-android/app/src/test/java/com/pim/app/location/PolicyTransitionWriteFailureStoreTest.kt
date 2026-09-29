package com.pim.app.location

import android.app.Application
import android.content.Context
import androidx.test.core.app.ApplicationProvider
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.testing.InMemorySharedPreferences
import kotlinx.coroutines.test.runTest
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 **REQ-3**：写入失败不再静默。
 *
 * 缺陷版本里写入异常被空 `catch` 吞掉（工单 §二-2），牌面上什么都看不到。
 * 这里守住四条契约：
 * - AC-3.1 ①②：失败 → error 日志（含异常类名与摘要）+ 连续失败计数；
 * - AC-3.2 / AC-3.4：成功一次，计数与最近失败时间**同时**归零；
 * - AC-3.3：重启（新实例、同一持久化）后计数与时间仍在；
 * - AC-3.5：error 日志不受「详细日志」开关限制（现状只有 `debug` 级受门控）。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34], application = Application::class)
class PolicyTransitionWriteFailureStoreTest {

    private val context: Context = ApplicationProvider.getApplicationContext()
    private val fixedNow = 1_756_684_800_000L
    private var now: Long = fixedNow

    private fun prefs(name: String = "policy-transition-failure-" + System.nanoTime()) =
        context.getSharedPreferences(name, Context.MODE_PRIVATE).also { it.edit().clear().commit() }

    /** 「详细日志」保持默认关闭（本用例组要证明 error 级不受它门控，AC-3.5）。 */
    private fun logs(): StructuredLogRepository {
        val trackingPrefs = InMemorySharedPreferences()
        return StructuredLogRepository(context, TrackingSettingsStore(trackingPrefs)) { now }
    }

    private fun store(
        prefs: android.content.SharedPreferences,
        logRepository: StructuredLogRepository = logs()
    ) = PolicyTransitionWriteFailureStore(prefs, logRepository, { now })

    @Test
    fun `连续失败计数递增并刷新最近失败时间`() = runTest {
        val store = store(prefs())
        assertEquals(PolicyTransitionWriteFailure.None, store.state.value)

        now = fixedNow
        store.recordFailure(IllegalStateException("磁盘已满"))
        assertEquals(1, store.state.value.consecutiveFailures)
        assertEquals(fixedNow, store.state.value.lastFailureAtUtcMillis)

        now = fixedNow + 60_000L
        store.recordFailure(RuntimeException("第二次失败"))
        assertEquals(2, store.state.value.consecutiveFailures)
        assertEquals(fixedNow + 60_000L, store.state.value.lastFailureAtUtcMillis)
    }

    @Test
    fun `失败会写一条含异常类名与摘要的 error 日志`() = runTest {
        val logRepository = logs()
        val store = store(prefs(), logRepository)

        store.recordFailure(IllegalStateException("写入被拒绝：数据库已关闭"))

        val entry = logRepository.recent(limit = 10)
            .firstOrNull { it.level == "error" && it.message.contains("策略切换记录写入失败") }
        assertNotNull("必须落一条 error 级结构化日志，实际：${logRepository.recent(10)}", entry)
        assertTrue(
            "日志必须含异常类名：${entry!!.message}",
            entry.message.contains("IllegalStateException")
        )
        assertTrue(
            "日志必须含异常摘要：${entry.message}",
            entry.message.contains("写入被拒绝")
        )
        assertNotNull("日志必须带异常堆栈", entry.throwable)
    }

    @Test
    fun `详细日志关闭时 error 级写入失败日志仍然落盘`() = runTest {
        // 现状：只有 debug 级受「详细日志」门控（StructuredLogRepository 第 163 行）。
        val logRepository = logs()
        val store = store(prefs(), logRepository)

        store.recordFailure(RuntimeException("verbose off 也要看得见"))

        assertTrue(
            "error 级不得依赖「详细日志」开关（AC-3.5）",
            logRepository.recent(limit = 10).any { it.level == "error" }
        )
    }

    @Test
    fun `成功一次同时归零计数与最近失败时间`() = runTest {
        val store = store(prefs())
        store.recordFailure(RuntimeException("第一次"))
        store.recordFailure(RuntimeException("第二次"))
        assertTrue(store.state.value.hasFailure)

        store.recordSuccess()

        assertEquals(0, store.state.value.consecutiveFailures)
        assertNull(store.state.value.lastFailureAtUtcMillis)
        assertTrue(!store.state.value.hasFailure)
    }

    @Test
    fun `失败状态在重启后仍在且任意一次成功即归零`() = runTest {
        val shared = prefs("policy-transition-restart-" + System.nanoTime())
        val first = store(shared)
        first.recordFailure(RuntimeException("重启前的失败"))
        first.recordFailure(RuntimeException("重启前的第二次失败"))

        // 「重启」= 新的 store 实例，读同一份持久化。
        val restarted = store(shared)
        assertEquals(2, restarted.state.value.consecutiveFailures)
        assertEquals(fixedNow, restarted.state.value.lastFailureAtUtcMillis)

        restarted.recordSuccess()
        assertEquals(0, restarted.state.value.consecutiveFailures)
        assertNull(restarted.state.value.lastFailureAtUtcMillis)

        // 归零也要落盘：再「重启」一次仍然是 0。
        val restartedAgain = store(shared)
        assertEquals(0, restartedAgain.state.value.consecutiveFailures)
        assertNull(restartedAgain.state.value.lastFailureAtUtcMillis)
    }
}
