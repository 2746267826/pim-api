package com.pim.app.status

import com.pim.app.location.quality.LocationQualityGate
import com.pim.app.schedule.ScheduleCacheFreshness
import com.pim.app.location.service.ForegroundLocationRuntimeState

enum class StatusSeverity {
    Info,
    Warning,
    Critical
}

enum class StatusOverall {
    Normal,
    Attention,
    Abnormal;

    companion object {
        fun compute(issues: List<StatusIssue>): StatusOverall {
            val highest = issues.maxOfOrNull { it.severity } ?: return Normal
            return when (highest) {
                StatusSeverity.Critical -> Abnormal
                StatusSeverity.Warning -> Attention
                StatusSeverity.Info -> Normal
            }
        }
    }
}

enum class SyncPhase {
    Idle,
    Accepted,
    Waiting,
    Running,
    CatchingUp,
    Blocked,
    Completed,
    Failed,
    Cancelled;

}

enum class StatusActionTarget {
    Settings,
    Login,
    Permissions,
    Sync,
    Queue,
    NetworkSettings,
    ConnectionCheck,
    None
}

enum class StatusActionRoute {
    OpenSettings,
    OpenPermissions,
    TriggerSync,
    OpenNetworkSettings,
    ConnectionCheck,
    None
}

object StatusActionRouter {
    fun route(target: StatusActionTarget): StatusActionRoute = when (target) {
        StatusActionTarget.Settings,
        StatusActionTarget.Login -> StatusActionRoute.OpenSettings
        StatusActionTarget.Permissions -> StatusActionRoute.OpenPermissions
        StatusActionTarget.Sync,
        StatusActionTarget.Queue -> StatusActionRoute.TriggerSync
        StatusActionTarget.NetworkSettings -> StatusActionRoute.OpenNetworkSettings
        StatusActionTarget.ConnectionCheck -> StatusActionRoute.ConnectionCheck
        StatusActionTarget.None -> StatusActionRoute.None
    }
}

