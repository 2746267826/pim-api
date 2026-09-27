package com.pim.app.ui

import android.Manifest
import android.app.usage.UsageStatsManager
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.CheckCircle
import androidx.compose.material.icons.filled.LocationOn
import androidx.compose.material.icons.filled.PlayArrow
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Security
import androidx.compose.material.icons.filled.Send
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Stop
import androidx.compose.material3.Button
import androidx.compose.material3.Divider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Tab
import androidx.compose.material3.TabRow
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewModelScope
import com.pim.app.data.AppDatabase
import com.pim.app.location.LocationCaptureRepository
import com.pim.app.location.LocationCaptureState
import com.pim.app.location.LocationSnapshot
import com.pim.app.location.quality.LocationQualityGate
import com.pim.app.mobile.logs.StructuredLogEntry
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.mobile.sync.MobileSyncCoordinator
import com.pim.app.mobile.sync.MobileSyncScheduler
import com.pim.app.mobile.sync.MobileSyncState

import com.pim.core.auth.ServerBoundLoginCoordinator
import com.pim.core.auth.ServerBoundLoginResult
import com.pim.core.auth.TokenManager
import com.pim.core.settings.ServerSettingsStore
import com.pim.core.settings.ServerUrlValidator
import com.pim.core.util.toCauseChainMessage
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import javax.inject.Inject

@Composable
fun PimAppScaffold(
    locationViewModel: LocationCaptureViewModel = hiltViewModel(),
    statusViewModel: MobileStatusViewModel = hiltViewModel()
) {
    val locationState by locationViewModel.state.collectAsStateWithLifecycle()
    val uiState by statusViewModel.state.collectAsStateWithLifecycle()
    var selectedTab by rememberSaveable { mutableStateOf(PimTab.Status) }
    val context = LocalContext.current
    val locationPermissionLauncher = rememberLauncherForActivityResult(
        contract = ActivityResultContracts.RequestMultiplePermissions()
    ) {
        locationViewModel.startCapture()
    }

    MaterialTheme {
        Scaffold(
            topBar = {
                Column {
                    Surface(color = MaterialTheme.colorScheme.primaryContainer) {
                        Row(
                            modifier = Modifier
                                .fillMaxWidth()
                                .padding(horizontal = 20.dp, vertical = 16.dp),
                            verticalAlignment = Alignment.CenterVertically,
                            horizontalArrangement = Arrangement.SpaceBetween
                        ) {
                            Column {
                                Text(
                                    text = "PIM Android",
                                    style = MaterialTheme.typography.titleLarge,
                                    fontWeight = FontWeight.SemiBold
                                )
                                Text(
                                    text = "手机采集、同步与诊断",
                                    style = MaterialTheme.typography.bodyMedium
                                )
                            }
                            Icon(imageVector = selectedTab.icon, contentDescription = null)
                        }
                    }
                    TabRow(selectedTabIndex = selectedTab.ordinal) {
                        PimTab.entries.forEach { tab ->
                            Tab(
                                selected = selectedTab == tab,
                                onClick = { selectedTab = tab },
                                text = { Text(tab.title) },
                                icon = { Icon(tab.icon, contentDescription = null) }
                            )
                        }
                    }
                }
            }
        ) { innerPadding ->
            Column(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(innerPadding)
                    .verticalScroll(rememberScrollState())
                    .padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                when (selectedTab) {
                    PimTab.Status -> StatusTab(
                        locationState = locationState,
                        uiState = uiState,
                        usagePermissionGranted = hasUsageAccess(context),
                        preciseLocationGranted = hasFineLocationPermission(context),
                        onRefresh = statusViewModel::refresh,
                        onSyncNow = statusViewModel::syncNow
                    )
                    PimTab.Usage -> UsageTab(context)
                    PimTab.Location -> LocationTab(
                        state = locationState,
                        onRequestPermission = {
                            locationPermissionLauncher.launch(
                                arrayOf(
                                    Manifest.permission.ACCESS_FINE_LOCATION,
                                    Manifest.permission.ACCESS_COARSE_LOCATION
                                )
                            )
                        },
                        onStart = locationViewModel::startCapture,
                        onStop = locationViewModel::stopCapture
                    )
                    PimTab.Settings -> SettingsTab(
                        uiState = uiState,
                        onSaveServerUrl = statusViewModel::saveServerUrl,
                        onLogin = statusViewModel::login,
                        onClearLogin = statusViewModel::clearLogin
                    )
                }
            }
        }
    }
}

