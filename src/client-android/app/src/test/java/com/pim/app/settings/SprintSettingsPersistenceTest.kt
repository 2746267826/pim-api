package com.pim.app.settings

import android.content.Context
import androidx.test.core.app.ApplicationProvider
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * WO-ANDROID-GATE-20260926 REQ-5（AC-5.1 / AC-5.2）。
 *
 * 「冲刺开关」必须**状态持久化**（关掉后杀进程重开仍是关）且**默认开启**（D1）。
 * 需求方对「假开关」零容忍（A4），所以这里断言的是真实落盘行为，
 * 不是 Compose 里的临时开关状态。
 *
 * 用 Robolectric 的真实 `SharedPreferences` 实现（而不是内存假件）：
 * 「杀进程重开」正是靠**重新读同一个持久化文件**来复现的，假件无法证明这一点。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class SprintSettingsPersistenceTest {

    private lateinit var context: Context
    private lateinit var preferences: android.content.SharedPreferences

    @Before
    fun setUp() {
        context = ApplicationProvider.getApplicationContext()
        preferences = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        preferences.edit().clear().commit()
    }

    /** AC-5.2：全新安装（未改过设置）时默认**开**。 */
    @Test
    fun `默认开启`() {
        val store = TrackingSettingsStore(preferences)

        assertTrue("AC-5.2：全新安装时冲刺开关默认必须是开启", store.read().sprintEnabled)
        assertTrue(
            "AC-5.2：默认值也必须是开（供 SettingsViewModel 展示用）",
            TrackingSettings.defaults().sprintEnabled
        )
    }

    /** AC-5.1：关闭后重新读盘（等价于杀进程重开）仍是关闭。 */
    @Test
    fun `关闭状态持久化`() {
        TrackingSettingsStore(preferences).setSprintEnabled(false)

        // 用同一个持久化文件重新构造 store = 进程重启后重新读盘
        val reopened = TrackingSettingsStore(
            context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        ).read()

        assertFalse("AC-5.1：关闭后重开必须是关闭", reopened.sprintEnabled)
    }

    /** AC-5.1：可反复开/关，最后一次写入生效。 */
    @Test
    fun `开关可反复切换且以最后一次为准`() {
        val store = TrackingSettingsStore(preferences)

        store.setSprintEnabled(false)
        store.setSprintEnabled(true)
        assertTrue(store.read().sprintEnabled)

        store.setSprintEnabled(false)
        assertFalse(
            TrackingSettingsStore(
                context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            ).read().sprintEnabled
        )
    }

    /** AC-5.1：写开关不得顺带改动其他采集参数（范围外一律不改）。 */
    @Test
    fun `切换开关不影响其他采集参数`() {
        val store = TrackingSettingsStore(preferences)
        store.write(
            TrackingSettings.defaults().copy(
                continuousCollectionEnabled = true,
                normalIntervalMillis = 120_000L,
                movementIntervalMillis = 45_000L
            )
        )
        val before = store.read()

        store.setSprintEnabled(false)

        assertEquals(before.copy(sprintEnabled = false), store.read())
    }

    private companion object {
        const val PREFS_NAME = "sprint-settings-persistence-test"
    }
}