data class StatusIssue(
    val code: String,
    val severity: StatusSeverity,
    val title: String,
    val message: String,
    val lastOccurredAtMillis: Long? = null,
    val actionLabel: String,
    val target: StatusActionTarget
) {
    companion object {
        fun apiAddressMissing(): StatusIssue = StatusIssue(
            code = "api-address-missing",
            severity = StatusSeverity.Critical,
            title = "配置 API 地址",
            message = "还没有保存可用的 API 地址，手机无法连接服务器。",
            actionLabel = "去设置",
            target = StatusActionTarget.Settings
        )

        fun apiUrlInvalid(reasonCode: String?): StatusIssue = StatusIssue(
            code = "api-url-invalid",
            severity = StatusSeverity.Critical,
            title = "API 地址无效",
            message = StatusDisplayText.apiReason(reasonCode).let { desc ->
                if (desc == "未配置") "当前 API 地址未配置。"
                else if (desc == "地址格式不正确") "当前 API 地址格式不正确。"
                else "当前 API 地址无效。"
            },
            actionLabel = "去设置",
            target = StatusActionTarget.Settings
        )

        fun apiLocalhostWarning(): StatusIssue = StatusIssue(
            code = "api-address-localhost",
            severity = StatusSeverity.Warning,
            title = "API 指向本机地址",
            message = "127.0.0.1 或 localhost 在真机上指向手机自身，请改为公网 IP 或域名。",
            actionLabel = "去设置",
            target = StatusActionTarget.Settings
        )

        fun loginMissing(): StatusIssue = StatusIssue(
            code = "login-missing",
            severity = StatusSeverity.Critical,
            title = "未登录",
            message = "需要登录后才能同步手机数据和定位队列。",
            actionLabel = "去登录",
            target = StatusActionTarget.Login
        )

        fun loginExpired(): StatusIssue = StatusIssue(
            code = "login-expired",
            severity = StatusSeverity.Critical,
            title = "登录已过期",
            message = "访问令牌已过期，请重新登录。",
            actionLabel = "去登录",
            target = StatusActionTarget.Login
        )

        fun notificationPermissionMissing(): StatusIssue = StatusIssue(
            code = "notification-permission-missing",
            severity = StatusSeverity.Warning,
            title = "通知未授权",
            message = "持续采集需要显示包含采集状态的常驻通知。",
            actionLabel = "去设置",
            target = StatusActionTarget.Permissions
        )

        fun foregroundLocationMissing(): StatusIssue = StatusIssue(
            code = "foreground-location-missing",
            severity = StatusSeverity.Critical,
            title = "前台定位未授权",
            message = "缺少精确定位权限，无法采集符合精度要求的位置。",
            actionLabel = "去授权",
            target = StatusActionTarget.Permissions
        )

        fun backgroundLocationMissing(): StatusIssue = StatusIssue(
            code = "background-location-missing",
            severity = StatusSeverity.Critical,
            title = "后台定位未授权",
            message = "持续采集需要“始终允许”定位权限。",
            actionLabel = "去设置",
            target = StatusActionTarget.Permissions
        )

        fun usageAccessMissing(): StatusIssue = StatusIssue(
            code = "usage-access-missing",
            severity = StatusSeverity.Warning,
            title = "使用情况权限未授权",
            message = "缺少使用情况访问权限，手机使用摘要将无法同步。",
            actionLabel = "去授权",
            target = StatusActionTarget.Permissions
        )

        fun activityRecognitionMissing(): StatusIssue = StatusIssue(
            code = "activity-recognition-missing",
            severity = StatusSeverity.Warning,
            title = "运动识别未授权",
            message = "缺少运动识别权限，移动恢复只能依赖位置变化。",
            actionLabel = "去授权",
            target = StatusActionTarget.Permissions
        )

        fun batteryOptimizationMissing(): StatusIssue = StatusIssue(
            code = "battery-optimization-missing",
            severity = StatusSeverity.Warning,
            title = "电池优化未豁免",
            message = "电池优化会影响持续采集。",
            actionLabel = "去授权",
            target = StatusActionTarget.Permissions
        )

        fun scheduleCacheStale(): StatusIssue = StatusIssue(
            code = "schedule-cache-stale",
            severity = StatusSeverity.Warning,
            title = "日程数据可能过期",
            message = "日程数据可能不是最新，请检查网络连接。",
            actionLabel = "检查连接",
            target = StatusActionTarget.ConnectionCheck
        )

        fun scheduleCacheError(): StatusIssue = StatusIssue(
            code = "schedule-cache-error",
            severity = StatusSeverity.Critical,
            title = "日程数据暂时不可用",
            message = "日程数据暂时不可用，请稍后重试或检查网络连接。",
            actionLabel = "检查连接",
            target = StatusActionTarget.ConnectionCheck
        )

        fun foregroundServiceNotRunning(): StatusIssue = StatusIssue(
            code = "foreground-service-not-running",
            severity = StatusSeverity.Critical,
            title = "前台定位服务未运行",
            message = "持续采集已开启，但前台定位服务没有运行。",
            actionLabel = "查看采集设置",
            target = StatusActionTarget.Settings
        )

        fun locationAccuracyRejected(lastOccurredAtMillis: Long? = null): StatusIssue = StatusIssue(
            code = "location-accuracy-rejected",
            severity = StatusSeverity.Warning,
            title = "定位精度不达标",
            // AC-12.3：与采集门槛同一口径（引用 LocationQualityGate 常量派生），
            // 旧的「大于等于 50m」是查询侧 maxAccuracyMeters 口径，与采集门槛无关。
            message = "最近有定位点因水平精度缺失或大于等于 " +
                "${LocationQualityGate.displayThresholdMeters()}m 被丢弃。",
            lastOccurredAtMillis = lastOccurredAtMillis,
            actionLabel = "查看采集设置",
            target = StatusActionTarget.Settings
        )

        fun altitudeMissingTimeout(lastOccurredAtMillis: Long? = null): StatusIssue = StatusIssue(
            code = "altitude-missing-timeout",
            severity = StatusSeverity.Info,
            title = "高度等待超时",
            message = "最近有定位点等待 15 秒后仍缺少高度，已按 null 高度并附带质量标记处理。",
            lastOccurredAtMillis = lastOccurredAtMillis,
            actionLabel = "查看采集设置",
            target = StatusActionTarget.Settings
        )

        fun uploadQueueBacklog(count: Int): StatusIssue = StatusIssue(
            code = "upload-queue-backlog",
            severity = StatusSeverity.Warning,
            title = "上传队列积压",
            message = "当前有 $count 条定位记录等待上传。",
            actionLabel = "立即同步",
            target = StatusActionTarget.Queue
        )

        fun networkDisconnected(): StatusIssue = StatusIssue(
            code = "network-disconnected",
            severity = StatusSeverity.Critical,
            title = "网络未连接",
            message = "当前设备没有可用的网络连接，无法与服务器通信。",
            actionLabel = "去设置网络",
            target = StatusActionTarget.NetworkSettings
        )

        fun systemNetworkRestricted(severity: StatusSeverity = StatusSeverity.Info): StatusIssue = StatusIssue(
            code = "system-network-restricted",
            severity = severity,
            title = "系统网络受限",
            message = if (severity == StatusSeverity.Info) {
                "系统未将当前网络标记为已验证，但 PIM 服务器探测成功。"
            } else {
                "系统未确认当前网络可访问互联网，PIM 服务器可能无法连接。"
            },
            actionLabel = if (severity == StatusSeverity.Warning) "去设置网络" else "",
            target = if (severity == StatusSeverity.Warning) StatusActionTarget.NetworkSettings else StatusActionTarget.None
        )

        fun probeBlocked(): StatusIssue = StatusIssue(
            code = "connection-probe-blocked",
            severity = StatusSeverity.Critical,
            title = "服务器连接中断",
            message = "连接探测未能成功到达服务器，请检查网络或服务器状态。",
            actionLabel = "重新检查连接",
            target = StatusActionTarget.ConnectionCheck
        )

        fun probePartial(): StatusIssue = StatusIssue(
            code = "connection-probe-partial",
            severity = StatusSeverity.Warning,
            title = "服务器连接不稳定",
            message = "连接探测部分成功，可能存在网络或服务器问题。",
            actionLabel = "重新检查连接",
            target = StatusActionTarget.ConnectionCheck
        )

        fun heartbeatFailure(): StatusIssue = StatusIssue(
            code = "heartbeat-failure",
            severity = StatusSeverity.Warning,
            title = "心跳上报异常",
            message = "最近一次心跳上报异常。",
            actionLabel = "重新同步",
            target = StatusActionTarget.Sync
        )

        fun syncFailure(): StatusIssue = StatusIssue(
            code = "sync-failure",
            severity = StatusSeverity.Critical,
            title = "同步失败",
            message = "最近同步出现异常，请导出日志查看详情。",
            actionLabel = "重新同步",
            target = StatusActionTarget.Sync
        )

        fun recentDroppedLocation(reason: String, lastOccurredAtMillis: Long?): StatusIssue = StatusIssue(
            code = "location-dropped-recent",
            severity = StatusSeverity.Info,
            title = "最近丢弃定位",
            message = "最近一次丢弃原因：${StatusDisplayText.droppedReason(reason)}。",
            lastOccurredAtMillis = lastOccurredAtMillis,
            actionLabel = "",
            target = StatusActionTarget.None
        )
    }
}

