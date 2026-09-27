package com.pim.app.ui.settings

import android.content.Context
import android.content.pm.PackageInfo
import android.content.pm.PackageManager
import android.os.Build
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.pim.app.location.service.ForegroundLocationController
import com.pim.app.mobile.sync.MobileSyncScheduler
import com.pim.app.permissions.PermissionStatusRepository
import com.pim.app.settings.TrackingSettings
import com.pim.app.settings.TrackingSettingsStore
import com.pim.app.status.ConnectionProbeOutcome
import com.pim.app.status.ConnectionProbeResult
import com.pim.app.status.ConnectionProbeService
import com.pim.app.status.ConnectionProbeStore
import com.pim.app.status.probeRefreshDelayMillis
import com.pim.app.status.resolveProbeResult
import com.pim.app.mobile.diagnostics.DiagnosticOperations
import com.pim.app.recovery.CollectionState
import com.pim.app.recovery.RunningStateRestorer
import com.pim.app.schedule.ScheduleCacheStore
import com.pim.app.status.PermissionStatusSnapshot
import com.pim.core.models.ClientShellLatestResponse
import com.pim.core.network.ApiService
import com.pim.core.settings.PimServerEndpoints
import com.pim.core.settings.ServerUrlValidator
import com.pim.core.auth.ServerBoundLoginCoordinator
import com.pim.core.auth.ServerBoundLoginResult
import com.pim.core.auth.TokenManager
import com.pim.core.settings.ServerSettingsStore
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import timber.log.Timber

enum class DiagnosticClearFeedback {
    Cleared,
    Failed
}

data class SettingsUiState(
    val apiAddress: String = "",
    val apiWarnings: Set<String> = emptySet(),
    val apiError: String? = null,
    val apiStatus: String? = null,
    val isLoggedIn: Boolean = false,
    val loginStatus: String? = null,
    val continuousCollectionEnabled: Boolean = false,
    val collectionStatus: String? = null,
    val isBusy: Boolean = false,
    val trackingProfile: String = TrackingSettings.defaults().profile,
    val normalMinText: String = (TrackingSettings.defaults().normalIntervalMillis / 60_000.0).toDisplayNumber(),
    val scheduleMinText: String = (TrackingSettings.defaults().scheduleLowFrequencyIntervalMillis / 60_000.0).toDisplayNumber(),
    val movementSecText: String = (TrackingSettings.defaults().movementIntervalMillis / 1_000.0).toDisplayNumber(),
    val recoveryMetersText: String = TrackingSettings.defaults().scheduleRecoveryThresholdMeters.toFloat().toDisplayNumber(),
    val altitudeSecText: String = (TrackingSettings.defaults().altitudeWaitTimeoutMillis / 1_000.0).toDisplayNumber(),
    val advancedErrors: Map<String, String> = emptyMap(),
    val syncOnUnmeteredOnly: Boolean = false,
    /** WO-ANDROID-GATE-20260926 REQ-5：高频冲刺开关（默认开，持久化，改后无需重启即生效）。 */
    val sprintEnabled: Boolean = true,
    val verboseLoggingEnabled: Boolean = false,
    val verboseLoggingUntilUtcMillis: Long? = null,
    val logRetentionDays: Int = 7,
    val operationalStatus: String? = null,
    val permissions: PermissionStatusSnapshot = PermissionStatusSnapshot(false, false, false, false, false, false),
    val showClearDiagnosticsConfirmation: Boolean = false,
    val isClearingDiagnostics: Boolean = false,
    val diagnosticClearFeedback: DiagnosticClearFeedback? = null,
    val appVersion: String = "unknown",
    val versionCode: Long = 0,
    val gitSha: String = "unknown",
    val hasUpdate: Boolean = false,
    val latestVersion: String? = null,
    val updateUrl: String? = null,
    val updateError: String? = null,
    val updateCheckedAt: String? = null
)

private enum class SaveServerUrlResult {
    Success,
    SessionClearFailed,
    CommitFailed
}

