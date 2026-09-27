package com.pim.app.ui.status

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.selection.toggleable
import androidx.compose.material.icons.Icons
import java.io.File
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.CloudOff
import androidx.compose.material.icons.filled.CloudSync
import androidx.compose.material.icons.filled.FileDownload
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.NetworkCheck
import androidx.compose.material.icons.filled.Sensors
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.Shield
import androidx.compose.material.icons.filled.Sync
import androidx.compose.material.icons.filled.Warning
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Divider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.key
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.pim.app.forensics.LivenessUiSnapshot
import com.pim.app.status.ConnectionProbeResult
import com.pim.app.status.DiagnosticSnapshot
import com.pim.app.status.NetworkAvailability
import com.pim.app.status.NetworkSettingsNavigator
import com.pim.app.status.PermissionStatusSnapshot
import com.pim.app.status.PolicyTransitionSnapshot
import com.pim.app.status.ScheduleCacheStatusSnapshot
import com.pim.app.status.StatusActionRoute
import com.pim.app.status.StatusActionRouter
import com.pim.app.status.StatusActionTarget
import com.pim.app.status.StatusCenterState
import com.pim.app.status.StatusDisplayText
import com.pim.app.status.StatusIssue
import com.pim.app.status.StatusOverall
import com.pim.app.status.StatusPermissionNavigator
import com.pim.app.status.StatusSeverity
import com.pim.app.status.SyncPhase
import com.pim.app.status.actionableStatusIssues
import com.pim.app.status.informationalStatusIssues
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@Composable
fun StatusCenterScreen(
    modifier: Modifier = Modifier,
    onOpenSettings: () -> Unit = {},
    viewModel: StatusCenterViewModel = hiltViewModel(),
    keepAliveViewModel: com.pim.app.keepalive.ui.KeepAliveViewModel = hiltViewModel()
) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current

    LaunchedEffect(lifecycleOwner, viewModel) {
        lifecycleOwner.lifecycle.repeatConnectionProbePolling {
            viewModel.refreshConnectionForVisibleScreen()
        }
    }
    val state by viewModel.state.collectAsStateWithLifecycle()
    val liveness by viewModel.liveness.collectAsStateWithLifecycle()
    val sprintSummary by viewModel.sprintSummary.collectAsStateWithLifecycle()
    val droppedReasons by viewModel.droppedReasons.collectAsStateWithLifecycle()
    val showDroppedReasons by viewModel.showDroppedReasons.collectAsStateWithLifecycle()
    val feedback by viewModel.feedback.collectAsStateWithLifecycle()
    val exportState by viewModel.exportState.collectAsStateWithLifecycle()
    val showMeteredSyncConfirmation by viewModel.showMeteredSyncConfirmation.collectAsStateWithLifecycle()
    var showGuidance by rememberSaveable { mutableStateOf(false) }
    // REQ-21：红点状态取自持久化设置（进程重启后仍在），进入状态页时刷新一次。
    val keepAliveAlert by keepAliveViewModel.healthAlert.collectAsStateWithLifecycle()
    LaunchedEffect(lifecycleOwner) {
        keepAliveViewModel.refresh()
    }
    if (showDroppedReasons) {
        DroppedReasonScreen(
            state = droppedReasons,
            onBack = { viewModel.closeDroppedReasons() },
            modifier = modifier
        )
        return
    }

    // AC-23.1：状态页是引导页的**入口之一**（另一处在设置页）。
    // 顶部红点点亮时用户最可能从这里进来，因此入口就放在存活区块旁。
    if (showGuidance) {
        val guidance by keepAliveViewModel.guidanceState.collectAsStateWithLifecycle()
        LaunchedEffect(Unit) { keepAliveViewModel.refresh() }
        com.pim.app.keepalive.ui.ColorOsGuidanceScreen(
            state = guidance,
            onOpenSettings = keepAliveViewModel::openSettingsFor,
            onToggleManual = keepAliveViewModel::setManualCompleted,
            onBack = { showGuidance = false },
            modifier = modifier
        )
        return
    }

    StatusCenterContent(
        state = state,
        feedback = feedback,
        liveness = liveness,
        // REQ-9：冲刺概况必须**真正传下去**。漏传会静默落到默认的 Empty，
        // 页面就会一直显示「暂无」——看起来像「没有采集数据」，而不是「没接线」。
        sprintSummary = sprintSummary,
        keepAliveHealthAlert = keepAliveAlert,
        onOpenDroppedReasons = { viewModel.openDroppedReasons() },
        onOpenGuidance = { showGuidance = true },
        modifier = modifier,
        onIssueAction = { issue ->
            when (StatusActionRouter.route(viewModel.onIssueAction(issue))) {
                StatusActionRoute.OpenSettings -> onOpenSettings()
                StatusActionRoute.OpenPermissions -> StatusPermissionNavigator.open(context, issue)
                StatusActionRoute.TriggerSync -> viewModel.syncNow()
                StatusActionRoute.OpenNetworkSettings -> {
                    NetworkSettingsNavigator.open(context)
                }
                StatusActionRoute.ConnectionCheck -> viewModel.forceConnectionProbe()
                StatusActionRoute.None -> Unit
            }
        },
        onSyncNow = { viewModel.syncNow() },
        exportState = exportState,
        onSetIncludeRecentLocations = { viewModel.setIncludeRecentLocations(it) },
        onRequestDiagnosticExport = { viewModel.requestDiagnosticExport() },
        onConfirmLocationExport = { viewModel.confirmLocationExport() },
        onDismissLocationConfirmation = { viewModel.dismissLocationConfirmation() },
        onShareDiagnostic = {
            val opened = DiagnosticShareLauncher.open(context, it)
            viewModel.reportShareResult(opened)
        },
        showMeteredSyncConfirmation = showMeteredSyncConfirmation,
        onConfirmMeteredSync = { viewModel.confirmMeteredSync() },
        onDismissMeteredSyncConfirmation = { viewModel.dismissMeteredSyncConfirmation() }
    )
}