internal fun actionableStatusIssues(issues: List<StatusIssue>): List<StatusIssue> = issues
    .filter { it.severity != StatusSeverity.Info }
    .sortedBy { if (it.severity == StatusSeverity.Critical) 0 else 1 }

internal fun informationalStatusIssues(issues: List<StatusIssue>): List<StatusIssue> =
    issues.filter { it.severity == StatusSeverity.Info }

data class PermissionStatusSnapshot(
    val notificationGranted: Boolean,
    val preciseLocationGranted: Boolean,
    val backgroundLocationGranted: Boolean,
    val usageAccessGranted: Boolean,
    val activityRecognitionGranted: Boolean,
    val batteryOptimizationGranted: Boolean
)

data class ApiConnectionSnapshot(
    val address: String,
    val isValid: Boolean,
    val reasonCode: String?,
    val warnings: Set<String>
)

data class AuthStatusSnapshot(
    val hasAccessToken: Boolean,
    val isExpired: Boolean
)

data class ForegroundServiceSnapshot(
    val continuousCollectionEnabled: Boolean,
    val serviceRunning: Boolean
)

data class ScheduleCacheStatusSnapshot(
    val freshness: ScheduleCacheFreshness = ScheduleCacheFreshness.Missing,
    val hasCachedWindows: Boolean = false,
    val lastSuccessAtMillis: Long? = null,
    val lastAttemptAtMillis: Long? = null,
    val lastError: String? = null
)