@Composable
private fun StatusTab(
    locationState: LocationCaptureState,
    uiState: MobileUiState,
    usagePermissionGranted: Boolean,
    preciseLocationGranted: Boolean,
    onRefresh: () -> Unit,
    onSyncNow: () -> Unit
) {
    Section(title = "运行状态") {
        StatusRow("Android 客户端", "已打开")
        StatusRow("版本", uiState.appVersion)
        StatusRow("服务器", uiState.serverUrl)
        StatusRow("登录", if (uiState.isLoggedIn) "已登录" else "未登录")
        StatusRow("使用权限", if (usagePermissionGranted) "已授权" else "未授权")
        StatusRow("精确定位", if (preciseLocationGranted) "已授权" else "未授权")
        StatusRow("同步方式", "打开 App 后单次同步")
        StatusRow("后台保活", "未启用")
        StatusRow("定位", if (locationState.isCapturing) "采集中" else "待机")
        StatusRow("定位提交", locationState.submitStatus)
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedButton(onClick = onRefresh) {
                Icon(Icons.Filled.Refresh, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("刷新状态")
            }
            Button(onClick = onSyncNow, enabled = !uiState.isSyncInProgress) {
                Icon(Icons.Filled.Send, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text(if (uiState.isSyncInProgress) "同步中" else "立即同步")
            }
        }
    }

    Section(title = "同步与传输") {
        StatusRow("阶段", phaseLabel(uiState.phase))
        StatusRow("状态", if (uiState.isSyncInProgress) "进行中" else "空闲")
        StatusRow("进度", localizedProgress(uiState.progressText))
        StatusRow("服务器窗口", windowProgress(uiState))
        StatusRow("当前窗口", currentWindowLabel(uiState))
        StatusRow("本次采集", "事件 ${uiState.currentEventCount} / 汇总 ${uiState.currentSummaryCount} / App ${uiState.currentAppMetadataCount}")
        StatusRow("最近批次", batchLabel(uiState))
        StatusRow("心跳", uiState.heartbeatStatus ?: "暂无")
        StatusRow("已接收", uiState.acceptedCount.toString())
        StatusRow("已跳过", uiState.skippedCount.toString())
        StatusRow("已拒绝", uiState.rejectedCount.toString())
        StatusRow("失败", uiState.failedCount.toString())
        StatusRow("待上传", "事件 ${uiState.pendingUsageEventCount} / 汇总 ${uiState.pendingUsageSummaryCount} / App ${uiState.pendingAppMetadataCount} / 定位 ${uiState.pendingLocationPointCount}")
        StatusRow("本地诊断队列", "设备 ${uiState.pendingDeviceProfileCount} / 批次 ${uiState.pendingSyncBatchCount}")
        StatusRow("待传队列", uiState.pendingQueueCount.toString())
        StatusRow("最近尝试", uiState.lastAttemptedUploadAt ?: "无")
        StatusRow("最近成功", uiState.lastSuccessfulUploadAt ?: "无")
        StatusRow("最近错误", uiState.lastError ?: "无")
        StatusRow("详细错误", uiState.lastErrorDetail ?: "无")
    }

    Section(title = "最近日志（仅本机保存）") {
        if (uiState.recentLogs.isEmpty()) {
            Text("暂无日志。")
        } else {
            uiState.recentLogs.forEach { log ->
                StatusRow("${log.level}/${log.tag}", logDisplayMessage(log))
            }
        }
    }
}

@Composable
private fun UsageTab(context: Context) {
    var hasUsageAccess by remember { mutableStateOf(hasUsageAccess(context)) }

    Section(title = "应用使用权限") {
        StatusRow("权限", if (hasUsageAccess) "已授权" else "未授权")
        Text("打开 App 后会请求服务器缺失窗口，只采集服务器返回的最近 14 天内数据。")
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button(
                onClick = {
                    context.startActivity(Intent(Settings.ACTION_USAGE_ACCESS_SETTINGS))
                }
            ) {
                Icon(Icons.Filled.Security, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("打开权限设置")
            }
            OutlinedButton(onClick = { hasUsageAccess = hasUsageAccess(context) }) {
                Icon(Icons.Filled.Refresh, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("刷新")
            }
        }
    }
}

@Composable
private fun LocationTab(
    state: LocationCaptureState,
    onRequestPermission: () -> Unit,
    onStart: () -> Unit,
    onStop: () -> Unit
) {
    val snapshot = state.latest
    val inlineReason = state.inlineReason

    Section(title = "手动定位") {
        StatusRow("状态", state.statusMessage)
        StatusRow(
            "精度规则",
            "精度门槛 < ${LocationQualityGate.displayThresholdMeters()}m"
        )
        StatusRow("等待时长", formatDuration(state.waitDurationMs))
        Divider()
        LocationSnapshotRows(snapshot)
        Divider()
        StatusRow("提交状态", state.submitStatus)
        if (inlineReason != null) {
            Text(
                text = inlineReason,
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium
            )
        }
        Row(
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Button(onClick = onRequestPermission) {
                Icon(Icons.Filled.LocationOn, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("授权定位")
            }
            OutlinedButton(onClick = onStart, enabled = !state.isCapturing) {
                Icon(Icons.Filled.PlayArrow, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("开始")
            }
            OutlinedButton(onClick = onStop, enabled = state.isCapturing) {
                Icon(Icons.Filled.Stop, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text("停止")
            }
        }
        if (state.showLowQualityWarning) {
            Text(
                text = "精度不足，已标记低质量",
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodyMedium
            )
        }
    }
}

@Composable
private fun LocationSnapshotRows(snapshot: LocationSnapshot?) {
    StatusRow("纬度", snapshot?.latitude?.let { "%.6f".format(Locale.US, it) } ?: "等待")
    StatusRow("经度", snapshot?.longitude?.let { "%.6f".format(Locale.US, it) } ?: "等待")
    StatusRow("水平误差", snapshot?.horizontalAccuracyMeters?.let { "%.1f m".format(Locale.US, it) } ?: "等待")
    StatusRow("来源", snapshot?.provider ?: "等待")
    StatusRow("海拔", snapshot?.altitudeMeters?.let { "%.1f m".format(Locale.US, it) } ?: "无")
    StatusRow("速度", snapshot?.speedMetersPerSecond?.let { "%.2f m/s".format(Locale.US, it) } ?: "无")
    StatusRow("方向", snapshot?.bearingDegrees?.let { "%.1f deg".format(Locale.US, it) } ?: "无")
    StatusRow("时间", snapshot?.timeMillis?.let(::formatTime) ?: "等待")
}

@Composable
private fun SettingsTab(
    uiState: MobileUiState,
    onSaveServerUrl: (String) -> Unit,
    onLogin: (String, String) -> Unit,
    onClearLogin: () -> Unit
) {
    var serverUrl by rememberSaveable(uiState.serverUrl) { mutableStateOf(uiState.serverUrl) }
    var username by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }
    var savedText by rememberSaveable { mutableStateOf("") }

    Section(title = "服务器设置") {
        OutlinedTextField(
            value = serverUrl,
            onValueChange = { serverUrl = it },
            modifier = Modifier.fillMaxWidth(),
            label = { Text("服务器 URL") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.None)
        )
        Button(
            onClick = {
                onSaveServerUrl(serverUrl)
                savedText = "已保存"
            }
        ) {
            Icon(Icons.Filled.CheckCircle, contentDescription = null)
            Spacer(Modifier.width(8.dp))
            Text("保存")
        }
        if (savedText.isNotEmpty()) {
            Text(savedText, color = MaterialTheme.colorScheme.primary)
        }
    }

    Section(title = "登录") {
        StatusRow("当前状态", if (uiState.isLoggedIn) "已保存令牌" else "未登录")
        StatusRow("应用版本", uiState.appVersion)
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
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Button(
                onClick = { onLogin(username, password) },
                enabled = !uiState.isLoginInProgress
            ) {
                Icon(Icons.Filled.CheckCircle, contentDescription = null)
                Spacer(Modifier.width(8.dp))
                Text(if (uiState.isLoginInProgress) "登录中" else "登录")
            }
            OutlinedButton(onClick = onClearLogin) {
                Text("清除登录")
            }
        }
        uiState.loginStatus?.let {
            Text(it, color = if (uiState.isLoggedIn) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error)
        }
    }
}

@Composable
private fun Section(
    title: String,
    content: @Composable ColumnScope.() -> Unit
) {
    Surface(
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(8.dp),
        border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant),
        color = MaterialTheme.colorScheme.surface
    ) {
        Column(
            modifier = Modifier.padding(16.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
            content = {
                Text(
                    text = title,
                    style = MaterialTheme.typography.titleMedium,
                    fontWeight = FontWeight.SemiBold
                )
                content()
            }
        )
    }
}

@Composable
private fun StatusRow(label: String, value: String) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.Top
    ) {
        Text(
            text = label,
            style = MaterialTheme.typography.bodyMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant
        )
        Spacer(Modifier.width(16.dp))
        Text(
            text = value,
            modifier = Modifier.weight(1f),
            style = MaterialTheme.typography.bodyMedium,
            fontWeight = FontWeight.Medium,
            textAlign = TextAlign.End
        )
    }
}

@HiltViewModel
class LocationCaptureViewModel @Inject constructor(
    private val repository: LocationCaptureRepository
) : ViewModel() {
    val state = repository.state

    fun startCapture() = repository.startCapture()

    fun stopCapture() = repository.stopCapture()

    override fun onCleared() {
        super.onCleared()
    }
}

data class MobileUiState(
    val serverUrl: String = ServerSettingsStore.DEFAULT_BASE_URL,
    val appVersion: String = "未知",
    val isLoggedIn: Boolean = false,
    val phase: String = "waiting",
    val progressText: String = "打开 App 后会自动同步一次。",
    val isSyncInProgress: Boolean = false,
    val acceptedCount: Int = 0,
    val skippedCount: Int = 0,
    val rejectedCount: Int = 0,
    val failedCount: Int = 0,
    val lastError: String? = null,
    val lastErrorDetail: String? = null,
    val pendingQueueCount: Int = 0,
    val pendingUsageEventCount: Int = 0,
    val pendingUsageSummaryCount: Int = 0,
    val pendingAppMetadataCount: Int = 0,
    val pendingLocationPointCount: Int = 0,
    val pendingSyncBatchCount: Int = 0,
    val pendingLogCount: Int = 0,
    val pendingDeviceProfileCount: Int = 0,
    val gapWindowCount: Int = 0,
    val currentWindowIndex: Int = 0,
    val currentWindowStartUtc: String? = null,
    val currentWindowEndUtc: String? = null,
    val currentEventCount: Int = 0,
    val currentSummaryCount: Int = 0,
    val currentAppMetadataCount: Int = 0,
    val lastBatchId: String? = null,
    val lastBatchStatus: String? = null,
    val heartbeatStatus: String? = null,
    val lastAttemptedUploadAt: String? = null,
    val lastSuccessfulUploadAt: String? = null,
    val recentLogs: List<MobileLogLine> = emptyList(),
    val loginStatus: String? = null,
    val isLoginInProgress: Boolean = false
)

data class MobileLogLine(
    val level: String,
    val tag: String,
    val message: String,
    val throwablePreview: String?,
    val occurredAtUtc: Long
)

private data class PendingQueueCounts(
    val usageEvents: Int,
    val usageSummaries: Int,
    val appMetadata: Int,
    val locations: Int,
    val syncBatches: Int,
    val deviceProfiles: Int
) {
    val uploadable: Int
        get() = usageEvents + usageSummaries + appMetadata + locations
}

@HiltViewModel
class MobileStatusViewModel @Inject constructor(
    @ApplicationContext private val context: Context,
    private val tokenManager: TokenManager,
    private val serverBoundLoginCoordinator: ServerBoundLoginCoordinator,
    private val serverSettingsStore: ServerSettingsStore,
    private val database: AppDatabase,
    private val logs: StructuredLogRepository,
    private val mobileSyncCoordinator: MobileSyncCoordinator,
    private val mobileSyncScheduler: MobileSyncScheduler
) : ViewModel() {
    private val mobileDataDao = database.mobileDataDao()

    private val _state = MutableStateFlow(MobileUiState())
    val state: StateFlow<MobileUiState> = _state.asStateFlow()

    init {
        refresh()
        viewModelScope.launch {
            mobileSyncCoordinator.currentState.collect { syncState ->
                _state.update { current -> current.copyFromSync(syncState) }
            }
        }
        viewModelScope.launch {
            combine(
                mobileDataDao.pendingUsageEventCount(),
                mobileDataDao.pendingUsageSummaryCount(),
                mobileDataDao.pendingAppMetadataCount(),
                mobileDataDao.pendingLocationPointCount(),
                mobileDataDao.pendingSyncBatchCount(),
                mobileDataDao.pendingDeviceProfileCount()
            ) { counts ->
                PendingQueueCounts(
                    usageEvents = counts[0],
                    usageSummaries = counts[1],
                    appMetadata = counts[2],
                    locations = counts[3],
                    syncBatches = counts[4],
                    deviceProfiles = counts[5]
                )
            }.collect { counts ->
                _state.update { current ->
                    current.copy(
                        pendingQueueCount = counts.uploadable,
                        pendingUsageEventCount = counts.usageEvents,
                        pendingUsageSummaryCount = counts.usageSummaries,
                        pendingAppMetadataCount = counts.appMetadata,
                        pendingLocationPointCount = counts.locations,
                        pendingSyncBatchCount = counts.syncBatches,
                        pendingLogCount = 0,
                        pendingDeviceProfileCount = counts.deviceProfiles
                    )
                }
            }
        }
    }

    fun refresh() {
        viewModelScope.launch {
            mobileSyncCoordinator.refreshPersistedState()
            val pending = pendingQueueCounts()
            val recentLogEntries = logs.recent()
            _state.update { current ->
                current.copy(
                    serverUrl = serverSettingsStore.getBaseUrl(),
                    appVersion = appVersionDisplay(),
                    isLoggedIn = hasCurrentServerSession(),
                    pendingQueueCount = pending.uploadable,
                    pendingUsageEventCount = pending.usageEvents,
                    pendingUsageSummaryCount = pending.usageSummaries,
                    pendingAppMetadataCount = pending.appMetadata,
                    pendingLocationPointCount = pending.locations,
                    pendingSyncBatchCount = pending.syncBatches,
                    pendingLogCount = 0,
                    pendingDeviceProfileCount = pending.deviceProfiles,
                    recentLogs = recentLogEntries.map { it.toLine() }
                )
            }
        }
    }

    fun saveServerUrl(value: String) {
        val validation = ServerUrlValidator.validate(value)
        if (!validation.isValid) {
            _state.update {
                it.copy(
                    phase = "server-invalid",
                    progressText = "服务器地址无效。",
                    lastError = validation.reasonCode,
                    lastErrorDetail = validation.reasonCode
                )
            }
            return
        }
        runCatching {
            serverSettingsStore.setBaseUrl(validation.normalizedUrl)
        }.getOrElse { error ->
            reloadPersistedServerState(
                phase = "server-save-failed",
                progressText = "服务器地址保存失败，已重新载入当前配置。",
                lastError = error.message,
                lastErrorDetail = error.message
            )
            return
        }
        reloadPersistedServerState(
            phase = "server-updated",
            progressText = "服务器地址已保存，请重新同步。",
            lastError = null,
            lastErrorDetail = null
        )
    }

    fun syncNow() {
        startSync("正在手动同步手机数据。")
    }

    fun login(username: String, password: String) {
        if (username.isBlank() || password.isBlank()) {
            _state.update { it.copy(loginStatus = "请输入用户名和密码。") }
            return
        }

        if (!ServerUrlValidator.validate(serverSettingsStore.getBaseUrl()).isValid) {
            _state.update { it.copy(loginStatus = "请先保存有效的服务器地址。") }
            return
        }

        viewModelScope.launch {
            _state.update { it.copy(isLoginInProgress = true, loginStatus = "正在登录...") }
            runCatching {
                when (val result = serverBoundLoginCoordinator.login(username, password)) {
                    ServerBoundLoginResult.Success -> Unit
                    ServerBoundLoginResult.StaleServer -> {
                        error("\u767b\u5f55\u671f\u95f4\u670d\u52a1\u5668\u5730\u5740\u5df2\u66f4\u6539\uff0c\u8bf7\u91cd\u8bd5\u3002")
                    }
                    ServerBoundLoginResult.SessionSaveFailed -> {
                        error("\u767b\u5f55\u51ed\u636e\u65e0\u6cd5\u5b89\u5168\u4fdd\u5b58\uff0c\u8bf7\u91cd\u8bd5\u3002")
                    }
                    is ServerBoundLoginResult.Failure -> throw result.error
                }
            }.fold(
                onSuccess = {
                    val loginSuccessMessage = "登录成功，正在同步手机数据。"
                    _state.update {
                        it.copy(
                            isLoggedIn = hasCurrentServerSession(),
                            isLoginInProgress = false,
                            loginStatus = loginSuccessMessage
                        )
                    }
                    startSync(loginSuccessMessage)
                },
                onFailure = { error ->
                    if (error is CancellationException) throw error
                    val failureMessage = error.toCauseChainMessage()
                    runCatching {
                        logs.error("mobile-auth", "登录失败：$failureMessage", error)
                    }
                    _state.update {
                        it.copy(
                            isLoggedIn = hasCurrentServerSession(),
                            isLoginInProgress = false,
                            loginStatus = "登录失败：$failureMessage",
                            lastError = failureMessage
                        )
                    }
                }
            )
        }
    }

    fun clearLogin() {
        if (!tokenManager.clear()) {
            _state.update {
                it.copy(
                    isLoggedIn = hasCurrentServerSession(),
                    loginStatus = "清除失败：安全存储暂时不可用。"
                )
            }
            return
        }
        _state.update {
            it.copy(
                isLoggedIn = false,
                loginStatus = "已清除登录令牌。"
            )
        }
    }

    private fun startSync(statusMessage: String) {
        viewModelScope.launch {
            _state.update {
                it.copy(
                    isLoggedIn = hasCurrentServerSession(),
                    loginStatus = statusMessage,
                    progressText = "同步请求已提交，等待系统执行。"
                )
            }
            mobileSyncScheduler.enqueueNow()
        }
    }

    private fun reloadPersistedServerState(
        phase: String,
        progressText: String,
        lastError: String?,
        lastErrorDetail: String?
    ) {
        val serverUrl = serverSettingsStore.getBaseUrl()
        _state.update {
            it.copy(
                serverUrl = serverUrl,
                isLoggedIn = hasCurrentServerSession(),
                phase = phase,
                progressText = progressText,
                lastError = lastError,
                lastErrorDetail = lastErrorDetail
            )
        }
    }

    private fun hasCurrentServerSession(): Boolean {
        return !tokenManager
            .getAccessTokenForServer(serverSettingsStore.getBaseUrl())
            .isNullOrBlank()
    }

    private suspend fun pendingQueueCounts(): PendingQueueCounts {
        return PendingQueueCounts(
            usageEvents = mobileDataDao.pendingUsageEventCount().first(),
            usageSummaries = mobileDataDao.pendingUsageSummaryCount().first(),
            appMetadata = mobileDataDao.pendingAppMetadataCount().first(),
            locations = mobileDataDao.pendingLocationPointCount().first(),
            syncBatches = mobileDataDao.pendingSyncBatchCount().first(),
            deviceProfiles = mobileDataDao.pendingDeviceProfileCount().first()
        )
    }

    private fun MobileUiState.copyFromSync(sync: MobileSyncState): MobileUiState {
        return copy(
            phase = sync.phase,
            progressText = sync.progressText,
            isSyncInProgress = sync.isInProgress,
            acceptedCount = sync.acceptedCount,
            skippedCount = sync.skippedCount,
            rejectedCount = sync.rejectedCount,
            failedCount = sync.failedCount,
            lastError = sync.lastError,
            lastErrorDetail = sync.lastErrorDetail,
            pendingQueueCount = sync.pendingQueueCount,
            gapWindowCount = sync.gapWindowCount,
            currentWindowIndex = sync.currentWindowIndex,
            currentWindowStartUtc = sync.currentWindowStartUtc,
            currentWindowEndUtc = sync.currentWindowEndUtc,
            currentEventCount = sync.currentEventCount,
            currentSummaryCount = sync.currentSummaryCount,
            currentAppMetadataCount = sync.currentAppMetadataCount,
            lastBatchId = sync.lastBatchId,
            lastBatchStatus = sync.lastBatchStatus,
            heartbeatStatus = sync.heartbeatStatus,
            lastAttemptedUploadAt = sync.lastAttemptedUploadAt,
            lastSuccessfulUploadAt = sync.lastSuccessfulUploadAt
        )
    }

    private fun StructuredLogEntry.toLine(): MobileLogLine {
        return MobileLogLine(
            level = level,
            tag = tag.ifBlank { "mobile" },
            message = message,
            throwablePreview = throwable?.lineSequence()?.firstOrNull(),
            occurredAtUtc = occurredAtUtc
        )
    }

    private fun appVersionDisplay(): String {
        return try {
            val info = packageInfo(context.packageManager, context.packageName)
            "${info.versionName ?: "unknown"} (${versionCode(info)})"
        } catch (_: Exception) {
            "unknown"
        }
    }

    private fun packageInfo(packageManager: PackageManager, packageName: String): PackageInfo {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            packageManager.getPackageInfo(packageName, PackageManager.PackageInfoFlags.of(0))
        } else {
            @Suppress("DEPRECATION")
            packageManager.getPackageInfo(packageName, 0)
        }
    }

    private fun versionCode(packageInfo: PackageInfo): Long {
        return if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            packageInfo.longVersionCode
        } else {
            @Suppress("DEPRECATION")
            packageInfo.versionCode.toLong()
        }
    }
}