@Composable
internal fun StatusCenterContent(
    state: StatusCenterState,
    /**
     * 冲刺概况（REQ-9）。
     *
     * **无默认值**：有默认值时漏传会静默显示「暂无」，把「没接线」伪装成
     * 「没有采集数据」（独立 review round 5 的 R5-1 正是这样漏掉的）。
     */
    sprintSummary: com.pim.app.location.sprint.SprintSummary,
    feedback: StatusActionFeedback? = null,
    liveness: LivenessUiSnapshot? = null,
    keepAliveHealthAlert: String? = null,
    onOpenDroppedReasons: () -> Unit = {},
    onOpenGuidance: () -> Unit = {},
    modifier: Modifier = Modifier,
    onIssueAction: (StatusIssue) -> Unit = {},
    onSyncNow: () -> Unit = {},
    exportState: DiagnosticExportUiState = DiagnosticExportUiState(),
    onSetIncludeRecentLocations: (Boolean) -> Unit = {},
    onRequestDiagnosticExport: () -> Unit = {},
    onConfirmLocationExport: () -> Unit = {},
    onDismissLocationConfirmation: () -> Unit = {},
    onShareDiagnostic: (File) -> Unit = {},
    showMeteredSyncConfirmation: Boolean = false,
    onConfirmMeteredSync: () -> Unit = {},
    onDismissMeteredSyncConfirmation: () -> Unit = {}
) {
    val snapshot = state.snapshot
    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        Text(
            "状态中心",
            style = MaterialTheme.typography.titleLarge,
            fontWeight = FontWeight.Bold
        )

        // REQ-21：保活健康红点放在**状态页最顶部**（工单第 6 节）。
        com.pim.app.keepalive.ui.KeepAliveHealthBanner(
            alertText = keepAliveHealthAlert,
            onOpenGuidance = onOpenGuidance
        )

        OverallStatusSurface(state)

        // 存活区块放在最顶部（REQ-8）：先回答"设备活没活"，再回答既有权限/同步状态。
        LivenessSection(
            snapshot = liveness,
            onOpenDroppedReasons = onOpenDroppedReasons,
            onOpenGuidance = onOpenGuidance
        )

        feedback?.let {
            FeedbackRow(it)
        }

        Divider()

        // REQ-9：状态页可见冲刺概况（开关当前状态 + 最近 24 小时冲刺次数）。
        SprintSection(sprintSummary)

        Divider()

        TransportSection(state, onSyncNow)

        Divider()

        CollectionAndConnectionSection(state)

        Divider()

        DiagnosticsSection(
            diagnostics = snapshot.diagnostics,
            exportState = exportState,
            onSetIncludeRecentLocations = onSetIncludeRecentLocations,
            onRequestDiagnosticExport = onRequestDiagnosticExport,
            onShareDiagnostic = onShareDiagnostic
        )

        Divider()

        IssuesSection(state.issues, onIssueAction)
    }

    if (exportState.showLocationConfirmation) {
        AlertDialog(
            onDismissRequest = onDismissLocationConfirmation,
            modifier = Modifier.testTag("status-diagnostics-export-confirm"),
            title = { Text("包含原始位置？") },
            text = {
                Text("此诊断包将包含最近 24 小时的原始位置记录，可能暴露精确行程。")
            },
            confirmButton = {
                TextButton(
                    onClick = onConfirmLocationExport,
                    modifier = Modifier.testTag("status-diagnostics-export-confirm-accept")
                ) {
                    Text("继续导出")
                }
            },
            dismissButton = {
                TextButton(
                    onClick = onDismissLocationConfirmation,
                    modifier = Modifier.testTag("status-diagnostics-export-confirm-cancel")
                ) {
                    Text("取消")
                }
            }
        )
    }

    if (showMeteredSyncConfirmation) {
        AlertDialog(
            onDismissRequest = onDismissMeteredSyncConfirmation,
            modifier = Modifier.testTag("status-metered-sync-confirm"),
            title = { Text("使用移动数据同步？") },
            text = {
                Text("当前设置为仅限非流量网络同步。继续将允许本次同步使用移动数据，持久设置不变。")
            },
            confirmButton = {
                TextButton(
                    onClick = onConfirmMeteredSync,
                    modifier = Modifier.testTag("status-metered-sync-confirm-accept")
                ) {
                    Text("继续同步")
                }
            },
            dismissButton = {
                TextButton(
                    onClick = onDismissMeteredSyncConfirmation,
                    modifier = Modifier.testTag("status-metered-sync-confirm-cancel")
                ) {
                    Text("取消")
                }
            }
        )
    }
}

