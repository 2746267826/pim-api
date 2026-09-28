package com.pim.app.ui.status

import androidx.activity.ComponentActivity
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.width
import androidx.compose.runtime.mutableStateOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.test.assertCountEquals
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.assertIsNotEnabled
import androidx.compose.ui.test.assertTextContains
import androidx.compose.ui.test.getUnclippedBoundsInRoot
import androidx.compose.ui.test.hasAnyDescendant
import androidx.compose.ui.test.hasTestTag
import androidx.compose.ui.test.hasText
import androidx.compose.ui.test.junit4.createAndroidComposeRule
import androidx.compose.ui.test.onAllNodesWithTag
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.onNodeWithText
import androidx.compose.ui.test.performClick
import androidx.compose.ui.test.performScrollTo
import androidx.compose.ui.unit.dp
import com.pim.app.status.ApiConnectionSnapshot
import com.pim.app.status.ConnectionProbeOutcome
import com.pim.app.status.ConnectionProbeResult
import com.pim.app.status.DiagnosticSnapshot
import com.pim.app.status.TrackingPolicySnapshot
import com.pim.app.status.ConnectionProbeStage
import com.pim.app.status.NetworkAvailability
import com.pim.app.status.PermissionStatusSnapshot
import com.pim.app.status.ServerCapabilities
import com.pim.app.status.StatusActionTarget
import com.pim.app.status.StatusCenterState
import com.pim.app.status.StatusIssue
import com.pim.app.status.StatusOverall
import com.pim.app.status.StatusSeverity
import com.pim.app.status.SyncPhase
import com.pim.app.ui.theme.PimTheme
import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Rule
import org.junit.Test

/** 仪表化测试用的冲刺概况（REQ-9：`StatusCenterContent` 的 sprintSummary 无默认值）。 */
private fun sprintSummaryForTest() = com.pim.app.location.sprint.SprintSummary(
    enabled = true,
    count = 0,
    countDisplay = com.pim.app.location.sprint.SprintCountDisplay.Value(0)
)

class StatusCenterScreenTest {
    @get:Rule
    val composeTestRule = createAndroidComposeRule<ComponentActivity>()