data class PolicyTransitionSnapshot(
    val fromMode: String?,
    val toMode: String,
    val reason: String,
    val occurredAtMillis: Long
)

data class TrackingPolicySnapshot(
    val profile: String,
    val currentPolicyMode: String,
    val nextExpectedLocationAtMillis: Long? = null,
    val currentPolicyReason: String? = null,
    val requestIntervalMillis: Long? = null
)

data class QueueStatusSnapshot(
    val pendingLocationPoints: Int,
    val pendingUsageEvents: Int,
    val pendingUsageSummaries: Int,
    val pendingAppMetadata: Int,
    val pendingDeviceProfile: Int,
    val pendingSyncBatches: Int = 0
) {
    val pendingUploadTotal: Int
        get() = pendingLocationPoints +
            pendingUsageEvents +
            pendingUsageSummaries +
            pendingAppMetadata +
            pendingDeviceProfile +
            pendingSyncBatches
}

data class DiagnosticSnapshot(
    val lastDroppedReason: String?,
    val lastDroppedAtMillis: Long?,
    val lastLogMessage: String?,
    val lastHeartbeatStatus: String?,
    val recentLogMessages: List<String> = emptyList()
)

object StatusTrackingMapper {
    fun fromRuntime(
        profile: String,
        runtime: ForegroundLocationRuntimeState
    ): TrackingPolicySnapshot = TrackingPolicySnapshot(
        profile = profile,
        currentPolicyMode = runtime.currentPolicyMode,
        currentPolicyReason = runtime.currentPolicyReason,
        requestIntervalMillis = runtime.requestIntervalMillis,
        nextExpectedLocationAtMillis = runtime.nextExpectedLocationAtMillis
    )
}

data class StatusCenterSnapshot(
    val permissions: PermissionStatusSnapshot,
    val api: ApiConnectionSnapshot,
    val auth: AuthStatusSnapshot,
    val service: ForegroundServiceSnapshot,
    val tracking: TrackingPolicySnapshot,
    val queues: QueueStatusSnapshot,
    val diagnostics: DiagnosticSnapshot,
    val schedule: ScheduleCacheStatusSnapshot = ScheduleCacheStatusSnapshot(),
    val recentPolicyTransitions: List<PolicyTransitionSnapshot> = emptyList()
)

data class StatusCenterState(
    val snapshot: StatusCenterSnapshot,
    val issues: List<StatusIssue>,
    val overall: StatusOverall = StatusOverall.compute(issues),
    val syncPhase: SyncPhase = SyncPhase.Idle,
    val pendingTotal: Int = 0,
    val acceptedCount: Int = 0,
    val permanentRejectedCount: Int = 0,
    val rejectedCount: Int = 0,
    val lastSuccessfulUploadAt: String? = null,
    val lastAttemptedUploadAt: String? = null,
    val nextAttemptAtMillis: Long? = null,
    val networkAvailability: NetworkAvailability = NetworkAvailability.Unavailable,
    val lastProbeResult: ConnectionProbeResult? = null,
    val lastProbeCheckedAtMillis: Long? = null,
    val isLoading: Boolean = false
) {
    companion object {
        fun empty(): StatusCenterState {
            val snapshot = StatusCenterSnapshot(
                permissions = PermissionStatusSnapshot(
                    notificationGranted = false,
                    preciseLocationGranted = false,
                    backgroundLocationGranted = false,
                    usageAccessGranted = false,
                    activityRecognitionGranted = false,
                    batteryOptimizationGranted = false
                ),
                api = ApiConnectionSnapshot("", isValid = false, reasonCode = null, warnings = emptySet()),
                auth = AuthStatusSnapshot(hasAccessToken = false, isExpired = false),
                service = ForegroundServiceSnapshot(continuousCollectionEnabled = false, serviceRunning = false),
                tracking = TrackingPolicySnapshot("power-saving", "PowerSavingNormal", null),
                queues = QueueStatusSnapshot(0, 0, 0, 0, 0, 0),
                diagnostics = DiagnosticSnapshot(null, null, null, null)
            )
            return StatusCenterState(
                snapshot = snapshot,
                issues = emptyList(),
                isLoading = true
            )
        }
    }
}