@Composable
private fun FeedbackRow(feedback: StatusActionFeedback) {
    val text = when (feedback) {
        StatusActionFeedback.ProbeChecking -> "检查中"
        StatusActionFeedback.ProbeCompleted -> "检查已完成"
        StatusActionFeedback.ProbeFailed -> "检查未完成，请稍后重试"
        StatusActionFeedback.SyncSubmitFailed -> "同步请求未能提交，请稍后重试"
    }
    Surface(
        modifier = Modifier.fillMaxWidth().testTag("status-feedback"),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.secondaryContainer,
        tonalElevation = 2.dp
    ) {
        Text(
            text = text,
            modifier = Modifier.padding(12.dp),
            style = MaterialTheme.typography.bodyMedium
        )
    }
}

/**
 * 冲刺概况区块（REQ-9 / AC-9.1 / AC-9.2 / §5 页面清单）。
 *
 * 三态（正常 / 空 / 错误）与加载占位都必须可区分：
 * - 空态：最近 24 小时无任何采集数据 → 「暂无」；
 * - 有数据：如实显示「N 次」（含 0 次）并同时展示开关状态；
 * - 错误态：显示「读取失败」并保留上次值。
 */
@Composable
private fun SprintSection(summary: com.pim.app.location.sprint.SprintSummary) {
    Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
        SectionHeader("高频冲刺", Icons.Filled.Sensors)

        FactRow(
            label = "冲刺开关",
            value = when (summary.enabled) {
                true -> "已开启"
                false -> "已关闭"
                // §5 加载/错误态：开关必有值，读不到时显示占位而不是猜一个。
                null -> "—"
            },
            tag = "sprint-switch"
        )

        FactRow(
            label = "最近 24 小时冲刺次数",
            value = when (val display = summary.countDisplay) {
                is com.pim.app.location.sprint.SprintCountDisplay.Value -> "${display.count} 次"
                com.pim.app.location.sprint.SprintCountDisplay.Loading -> "—"
                com.pim.app.location.sprint.SprintCountDisplay.Empty -> "暂无"
                com.pim.app.location.sprint.SprintCountDisplay.Failed -> "读取失败"
            },
            tag = "sprint-count-24h"
        )

        // AC-2.2 / AC-3.1：最近一次窗口跨度直接可见，验收方无需导出诊断包即可核对
        // 「约 30 秒、不早退」。
        summary.lastWindowDurationMillis?.let { duration ->
            FactRow(
                label = "最近一次冲刺窗口",
                value = "${duration / 1000} 秒",
                tag = "sprint-last-window"
            )
        }

        Text(
            text = when (val display = summary.countDisplay) {
                com.pim.app.location.sprint.SprintCountDisplay.Loading ->
                    "正在读取本地冲刺台账。"
                is com.pim.app.location.sprint.SprintCountDisplay.Value -> {
                    if (display.count == 0) {
                        "最近 24 小时有采集数据，但一次冲刺都没执行。"
                    } else {
                        "每个采集周期会做一次最长 30 秒的高频取点。"
                    }
                }
                com.pim.app.location.sprint.SprintCountDisplay.Empty ->
                    "最近 24 小时没有采集数据，因此无法统计冲刺次数。"
                com.pim.app.location.sprint.SprintCountDisplay.Failed ->
                    summary.count?.let { "读取冲刺台账失败，上次成功读取为 $it 次。" }
                        ?: "读取冲刺台账失败。"
            },
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

@Composable
private fun OverallStatusSurface(state: StatusCenterState) {
    if (state.isLoading) {
        Surface(
            modifier = Modifier.fillMaxWidth().testTag("status-loading"),
            shape = RoundedCornerShape(8.dp),
            color = when (state.overall) {
                StatusOverall.Normal -> MaterialTheme.colorScheme.primaryContainer
                StatusOverall.Attention -> MaterialTheme.colorScheme.tertiaryContainer
                StatusOverall.Abnormal -> MaterialTheme.colorScheme.errorContainer
            },
            tonalElevation = 2.dp
        ) {
            Row(
                modifier = Modifier.padding(16.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                CircularProgressIndicator(modifier = Modifier.size(24.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(12.dp))
                Text("正在读取状态", style = MaterialTheme.typography.bodyMedium)
            }
        }
    } else {
        Surface(
            modifier = Modifier.fillMaxWidth().testTag("status-overall"),
            shape = RoundedCornerShape(8.dp),
            color = MaterialTheme.colorScheme.surfaceVariant,
            tonalElevation = 2.dp
        ) {
            Row(
                modifier = Modifier.padding(16.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Icon(
                    imageVector = when (state.overall) {
                        StatusOverall.Normal -> Icons.Filled.CheckCircle
                        StatusOverall.Attention -> Icons.Filled.Warning
                        StatusOverall.Abnormal -> Icons.Filled.CloudOff
                    },
                    contentDescription = null,
                    modifier = Modifier.size(28.dp),
                    tint = when (state.overall) {
                        StatusOverall.Normal -> MaterialTheme.colorScheme.primary
                        StatusOverall.Attention -> MaterialTheme.colorScheme.tertiary
                        StatusOverall.Abnormal -> MaterialTheme.colorScheme.error
                    }
                )
                Spacer(Modifier.width(12.dp))
                Column {
                    Text(
                        when (state.overall) {
                            StatusOverall.Normal -> "正常"
                            StatusOverall.Attention -> "需注意"
                            StatusOverall.Abnormal -> "异常"
                        },
                        style = MaterialTheme.typography.titleMedium,
                        fontWeight = FontWeight.SemiBold
                    )
                    Text(
                        summaryText(state),
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 2
                    )
                }
            }
        }
    }
}

private fun summaryText(state: StatusCenterState): String {
    val parts = mutableListOf<String>()
    val criticalCount = state.issues.count { it.severity == StatusSeverity.Critical }
    val warningCount = state.issues.count { it.severity == StatusSeverity.Warning }
    if (criticalCount > 0) parts += "${criticalCount}个阻塞"
    if (warningCount > 0) parts += "${warningCount}个警告"
    if (parts.isEmpty()) parts += "一切正常"
    return parts.joinToString("，")
}

@Composable
private fun TransportSection(
    state: StatusCenterState,
    onSyncNow: () -> Unit
) {
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        SectionHeader("数据传输", Icons.Filled.CloudSync)

        Row(
            verticalAlignment = Alignment.CenterVertically,
            modifier = Modifier.fillMaxWidth()
        ) {
            val displayLabel = if (state.syncPhase == SyncPhase.CatchingUp && state.pendingTotal > 0) {
                "正在补传（剩 ${state.pendingTotal} 条）"
            } else {
                syncPhaseLabel(state.syncPhase)
            }
            Text(
                text = displayLabel,
                modifier = Modifier.weight(1f).testTag("status-sync-phase"),
                style = MaterialTheme.typography.bodyMedium,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis
            )
            Button(
                onClick = onSyncNow,
                modifier = Modifier.width(144.dp).testTag("status-sync-button"),
                enabled = syncButtonEnabled(state)
            ) {
                Icon(Icons.Filled.Sync, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(4.dp))
                Text(
                    text = syncButtonLabel(state.syncPhase),
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            CountChip("待传总数", state.pendingTotal.toString(), "status-pending", Modifier.weight(1f))
            CountChip("定位待传", state.snapshot.queues.pendingLocationPoints.toString(), "status-pending-location", Modifier.weight(1f))
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.SpaceBetween
        ) {
            CountChip("本轮确认", state.acceptedCount.toString(), "status-confirmed", Modifier.weight(1f))
            CountChip("本轮拒绝", state.rejectedCount.toString(), "status-rejected", Modifier.weight(1f))
            CountChip("永久拒绝", state.permanentRejectedCount.toString(), "status-permanent-rejected", Modifier.weight(1f))
        }

        FactRow("上次成功", formatRecordedTime(state.lastSuccessfulUploadAt), "status-last-success")
        FactRow("上次尝试", formatRecordedTime(state.lastAttemptedUploadAt), "status-last-attempt")
        FactRow(
            "下次尝试",
            state.nextAttemptAtMillis?.takeIf { it > 0L }?.let(::formatEpochMillis) ?: "未安排",
            "status-next-attempt"
        )
    }
}

internal fun syncPhaseLabel(phase: SyncPhase): String = when (phase) {
    SyncPhase.Idle -> "当前空闲"
    SyncPhase.Accepted -> "请求已接受"
    SyncPhase.Waiting -> "等待网络或系统调度"
    SyncPhase.Running -> "同步中"
    SyncPhase.CatchingUp -> "补传中"
    SyncPhase.Blocked -> "同步条件未满足"
    SyncPhase.Completed -> "同步已完成"
    SyncPhase.Failed -> "同步失败"
    SyncPhase.Cancelled -> "已取消"
}

internal fun syncButtonLabel(phase: SyncPhase): String = when (phase) {
    SyncPhase.Idle -> "立即同步"
    SyncPhase.Accepted -> "请求已接受"
    SyncPhase.Waiting -> "立即同步"
    SyncPhase.Running -> "同步中"
    SyncPhase.CatchingUp -> "补传中"
    SyncPhase.Blocked -> "暂不可同步"
    SyncPhase.Completed -> "再次同步"
    SyncPhase.Failed -> "重新同步"
    SyncPhase.Cancelled -> "再次同步"
}

private fun syncButtonEnabled(phase: SyncPhase): Boolean = when (phase) {
    SyncPhase.Idle, SyncPhase.Waiting, SyncPhase.Completed, SyncPhase.Failed, SyncPhase.Cancelled -> true
    SyncPhase.Accepted, SyncPhase.Running, SyncPhase.CatchingUp, SyncPhase.Blocked -> false
}

internal fun syncButtonEnabled(state: StatusCenterState): Boolean {
    if (state.isLoading) return false
    return syncButtonEnabled(state.syncPhase)
}

@Composable
private fun CollectionAndConnectionSection(state: StatusCenterState) {
    val snap = state.snapshot
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        SectionHeader("采集与连接", Icons.Filled.Sensors)

        FactRow("服务器地址", snap.api.address.ifBlank { "未配置" })
        FactRow(
            "地址状态",
            if (snap.api.isValid) "格式可用" else StatusDisplayText.apiReason(snap.api.reasonCode),
            "status-api-reason"
        )
        FactRow("登录状态",
            if (snap.auth.hasAccessToken && !snap.auth.isExpired) "已登录"
            else if (snap.auth.hasAccessToken) "已过期"
            else "未登录"
        )
        FactRow("持续采集", if (snap.service.continuousCollectionEnabled) "已开启" else "未开启")
        FactRow("服务运行", if (snap.service.serviceRunning) "运行中" else "已停止")
        FactRow("策略档位", StatusDisplayText.profile(snap.tracking.profile), "status-tracking-profile")
        FactRow("当前策略", StatusDisplayText.policyMode(snap.tracking.currentPolicyMode), "status-policy-mode")
        FactRow(
            "下次定位",
            snap.tracking.nextExpectedLocationAtMillis?.takeIf { it > 0L }?.let(::formatEpochMillis) ?: "未安排",
            "status-next-location"
        )

        FactRow("策略原因", StatusDisplayText.scheduleReason(snap.tracking.currentPolicyReason), "status-policy-reason")
        snap.tracking.requestIntervalMillis?.let { interval ->
            FactRow("采集间隔", formatPolicyInterval(interval), "status-policy-interval")
        }

        ScheduleFactsSection(snap.schedule, snap.recentPolicyTransitions)

        PermissionsSection(snap.permissions)

        FactRow("系统网络", when (state.networkAvailability) {
            NetworkAvailability.Unavailable -> "不可用"
            NetworkAvailability.Restricted -> "受限"
            NetworkAvailability.Validated -> "已验证"
        }, "status-network")

        ProbeSection(state.lastProbeResult, state.lastProbeCheckedAtMillis)
    }
}

@Composable
private fun PermissionsSection(permissions: PermissionStatusSnapshot) {
    PermissionRow("通知", permissions.notificationGranted, "status-permission-notification")
    PermissionRow("精确定位", permissions.preciseLocationGranted, "status-permission-precise-location")
    PermissionRow("后台定位", permissions.backgroundLocationGranted, "status-permission-background-location")
    PermissionRow("使用情况", permissions.usageAccessGranted, "status-permission-usage-access")
    PermissionRow("运动识别", permissions.activityRecognitionGranted, "status-permission-activity-recognition")
    PermissionRow("电池优化", permissions.batteryOptimizationGranted, "status-permission-battery-optimization")
}

@Composable
private fun PermissionRow(label: String, granted: Boolean, tag: String) {
    FactRow("权限 $label", if (granted) "已就绪" else "未就绪", tag)
}

@Composable
private fun ScheduleFactsSection(
    schedule: ScheduleCacheStatusSnapshot,
    transitions: List<PolicyTransitionSnapshot>
) {
    Column(
        modifier = Modifier.testTag("status-schedule-facts"),
        verticalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        FactRow("日程缓存", StatusDisplayText.scheduleFreshness(schedule.freshness), "status-schedule-freshness")
        if (schedule.lastSuccessAtMillis != null && schedule.lastSuccessAtMillis > 0L) {
            FactRow("上次成功", formatEpochMillis(schedule.lastSuccessAtMillis), "status-schedule-last-success")
        }
        if (schedule.lastAttemptAtMillis != null && schedule.lastAttemptAtMillis > 0L) {
            FactRow("上次检查", formatEpochMillis(schedule.lastAttemptAtMillis), "status-schedule-last-attempt")
        }

        transitions.forEachIndexed { index, t ->
            FactRow(
                label = "策略切换",
                value = formatPolicyTransition(t),
                tag = "status-policy-transition-$index"
            )
        }
    }
}

internal fun formatPolicyTransition(
    transition: PolicyTransitionSnapshot,
    zoneId: ZoneId = ZoneId.systemDefault()
): String = buildString {
    append(formatEpochMillis(transition.occurredAtMillis, zoneId))
    append(" · ")
    append(StatusDisplayText.policyMode(transition.fromMode))
    append(" → ")
    append(StatusDisplayText.policyMode(transition.toMode))
    append(" · ")
    append(StatusDisplayText.scheduleReason(transition.reason))
}

internal fun formatPolicyInterval(millis: Long): String {
    if (millis <= 0L) return "未安排"
    val minutes = millis / 60_000L
    val seconds = (millis % 60_000L) / 1_000L
    return when {
        minutes > 0L && seconds > 0L -> "${minutes}分${seconds}秒"
        minutes > 0L -> "${minutes} 分钟"
        else -> "${seconds} 秒"
    }
}

@Composable
private fun ProbeSection(result: ConnectionProbeResult?, checkedAtMillis: Long?) {
    val outcomeText = when (result?.outcome) {
        com.pim.app.status.ConnectionProbeOutcome.Reachable -> "可达"
        com.pim.app.status.ConnectionProbeOutcome.Partial -> "部分可达"
        com.pim.app.status.ConnectionProbeOutcome.Blocked -> "不可达"
        null -> "未检查"
    }
    Column(modifier = Modifier.testTag("status-probe")) {
        FactRow("PIM 服务器", outcomeText)
        if (!result?.safeMessage.isNullOrBlank()) {
            FactRow("探测信息", result?.safeMessage.orEmpty())
        }
        val checkedAt = checkedAtMillis ?: result?.checkedAtUtcMillis
        if (checkedAt != null && checkedAt > 0L) {
            FactRow("检查时间", formatEpochMillis(checkedAt))
        }
    }
}

@Composable
private fun DiagnosticsSection(
    diagnostics: DiagnosticSnapshot,
    exportState: DiagnosticExportUiState = DiagnosticExportUiState(),
    onSetIncludeRecentLocations: (Boolean) -> Unit = {},
    onRequestDiagnosticExport: () -> Unit = {},
    onShareDiagnostic: (File) -> Unit = {}
) {
    Column(modifier = Modifier.testTag("status-diagnostics"), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        SectionHeader("诊断摘要", Icons.Filled.Info)

        FactRow("最近丢弃", StatusDisplayText.droppedReason(diagnostics.lastDroppedReason), "status-dropped-reason")
        FactRow("心跳", StatusDisplayText.heartbeat(diagnostics.lastHeartbeatStatus), "status-heartbeat")

        val hasRecentLogs = diagnostics.recentLogMessages.isNotEmpty() ||
            !diagnostics.lastLogMessage.isNullOrBlank()
        if (hasRecentLogs) {
            FactRow("最近记录", "有近期诊断记录", "status-diagnostic-record")
        } else {
            FactRow("最近记录", "无", "status-diagnostic-record")
        }

        Row(
            modifier = Modifier
                .fillMaxWidth()
                .toggleable(
                    value = exportState.includeRecentLocations,
                    enabled = !exportState.isExporting,
                    role = Role.Switch,
                    onValueChange = onSetIncludeRecentLocations
                )
                .testTag("status-diagnostics-export-option"),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                "包含最近 24 小时原始位置",
                modifier = Modifier.weight(1f),
                style = MaterialTheme.typography.bodyMedium
            )
            Switch(
                checked = exportState.includeRecentLocations,
                onCheckedChange = null,
                enabled = !exportState.isExporting
            )
        }

        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(8.dp)
        ) {
            OutlinedButton(
                onClick = onRequestDiagnosticExport,
                modifier = Modifier
                    .weight(1f)
                    .testTag("status-diagnostics-export-button"),
                enabled = !exportState.isExporting
            ) {
                if (exportState.isExporting) {
                    CircularProgressIndicator(
                        modifier = Modifier
                            .size(18.dp)
                            .testTag("status-diagnostics-export-progress"),
                        strokeWidth = 2.dp
                    )
                    Spacer(Modifier.width(4.dp))
                } else {
                    Icon(Icons.Filled.FileDownload, contentDescription = null, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(4.dp))
                }
                Text(if (exportState.isExporting) "导出中" else "导出诊断")
            }
        }

        exportState.feedback?.let { feedback ->
            ExportFeedbackSurface(feedback, exportState.coordinateCount)
        }

        exportState.exportedFile?.takeIf { !exportState.isExporting }?.let { file ->
            Button(
                onClick = { onShareDiagnostic(file) },
                modifier = Modifier
                    .fillMaxWidth()
                    .testTag("status-diagnostics-export-share")
            ) {
                Icon(Icons.Filled.Share, contentDescription = null, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(4.dp))
                Text("分享")
            }
        }
    }
}

@Composable
private fun ExportFeedbackSurface(
    feedback: DiagnosticExportFeedback,
    coordinateCount: Int
) {
    val text = when (feedback) {
        DiagnosticExportFeedback.PackageReady -> "诊断包已生成"
        DiagnosticExportFeedback.PackageReadyWithLocations ->
            "诊断包已生成，包含 ${coordinateCount} 条原始位置记录"
        DiagnosticExportFeedback.ShareOpened -> "已打开系统分享"
        DiagnosticExportFeedback.ShareUnavailable -> "无法打开系统分享"
        DiagnosticExportFeedback.InsufficientStorage -> "存储空间不足，无法生成诊断包"
        DiagnosticExportFeedback.ExportFailed -> "导出失败，请重试"
    }
    Surface(
        modifier = Modifier.fillMaxWidth().testTag("status-diagnostics-export-feedback"),
        shape = RoundedCornerShape(8.dp),
        color = MaterialTheme.colorScheme.secondaryContainer,
        tonalElevation = 2.dp
    ) {
        Text(
            text = text,
            modifier = Modifier.padding(12.dp),
            style = MaterialTheme.typography.bodyMedium
        )
    }
}

@Composable
private fun IssuesSection(
    issues: List<StatusIssue>,
    onIssueAction: (StatusIssue) -> Unit
) {
    val actionable = actionableStatusIssues(issues)
    val information = informationalStatusIssues(issues)

    Column(
        modifier = Modifier.testTag("status-issues"),
        verticalArrangement = Arrangement.spacedBy(6.dp)
    ) {
        Column(
            modifier = Modifier.testTag("status-actionable-issues"),
            verticalArrangement = Arrangement.spacedBy(6.dp)
        ) {
            SectionHeader("需要处理", Icons.Filled.Shield)
            if (actionable.isEmpty()) {
                Text(
                    "未发现需要处理的问题",
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            } else {
                actionable.forEach { issue ->
                    key(issue.code) {
                        StatusIssueRow(issue, onAction = { onIssueAction(issue) })
                    }
                }
            }
        }

        if (information.isNotEmpty()) {
            Divider()
            Column(
                modifier = Modifier.testTag("status-information-issues"),
                verticalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                SectionHeader("状态信息", Icons.Filled.Info)
                information.forEach { issue ->
                    key(issue.code) {
                        StatusIssueRow(issue, onAction = { onIssueAction(issue) })
                    }
                }
            }
        }
    }
}

@Composable
internal fun StatusIssueRow(
    issue: StatusIssue,
    onAction: () -> Unit
) {
    Column(
        modifier = Modifier.fillMaxWidth().testTag("status-issue-${issue.code}")
    ) {
        Row(
            modifier = Modifier.fillMaxWidth(),
            verticalAlignment = Alignment.Top
        ) {
            Icon(
                imageVector = when (issue.severity) {
                    StatusSeverity.Critical -> Icons.Filled.Warning
                    StatusSeverity.Warning -> Icons.Filled.Warning
                    StatusSeverity.Info -> Icons.Filled.Info
                },
                contentDescription = null,
                modifier = Modifier.size(20.dp).padding(top = 2.dp),
                tint = when (issue.severity) {
                    StatusSeverity.Critical -> MaterialTheme.colorScheme.error
                    StatusSeverity.Warning -> MaterialTheme.colorScheme.tertiary
                    StatusSeverity.Info -> MaterialTheme.colorScheme.onSurfaceVariant
                }
            )
            Spacer(Modifier.width(8.dp))
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    issue.title,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.SemiBold
                )
                Text(
                    issue.message,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
        if (issue.target != StatusActionTarget.None) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.End
            ) {
                TextButton(
                    onClick = onAction,
                    modifier = Modifier.testTag("status-issue-action-${issue.code}")
                ) {
                    Icon(issueActionIcon(issue.target), contentDescription = null, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(4.dp))
                    Text(issue.actionLabel)
                }
            }
        }
    }
}

@Composable
private fun SectionHeader(title: String, icon: androidx.compose.ui.graphics.vector.ImageVector) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(icon, contentDescription = null, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(6.dp))
        Text(title, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold)
    }
}

@Composable
private fun FactRow(label: String, value: String, tag: String? = null) {
    Row(
        modifier = Modifier.fillMaxWidth().let { m ->
            if (tag != null) m.testTag(tag) else m
        },
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Text(
            label,
            modifier = Modifier
                .widthIn(min = 72.dp, max = 112.dp)
                .padding(end = 12.dp),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Text(
            value,
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.bodySmall,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.End
        )
    }
}

@Composable
private fun CountChip(label: String, value: String, tag: String, modifier: Modifier = Modifier) {
    Column(
        horizontalAlignment = Alignment.CenterHorizontally,
        modifier = modifier.testTag(tag)
    ) {
        Text(
            value,
            style = MaterialTheme.typography.titleSmall,
            fontWeight = FontWeight.Bold,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
        Text(
            label,
            style = MaterialTheme.typography.labelSmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis
        )
    }
}

private fun issueActionIcon(target: StatusActionTarget) = when (target) {
    StatusActionTarget.Settings, StatusActionTarget.Login -> Icons.Filled.Settings
    StatusActionTarget.Permissions -> Icons.Filled.Shield
    StatusActionTarget.Sync, StatusActionTarget.Queue -> Icons.Filled.Sync
    StatusActionTarget.NetworkSettings -> Icons.Filled.Wifi
    StatusActionTarget.ConnectionCheck -> Icons.Filled.NetworkCheck
    StatusActionTarget.None -> Icons.Filled.Info
}

internal fun formatEpochMillis(
    millis: Long,
    zoneId: ZoneId = ZoneId.systemDefault()
): String {
    if (millis <= 0L) return "暂无"
    return runCatching {
        STATUS_TIME_FORMATTER.format(Instant.ofEpochMilli(millis).atZone(zoneId))
    }.getOrDefault("暂无")
}

private fun formatRecordedTime(value: String?): String {
    if (value.isNullOrBlank()) return "暂无"
    return runCatching {
        formatEpochMillis(Instant.parse(value).toEpochMilli())
    }.getOrDefault(value)
}

private val STATUS_TIME_FORMATTER: DateTimeFormatter = DateTimeFormatter.ofPattern("MM-dd HH:mm")