@HiltViewModel
class SettingsViewModel @Inject constructor(
    private val serverSettingsStore: ServerSettingsStore,
    private val tokenManager: TokenManager,
    private val serverBoundLoginCoordinator: ServerBoundLoginCoordinator,
    private val trackingSettingsStore: TrackingSettingsStore,
    private val foregroundLocationController: ForegroundLocationController,
    private val permissionStatusRepository: PermissionStatusRepository,
    private val connectionProbeService: ConnectionProbeService,
    private val connectionProbeStore: ConnectionProbeStore,
    private val mobileSyncScheduler: MobileSyncScheduler,
    private val diagnosticOperations: DiagnosticOperations,
    private val runningStateRestorer: RunningStateRestorer,
    private val webViewSiteDataCleaner: WebViewSiteDataCleaner,
    private val scheduleCacheStore: ScheduleCacheStore,
    private val api: ApiService,
    @ApplicationContext private val appContext: Context
) : ViewModel() {
    private val _state = MutableStateFlow(SettingsUiState())
    val state: StateFlow<SettingsUiState> = _state.asStateFlow()

    init {
        refresh(runProbe = false)
        viewModelScope.launch { checkUpdate() }
    }

    fun refresh() {
        refresh(runProbe = true)
    }

    private fun refresh(runProbe: Boolean) {
        reloadOperationalState()
        viewModelScope.launch {
            val address = serverSettingsStore.getBaseUrl()
            val validation = ServerUrlValidator.validate(address)
            _state.update {
                it.copy(
                    apiAddress = address,
                    apiWarnings = validation.warnings,
                    apiError = validation.reasonCode?.takeUnless { validation.isValid },
                    isLoggedIn = hasCurrentServerSession(),
                    continuousCollectionEnabled = persistedCollectionEnabled(),
                    permissions = permissionStatusRepository.snapshot()
                )
            }
            if (runProbe) {
                runConnectionProbe(force = false)
            }
        }
    }

    fun updateApiAddress(value: String) {
        val validation = ServerUrlValidator.validate(value)
        _state.update {
            it.copy(
                apiAddress = value,
                apiWarnings = validation.warnings,
                apiError = validation.reasonCode?.takeUnless { validation.isValid },
                apiStatus = null
            )
        }
    }

    fun saveApiAddress(): Boolean {
        val validation = ServerUrlValidator.validate(state.value.apiAddress)
        if (!validation.isValid) {
            _state.update {
                it.copy(
                    apiWarnings = validation.warnings,
                    apiError = validation.reasonCode,
                    apiStatus = "API 地址无效，无法保存。"
                )
            }
            return false
        }

        return when (saveServerUrl(validation.normalizedUrl)) {
            SaveServerUrlResult.Success -> {
                reloadPersistedServerState(
                    apiError = null,
                    apiStatus = "API 地址已保存。"
                )
                true
            }
            SaveServerUrlResult.SessionClearFailed -> {
                reloadPersistedServerState(
                    apiError = "旧会话无法清除，请重试。",
                    apiStatus = "API 地址保存失败，无法清理旧会话。"
                )
                false
            }
            SaveServerUrlResult.CommitFailed -> {
                reloadPersistedServerState(
                    apiError = "API 地址无法写入本地设置，请重试。",
                    apiStatus = "API 地址保存失败。"
                )
                false
            }
        }
    }

    fun testConnection() {
        val validation = ServerUrlValidator.validate(state.value.apiAddress)
        if (!validation.isValid) {
            _state.update { it.copy(apiError = validation.reasonCode, apiStatus = "请先输入有效的 API 地址。") }
            return
        }
        if (!saveApiAddress()) return
        viewModelScope.launch {
            _state.update {
                it.copy(
                    isBusy = true,
                    apiStatus = "正在测试连接…"
                )
            }
            runConnectionProbe(force = true, finishBusyState = true)
        }
    }

    fun login(username: String, password: String) {
        if (username.isBlank() || password.isBlank()) {
            _state.update { it.copy(loginStatus = "请输入用户名和密码。") }
            return
        }
        val validation = ServerUrlValidator.validate(state.value.apiAddress)
        if (!validation.isValid) {
            _state.update { it.copy(apiError = validation.reasonCode, loginStatus = "请先保存有效的 API 地址。") }
            return
        }

        if (!saveApiAddress()) return
        viewModelScope.launch {
            _state.update { it.copy(isBusy = true, loginStatus = "正在登录...") }
            runCatching {
                when (val result = serverBoundLoginCoordinator.login(username, password)) {
                    ServerBoundLoginResult.Success -> Unit
                    ServerBoundLoginResult.StaleServer -> {
                        error("登录期间服务器地址已更改，请重试。")
                    }
                    ServerBoundLoginResult.SessionSaveFailed -> {
                        error("登录凭据无法安全保存，请重试。")
                    }
                    is ServerBoundLoginResult.Failure -> throw result.error
                }
            }.fold(
                onSuccess = {
                    _state.update {
                        it.copy(
                            isBusy = false,
                            isLoggedIn = hasCurrentServerSession(),
                            loginStatus = "登录成功。",
                            continuousCollectionEnabled = persistedCollectionEnabled()
                        )
                    }
                },
                onFailure = { error ->
                    if (error is CancellationException) throw error
                    _state.update {
                        it.copy(
                            isBusy = false,
                            isLoggedIn = hasCurrentServerSession(),
                            loginStatus = "登录失败，请重试"
                        )
                    }
                }
            )
        }
    }

    fun logout() {
        val oldOrigin = extractOrigin(serverSettingsStore.getBaseUrl())
        if (!tokenManager.clear()) {
            _state.update {
                it.copy(
                    isLoggedIn = hasCurrentServerSession(),
                    loginStatus = "退出失败：安全存储暂时不可用。"
                )
            }
            return
        }
        if (oldOrigin != null) {
            runCatching { webViewSiteDataCleaner.clearOrigin(oldOrigin) }
        }
        val collectionIntent = persistedCollectionEnabled()
        _state.update {
            it.copy(
                isLoggedIn = false,
                loginStatus = "已退出登录。",
                continuousCollectionEnabled = collectionIntent,
                collectionStatus = "已退出登录，持续采集设置保持不变。"
            )
        }
        val identity = serverIdentityFor(serverSettingsStore.getBaseUrl())
        if (identity != null) {
            scheduleCacheStore.clear(identity)
        }
    }

    fun setContinuousCollectionEnabled(enabled: Boolean) {
        if (!enabled) {
            trackingSettingsStore.setContinuousCollectionEnabled(false)
            foregroundLocationController.stop()
            _state.update {
                it.copy(
                    continuousCollectionEnabled = false,
                    collectionStatus = "持续采集已关闭。"
                )
            }
            return
        }

        val validation = ServerUrlValidator.validate(state.value.apiAddress)
        if (!validation.isValid) {
            keepCollectionOff("请先保存有效的 API 地址。") {
                it.copy(apiError = validation.reasonCode)
            }
            return
        }

        when (saveServerUrl(validation.normalizedUrl)) {
            SaveServerUrlResult.Success -> {
                val normalized = validation.normalizedUrl
                reloadPersistedServerState(apiError = null, apiStatus = state.value.apiStatus)

                if (!hasCurrentServerSession()) {
                    showCollectionBlocked("请先登录后再开启持续采集。")
                    return
                }

                trackingSettingsStore.setContinuousCollectionEnabled(true)

                val missingPermissions = missingCollectionPermissions()
                if (missingPermissions.isNotEmpty()) {
                    _state.update {
                        it.copy(
                            apiAddress = normalized,
                            apiWarnings = validation.warnings,
                            apiError = null,
                            continuousCollectionEnabled = true,
                            collectionStatus = "缺少权限：${missingPermissions.joinToString("、")}。"
                        )
                    }
                    return
                }

                _state.update {
                    it.copy(
                        apiAddress = normalized,
                        apiWarnings = validation.warnings,
                        apiError = null,
                        continuousCollectionEnabled = true,
                        collectionStatus = "正在启动前台定位服务。"
                    )
                }
                runCatching {
                    foregroundLocationController.start()
                }.onFailure {
                    _state.update {
                        it.copy(
                            continuousCollectionEnabled = true,
                            collectionStatus = "启动失败，请查看状态页"
                        )
                    }
                }
            }
            SaveServerUrlResult.SessionClearFailed -> {
                reloadPersistedServerState(
                    apiError = "旧会话无法清除，请重试。",
                    apiStatus = "API 地址保存失败，已重新载入当前配置。"
                )
                _state.update {
                    it.copy(collectionStatus = "API 地址保存失败，持续采集设置保持不变。")
                }
            }
            SaveServerUrlResult.CommitFailed -> {
                reloadPersistedServerState(
                    apiError = "API 地址无法写入本地设置，请重试。",
                    apiStatus = "API 地址保存失败，已重新载入当前配置。"
                )
                _state.update {
                    it.copy(collectionStatus = "API 地址保存失败，持续采集设置保持不变。")
                }
            }
        }
    }

    fun applyTrackingPreset(profileId: String) {
        trackingSettingsStore.applyPreset(profileId)
        reloadOperationalState()
        reloadForegroundCollectionIfEnabled()
    }

    fun updateNormalMinText(value: String) {
        _state.update { it.copy(normalMinText = value) }
    }

    fun updateScheduleMinText(value: String) {
        _state.update { it.copy(scheduleMinText = value) }
    }

    fun updateMovementSecText(value: String) {
        _state.update { it.copy(movementSecText = value) }
    }

    fun updateRecoveryMetersText(value: String) {
        _state.update { it.copy(recoveryMetersText = value) }
    }

    fun updateAltitudeSecText(value: String) {
        _state.update { it.copy(altitudeSecText = value) }
    }

    fun saveAdvancedSettings(): Boolean {
        val errors = mutableMapOf<String, String>()

        val normalMin = state.value.normalMinText.toDoubleOrNull()
        val scheduleMin = state.value.scheduleMinText.toDoubleOrNull()
        val movementSec = state.value.movementSecText.toDoubleOrNull()
        val recoveryMeters = state.value.recoveryMetersText.toDoubleOrNull()
        val altitudeSec = state.value.altitudeSecText.toDoubleOrNull()

        if (normalMin == null || !normalMin.isFinite() || normalMin < 1.0 || normalMin > 15.0) {
            errors["normalInterval"] = "正常间隔需在 1–15 分钟之间。"
        }
        if (scheduleMin == null || !scheduleMin.isFinite() || scheduleMin < 5.0 || scheduleMin > 60.0) {
            errors["scheduleInterval"] = "调度间隔需在 5–60 分钟之间。"
        }
        if (movementSec == null || !movementSec.isFinite() || movementSec < 30.0 || movementSec > 300.0) {
            errors["movementInterval"] = "移动间隔需在 30–300 秒之间。"
        }
        if (recoveryMeters == null || !recoveryMeters.isFinite() || recoveryMeters < 25.0 || recoveryMeters > 500.0) {
            errors["recoveryThreshold"] = "恢复阈值需在 25–500 米之间。"
        }
        if (altitudeSec == null || !altitudeSec.isFinite() || altitudeSec < 0.0 || altitudeSec > 30.0) {
            errors["altitudeWait"] = "海拔等待需在 0–30 秒之间。"
        }

        if (errors.isNotEmpty()) {
            _state.update { it.copy(advancedErrors = errors) }
            return false
        }

        val current = trackingSettingsStore.read()
        trackingSettingsStore.write(
            current.copy(
                profile = "custom",
                normalIntervalMillis = (normalMin!! * 60_000).toLong(),
                scheduleLowFrequencyIntervalMillis = (scheduleMin!! * 60_000).toLong(),
                movementIntervalMillis = (movementSec!! * 1_000).toLong(),
                scheduleRecoveryThresholdMeters = recoveryMeters!!,
                altitudeWaitTimeoutMillis = (altitudeSec!! * 1_000).toLong()
            )
        )
        reloadOperationalState()
        _state.update { it.copy(advancedErrors = emptyMap()) }
        reloadForegroundCollectionIfEnabled()
        return true
    }

    fun setSyncOnUnmeteredOnly(enabled: Boolean) {
        trackingSettingsStore.write(trackingSettingsStore.read().copy(syncOnUnmeteredOnly = enabled))
        mobileSyncScheduler.ensurePeriodic()
        _state.update { it.copy(syncOnUnmeteredOnly = trackingSettingsStore.read().syncOnUnmeteredOnly) }
    }

    /**
     * 切换高频冲刺开关（REQ-5 / AC-5.5）。
     *
     * **只写设置，不做任何重启动作**：采集循环与手动会话每个周期都重新读这个字段，
     * 因此自下一个周期起生效 —— 这正是 AC-5.5 要求的「无需重启应用或采集服务」。
     * 需求方对「假开关」零容忍（A4），所以这里刻意不附加任何需要重启的副作用。
     */
    fun setSprintEnabled(enabled: Boolean) {
        trackingSettingsStore.setSprintEnabled(enabled)
        _state.update { it.copy(sprintEnabled = trackingSettingsStore.read().sprintEnabled) }
    }

    fun setVerboseLoggingEnabled(enabled: Boolean) {
        val now = System.currentTimeMillis()
        trackingSettingsStore.setVerboseLoggingEnabled(enabled, now)
        val settings = trackingSettingsStore.read()
        _state.update {
            it.copy(
                verboseLoggingEnabled = enabled,
                verboseLoggingUntilUtcMillis = settings.verboseLoggingUntilUtcMillis
            )
        }
    }

    fun setLogRetentionDays(days: Int) {
        if (days !in setOf(1, 7, 14, 30)) return
        trackingSettingsStore.write(trackingSettingsStore.read().copy(logRetentionDays = days))
        _state.update { it.copy(logRetentionDays = trackingSettingsStore.read().logRetentionDays) }
    }

    fun resetOperationalDefaults() {
        trackingSettingsStore.resetOperationalDefaults()
        foregroundLocationController.stop()
        mobileSyncScheduler.ensurePeriodic()
        reloadOperationalState()
        _state.update { it.copy(collectionStatus = "持续采集已关闭。") }
    }

    fun isNewer(current: String?, remote: String?): Boolean {
        if (remote.isNullOrBlank()) return false
        if (current.isNullOrBlank()) return true
        fun parseN(v: String): Int? {
            val t = v.trim().trimStart('v', 'V')
            if (t.isEmpty()) return null
            val core = t.split("+", "-").firstOrNull()?.trim() ?: return null
            if (core.isEmpty()) return null
            return core.split(".").lastOrNull()?.toIntOrNull()
        }
        val rn = parseN(remote)
        val cn = parseN(current)
        if (rn != null && cn != null) return rn > cn
        return remote.trim() > current.trim()
    }

    suspend fun checkUpdate() {
        try {
            val latest = api.getClientLatest()
            if (latest.error != null) {
                Timber.w("update check failed ${latest.error}")
                _state.update { it.copy(updateError = latest.error, updateCheckedAt = latest.checkedAt) }
                return
            }
            val current = appVersionName()
            val checkedAt = latest.checkedAt
            if (isNewer(current, latest.androidVersion)) {
                _state.update {
                    it.copy(
                        hasUpdate = true,
                        latestVersion = latest.androidVersion,
                        updateUrl = latest.androidUrl,
                        updateCheckedAt = checkedAt,
                        updateError = null
                    )
                }
            } else {
                _state.update {
                    it.copy(
                        hasUpdate = false,
                        latestVersion = latest.androidVersion,
                        updateUrl = latest.androidUrl,
                        updateCheckedAt = checkedAt,
                        updateError = null
                    )
                }
            }
        } catch (e: Exception) {
            if (e is CancellationException) throw e
            Timber.w(e, "update check exception")
            _state.update { it.copy(updateError = e.message) }
        }
    }

    fun checkUpdateAsync() {
        viewModelScope.launch { checkUpdate() }
    }

    suspend fun refreshUpdateForVisibleScreen(): Long {
        checkUpdate()
        return 6 * 60 * 60 * 1000L
    }

    private fun appVersionName(): String {
        return try {
            val pm = appContext.packageManager
            val pkg = appContext.packageName
            val info = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                pm.getPackageInfo(pkg, PackageManager.PackageInfoFlags.of(0))
            } else {
                @Suppress("DEPRECATION") pm.getPackageInfo(pkg, 0)
            }
            info.versionName ?: "unknown"
        } catch (_: Exception) {
            "unknown"
        }
    }

    private fun appVersionInfo(): Pair<String, Long> {
        return try {
            val pm = appContext.packageManager
            val pkg = appContext.packageName
            val info = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                pm.getPackageInfo(pkg, PackageManager.PackageInfoFlags.of(0))
            } else {
                @Suppress("DEPRECATION") pm.getPackageInfo(pkg, 0)
            }
            val vc = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) info.longVersionCode else @Suppress("DEPRECATION") info.versionCode.toLong()
            (info.versionName ?: "unknown") to vc
        } catch (_: Exception) {
            "unknown" to 0L
        }
    }

    private fun resolveGitSha(): String {
        try {
            val clazz = Class.forName("com.pim.app.BuildConfig")
            val field = clazz.getField("GIT_SHA")
            val v = field.get(null) as? String
            if (!v.isNullOrBlank() && v != "unknown") return v
        } catch (_: Exception) {}
        try {
            val env = System.getenv("CI_GIT_SHA")
            if (!env.isNullOrBlank()) return env
        } catch (_: Exception) {}
        try {
            val env2 = System.getenv("GIT_SHA")
            if (!env2.isNullOrBlank()) return env2
        } catch (_: Exception) {}
        return "unknown"
    }

    fun onResume() {
        val snapshot = permissionStatusRepository.snapshot()
        val collectionIntent = persistedCollectionEnabled()
        val (vn, vc) = appVersionInfo()
        val sha = resolveGitSha()
        _state.update {
            it.copy(
                permissions = snapshot,
                continuousCollectionEnabled = collectionIntent,
                appVersion = vn,
                versionCode = vc,
                gitSha = sha
            )
        }
        viewModelScope.launch { checkUpdate() }

        viewModelScope.launch {
            runCatching {
                runningStateRestorer.ensureRunningState()
            }.fold(
                onSuccess = { result ->
                    when (result.collectionState) {
                        CollectionState.Disabled -> {
                            _state.update { it.copy(collectionStatus = null) }
                        }
                        CollectionState.Blocked -> {
                            val missingNames = result.missingPermissions.map { perm ->
                                toDisplayPermissionName(perm)
                            }
                            _state.update {
                                it.copy(
                                    collectionStatus = "缺少权限：${missingNames.joinToString("、")}。",
                                    continuousCollectionEnabled = persistedCollectionEnabled()
                                )
                            }
                        }
                        CollectionState.AlreadyRunning -> {
                            _state.update {
                                it.copy(collectionStatus = "持续采集正在运行。")
                            }
                        }
                        CollectionState.StartRequested -> {
                            _state.update {
                                it.copy(collectionStatus = "已恢复持续采集。")
                            }
                        }
                        CollectionState.Failed -> {
                            _state.update {
                                it.copy(collectionStatus = "恢复失败，请查看状态页。")
                            }
                        }
                    }
                },
                onFailure = { error ->
                    if (error is CancellationException) throw error
                    _state.update {
                        it.copy(collectionStatus = "恢复失败，请查看状态页。")
                    }
                }
            )
        }
    }

    fun requestClearDiagnostics() {
        if (state.value.isBusy) return
        _state.update { it.copy(showClearDiagnosticsConfirmation = true) }
    }

    fun dismissClearDiagnosticsConfirmation() {
        if (state.value.isBusy) return
        _state.update { it.copy(showClearDiagnosticsConfirmation = false) }
    }

    fun confirmClearDiagnostics() {
        if (state.value.isBusy || state.value.isClearingDiagnostics) return
        _state.update {
            it.copy(
                isBusy = true,
                isClearingDiagnostics = true,
                showClearDiagnosticsConfirmation = false,
                diagnosticClearFeedback = null
            )
        }
        viewModelScope.launch {
            runCatching {
                diagnosticOperations.clearDiagnostics()
            }.fold(
                onSuccess = {
                    _state.update {
                        it.copy(
                            diagnosticClearFeedback = DiagnosticClearFeedback.Cleared,
                            isBusy = false,
                            isClearingDiagnostics = false
                        )
                    }
                },
                onFailure = { error ->
                    if (error is CancellationException) throw error
                    _state.update {
                        it.copy(
                            diagnosticClearFeedback = DiagnosticClearFeedback.Failed,
                            isBusy = false,
                            isClearingDiagnostics = false
                        )
                    }
                }
            )
        }
    }

    private fun saveServerUrl(newUrl: String): SaveServerUrlResult {
        val oldOrigin = extractOrigin(serverSettingsStore.getBaseUrl())
        val newOrigin = extractOrigin(newUrl)
        val oldIdentity = serverIdentityFor(serverSettingsStore.getBaseUrl())

        var clearedOldSession = false
        if (newOrigin != oldOrigin && newOrigin != null) {
            val snapshot = tokenManager.snapshot()
            if (snapshot.tokens != null && snapshot.serverIdentity != newOrigin) {
                clearedOldSession = tokenManager.clear()
                if (!clearedOldSession) return SaveServerUrlResult.SessionClearFailed
            }
        }

        val success = runCatching {
            serverSettingsStore.setBaseUrl(newUrl)
        }.isSuccess

        val shouldClearSiteData = oldOrigin != null && newOrigin != oldOrigin
            && (clearedOldSession || success)
        if (shouldClearSiteData) {
            runCatching { webViewSiteDataCleaner.clearOrigin(oldOrigin!!) }
        }

        if (success) {
            val newIdentity = serverIdentityFor(newUrl)
            if (oldIdentity != null && newIdentity != null && oldIdentity != newIdentity) {
                scheduleCacheStore.clear(oldIdentity)
            }
        }

        return if (success) SaveServerUrlResult.Success else SaveServerUrlResult.CommitFailed
    }

    private fun extractOrigin(url: String): String? {
        return runCatching { PimServerEndpoints.from(url).trustedOrigin }.getOrNull()
    }

    private fun missingCollectionPermissions(snapshot: PermissionStatusSnapshot? = null): List<String> {
        val permissions = snapshot ?: permissionStatusRepository.snapshot()
        return buildList {
            if (!permissions.notificationGranted) add("通知")
            if (!permissions.preciseLocationGranted) add("精确定位")
            if (!permissions.backgroundLocationGranted) add("后台定位")
        }
    }

    private fun keepCollectionOff(
        message: String,
        extraState: (SettingsUiState) -> SettingsUiState = { it }
    ) {
        trackingSettingsStore.setContinuousCollectionEnabled(false)
        foregroundLocationController.stop()
        _state.update {
            extraState(
                it.copy(
                    continuousCollectionEnabled = false,
                    collectionStatus = message
                )
            )
        }
    }

    private fun showCollectionBlocked(message: String) {
        _state.update {
            it.copy(
                continuousCollectionEnabled = persistedCollectionEnabled(),
                collectionStatus = message
            )
        }
    }

    suspend fun refreshConnectionForVisibleScreen(): Long {
        val succeeded = runConnectionProbe(force = false)
        return if (succeeded) {
            millisUntilRefresh()
        } else {
            PROBE_RETRY_MILLIS
        }
    }

    private fun reloadOperationalState() {
        val now = System.currentTimeMillis()
        val verboseEnabled = trackingSettingsStore.isVerboseLoggingEnabled(now)
        val settings = trackingSettingsStore.read()
        val (vn, vc) = appVersionInfo()
        val sha = resolveGitSha()
        _state.update {
            it.copy(
                trackingProfile = settings.profile,
                syncOnUnmeteredOnly = settings.syncOnUnmeteredOnly,
                sprintEnabled = settings.sprintEnabled,
                verboseLoggingEnabled = verboseEnabled,
                verboseLoggingUntilUtcMillis = settings.verboseLoggingUntilUtcMillis,
                logRetentionDays = settings.logRetentionDays,
                continuousCollectionEnabled = settings.continuousCollectionEnabled,
                normalMinText = (settings.normalIntervalMillis / 60_000.0).toDisplayNumber(),
                scheduleMinText = (settings.scheduleLowFrequencyIntervalMillis / 60_000.0).toDisplayNumber(),
                movementSecText = (settings.movementIntervalMillis / 1_000.0).toDisplayNumber(),
                recoveryMetersText = settings.scheduleRecoveryThresholdMeters.toFloat().toDisplayNumber(),
                altitudeSecText = (settings.altitudeWaitTimeoutMillis / 1_000.0).toDisplayNumber(),
                appVersion = vn,
                versionCode = vc,
                gitSha = sha
            )
        }
    }

    private fun reloadForegroundCollectionIfEnabled() {
        if (!trackingSettingsStore.read().continuousCollectionEnabled) return
        runCatching {
            foregroundLocationController.start()
        }.onFailure {
            _state.update {
                it.copy(collectionStatus = "设置已保存，但服务启动失败，请查看状态页")
            }
        }
    }

    private suspend fun runConnectionProbe(
        force: Boolean,
        finishBusyState: Boolean = false
    ): Boolean {
        val targetUrl = serverSettingsStore.getBaseUrl()
        val serverIdentity = runCatching {
            PimServerEndpoints.from(targetUrl).apiBaseUrl.toString()
        }.getOrNull()
        return try {
            val probeResult = resolveProbeResult(
                force = force,
                serverIdentity = serverIdentity,
                store = connectionProbeStore,
                probe = { connectionProbeService.probe(targetUrl) },
                save = { r ->
                    if (serverSettingsStore.getBaseUrl() == targetUrl) {
                        connectionProbeStore.save(r)
                    } else false
                },
                nowMillis = System.currentTimeMillis()
            )
            if (finishBusyState) {
                val currentUrl = serverSettingsStore.getBaseUrl()
                if (currentUrl == targetUrl) {
                    _state.update {
                        it.copy(apiStatus = probeResult.statusMessage(), isBusy = false)
                    }
                } else {
                    _state.update { it.copy(isBusy = false) }
                }
            }
            true
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
            if (finishBusyState) {
                val currentUrl = serverSettingsStore.getBaseUrl()
                if (currentUrl == targetUrl) {
                    _state.update {
                        it.copy(apiStatus = "连接测试失败。", isBusy = false)
                    }
                } else {
                    _state.update { it.copy(isBusy = false) }
                }
            }
            false
        }
    }

    private fun millisUntilRefresh(): Long {
        val serverUrl = serverSettingsStore.getBaseUrl()
        val serverIdentity = runCatching {
            com.pim.core.settings.PimServerEndpoints.from(serverUrl).apiBaseUrl.toString()
        }.getOrNull() ?: return ConnectionProbeStore.FRESHNESS_MILLIS
        return probeRefreshDelayMillis(
            result = connectionProbeStore.result.value,
            serverIdentity = serverIdentity,
            nowMillis = System.currentTimeMillis()
        )
    }

    private fun persistedCollectionEnabled(): Boolean {
        return trackingSettingsStore.read().continuousCollectionEnabled
    }

    private fun reloadPersistedServerState(
        apiError: String?,
        apiStatus: String?
    ) {
        val address = serverSettingsStore.getBaseUrl()
        val validation = ServerUrlValidator.validate(address)
        _state.update {
            it.copy(
                apiAddress = address,
                apiWarnings = validation.warnings,
                apiError = apiError ?: validation.reasonCode?.takeUnless { validation.isValid },
                apiStatus = apiStatus,
                isLoggedIn = hasCurrentServerSession(),
                continuousCollectionEnabled = persistedCollectionEnabled()
            )
        }
    }

    private fun hasCurrentServerSession(): Boolean {
        return !tokenManager
            .getAccessTokenForServer(serverSettingsStore.getBaseUrl())
            .isNullOrBlank()
    }

    private fun serverIdentityFor(url: String): String? {
        if (url.isBlank()) return null
        return runCatching {
            PimServerEndpoints.from(url).apiBaseUrl.toString()
        }.getOrNull()
    }
}

private fun Double.toDisplayNumber(): String =
    if (this == toLong().toDouble()) toLong().toString() else toString()

private fun Float.toDisplayNumber(): String =
    if (this == toLong().toFloat()) toLong().toString() else toString()

internal fun toDisplayPermissionName(code: String): String {
    return when (code) {
        "notification" -> "通知"
        "precise_location" -> "精确定位"
        "background_location" -> "后台定位"
        else -> "必要权限"
    }
}

private const val PROBE_RETRY_MILLIS = 30_000L

private fun ConnectionProbeResult.statusMessage(): String {
    return when (outcome) {
        ConnectionProbeOutcome.Reachable -> "连接成功。"
        ConnectionProbeOutcome.Partial -> safeMessage
            ?: "连接部分可用。"
        ConnectionProbeOutcome.Blocked -> safeMessage
            ?: "无法连接服务器。"
    }
}