object StatusIssuePlanner {
    private const val LOCATION_QUEUE_BACKLOG_THRESHOLD = 10

    fun plan(snapshot: StatusCenterSnapshot): List<StatusIssue> {
        val issues = mutableListOf<StatusIssue>()

        if (snapshot.api.address.isBlank() || snapshot.api.reasonCode == "missing") {
            issues += StatusIssue.apiAddressMissing()
        } else if (!snapshot.api.isValid) {
            issues += StatusIssue.apiUrlInvalid(snapshot.api.reasonCode)
        }
        if ("real-device-localhost" in snapshot.api.warnings) {
            issues += StatusIssue.apiLocalhostWarning()
        }

        if (!snapshot.auth.hasAccessToken) {
            issues += StatusIssue.loginMissing()
        } else if (snapshot.auth.isExpired) {
            issues += StatusIssue.loginExpired()
        }

        if (!snapshot.permissions.notificationGranted) {
            issues += StatusIssue.notificationPermissionMissing()
        }
        if (!snapshot.permissions.preciseLocationGranted) {
            issues += StatusIssue.foregroundLocationMissing()
        }
        if (!snapshot.permissions.backgroundLocationGranted) {
            issues += StatusIssue.backgroundLocationMissing()
        }
        if (!snapshot.permissions.usageAccessGranted) {
            issues += StatusIssue.usageAccessMissing()
        }
        if (!snapshot.permissions.activityRecognitionGranted) {
            issues += StatusIssue.activityRecognitionMissing()
        }
        if (!snapshot.permissions.batteryOptimizationGranted) {
            issues += StatusIssue.batteryOptimizationMissing()
        }

        if (snapshot.service.continuousCollectionEnabled && !snapshot.service.serviceRunning) {
            issues += StatusIssue.foregroundServiceNotRunning()
        }

        when (snapshot.diagnostics.lastDroppedReason) {
            "missing-horizontal-accuracy",
            "horizontal-accuracy-too-low" -> issues += StatusIssue.locationAccuracyRejected(
                snapshot.diagnostics.lastDroppedAtMillis
            )
            "altitude-missing-timeout" -> issues += StatusIssue.altitudeMissingTimeout(
                snapshot.diagnostics.lastDroppedAtMillis
            )
            null, "" -> { /* no issue */ }
            else -> issues += StatusIssue.recentDroppedLocation(
                snapshot.diagnostics.lastDroppedReason,
                snapshot.diagnostics.lastDroppedAtMillis
            )
        }

        if (snapshot.queues.pendingLocationPoints >= LOCATION_QUEUE_BACKLOG_THRESHOLD) {
            issues += StatusIssue.uploadQueueBacklog(snapshot.queues.pendingLocationPoints)
        }

        when (snapshot.schedule.freshness) {
            ScheduleCacheFreshness.Stale -> {
                if (snapshot.schedule.hasCachedWindows) {
                    issues += StatusIssue.scheduleCacheStale()
                }
            }
            ScheduleCacheFreshness.Missing -> {
                if (snapshot.schedule.lastError != null) {
                    issues += StatusIssue.scheduleCacheError()
                }
            }
            ScheduleCacheFreshness.Fresh -> { /* no issue */ }
        }

        val heartbeat = snapshot.diagnostics.lastHeartbeatStatus.orEmpty()
        if (heartbeat.contains("fail", ignoreCase = true) || heartbeat.contains("失败")) {
            issues += StatusIssue.heartbeatFailure()
        }

        return issues.distinctBy { it.code }
    }
}
