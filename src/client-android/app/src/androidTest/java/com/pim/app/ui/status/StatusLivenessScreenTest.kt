package com.pim.app.ui.status

import androidx.activity.ComponentActivity
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithText
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import com.pim.app.forensics.CoverageFormatting
import com.pim.app.forensics.DroppedReasonCountOnly
import com.pim.app.forensics.DroppedReasonUiState
import com.pim.app.forensics.LivenessRules
import com.pim.app.forensics.LivenessSummaryCalculator
import com.pim.app.forensics.LivenessUiSnapshot
import com.pim.app.status.StatusCenterState
import com.pim.app.status.StatusOverall
import com.pim.app.status.SyncPhase
import com.pim.app.ui.theme.PimTheme
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

/**
 * REQ-8 / REQ-9 的真机（模拟器）UI 验证：存活区块在真实 Compose 运行时里的实际渲染。
 *
 * 这是 `AGENTS.md` 要求的「连接 Android 测试门禁」的一部分——Linux 编译通过不能冒充真机通过，
 * 因此状态页存活区块与丢弃原因页都在这里跑一次真实渲染。
 */
/** 仪表化测试用的冲刺概况（REQ-9：`StatusCenterContent` 的 sprintSummary 无默认值）。 */
private fun sprintSummaryForTest() = com.pim.app.location.sprint.SprintSummary(
    enabled = true,
    count = 0,
    countDisplay = com.pim.app.location.sprint.SprintCountDisplay.Value(0)
)

class StatusLivenessScreenTest {

    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    private fun normalState(): StatusCenterState = StatusCenterState.empty().copy(
        isLoading = false,
        overall = StatusOverall.Normal,
        syncPhase = SyncPhase.Idle
    )

    private fun noDataSnapshot(nowUtcMillis: Long): LivenessUiSnapshot {
        // 无数据时用真实的格式化函数产出文案，断言的就是用户实际会看到的那一行。
        val summary = LivenessSummaryCalculator.summarize(
            heartbeatTimestampsUtcMillis = emptyList(),
            rangeStartUtcMillis = nowUtcMillis - 7 * 24 * 3_600_000L,
            rangeEndUtcMillis = nowUtcMillis
        )
        return LivenessUiSnapshot(
            hasData = false,
            conclusion = summary.conclusion,
            lastHeartbeatAtUtcMillis = null,
            coverageByHourText = CoverageFormatting.formatByHour(summary),
            coverageByExpectedHeartbeatText = CoverageFormatting.formatByExpectedHeartbeat(summary),
            coverageByHourDefinition = LivenessRules.COVERAGE_BY_HOUR_DEFINITION,
            coverageByExpectedHeartbeatDefinition = LivenessRules.COVERAGE_BY_EXPECTED_HEARTBEAT_DEFINITION,
            // AC-1.3：即使"没有死亡记录"也要显示未知 + 推断依据，不给"无异常"式结论。
            latestCauseLabel = "未知",
            latestCauseInference = "本次区间内没有读到进程退出记录；这只说明系统没有留下记录，" +
                "不能据此判定设备没有异常。",
            staleNote = null,
            exitReasonSupported = true,
            exitReasonUnavailableReason = null
        )
    }

    private fun snapshot(hasData: Boolean, staleNote: String? = null) = LivenessUiSnapshot(
        hasData = hasData,
        conclusion = if (hasData) {
            "存活有缺口：最长静默 135 分钟，区间内共 1 段 ≥30 分钟静默。"
        } else {
            LivenessRules.NO_DATA_CONCLUSION
        },
        lastHeartbeatAtUtcMillis = if (hasData) System.currentTimeMillis() - 90 * 60_000L else null,
        coverageByHourText = if (hasData) "87.5%（7/8 小时）" else "—（0/168 小时）",
        coverageByExpectedHeartbeatText = if (hasData) "71.4%（20/28 次）" else "—（0/672 次）",
        coverageByHourDefinition = LivenessRules.COVERAGE_BY_HOUR_DEFINITION,
        coverageByExpectedHeartbeatDefinition = LivenessRules.COVERAGE_BY_EXPECTED_HEARTBEAT_DEFINITION,
        latestCauseLabel = if (hasData) "内存不足被系统回收" else null,
        latestCauseInference = if (hasData) "系统未提供该次退出的原因。" else null,
        staleNote = staleNote,
        exitReasonSupported = true,
        exitReasonUnavailableReason = null
    )

