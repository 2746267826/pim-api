package com.pim.app.location.sprint

import android.content.Context
import androidx.room.Room
import androidx.test.core.app.ApplicationProvider
import com.pim.app.data.AppDatabase
import com.pim.app.forensics.ForensicLedger
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.settings.TrackingSettingsStore
import kotlinx.coroutines.test.runTest
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.RobolectricTestRunner
import org.robolectric.annotation.Config

/**
 * WO-ANDROID-GATE-20260926 REQ-9 / AC-9.1 / AC-9.2。
 *
 * 状态页要展示「冲刺开关状态 + 最近 24 小时冲刺次数」。AC-9.2 要求**分两态**：
 * 1. 「最近 24 小时**无任何采集数据**」→ 显示「暂无」；
 * 2. 「有采集数据但冲刺次数为 0」→ 如实显示「0 次」并同时展示开关状态。
 *
 * 这两态的区分靠 [LocationSprintSummaryRepository] 同时给出「次数」与「是否有采集数据」，
 * 不能用「次数 == 0」糊在一起 —— 那会让「关了冲刺」与「压根没采集」看起来一样。
 */
@RunWith(RobolectricTestRunner::class)
@Config(sdk = [34])
class LocationSprintSummaryTest {

    private lateinit var db: AppDatabase
    private lateinit var dao: com.pim.app.data.MobileDataDao
    private lateinit var repository: LocationSprintSummaryRepository
    private lateinit var settingsStore: TrackingSettingsStore

    private val now = 1_700_000_000_000L
    private val dayMillis = 24L * 60L * 60L * 1_000L

    @Before
    fun setUp() {
        val context = ApplicationProvider.getApplicationContext<Context>()
        db = Room.inMemoryDatabaseBuilder(context, AppDatabase::class.java)
            .allowMainThreadQueries()
            .build()
        dao = db.mobileDataDao()
        settingsStore = TrackingSettingsStore(
            context.getSharedPreferences("sprint-summary-test", Context.MODE_PRIVATE)
        )
        settingsStore.write(TrackingSettingsStoreSettings())
        repository = LocationSprintSummaryRepository(
            ledger = LocationSprintLedger(
                db.forensicEventDao(),
                StructuredLogRepository(context, settingsStore) { now }
            ),
            dao = dao,
            trackingSettingsStore = settingsStore,
            nowUtcMillis = { now }
        )
    }

    @After
    fun tearDown() {
        db.close()
    }

    /** AC-9.2 态一：无任何采集数据 → 「暂无」，且开关状态仍要显示。 */
    @Test
    fun `无采集数据时次数显示暂无`() = runTest {
        val summary = repository.read()

        assertEquals(
            "AC-9.2：最近 24 小时无任何采集数据时，次数显示「暂无」",
            SprintCountDisplay.Empty,
            summary.countDisplay
        )
        assertEquals("次数为 null（不是 0）", null, summary.count)
        assertEquals(
            "AC-9.2：无采集数据时仍必须展示开关当前状态",
            true,
            summary.enabled
        )
    }

    /** AC-9.2 态二：有采集数据但冲刺 0 次 → 如实显示「0 次」。 */
    @Test
    fun `有采集数据但无冲刺时显示零次`() = runTest {
        // 关闭状态下跑一拍：产生「跳过」记录（属于采集数据），但没有已执行冲刺
        settingsStore.setSprintEnabled(false)
        LocationSprintLedger(
            db.forensicEventDao(),
            StructuredLogRepository(
                ApplicationProvider.getApplicationContext(),
                settingsStore
            ) { now }
        ).recordSkipped(now - 60_000L, SprintSkipReasons.DISABLED)

        val summary = repository.read()

        assertEquals(
            "AC-9.2：有采集数据但冲刺次数为 0 时必须如实显示 0 次",
            SprintCountDisplay.Value(0),
            summary.countDisplay
        )
        assertEquals(0, summary.count)
        assertEquals(
            "AC-9.2：同时必须展示开关当前状态（关闭）",
            false,
            summary.enabled
        )
    }

    /** AC-9.1：次数与台账一致，且只数「已执行」。 */
    @Test
    fun `次数与台账已执行记录一致`() = runTest {
        val ledger = LocationSprintLedger(
            db.forensicEventDao(),
            StructuredLogRepository(
                ApplicationProvider.getApplicationContext(),
                settingsStore
            ) { now }
        )
        repeat(3) { index ->
            ledger.recordExecuted(
                SprintWindowResult(
                    startedAtUtcMillis = now - 3_600_000L + index * 60_000L,
                    endedAtUtcMillis = now - 3_600_000L + index * 60_000L + 30_000L,
                    sampleCount = 30,
                    bestAccuracyMeters = 8f,
                    acceptedCount = 30
                )
            )
        }
        ledger.recordSkipped(now - 60_000L, SprintSkipReasons.DISABLED)

        val summary = repository.read()

        assertEquals(
            "AC-9.1：次数必须与台账中的已执行记录一致（跳过记录不计入）",
            SprintCountDisplay.Value(3),
            summary.countDisplay
        )
    }

    /**
     * AC-9.1 / AC-9.2：窗口外的冲刺不计入次数；窗口内有采集数据（心跳）
     * 因此仍如实显示「0 次」而不是「暂无」。
     */
    @Test
    fun `窗口外的冲刺不计入但有采集数据时显示零次`() = runTest {
        val ledger = LocationSprintLedger(
            db.forensicEventDao(),
            StructuredLogRepository(
                ApplicationProvider.getApplicationContext(),
                settingsStore
            ) { now }
        )
        // 窗口内有一次心跳（有采集数据），但没有任何冲刺
        ForensicLedger(db.forensicEventDao(), StructuredLogRepository(
            ApplicationProvider.getApplicationContext(),
            settingsStore
        ) { now }).recordHeartbeat(now - 60_000L, "{}")
        // 25 小时前有一次冲刺（窗口外）
        ledger.recordExecuted(
            SprintWindowResult(
                startedAtUtcMillis = now - dayMillis - 60_000L,
                endedAtUtcMillis = now - dayMillis - 30_000L,
                sampleCount = 30,
                bestAccuracyMeters = 8f,
                acceptedCount = 30
            )
        )

        val summary = repository.read()

        assertEquals(
            "AC-9.1：24 小时窗口外的冲刺不得计入次数",
            SprintCountDisplay.Value(0),
            summary.countDisplay
        )
    }

    private fun TrackingSettingsStoreSettings() =
        com.pim.app.settings.TrackingSettings.defaults().copy(sprintEnabled = true)
}