    @Test
    fun loadingShowsProgressAndDisablesSync() {
        val state = mutableStateOf(StatusCenterState.empty().copy(isLoading = true, syncPhase = SyncPhase.Idle))
        composeTestRule.setContent { PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state.value) } }

        composeTestRule.onNodeWithTag("status-loading").assertIsDisplayed()
        composeTestRule.onNodeWithText("正在读取状态").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-sync-button").assertIsNotEnabled()
    }

    @Test
    fun overallNormalShowsNormalText() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(syncPhase = SyncPhase.Idle)) }
        }
        composeTestRule.onNodeWithTag("status-overall").assertIsDisplayed()
        composeTestRule.onNodeWithText("正常").assertIsDisplayed()
    }

    @Test
    fun overallAttentionShowsAttentionText() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(overall = StatusOverall.Attention)) }
        }
        composeTestRule.onNodeWithText("需注意").assertIsDisplayed()
    }

    @Test
    fun overallAbnormalShowsAbnormalText() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(overall = StatusOverall.Abnormal)) }
        }
        composeTestRule.onNodeWithText("异常").assertIsDisplayed()
    }

    @Test
    fun syncPhaseRunningShowsRunningText() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(syncPhase = SyncPhase.Running)) }
        }
        composeTestRule.onNodeWithTag("status-sync-phase").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-sync-button").assertIsNotEnabled()
    }

    @Test
    fun transportFactsShowCountsAndTimes() {
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(
                        pendingTotal = 42,
                        acceptedCount = 5,
                        rejectedCount = 3,
                        permanentRejectedCount = 1,
                        lastSuccessfulUploadAt = "2026-07-14T10:00:00Z",
                        lastAttemptedUploadAt = "2026-07-14T10:05:00Z",
                        nextAttemptAtMillis = 60_000L
                    ))
            }
        }

        composeTestRule.onNodeWithTag("status-pending").assertIsDisplayed()
        composeTestRule.onNodeWithText("42").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-pending-location").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-confirmed").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-rejected").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-permanent-rejected").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-last-success").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-last-attempt").assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-next-attempt").assertIsDisplayed()
    }

    @Test
    fun transportShowsPlaceholdersWithoutHistoryOrSchedule() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState()) }
        }

        composeTestRule.onNode(
            hasTestTag("status-last-success") and hasAnyDescendant(hasText("暂无"))
        ).assertExists()
        composeTestRule.onNode(
            hasTestTag("status-last-attempt") and hasAnyDescendant(hasText("暂无"))
        ).assertExists()
        composeTestRule.onNode(
            hasTestTag("status-next-attempt") and hasAnyDescendant(hasText("未安排"))
        ).assertExists()
    }

    @Test
    fun collectionPermissionsNetworkProbeAndDiagnosticsAreVisible() {
        val probeResult = ConnectionProbeResult(
            outcome = ConnectionProbeOutcome.Reachable,
            checkedAtUtcMillis = 1_000L,
            lastCompletedStage = ConnectionProbeStage.WebRoot,
            latencyMillisByStage = emptyMap(),
            capabilities = ServerCapabilities(true, true),
            serverIdentity = "https://example/api/v1/"
        )
        val state = normalState().copy(
            snapshot = normalState().snapshot.copy(
                permissions = PermissionStatusSnapshot(
                    notificationGranted = true,
                    preciseLocationGranted = false,
                    backgroundLocationGranted = true,
                    usageAccessGranted = false,
                    activityRecognitionGranted = true,
                    batteryOptimizationGranted = false
                ),
                tracking = normalState().snapshot.tracking.copy(
                    nextExpectedLocationAtMillis = 1_752_488_200_000L
                ),
                diagnostics = normalState().snapshot.diagnostics.copy(
                    lastLogMessage = "手机同步已完成。",
                    recentLogMessages = emptyList()
                )
            ),
            lastProbeResult = probeResult,
            lastProbeCheckedAtMillis = 1_000L,
            networkAvailability = NetworkAvailability.Validated
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        composeTestRule.onNodeWithTag("status-permission-notification").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-permission-precise-location").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-permission-background-location").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-permission-usage-access").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-permission-activity-recognition").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-permission-battery-optimization").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-network").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-probe").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-diagnostics").performScrollTo().assertIsDisplayed()
        composeTestRule.onNodeWithTag("status-next-location").assertExists()
        composeTestRule.onNodeWithText("最近记录").assertExists()
        composeTestRule.onNodeWithText("有近期诊断记录").assertExists()
    }

    @Test
    fun connectionProbeShowsNotCheckedWhenNoEvidenceExists() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(lastProbeResult = null)) }
        }

        composeTestRule.onNode(
            hasTestTag("status-probe") and hasAnyDescendant(hasText("未检查"))
        ).performScrollTo().assertIsDisplayed()
    }

    @Test
    fun syncButtonIdleIsEnabled() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(syncPhase = SyncPhase.Idle, isLoading = false)) }
        }
        composeTestRule.onNodeWithTag("status-sync-button").assertIsEnabled()
    }

    @Test
    fun syncButtonWaitingAllowsOneTimeNetworkOverride() {
        var clickCount = 0
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(syncPhase = SyncPhase.Waiting, isLoading = false),
                    onSyncNow = { clickCount++ })
            }
        }

        composeTestRule.onNodeWithTag("status-sync-button")
            .assertIsEnabled()
            .assertTextContains("立即同步")
            .performClick()
        assertTrue("Expected waiting sync to remain actionable", clickCount == 1)
    }

    @Test
    fun syncButtonRunningIsDisabled() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(syncPhase = SyncPhase.Running, isLoading = false)) }
        }
        composeTestRule.onNodeWithTag("status-sync-button").assertIsNotEnabled()
    }

    @Test
    fun syncButtonFailedIsEnabled() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(syncPhase = SyncPhase.Failed, isLoading = false)) }
        }
        composeTestRule.onNodeWithTag("status-sync-button").assertIsEnabled()
    }

    @Test
    fun syncButtonClicksOnceForIdle() {
        var clickCount = 0
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(syncPhase = SyncPhase.Idle, isLoading = false),
                    onSyncNow = { clickCount++ })
            }
        }
        composeTestRule.onNodeWithTag("status-sync-button").performClick()
        assertTrue("Expected 1 click", clickCount == 1)
    }

    @Test
    fun syncButtonIdleEnabledEvenWithZeroPendingAndOffline() {
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState().copy(
                        syncPhase = SyncPhase.Idle,
                        pendingTotal = 0,
                        networkAvailability = NetworkAvailability.Unavailable,
                        isLoading = false
                    ))
            }
        }
        composeTestRule.onNodeWithTag("status-sync-button").assertIsEnabled()
    }

    @Test
    fun issueActionsAreExplicitAndNonActionableItemsHaveNoButton() {
        var actionClickCount = 0
        val actionableIssue = StatusIssue(
            code = "login-missing",
            severity = StatusSeverity.Critical,
            title = "未登录",
            message = "需要登录后才能同步。",
            actionLabel = "去登录",
            target = StatusActionTarget.Settings
        )
        val nonActionableIssue = StatusIssue.recentDroppedLocation("unknown", null)
        val state = normalState().copy(
            issues = listOf(actionableIssue, nonActionableIssue)
        )
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state,
                    onIssueAction = { actionClickCount++ })
            }
        }

        composeTestRule.onNodeWithTag("status-issue-login-missing").assertExists()
        composeTestRule.onNodeWithTag("status-issue-action-login-missing")
            .performScrollTo()
            .assertIsDisplayed()
            .performClick()
        assertTrue("Expected 1 action click", actionClickCount == 1)
        composeTestRule.onNodeWithTag("status-issue-location-dropped-recent").assertExists()
        composeTestRule.onNodeWithTag("status-issue-action-location-dropped-recent").assertDoesNotExist()
    }

    @Test
    fun feedbackProbeCheckingShowsChinese() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(), feedback = StatusActionFeedback.ProbeChecking) }
        }
        composeTestRule.onNode(
            hasTestTag("status-feedback") and hasAnyDescendant(hasText("检查中"))
        ).assertExists()
    }

    @Test
    fun feedbackProbeCompletedShowsChinese() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(), feedback = StatusActionFeedback.ProbeCompleted) }
        }
        composeTestRule.onNode(
            hasTestTag("status-feedback") and hasAnyDescendant(hasText("检查已完成"))
        ).assertExists()
    }

    @Test
    fun feedbackProbeFailedShowsChinese() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(), feedback = StatusActionFeedback.ProbeFailed) }
        }
        composeTestRule.onNode(
            hasTestTag("status-feedback") and hasAnyDescendant(hasText("检查未完成，请稍后重试"))
        ).assertExists()
    }

    @Test
    fun feedbackSyncSubmitFailedShowsChinese() {
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(), feedback = StatusActionFeedback.SyncSubmitFailed) }
        }
        composeTestRule.onNode(
            hasTestTag("status-feedback") and hasAnyDescendant(hasText("同步请求未能提交，请稍后重试"))
        ).assertExists()
    }

    @Test
    fun noRawReasonCodeOrProfileLeaksIntoDisplayedText() {
        val state = normalState().copy(
            snapshot = normalState().snapshot.copy(
                api = ApiConnectionSnapshot(
                    address = "https://invalid.example",
                    isValid = false,
                    reasonCode = "invalid-api-url",
                    warnings = emptySet()
                ),
                tracking = TrackingPolicySnapshot(
                    profile = "power-saving",
                    currentPolicyMode = "PowerSavingNormal",
                    nextExpectedLocationAtMillis = null
                ),
                diagnostics = DiagnosticSnapshot(
                    lastDroppedReason = "missing-horizontal-accuracy",
                    lastDroppedAtMillis = null,
                    lastLogMessage = "java.lang.RuntimeException",
                    lastHeartbeatStatus = "心跳上报成功",
                    recentLogMessages = listOf("raw log")
                )
            )
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        composeTestRule.onNode(
            hasTestTag("status-api-reason") and hasAnyDescendant(hasText("地址格式不正确"))
        ).assertExists()
        composeTestRule.onNodeWithText("invalid-api-url").assertDoesNotExist()

        // WO-ANDROID-POLICY-TRANSITION-20260928 REQ-4（D-1）：原「策略档位」「当前策略」
        // 两行合并为「当前状态」一行，值 = 档位 · 模式名 · 已持续 X。
        composeTestRule.onNode(
            hasTestTag("status-current-state") and
                hasAnyDescendant(hasText("省电 · 常规省电 · 已持续 未知"))
        ).assertExists()
        composeTestRule.onNodeWithText("power-saving").assertDoesNotExist()
        composeTestRule.onNodeWithText("PowerSavingNormal").assertDoesNotExist()

        composeTestRule.onNode(
            hasTestTag("status-dropped-reason") and hasAnyDescendant(hasText("缺少水平精度"))
        ).assertExists()
        composeTestRule.onNodeWithText("missing-horizontal-accuracy").assertDoesNotExist()

        composeTestRule.onNode(
            hasTestTag("status-heartbeat") and hasAnyDescendant(hasText("正常"))
        ).assertExists()
        composeTestRule.onNodeWithText("心跳上报成功").assertDoesNotExist()

        composeTestRule.onNode(
            hasTestTag("status-diagnostic-record") and hasAnyDescendant(hasText("有近期诊断记录"))
        ).assertExists()
        composeTestRule.onNodeWithText("java.lang.RuntimeException").assertDoesNotExist()
        composeTestRule.onNodeWithText("raw log").assertDoesNotExist()
    }

    @Test
    fun needAttentionSectionContainsOnlyCriticalAndWarning() {
        val state = normalState().copy(
            issues = listOf(
                StatusIssue.altitudeMissingTimeout(),
                StatusIssue.probeBlocked(),
                StatusIssue.usageAccessMissing(),
                StatusIssue.apiAddressMissing()
            )
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        val actionable = hasTestTag("status-actionable-issues")
        composeTestRule.onNode(
            actionable and hasAnyDescendant(hasTestTag("status-issue-connection-probe-blocked"))
        ).assertExists()
        composeTestRule.onNode(
            actionable and hasAnyDescendant(hasTestTag("status-issue-api-address-missing"))
        ).assertExists()
        composeTestRule.onNode(
            actionable and hasAnyDescendant(hasTestTag("status-issue-usage-access-missing"))
        ).assertExists()
        composeTestRule.onNode(
            actionable and hasAnyDescendant(hasTestTag("status-issue-altitude-missing-timeout"))
        ).assertDoesNotExist()
    }

    @Test
    fun statusInformationSectionAppearsWhenInfoExists() {
        val state = normalState().copy(
            issues = listOf(
                StatusIssue.altitudeMissingTimeout(),
                StatusIssue.recentDroppedLocation("x", null)
            )
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        val information = hasTestTag("status-information-issues")
        composeTestRule.onNode(
            information and hasAnyDescendant(hasText("状态信息"))
        ).assertExists()
        composeTestRule.onNode(
            information and hasAnyDescendant(hasTestTag("status-issue-altitude-missing-timeout"))
        ).assertExists()
        composeTestRule.onNode(
            information and hasAnyDescendant(hasTestTag("status-issue-location-dropped-recent"))
        ).assertExists()
    }

    @Test
    fun statusInformationSectionIsAbsentWhenNoInfoIssues() {
        val state = normalState().copy(
            issues = listOf(
                StatusIssue.probeBlocked(),
                StatusIssue.usageAccessMissing()
            )
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        composeTestRule.onNodeWithTag("status-information-issues").assertDoesNotExist()
    }

    @Test
    fun actionableCriticalIssuesAppearBeforeWarning() {
        val state = normalState().copy(
            issues = listOf(
                StatusIssue.usageAccessMissing(),
                StatusIssue.apiAddressMissing(),
                StatusIssue.heartbeatFailure(),
                StatusIssue.probeBlocked()
            )
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        composeTestRule.onNodeWithTag("status-issues").performScrollTo()
        val lastCriticalTop = composeTestRule
            .onNodeWithTag("status-issue-connection-probe-blocked")
            .getUnclippedBoundsInRoot()
            .top
        val firstWarningTop = composeTestRule
            .onNodeWithTag("status-issue-usage-access-missing")
            .getUnclippedBoundsInRoot()
            .top

        assertTrue(
            "Critical issues must be laid out before warning issues",
            lastCriticalTop < firstWarningTop
        )
    }

    @Test
    fun narrowStatusContentKeepsSyncCountsAndActionsVisible() {
        val narrowIssue = StatusIssue(
            code = "narrow-layout",
            severity = StatusSeverity.Critical,
            title = "需要处理一个较长的问题标题",
            message = "这里保留较长的问题说明，用来验证窄屏上的操作仍然可达。",
            actionLabel = "查看详细设置",
            target = StatusActionTarget.Settings
        )
        val state = normalState(syncPhase = SyncPhase.Waiting).copy(
            overall = StatusOverall.Abnormal,
            issues = listOf(narrowIssue),
            pendingTotal = Int.MAX_VALUE,
            acceptedCount = 123_456_789,
            rejectedCount = 98_765_432,
            permanentRejectedCount = 12_345_678
        )
        composeTestRule.setContent {
            PimTheme {
                Box(Modifier.width(320.dp).testTag("narrow-status-host")) {
                    StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state)
                }
            }
        }

        val topTags = listOf(
            "status-sync-phase",
            "status-sync-button",
            "status-pending",
            "status-confirmed",
            "status-rejected",
            "status-permanent-rejected"
        )
        val hostBounds = composeTestRule
            .onNodeWithTag("narrow-status-host")
            .getUnclippedBoundsInRoot()

        topTags.forEach { tag ->
            composeTestRule.onAllNodesWithTag(tag).assertCountEquals(1)
            val node = composeTestRule.onNodeWithTag(tag).assertIsDisplayed()
            val bounds = node.getUnclippedBoundsInRoot()
            assertTrue("$tag must stay inside the 320dp host", bounds.left >= hostBounds.left)
            assertTrue("$tag must stay inside the 320dp host", bounds.right <= hostBounds.right)
        }

        composeTestRule.onAllNodesWithTag("status-issue-action-narrow-layout").assertCountEquals(1)
        val action = composeTestRule
            .onNodeWithTag("status-issue-action-narrow-layout")
            .performScrollTo()
            .assertIsDisplayed()
        val actionBounds = action.getUnclippedBoundsInRoot()
        assertTrue(actionBounds.left >= hostBounds.left)
        assertTrue(actionBounds.right <= hostBounds.right)
    }

    @Test
    fun exportSwitchTogglesIncludeLocations() {
        var captured = false
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    exportState = DiagnosticExportUiState(includeRecentLocations = false),
                    onSetIncludeRecentLocations = { captured = it })
            }
        }
        composeTestRule.onNodeWithTag("status-diagnostics-export-option")
            .performScrollTo()
            .performClick()
        assertTrue(captured)
    }

    @Test
    fun exportButtonTriggersExport() {
        var clicked = false
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    onRequestDiagnosticExport = { clicked = true })
            }
        }
        composeTestRule.onNodeWithTag("status-diagnostics-export-button")
            .performScrollTo()
            .performClick()
        assertTrue(clicked)
    }

    @Test
    fun exportButtonDisabledDuringExport() {
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    exportState = DiagnosticExportUiState(isExporting = true))
            }
        }
        composeTestRule.onNodeWithTag("status-diagnostics-export-button")
            .performScrollTo()
            .assertIsNotEnabled()
    }

    @Test
    fun exportProgressShowsWhenExporting() {
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    exportState = DiagnosticExportUiState(isExporting = true))
            }
        }
        composeTestRule.onNodeWithTag("status-diagnostics-export-progress")
            .performScrollTo()
            .assertIsDisplayed()
    }

    @Test
    fun confirmationDialogConfirmTriggersCallback() {
        var confirmed = false
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    exportState = DiagnosticExportUiState(showLocationConfirmation = true),
                    onConfirmLocationExport = { confirmed = true })
            }
        }
        composeTestRule.onNodeWithTag("status-diagnostics-export-confirm-accept").performClick()
        assertTrue(confirmed)
    }

    @Test
    fun confirmationDialogDismissTriggersCallback() {
        var dismissed = false
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    exportState = DiagnosticExportUiState(showLocationConfirmation = true),
                    onDismissLocationConfirmation = { dismissed = true })
            }
        }
        composeTestRule.onNodeWithTag("status-diagnostics-export-confirm-cancel").performClick()
        assertTrue(dismissed)
    }

    @Test
    fun shareButtonTriggersShareWhenFileReady() {
        var shared = false
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    exportState = DiagnosticExportUiState(
                        exportedFile = File("/tmp/test.zip"),
                        feedback = DiagnosticExportFeedback.PackageReady
                    ),
                    onShareDiagnostic = { shared = true })
            }
        }
        composeTestRule.onNodeWithTag("status-diagnostics-export-share")
            .performScrollTo()
            .assertIsDisplayed()
            .performClick()
        assertTrue(shared)
    }

    @Test
    fun shareButtonRemainsAvailableAfterShareCannotOpen() {
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    exportState = DiagnosticExportUiState(
                        exportedFile = File("/tmp/test.zip"),
                        feedback = DiagnosticExportFeedback.ShareUnavailable
                    ))
            }
        }

        composeTestRule.onNodeWithTag("status-diagnostics-export-share")
            .performScrollTo()
            .assertIsDisplayed()
    }

    @Test
    fun exportFeedbackSurfaceShowsChinese() {
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    exportState = DiagnosticExportUiState(
                        feedback = DiagnosticExportFeedback.PackageReady
                    ))
            }
        }
        composeTestRule.onNodeWithTag("status-diagnostics-export-feedback")
            .performScrollTo()
            .assertIsDisplayed()
    }

    @Test
    fun meteredSyncConfirmationDialogShowsWhenFlagged() {
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    showMeteredSyncConfirmation = true)
            }
        }
        composeTestRule.onNodeWithTag("status-metered-sync-confirm").assertIsDisplayed()
        composeTestRule.onNodeWithText("使用移动数据同步？").assertIsDisplayed()
        composeTestRule.onNodeWithText("当前设置为仅限非流量网络同步。继续将允许本次同步使用移动数据，持久设置不变。").assertIsDisplayed()
    }

    @Test
    fun meteredSyncConfirmationDialogHiddenWhenNotFlagged() {
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    showMeteredSyncConfirmation = false)
            }
        }
        composeTestRule.onNodeWithTag("status-metered-sync-confirm").assertDoesNotExist()
    }

    @Test
    fun meteredSyncConfirmationConfirmTriggersCallback() {
        var confirmed = false
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    showMeteredSyncConfirmation = true,
                    onConfirmMeteredSync = { confirmed = true })
            }
        }
        composeTestRule.onNodeWithTag("status-metered-sync-confirm-accept").performClick()
        assertTrue(confirmed)
    }

    @Test
    fun meteredSyncConfirmationDismissTriggersCallback() {
        var dismissed = false
        composeTestRule.setContent {
            PimTheme {
                StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = normalState(),
                    showMeteredSyncConfirmation = true,
                    onDismissMeteredSyncConfirmation = { dismissed = true })
            }
        }
        composeTestRule.onNodeWithTag("status-metered-sync-confirm-cancel").performClick()
        assertTrue(dismissed)
    }

    // ── WO-ANDROID-POLICY-TRANSITION-20260928 REQ-4 / REQ-3 ─────────────────

    @Test
    fun currentStateAndLastTransitionAreExactlyTwoRows() {
        val transition = com.pim.app.status.PolicyTransitionSnapshot(
            fromMode = "PowerSavingNormal",
            toMode = "MotionObservation",
            reason = "检测到运动状态：步行",
            occurredAtMillis = java.time.Instant.parse("2026-09-28T02:00:00Z").toEpochMilli()
        )
        val state = normalState().copy(
            snapshot = normalState().snapshot.copy(
                tracking = TrackingPolicySnapshot(
                    profile = "standard",
                    currentPolicyMode = "MotionObservation",
                    nextExpectedLocationAtMillis = null
                ),
                latestPolicyTransition = transition,
                currentPolicyDurationMillis = (2L * 60L + 5L) * 60_000L
            )
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        // 只 1 行「当前状态」+ 只 1 行「上次切换」；不再有「最近 5 条切换」。
        composeTestRule.onAllNodesWithTag("status-current-state").assertCountEquals(1)
        composeTestRule.onAllNodesWithTag("status-policy-transition-last").assertCountEquals(1)
        composeTestRule.onNode(
            hasTestTag("status-current-state") and
                hasAnyDescendant(hasText("标准 · 运动观察 · 已持续 2 小时 5 分钟"))
        ).assertExists()
        composeTestRule.onNode(
            hasTestTag("status-policy-transition-last") and
                hasAnyDescendant(
                    hasText(
                        "常规省电 → 运动观察 · 检测到运动状态：步行",
                        substring = true
                    )
                )
        ).assertExists()
        // 旧的多条「策略切换」标签整体消失。
        composeTestRule.onAllNodesWithTag("status-policy-transition-0").assertCountEquals(0)
        composeTestRule.onAllNodesWithTag("status-policy-transition-4").assertCountEquals(0)
    }

    @Test
    fun emptyHistoryShowsUnknownDurationAndNoRecord() {
        val state = normalState().copy(
            snapshot = normalState().snapshot.copy(
                latestPolicyTransition = null,
                currentPolicyDurationMillis = null
            )
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        composeTestRule.onNode(
            hasTestTag("status-current-state") and
                hasAnyDescendant(hasText("已持续 未知", substring = true))
        ).assertExists()
        composeTestRule.onNode(
            hasTestTag("status-policy-transition-last") and
                hasAnyDescendant(hasText("暂无记录", substring = true))
        ).assertExists()
    }

    @Test
    fun writeFailureAppearsAsWarningInExistingIssuesSection() {
        val failedAt = java.time.Instant.parse("2026-09-28T04:30:00Z").toEpochMilli()
        val issue = StatusIssue.policyTransitionWriteFailure(
            consecutiveFailures = 3,
            lastFailureAtMillis = failedAt
        )
        val state = normalState().copy(issues = listOf(issue))
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        // 落在既有「需要处理」告警区，且仍然只有两行状态切换信息。
        composeTestRule.onNodeWithTag("status-actionable-issues").assertExists()
        composeTestRule.onNodeWithTag("status-issue-policy-transition-write-failure").assertExists()
        composeTestRule.onNode(
            hasTestTag("status-issue-policy-transition-write-failure") and
                hasAnyDescendant(
                    hasText(
                        com.pim.app.status.PolicyTransitionDisplay.writeFailureText(3, failedAt)
                    )
                )
        ).assertExists()
        composeTestRule.onNodeWithTag("status-issue-action-policy-transition-write-failure")
            .assertExists()
        composeTestRule.onAllNodesWithTag("status-current-state").assertCountEquals(1)
        composeTestRule.onAllNodesWithTag("status-policy-transition-last").assertCountEquals(1)
    }

    @Test
    fun existingPolicyAndScheduleRowsAreRetained() {
        // AC-4.4：撤回项检查——「策略原因 / 采集间隔 / 下次定位 / 日程缓存 / 上次成功 / 上次检查」
        // 六行在非空快照下仍必须存在（后三者为条件渲染）。
        val state = normalState().copy(
            snapshot = normalState().snapshot.copy(
                tracking = TrackingPolicySnapshot(
                    profile = "standard",
                    currentPolicyMode = "MotionObservation",
                    nextExpectedLocationAtMillis = 1_800_000_000_000L,
                    currentPolicyReason = "当前日程时段，降低定位频率",
                    requestIntervalMillis = 30_000L
                ),
                schedule = com.pim.app.status.ScheduleCacheStatusSnapshot(
                    freshness = com.pim.app.schedule.ScheduleCacheFreshness.Fresh,
                    hasCachedWindows = true,
                    lastSuccessAtMillis = 1_799_999_000_000L,
                    lastAttemptAtMillis = 1_799_999_500_000L,
                    lastError = null
                )
            )
        )
        composeTestRule.setContent {
            PimTheme { StatusCenterContent(sprintSummary = sprintSummaryForTest(), state = state) }
        }

        composeTestRule.onNodeWithTag("status-policy-reason").assertExists()
        composeTestRule.onNodeWithTag("status-policy-interval").assertExists()
        composeTestRule.onNodeWithTag("status-next-location").assertExists()
        composeTestRule.onNodeWithTag("status-schedule-freshness").assertExists()
        composeTestRule.onNodeWithTag("status-schedule-last-success").assertExists()
        composeTestRule.onNodeWithTag("status-schedule-last-attempt").assertExists()
        // 并且仍然只有两行状态切换信息。
        composeTestRule.onAllNodesWithTag("status-current-state").assertCountEquals(1)
        composeTestRule.onAllNodesWithTag("status-policy-transition-last").assertCountEquals(1)
    }

    private fun normalState(
        syncPhase: SyncPhase = SyncPhase.Idle
    ): StatusCenterState {
        return StatusCenterState.empty().copy(
            isLoading = false,
            overall = StatusOverall.Normal,
            syncPhase = syncPhase,
            snapshot = StatusCenterState.empty().snapshot
        )
    }
}