    @Test
    fun livenessBlockShowsAllFourRequiredFacts() {
        // AC-8.1：结论、最近心搏、覆盖率、最近死因四项都要出现在状态页顶部。
        val state = mutableStateOf(normalState())
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state.value,
                    liveness = snapshot(hasData = true))
            }
        }

        composeTestRule.onNodeWithTag("status-liveness").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-liveness-conclusion").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-liveness-last-heartbeat").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-liveness-coverage-hour").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-liveness-coverage-heartbeat").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-liveness-last-cause").assertIsDisplayed().performScrollTo()
        composeTestRule.onNodeWithText("存活有缺口：最长静默 135 分钟，区间内共 1 段 ≥30 分钟静默。")
            .assertIsDisplayed()
    }

    @Test
    fun staleDataIsLabelledWithItsAge() {
        // AC-8.2：数据陈旧时必须标注"数据为 N 小时前"。
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    liveness = snapshot(hasData = true, staleNote = "数据为 2 小时前"))
            }
        }

        composeTestRule.onNodeWithTag("status-liveness-stale")
            .assertIsDisplayed()
            .performScrollTo()
        composeTestRule.onNodeWithText("数据为 2 小时前").assertIsDisplayed()
    }

    @Test
    fun withoutLocalDataTheBlockSaysNoDataAndNeverShowsFullCoverage() {
        // AC-8.2：离线且无本地数据时显示"无数据"，不得给出"无异常"结论，也不显示 100%。
        val now = System.currentTimeMillis()
        val empty = noDataSnapshot(now)
        // 先钉住"无数据时真实的格式化结果就是 —，且分母是可核对的数字"。
        assertTrue(empty.coverageByHourText.startsWith("—（0/"))
        assertTrue(empty.coverageByHourText.endsWith(" 小时）"))
        assertTrue(empty.coverageByExpectedHeartbeatText.startsWith("—（0/"))
        assertTrue(empty.coverageByExpectedHeartbeatText.endsWith(" 次）"))
        assertTrue(!empty.coverageByHourText.contains("100"))

        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(), liveness = empty)
            }
        }

        composeTestRule.onNodeWithText(LivenessRules.NO_DATA_CONCLUSION).assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-liveness-coverage-hour")
            .assertIsDisplayed()
            .assertTextContains(empty.coverageByHourText)
        composeTestRule.onNodeWithTag("status-liveness-coverage-heartbeat")
            .assertIsDisplayed()
            .assertTextContains(empty.coverageByExpectedHeartbeatText)
        composeTestRule.onNodeWithTag("status-liveness-last-cause")
            .assertIsDisplayed()
            .assertTextContains("未知")
        // 推断依据整行是"推断依据：…"，因此按子串断言（assertTextContains 默认是整段相等）。
        composeTestRule.onNodeWithTag("status-liveness-cause-inference")
            .assertIsDisplayed()
            .assertTextContains("不能据此判定设备没有异常", substring = true)
    }

    @Test
    fun droppedReasonsPageShowsCountsDetailsAndEmptyState() {
        // REQ-9：统计 + 最近 20 条明细；空态明确。
        val state = mutableStateOf<DroppedReasonUiState?>(null)
        val back = mutableStateOf(false)
        composeTestRule.setContent {
            PimTheme {
                DroppedReasonScreen(state = state.value, onBack = { back.value = true })
            }
        }

        composeTestRule.onNodeWithTag("dropped-reasons-loading").assertIsDisplayed()

        state.value = DroppedReasonUiState(
            totalCount = 15,
            byReason = listOf(
                DroppedReasonCountOnly("horizontal-accuracy-too-low", 12),
                DroppedReasonCountOnly("missing-horizontal-accuracy", 3)
            ),
            recentDetails = listOf(
                com.pim.app.data.DroppedDiagnosticExportRow(
                    recordedAtUtc = System.currentTimeMillis() - 60_000L,
                    provider = "gps",
                    accuracyMeters = 55f,
                    policyMode = "PowerSavingNormal",
                    reason = "horizontal-accuracy-too-low"
                )
            ),
            detailLimit = 20
        )
        composeTestRule.waitForIdle()

        composeTestRule.onNodeWithTag("dropped-reasons-total").assertIsDisplayed()
        composeTestRule.onNodeWithText("水平准确度不达标").assertIsDisplayed()
        composeTestRule.onNodeWithText("12 条").assertIsDisplayed()
        assertTrue(
            composeTestRule.onAllNodesWithText("水平准确度不达标").fetchSemanticsNodes().isNotEmpty()
        )

        composeTestRule.onNodeWithTag("dropped-reasons-back").performClick()
        assertTrue(back.value)
    }

    @Test
    fun droppedReasonsEmptyStateIsExplicit() {
        composeTestRule.setContent {
            PimTheme {
                DroppedReasonScreen(
                    state = DroppedReasonUiState(
                        totalCount = 0,
                        byReason = emptyList(),
                        recentDetails = emptyList(),
                        detailLimit = 20
                    ),
                    onBack = {}
                )
            }
        }

        composeTestRule.onNodeWithTag("dropped-reasons-empty").assertIsDisplayed()
        composeTestRule.onNodeWithText("暂无被丢弃的定位点。").assertIsDisplayed()
    }
}
