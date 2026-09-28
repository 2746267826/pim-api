package com.pim.app.ui.settings

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.ui.Modifier
import androidx.compose.ui.test.SemanticsNodeInteraction
import androidx.compose.ui.test.assert
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import com.pim.app.location.PolicyTransitionWriteFailure
import com.pim.app.status.PolicyTransitionSnapshot
import com.pim.app.ui.theme.PimTheme
import java.time.Instant
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 **REQ-5**：「策略切换历史」板块的界面契约。
 *
 * 逐条对应验收：AC-5.1（空态逐字）/ AC-5.2（20 条 + 提示行 + 展开全部）/ AC-5.4（可见条数
 * 与窗口计数对齐）/ AC-5.5（四处文案逐字）/ REQ-3（板块顶部失败提示）。
 */
class PolicyTransitionHistorySectionTest {

    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    private fun transitions(count: Int): List<PolicyTransitionSnapshot> =
        (1..count).map { index ->
            PolicyTransitionSnapshot(
                fromMode = "PowerSavingNormal",
                toMode = "MotionObservation",
                reason = "第 $index 条",
                // 时间倒序：越靠前的行时间越新。
                occurredAtMillis = 1_800_000_000_000L - index * 60_000L
            )
        }

    private fun stateWith(
        rows: List<PolicyTransitionSnapshot>,
        expanded: Boolean = false,
        totalInWindow: Int = rows.size,
        failure: PolicyTransitionWriteFailure = PolicyTransitionWriteFailure.None
    ) = SettingsUiState(
        policyHistory = rows,
        policyHistoryTotalInWindow = totalInWindow,
        policyHistoryExpanded = expanded,
        policyTransitionWriteFailure = failure
    )

    private fun render(
        state: SettingsUiState,
        onExpand: () -> Unit = {}
    ) {
        composeTestRule.setContent {
            PimTheme {
                Column(modifier = Modifier.verticalScroll(rememberScrollState())) {
                    PolicyTransitionHistorySection(
                        state = state,
                        onExpandPolicyHistory = onExpand
                    )
                }
            }
        }
    }

    /**
     * 与真实页面同构的渲染：`expanded` 是状态，点击「展开全部」后**同一次组合**里重画，
     * 因此可以在一个用例里断言「20 条 → 25 条」。
     */
    private fun renderStateful(rows: List<PolicyTransitionSnapshot>, totalInWindow: Int = rows.size) {
        val expanded = androidx.compose.runtime.mutableStateOf(false)
        composeTestRule.setContent {
            PimTheme {
                Column(modifier = Modifier.verticalScroll(rememberScrollState())) {
                    PolicyTransitionHistorySection(
                        state = stateWith(
                            rows = rows,
                            expanded = expanded.value,
                            totalInWindow = totalInWindow
                        ),
                        onExpandPolicyHistory = { expanded.value = true }
                    )
                }
            }
        }
    }

    /** 断言节点存在，且其文本与期望**逐字相等**（不是包含）。 */
    private fun SemanticsNodeInteraction.assertExactText(expected: String) {
        assertExists()
        assert(hasText(expected))
    }

    @Test
    fun emptyHistoryShowsVerbatimEmptyStateAndRetentionNote() {
        render(stateWith(emptyList()))

        composeTestRule.onNodeWithText("策略切换历史").assertExists()
        composeTestRule.onNodeWithTag("settings-policy-history-empty")
            .assertExactText("暂无记录（修复后的新版本开始记录）")
        composeTestRule.onNodeWithTag("settings-policy-history-note")
            .assertExactText("本地保留 30 天")
        composeTestRule.onNodeWithTag("settings-policy-history-more").assertDoesNotExist()
        composeTestRule.onNodeWithTag("settings-policy-history-expand").assertDoesNotExist()
    }

    @Test
    fun defaultPageShowsTwentyRowsAndExpandingRevealsAll() {
        renderStateful(transitions(25))

        // 默认 20 条 + 底部提示行（AC-5.2 / AC-5.4）。
        composeTestRule.onNodeWithTag("settings-policy-history-row-19").assertExists()
        composeTestRule.onAllNodesWithTag("settings-policy-history-row-20").assertCountEquals(0)
        composeTestRule.onNodeWithTag("settings-policy-history-more")
            .assertExactText("仅显示最近 20 条 · 30 天内共 25 条")

        composeTestRule.onNodeWithTag("settings-policy-history-expand").performClick()

        // 同一次组合里点完之后：25 条全部可见，提示行消失（板块内展开，D-7）。
        composeTestRule.onNodeWithTag("settings-policy-history-row-24").assertExists()
        for (index in 0 until 25) {
            composeTestRule.onNodeWithTag("settings-policy-history-row-$index").assertExists()
        }
        composeTestRule.onNodeWithTag("settings-policy-history-more").assertDoesNotExist()
    }

    @Test
    fun expandedHistoryShowsEveryRowInsideTheSameSection() {
        val rows = transitions(25)
        render(stateWith(rows, expanded = true))

        for (index in 0 until 25) {
            composeTestRule.onNodeWithTag("settings-policy-history-row-$index").assertExists()
        }
        composeTestRule.onNodeWithTag("settings-policy-history-more").assertDoesNotExist()
        composeTestRule.onNodeWithTag("settings-policy-history-expand").assertDoesNotExist()
    }

    @Test
    fun rowsAreRenderedNewestFirstWithVerbatimFormat() {
        val newest = PolicyTransitionSnapshot(
            fromMode = "PowerSavingNormal",
            toMode = "MotionObservation",
            reason = "检测到运动状态：步行",
            occurredAtMillis = Instant.parse("2026-09-28T04:30:00Z").toEpochMilli()
        )
        val older = PolicyTransitionSnapshot(
            fromMode = "ScheduleLowFrequency",
            toMode = "PowerSavingNormal",
            reason = "默认省电档",
            occurredAtMillis = Instant.parse("2026-09-27T04:30:00Z").toEpochMilli()
        )
        render(stateWith(listOf(newest, older)))

        // 时间倒序（AC-5.3）：第 0 行比第 1 行新。
        assertTrue(newest.occurredAtMillis > older.occurredAtMillis)
        composeTestRule.onNodeWithTag("settings-policy-history-row-0")
            .assertExactText(com.pim.app.ui.status.formatPolicyTransition(newest))
        composeTestRule.onNodeWithTag("settings-policy-history-row-1")
            .assertExactText(com.pim.app.ui.status.formatPolicyTransition(older))
        composeTestRule.onNodeWithText("正常省电 → 运动观察").assertDoesNotExist()
    }

    @Test
    fun writeFailureIsShownAtTopOfSectionWithSameTextAsStatusPage() {
        val failedAt = Instant.parse("2026-09-28T04:30:00Z").toEpochMilli()
        render(
            stateWith(
                transitions(3),
                failure = PolicyTransitionWriteFailure(2, failedAt)
            )
        )

        composeTestRule.onNodeWithTag("settings-policy-history-failure")
            .assertExactText(
                com.pim.app.status.PolicyTransitionDisplay.writeFailureText(2, failedAt)
            )
    }

    @Test
    fun noFailureWarningWhenConsecutiveFailuresAreZero() {
        render(stateWith(transitions(3)))
        composeTestRule.onNodeWithTag("settings-policy-history-failure").assertDoesNotExist()
    }
}