private enum class PimTab(
    val title: String,
    val icon: ImageVector
) {
    Status("状态", Icons.Filled.CheckCircle),
    Usage("使用", Icons.Filled.Security),
    Location("定位", Icons.Filled.LocationOn),
    Settings("设置", Icons.Filled.Settings)
}

private fun phaseLabel(phase: String): String {
    return when (phase) {
        "waiting" -> "等待同步"
        "server-updated" -> "服务器已更新"
        "server-missing" -> "缺少服务器"
        "auth-missing" -> "缺少登录"
        "usage-permission-missing" -> "缺少使用权限"
        "preparing" -> "准备中"
        "gap-checking" -> "查询缺口"
        "collecting" -> "采集中"
        "uploading" -> "上传中"
        "uploaded" -> "已上传"
        "upload-failed" -> "上传失败"
        "completed" -> "已完成"
        "completed-with-errors" -> "完成但有错误"
        "failed" -> "失败"
        "catching-up" -> "补传中"
        else -> phase
    }
}

private fun localizedProgress(progress: String): String {
    return when (progress) {
        "Auth token missing; sync skipped." -> "缺少登录令牌，已跳过同步。请登录后重新同步。"
        "Usage access is missing; sync skipped." -> "缺少应用使用情况权限，已跳过同步。"
        "Checking server gaps." -> "正在询问服务器缺失时间窗。"
        "Collecting server-requested windows." -> "正在采集服务器要求补全的窗口。"
        "Uploading server-requested windows." -> "正在上传服务器要求补全的窗口。"
        "Mobile sync completed." -> "手机同步已完成。"
        "Mobile sync completed with upload errors." -> "手机同步已完成，但部分上传失败。"
        "Mobile sync failed." -> "手机同步失败。"
        "Usage batch uploaded." -> "使用记录批次已上传。"
        else -> progress
    }
}

