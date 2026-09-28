package com.pim.app.ui.settings

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.ExpandLess
import androidx.compose.material.icons.filled.ExpandMore
import androidx.compose.material.icons.filled.KeyboardArrowRight
import androidx.compose.material.icons.filled.Login
import androidx.compose.material.icons.filled.Logout
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Restore
import androidx.compose.material.icons.filled.Save
import androidx.compose.material.icons.filled.Sync
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Divider
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Snackbar
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.withFrameNanos
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.material3.ExperimentalMaterial3Api
import com.pim.app.settings.TrackingPresetCatalog
import com.pim.app.status.PolicyTransitionDisplay
import com.pim.app.status.StatusPermissionNavigator
import com.pim.app.ui.status.formatPolicyTransition
import com.pim.app.keepalive.ui.ColorOsGuidanceScreen
import com.pim.app.keepalive.ui.KeepAliveSection
import com.pim.app.keepalive.ui.KeepAliveViewModel
import com.pim.app.ui.components.PimSection
import com.pim.app.ui.permissions.permissionSettingRows
import com.pim.app.ui.status.repeatConnectionProbePolling
import kotlinx.coroutines.CancellationException
import timber.log.Timber
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
fun SettingsScreen(
    modifier: Modifier = Modifier,
    /**
     * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-3（D-10）：状态页写入失败告警的动作入口
     * 会带着这个标记进入设置页，页面把它滚到「策略切换历史」板块（不新建二级页面）。
     */
    scrollToPolicyTransitionHistory: Boolean = false,
    onPolicyTransitionHistoryScrolled: () -> Unit = {},
    viewModel: SettingsViewModel = hiltViewModel(),
    keepAliveViewModel: KeepAliveViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val keepAliveState by keepAliveViewModel.sectionState.collectAsStateWithLifecycle()
    val guidanceState by keepAliveViewModel.guidanceState.collectAsStateWithLifecycle()
    var showGuidance by rememberSaveable { mutableStateOf(false) }
    var username by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }
    var advancedExpanded by rememberSaveable { mutableStateOf(false) }
    var showResetDialog by rememberSaveable { mutableStateOf(false) }
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val scrollState = rememberScrollState()
    val policyHistoryRequester = remember { BringIntoViewRequester() }

    // REQ-3（D-10）：从状态页告警的动作按钮进来时，把「策略切换历史」板块滚进视野。
    LaunchedEffect(scrollToPolicyTransitionHistory) {
        if (scrollToPolicyTransitionHistory) {
            // 等首帧：requester 要挂到节点上之后才能 bringIntoView。
            withFrameNanos { }
            withFrameNanos { }
            runCatching { policyHistoryRequester.bringIntoView() }
            onPolicyTransitionHistoryScrolled()
        }
    }

    // AC-23.2：每次进入引导页都重新读一次系统状态，避免显示上次进入时的旧读数。
    LaunchedEffect(showGuidance, lifecycleOwner) {
        if (showGuidance) keepAliveViewModel.refresh()
    }

    LaunchedEffect(lifecycleOwner, viewModel) {
        lifecycleOwner.lifecycle.repeatConnectionProbePolling {
            viewModel.refreshConnectionForVisibleScreen()
        }
    }
    LaunchedEffect(lifecycleOwner, viewModel) {
        while (true) {
            val delayMs = try {
                viewModel.refreshUpdateForVisibleScreen()
            } catch (e: Exception) {
                if (e is CancellationException) throw e
                Timber.w(e, "update poll failed")
                6 * 60 * 60 * 1000L
            }
            kotlinx.coroutines.delay(delayMs)
        }
    }

    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) {
                viewModel.onResume()
            }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    if (showGuidance) {
        ColorOsGuidanceScreen(
            state = guidanceState,
            onOpenSettings = keepAliveViewModel::openSettingsFor,
            onToggleManual = keepAliveViewModel::setManualCompleted,
            onBack = { showGuidance = false },
            modifier = modifier
        )
        return
    }

    if (showResetDialog) {
        AlertDialog(
            onDismissRequest = { showResetDialog = false },
            title = { Text("恢复默认设置") },
            text = { Text("确定恢复采集、网络和日志的默认设置吗？服务器地址和登录状态将保留。") },
            confirmButton = {
                TextButton(onClick = {
                    viewModel.resetOperationalDefaults()
                    showResetDialog = false
                }) {
                    Text("确定")
                }
            },
            dismissButton = {
                TextButton(onClick = { showResetDialog = false }) {
                    Text("取消")
                }
            }
        )
    }

    if (state.showClearDiagnosticsConfirmation) {
        AlertDialog(
            onDismissRequest = {
                if (!state.isBusy) viewModel.dismissClearDiagnosticsConfirmation()
            },
            modifier = Modifier.testTag("settings-diagnostics-confirm"),
            title = { Text("确认清除诊断数据？") },
            text = {
                Text(
                    "将清除本地诊断日志、诊断状态和导出文件，并一并清除策略切换历史。" +
                        "业务队列、服务器地址和登录状态不受影响。"
                )
            },
            confirmButton = {
                TextButton(
                    onClick = viewModel::confirmClearDiagnostics,
                    modifier = Modifier.testTag("settings-diagnostics-confirm-accept")
                ) {
                    Text("确认清除")
                }
            },
            dismissButton = {
                TextButton(
                    onClick = {
                        if (!state.isBusy) viewModel.dismissClearDiagnosticsConfirmation()
                    },
                    modifier = Modifier.testTag("settings-diagnostics-confirm-cancel")
                ) {
                    Text("取消")
                }
            }
        )
    }

    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(scrollState)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp)
    ) {
        Text("设置", style = MaterialTheme.typography.titleLarge)

        PimSection("API 地址") {
            OutlinedTextField(
                value = state.apiAddress,
                onValueChange = viewModel::updateApiAddress,
                modifier = Modifier.fillMaxWidth(),
                label = { Text("API 地址") },
                placeholder = { Text("https://pim.example.com/api/v1/") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.None)
            )
            if (state.apiWarnings.contains("real-device-localhost")) {
                Text(
                    text = "真机上的 127.0.0.1 指向手机本机，通常无法连接你的服务器。",
                    color = MaterialTheme.colorScheme.tertiary
                )
            }
            state.apiError?.let { reason ->
                Text("地址问题：$reason", color = MaterialTheme.colorScheme.error)
            }
            state.apiStatus?.let { Text(it, color = MaterialTheme.colorScheme.primary) }
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Button(onClick = { viewModel.saveApiAddress() }, modifier = Modifier.weight(1f)) {
                    Icon(Icons.Default.Save, contentDescription = null)
                    Spacer(Modifier.width(4.dp))
                    Text("保存")
                }
                OutlinedButton(onClick = viewModel::testConnection, modifier = Modifier.weight(1f)) {
                    Icon(Icons.Default.Sync, contentDescription = null)
                    Spacer(Modifier.width(4.dp))
                    Text("测试连接")
                }
            }
        }

        PimSection("账号") {
            if (state.isLoggedIn) {
                Text("当前状态：已登录", color = MaterialTheme.colorScheme.primary)
                OutlinedButton(onClick = viewModel::logout, modifier = Modifier.fillMaxWidth()) {
                    Icon(Icons.Default.Logout, contentDescription = null)
                    Spacer(Modifier.width(4.dp))
                    Text("退出登录")
                }
            } else {
                Text("当前状态：未登录")
                OutlinedTextField(
                    value = username,
                    onValueChange = { username = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("用户名") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.None)
                )
                OutlinedTextField(
                    value = password,
                    onValueChange = { password = it },
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("密码") },
                    singleLine = true,
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.None)
                )
                Button(
                    onClick = { viewModel.login(username, password) },
                    modifier = Modifier.fillMaxWidth(),
                    enabled = !state.isBusy && username.isNotBlank() && password.isNotBlank()
                ) {
                    Icon(Icons.Default.Login, contentDescription = null)
                    Spacer(Modifier.width(4.dp))
                    Text(if (state.isBusy) "登录中" else "登录")
                }
            }
            state.loginStatus?.let { Text(it) }
        }

        PimSection("持续采集") {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("持续采集")
                Switch(
                    checked = state.continuousCollectionEnabled,
                    onCheckedChange = viewModel::setContinuousCollectionEnabled
                )
            }
            Text(
                text = if (state.continuousCollectionEnabled) "当前：已开启" else "当前：已关闭",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            state.collectionStatus?.let { Text(it) }
        }

        PimSection("高频冲刺") {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("定位冲刺")
                Switch(
                    checked = state.sprintEnabled,
                    onCheckedChange = viewModel::setSprintEnabled
                )
            }
            Text(
                text = if (state.sprintEnabled) {
                    "当前：已开启。每个采集周期会做一次最长 30 秒的高频取点。"
                } else {
                    "当前：已关闭。不会再发起任何冲刺。"
                },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            Text(
                text = "开关切换后立即生效，不需要重启应用或采集服务。",
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        PimSection("采集预设") {
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                TrackingPresetCatalog.presets.forEach { preset ->
                    FilterChip(
                        selected = state.trackingProfile == preset.id,
                        onClick = { viewModel.applyTrackingPreset(preset.id) },
                        label = { Text(preset.displayName) }
                    )
                }
            }
        }

        PimSection("高级参数") {
            Row(
                modifier = Modifier.fillMaxWidth().clickable { advancedExpanded = !advancedExpanded },
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("高级参数", style = MaterialTheme.typography.titleSmall)
                Spacer(Modifier.weight(1f))
                Icon(
                    if (advancedExpanded) Icons.Default.ExpandLess else Icons.Default.ExpandMore,
                    contentDescription = if (advancedExpanded) "收起" else "展开"
                )
            }
            if (advancedExpanded) {
                OutlinedTextField(
                    value = state.normalMinText,
                    onValueChange = viewModel::updateNormalMinText,
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("正常间隔（分钟）") },
                    singleLine = true,
                    isError = state.advancedErrors.containsKey("normalInterval"),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    supportingText = state.advancedErrors["normalInterval"]?.let { { Text(it) } }
                )
                OutlinedTextField(
                    value = state.scheduleMinText,
                    onValueChange = viewModel::updateScheduleMinText,
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("日程低频间隔（分钟）") },
                    singleLine = true,
                    isError = state.advancedErrors.containsKey("scheduleInterval"),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    supportingText = state.advancedErrors["scheduleInterval"]?.let { { Text(it) } }
                )
                OutlinedTextField(
                    value = state.movementSecText,
                    onValueChange = viewModel::updateMovementSecText,
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("运动观察间隔（秒）") },
                    singleLine = true,
                    isError = state.advancedErrors.containsKey("movementInterval"),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    supportingText = state.advancedErrors["movementInterval"]?.let { { Text(it) } }
                )
                OutlinedTextField(
                    value = state.recoveryMetersText,
                    onValueChange = viewModel::updateRecoveryMetersText,
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("恢复阈值（米）") },
                    singleLine = true,
                    isError = state.advancedErrors.containsKey("recoveryThreshold"),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    supportingText = state.advancedErrors["recoveryThreshold"]?.let { { Text(it) } }
                )
                OutlinedTextField(
                    value = state.altitudeSecText,
                    onValueChange = viewModel::updateAltitudeSecText,
                    modifier = Modifier.fillMaxWidth(),
                    label = { Text("高度等待（秒）") },
                    singleLine = true,
                    isError = state.advancedErrors.containsKey("altitudeWait"),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal),
                    supportingText = state.advancedErrors["altitudeWait"]?.let { { Text(it) } }
                )
                Button(
                    onClick = viewModel::saveAdvancedSettings,
                    modifier = Modifier.fillMaxWidth()
                ) {
                    Icon(Icons.Default.Save, contentDescription = null)
                    Spacer(Modifier.width(4.dp))
                    Text("保存高级参数")
                }
            }
        }

        PimSection("网络") {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("自动同步仅限非流量网络")
                Switch(
                    checked = state.syncOnUnmeteredOnly,
                    onCheckedChange = viewModel::setSyncOnUnmeteredOnly
                )
            }
        }

        PimSection("日志") {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text("详细日志")
                Switch(
                    checked = state.verboseLoggingEnabled,
                    onCheckedChange = viewModel::setVerboseLoggingEnabled
                )
            }
            if (state.verboseLoggingEnabled && state.verboseLoggingUntilUtcMillis != null) {
                val localTime = Instant.ofEpochMilli(state.verboseLoggingUntilUtcMillis!!)
                    .atZone(ZoneId.systemDefault())
                    .format(DateTimeFormatter.ofPattern("yyyy-MM-dd HH:mm:ss"))
                Text("自动关闭时间：$localTime")
            }
            Text("保留天数")
            Row(
                modifier = Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                listOf(1, 7, 14, 30).forEach { days ->
                    FilterChip(
                        selected = state.logRetentionDays == days,
                        onClick = { viewModel.setLogRetentionDays(days) },
                        label = { Text("${days}天") }
                    )
                }
            }
        }

        PimSection("诊断") {
            OutlinedButton(
                onClick = { viewModel.requestClearDiagnostics() },
                modifier = Modifier.fillMaxWidth().testTag("settings-diagnostics-clear"),
                enabled = !state.isBusy
            ) {
                if (state.isClearingDiagnostics) {
                    CircularProgressIndicator(modifier = Modifier.size(18.dp), strokeWidth = 2.dp)
                    Spacer(Modifier.width(8.dp))
                    Text("正在清理")
                } else {
                    Icon(Icons.Default.Delete, contentDescription = null)
                    Spacer(Modifier.width(4.dp))
                    Text("清除诊断数据")
                }
            }
            // REQ-5（D-6）：一并清空行为保持不变，但必须让用户看得见。
            Text(
                text = "将一并清除策略切换历史",
                modifier = Modifier.testTag("settings-diagnostics-clear-note"),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            state.diagnosticClearFeedback?.let { feedback ->
                Text(
                    modifier = Modifier.testTag("settings-diagnostics-feedback"),
                    text = when (feedback) {
                        DiagnosticClearFeedback.Cleared -> "诊断数据已清除"
                        DiagnosticClearFeedback.Failed -> "清理失败，请重试"
                    },
                    color = when (feedback) {
                        DiagnosticClearFeedback.Cleared -> MaterialTheme.colorScheme.primary
                        DiagnosticClearFeedback.Failed -> MaterialTheme.colorScheme.error
                    }
                )
            }
        }

        // WO-ANDROID-POLICY-TRANSITION-20260928 REQ-5：位置在「诊断」板块**之后**（D-3 / AC-5.6）。
        PolicyTransitionHistorySection(
            state = state,
            onExpandPolicyHistory = viewModel::expandPolicyTransitionHistory,
            modifier = Modifier.bringIntoViewRequester(policyHistoryRequester)
        )

        KeepAliveSection(
            state = keepAliveState,
            onToggleEnabled = keepAliveViewModel::setEnabled,
            onIntervalChange = keepAliveViewModel::setIntervalMinutes,
            onOpenGuidance = { showGuidance = true },
            onOpenExactAlarmSettings = {
                keepAliveViewModel.openSettingsFor(
                    com.pim.app.keepalive.ColorOsGuidanceCatalog.EXACT_ALARM
                )
            }
        )

        PimSection("权限") {
            val rows = permissionSettingRows(state.permissions)
            rows.forEachIndexed { index, row ->
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .clickable { StatusPermissionNavigator.open(context, row.issueCode) }
                        .padding(vertical = 10.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column(Modifier.weight(1f)) {
                        Text(row.title)
                        Text(
                            text = if (row.isHardBlock) "采集必需" else "建议",
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    Text(
                        text = if (row.granted) "已授权" else "未授权",
                        color = if (row.granted) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error
                    )
                    Icon(
                        Icons.Default.KeyboardArrowRight,
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                if (index < rows.lastIndex) {
                    Divider()
                }
            }
        }

        PimSection("恢复默认") {
            OutlinedButton(
                onClick = { showResetDialog = true },
                modifier = Modifier.fillMaxWidth()
            ) {
                Icon(Icons.Default.Restore, contentDescription = null)
                Spacer(Modifier.width(4.dp))
                Text("恢复默认设置")
            }
        }

        PimSection("关于 PIM") {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.SpaceBetween
            ) {
                Text("关于 PIM")
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("${state.appVersion} (${state.versionCode})")
                    IconButton(onClick = {
                        val cm = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                        cm.setPrimaryClip(ClipData.newPlainText("PIM version", "PIM ${state.appVersion} sha=${state.gitSha}"))
                    }) {
                        Icon(Icons.Filled.ContentCopy, contentDescription = "复制版本信息")
                    }
                }
            }
            OutlinedButton(
                onClick = { viewModel.checkUpdateAsync() },
                modifier = Modifier.fillMaxWidth().testTag("settings-check-update")
            ) {
                Text("检查更新")
            }
            state.latestVersion?.let {
                Text(
                    text = "最新版本：$it",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            state.updateCheckedAt?.let {
                Text(
                    text = "检查时间：$it",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
            state.updateError?.let {
                Text(
                    text = it,
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.error
                )
            }
            if (!state.hasUpdate && state.latestVersion != null && state.updateError == null) {
                Text(
                    text = "已是最新版本",
                    style = MaterialTheme.typography.bodySmall,
                    color = MaterialTheme.colorScheme.primary
                )
            }
        }

        if (state.hasUpdate) {
            Snackbar(
                action = {
                    Button(onClick = {
                        val url = state.updateUrl
                        if (!url.isNullOrBlank()) {
                            try {
                                context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)))
                            } catch (_: Exception) {}
                        }
                    }) { Text("去下载") }
                }
            ) { Text("发现新版 v${state.latestVersion}") }
        }
    }
}

/** REQ-5（P2）：板块默认展示最近 20 条。 */
internal const val POLICY_HISTORY_PAGE_SIZE = 20

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-5：「策略切换历史」板块。
 *
 * - 内容：最近 **30 天**内全部切换记录，**时间倒序**（DAO 侧排序）；
 * - 行格式：`MM-dd HH:mm · 旧模式 → 新模式 · 原因`（与状态页共用 `formatPolicyTransition`）；
 * - 默认 20 条 + 底部 `仅显示最近 20 条 · 30 天内共 N 条` + `展开全部`（板块内展开，D-7）；
 * - 空态：`暂无记录（修复后的新版本开始记录）`（D-8）；
 * - 说明：`本地保留 30 天`（**不**写"与日志清理窗口一致"——日志窗口默认 7 天且可改）；
 * - 顶部：REQ-3 的写入失败提示（与状态页告警区同一文案）。
 */
@Composable
internal fun PolicyTransitionHistorySection(
    state: SettingsUiState,
    onExpandPolicyHistory: () -> Unit,
    modifier: Modifier = Modifier
) {
    PimSection("策略切换历史", modifier = modifier.testTag("settings-policy-history")) {
        val failure = state.policyTransitionWriteFailure
        if (failure.hasFailure) {
            Text(
                text = PolicyTransitionDisplay.writeFailureText(
                    consecutiveFailures = failure.consecutiveFailures,
                    lastFailureAtMillis = failure.lastFailureAtUtcMillis
                ),
                modifier = Modifier.testTag("settings-policy-history-failure"),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.tertiary
            )
        }

        val withinWindow = state.policyHistory
        val visibleRows = if (state.policyHistoryExpanded) {
            withinWindow
        } else {
            withinWindow.take(POLICY_HISTORY_PAGE_SIZE)
        }

        if (withinWindow.isEmpty()) {
            Text(
                text = "暂无记录（修复后的新版本开始记录）",
                modifier = Modifier.testTag("settings-policy-history-empty"),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        } else {
            visibleRows.forEachIndexed { index, transition ->
                Text(
                    text = formatPolicyTransition(transition),
                    modifier = Modifier.testTag("settings-policy-history-row-$index"),
                    style = MaterialTheme.typography.bodyMedium
                )
            }
        }

        if (withinWindow.size > POLICY_HISTORY_PAGE_SIZE && !state.policyHistoryExpanded) {
            Text(
                text = "仅显示最近 $POLICY_HISTORY_PAGE_SIZE 条 · " +
                    "30 天内共 ${state.policyHistoryTotalInWindow} 条",
                modifier = Modifier.testTag("settings-policy-history-more"),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
            OutlinedButton(
                onClick = onExpandPolicyHistory,
                modifier = Modifier.testTag("settings-policy-history-expand")
            ) {
                Text("展开全部")
            }
        }

        Text(
            text = "本地保留 30 天",
            modifier = Modifier.testTag("settings-policy-history-note"),
            style = MaterialTheme.typography.bodySmall,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
    }
}