private fun windowProgress(state: MobileUiState): String {
    return if (state.gapWindowCount > 0) {
        "第 ${state.currentWindowIndex.coerceAtLeast(0)} / ${state.gapWindowCount} 个"
    } else {
        "无待补全窗口"
    }
}

private fun currentWindowLabel(state: MobileUiState): String {
    val start = state.currentWindowStartUtc
    val end = state.currentWindowEndUtc
    return if (start.isNullOrBlank() || end.isNullOrBlank()) {
        "无"
    } else {
        "$start 到 $end"
    }
}

private fun batchLabel(state: MobileUiState): String {
    val batchId = state.lastBatchId ?: return "无"
    val status = state.lastBatchStatus ?: "未知"
    return "${batchId.takeLast(18)} / $status"
}

private fun logDisplayMessage(log: MobileLogLine): String {
    val throwable = log.throwablePreview
    return if (throwable.isNullOrBlank()) {
        "${formatTime(log.occurredAtUtc)}  ${log.message}"
    } else {
        "${formatTime(log.occurredAtUtc)}  ${log.message}\n$throwable"
    }
}

private fun syncResultMessage(sync: MobileSyncState): String {
    return when (sync.phase) {
        "completed" -> "同步完成。"
        "completed-with-errors" -> "同步完成，但有部分错误。"
        "auth-missing" -> "请先登录后再同步。"
        "usage-permission-missing" -> "请先授权应用使用情况权限。"
        "server-missing" -> "请先配置服务器地址。"
        "failed" -> "同步失败：${sync.lastErrorDetail ?: sync.lastError ?: "未知错误"}"
        else -> "同步结束：${phaseLabel(sync.phase)}"
    }
}

private fun hasUsageAccess(context: Context): Boolean {
    val manager = context.getSystemService(Context.USAGE_STATS_SERVICE) as? UsageStatsManager
        ?: return false
    val now = System.currentTimeMillis()
    return try {
        manager.queryUsageStats(
            UsageStatsManager.INTERVAL_DAILY,
            (now - 24 * 60 * 60 * 1000L).coerceAtLeast(0L),
            now
        ).orEmpty().isNotEmpty()
    } catch (_: SecurityException) {
        false
    }
}

private fun hasFineLocationPermission(context: Context): Boolean {
    return ContextCompat.checkSelfPermission(
        context,
        Manifest.permission.ACCESS_FINE_LOCATION
    ) == PackageManager.PERMISSION_GRANTED
}

private fun formatDuration(durationMs: Long): String {
    val seconds = (durationMs / 1000).coerceAtLeast(0L)
    val minutes = seconds / 60
    val remainder = seconds % 60
    return if (minutes > 0) {
        "${minutes}分${remainder}秒"
    } else {
        "${remainder}秒"
    }
}

private fun formatTime(timeMillis: Long): String {
    val formatter = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.getDefault())
    return formatter.format(Date(timeMillis))
}
