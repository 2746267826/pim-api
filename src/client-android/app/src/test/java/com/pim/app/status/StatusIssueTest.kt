package com.pim.app.status

import com.pim.app.location.PolicyTransitionWriteFailure
import com.pim.app.location.service.ForegroundLocationRuntimeState
import com.pim.app.schedule.ScheduleCacheFreshness
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlinx.coroutines.runBlocking

class StatusIssueTest {
    @Test
    fun actionLabelsAreReadable() {
        assertEquals("去设置", StatusIssue.apiAddressMissing().actionLabel)
        assertEquals("去设置", StatusIssue.backgroundLocationMissing().actionLabel)
        assertEquals("立即同步", StatusIssue.uploadQueueBacklog(18).actionLabel)
        assertEquals("重新检查连接", StatusIssue.probeBlocked().actionLabel)
        assertEquals("重新检查连接", StatusIssue.probePartial().actionLabel)
    }

    @Test
    fun snapshotPlannerAddsActionableBlockingIssues() {
        val snapshot = StatusCenterSnapshot(
            permissions = PermissionStatusSnapshot(
                notificationGranted = false,
                preciseLocationGranted = true,
                backgroundLocationGranted = false,
                usageAccessGranted = true,
                activityRecognitionGranted = false,
                batteryOptimizationGranted = false
            ),
            api = ApiConnectionSnapshot(
                address = "",
                isValid = false,
                reasonCode = "missing",
                warnings = emptySet()
            ),
            auth = AuthStatusSnapshot(hasAccessToken = false, isExpired = false),
            service = ForegroundServiceSnapshot(
                continuousCollectionEnabled = true,
                serviceRunning = false
            ),
            tracking = TrackingPolicySnapshot(
                profile = "power-saving",
                currentPolicyMode = "PowerSavingNormal",
                nextExpectedLocationAtMillis = null
            ),
            queues = QueueStatusSnapshot(
                pendingLocationPoints = 12,
                pendingUsageEvents = 0,
                pendingUsageSummaries = 0,
                pendingAppMetadata = 0,
                pendingDeviceProfile = 0
            ),
            diagnostics = DiagnosticSnapshot(
                lastDroppedReason = "horizontal-accuracy-too-low",
                lastDroppedAtMillis = 1_000L,
                lastLogMessage = "timeout",
                lastHeartbeatStatus = "failed",
                recentLogMessages = listOf("timeout", "retry scheduled")
            )
        )

        val issues = StatusIssuePlanner.plan(snapshot).associateBy { it.code }

        assertEquals("配置 API 地址", issues.getValue("api-address-missing").title)
        assertEquals("后台定位未授权", issues.getValue("background-location-missing").title)
        assertEquals("前台定位服务未运行", issues.getValue("foreground-service-not-running").title)
        assertEquals("定位精度不达标", issues.getValue("location-accuracy-rejected").title)
        assertEquals("上传队列积压", issues.getValue("upload-queue-backlog").title)
        assertFalse("current-policy-state must not appear when policy is valid", issues.containsKey("current-policy-state"))
        assertFalse("location-dropped-recent must not appear for known dropped reason", issues.containsKey("location-dropped-recent"))
    }

    @Test
    fun trackingSnapshotUsesForegroundServiceRuntimeState() {
        val runtime = ForegroundLocationRuntimeState(
            isRunning = true,
            currentPolicyMode = "ScheduleLowFrequency",
            nextExpectedLocationAtMillis = 2_000L
        )

        val tracking = StatusTrackingMapper.fromRuntime("power-saving", runtime)

        assertEquals("power-saving", tracking.profile)
        assertEquals("ScheduleLowFrequency", tracking.currentPolicyMode)
        assertEquals(2_000L, tracking.nextExpectedLocationAtMillis)
    }

    @Test
    fun diagnosticSnapshotKeepsRecentLogs() {
        val diagnostics = DiagnosticSnapshot(
            lastDroppedReason = null,
            lastDroppedAtMillis = null,
            lastLogMessage = "first",
            lastHeartbeatStatus = null,
            recentLogMessages = listOf("first", "second")
        )

        assertEquals(listOf("first", "second"), diagnostics.recentLogMessages)
    }

    @Test
    fun actionRouterMapsTargetsToVisibleActions() {
        assertEquals(StatusActionRoute.OpenSettings, StatusActionRouter.route(StatusActionTarget.Settings))
        assertEquals(StatusActionRoute.OpenSettings, StatusActionRouter.route(StatusActionTarget.Login))
        assertEquals(StatusActionRoute.OpenPermissions, StatusActionRouter.route(StatusActionTarget.Permissions))
        assertEquals(StatusActionRoute.TriggerSync, StatusActionRouter.route(StatusActionTarget.Sync))
        assertEquals(StatusActionRoute.TriggerSync, StatusActionRouter.route(StatusActionTarget.Queue))
        assertEquals(StatusActionRoute.OpenNetworkSettings, StatusActionRouter.route(StatusActionTarget.NetworkSettings))
        assertEquals(StatusActionRoute.ConnectionCheck, StatusActionRouter.route(StatusActionTarget.ConnectionCheck))
        assertEquals(StatusActionRoute.None, StatusActionRouter.route(StatusActionTarget.None))
    }

    @Test
    fun syncActionRunnerRunsMobileSyncAndRefreshesStatus() = runBlocking {
        var synced = false
        var refreshed = false
        val runner = StatusSyncActionRunner(
            syncNow = { _ -> synced = true },
            refresh = { refreshed = true },
            acceptedSignal = StatusAcceptedSignal()
        )

        runner.run(StatusActionRoute.TriggerSync)

        assertTrue(synced)
        assertTrue(refreshed)
    }

    @Test
    fun syncActionRunnerIgnoresNonSyncRoutes() = runBlocking {
        var synced = false
        val runner = StatusSyncActionRunner(
            syncNow = { _ -> synced = true },
            refresh = {},
            acceptedSignal = StatusAcceptedSignal()
        )

        runner.run(StatusActionRoute.OpenSettings)

        assertFalse(synced)
    }

    @Test
    fun refreshSignalStartsAtZeroAndAdvances() {
        val signal = StatusRefreshSignal()

        assertEquals(0L, signal.version.value)
        signal.requestRefresh()

        assertEquals(1L, signal.version.value)
    }

    @Test
    fun acceptedSignalStartsFalse() {
        val signal = StatusAcceptedSignal()
        assertEquals(false, signal.state.value.isAccepted)
    }

    @Test
    fun acceptedSignalTriggerSetsTrue() {
        val signal = StatusAcceptedSignal()
        signal.trigger()
        assertEquals(true, signal.state.value.isAccepted)
    }

    @Test
    fun acceptedSignalClearIfGenerationResetsMatchingTrigger() {
        val signal = StatusAcceptedSignal()
        val generation = signal.trigger()
        signal.clearIfGeneration(generation)
        assertEquals(false, signal.state.value.isAccepted)
    }

    @Test
    fun acceptedSignalClearIfGenerationDoesNothingWhenNotTriggered() {
        val signal = StatusAcceptedSignal()
        signal.clearIfGeneration(0L)
        assertEquals(false, signal.state.value.isAccepted)
    }

    @Test
    fun syncActionPublishesAcceptedOnlyAfterEnqueueSucceeds() = runBlocking {
        val signal = StatusAcceptedSignal()
        var acceptedAtSyncTime = false
        var refreshed = false
        val runner = StatusSyncActionRunner(
            syncNow = { _ ->
                acceptedAtSyncTime = signal.state.value.isAccepted
            },
            refresh = { refreshed = true },
            acceptedSignal = signal
        )

        runner.run(StatusActionRoute.TriggerSync)

        assertFalse("accepted must NOT be true during syncNow", acceptedAtSyncTime)
        assertTrue(refreshed)
        assertTrue("accepted must be true after syncNow succeeds", signal.state.value.isAccepted)
    }

    @Test
    fun syncActionRunnerPublishesAcceptedAndRefresh() = runBlocking {
        var synced = false
        var refreshed = false
        val signal = StatusAcceptedSignal()
        val runner = StatusSyncActionRunner(
            syncNow = { _ -> synced = true },
            refresh = { refreshed = true },
            acceptedSignal = signal
        )

        runner.run(StatusActionRoute.TriggerSync)

        assertTrue(synced)
        assertTrue(refreshed)
        assertEquals(true, signal.state.value.isAccepted)
    }

    @Test
    fun syncActionRunnerDoesNotPublishAcceptedForNonSyncRoutes() = runBlocking {
        var synced = false
        val signal = StatusAcceptedSignal()
        val runner = StatusSyncActionRunner(
            syncNow = { _ -> synced = true },
            refresh = {},
            acceptedSignal = signal
        )

        runner.run(StatusActionRoute.OpenSettings)

        assertFalse(synced)
        assertEquals(false, signal.state.value.isAccepted)
    }

    @Test
    fun syncActionFailureDoesNotPublishAccepted() = runBlocking {
        val signal = StatusAcceptedSignal()
        val expectedException = RuntimeException("sync failed")
        var refreshCount = 0
        val runner = StatusSyncActionRunner(
            syncNow = { _ -> throw expectedException },
            refresh = { refreshCount++ },
            acceptedSignal = signal
        )

        val actualException = try {
            runner.run(StatusActionRoute.TriggerSync)
            null
        } catch (e: Throwable) {
            e
        }

        assertSame(expectedException, actualException)
        assertFalse("accepted must not be published on exception", signal.state.value.isAccepted)
        assertEquals(1, refreshCount)
    }

    @Test
    fun syncActionForwardsAllowMeteredOnce() = runBlocking {
        var captured: Boolean? = null
        val runner = StatusSyncActionRunner(
            syncNow = { captured = it },
            refresh = {},
            acceptedSignal = StatusAcceptedSignal()
        )

        runner.run(StatusActionRoute.TriggerSync, allowMeteredOnce = true)

        assertEquals(true, captured)
    }

    @Test
    fun syncActionDefaultsAllowMeteredOnceToFalse() = runBlocking {
        var captured: Boolean? = null
        val runner = StatusSyncActionRunner(
            syncNow = { captured = it },
            refresh = {},
            acceptedSignal = StatusAcceptedSignal()
        )

        runner.run(StatusActionRoute.TriggerSync)

        assertEquals(false, captured)
    }

    @Test
    fun pendingUploadTotalAggregatesAllQueues() {
        val queues = QueueStatusSnapshot(
            pendingLocationPoints = 10,
            pendingUsageEvents = 5,
            pendingUsageSummaries = 3,
            pendingAppMetadata = 2,
            pendingDeviceProfile = 1,
            pendingSyncBatches = 0
        )
        assertEquals(10 + 5 + 3 + 2 + 1 + 0, queues.pendingUploadTotal)
    }

    @Test
    fun snapshotPlannerAddsBatteryOptimizationIssue() {
        val snapshot = StatusCenterSnapshot(
            permissions = PermissionStatusSnapshot(
                notificationGranted = true,
                preciseLocationGranted = true,
                backgroundLocationGranted = true,
                usageAccessGranted = true,
                activityRecognitionGranted = true,
                batteryOptimizationGranted = false
            ),
            api = ApiConnectionSnapshot(
                address = "https://valid.example",
                isValid = true,
                reasonCode = null,
                warnings = emptySet()
            ),
            auth = AuthStatusSnapshot(hasAccessToken = true, isExpired = false),
            service = ForegroundServiceSnapshot(
                continuousCollectionEnabled = true,
                serviceRunning = true
            ),
            tracking = TrackingPolicySnapshot(
                profile = "power-saving",
                currentPolicyMode = "PowerSavingNormal",
                nextExpectedLocationAtMillis = null
            ),
            queues = QueueStatusSnapshot(0, 0, 0, 0, 0, 0),
            diagnostics = DiagnosticSnapshot(null, null, null, null)
        )

        val issues = StatusIssuePlanner.plan(snapshot).associateBy { it.code }

        assertTrue(issues.containsKey("battery-optimization-missing"))
        val issue = issues.getValue("battery-optimization-missing")
        assertEquals(StatusSeverity.Warning, issue.severity)
        assertTrue(issue.title.contains("电池") || issue.title.contains("优化"))
        assertEquals(StatusActionTarget.Permissions, issue.target)
    }

    @Test
    fun unknownDroppedReasonProducesLocationDroppedRecent() {
        val snapshot = StatusCenterSnapshot(
            permissions = PermissionStatusSnapshot(true, true, true, true, true, true),
            api = ApiConnectionSnapshot("https://valid.example", isValid = true, reasonCode = null, warnings = emptySet()),
            auth = AuthStatusSnapshot(hasAccessToken = true, isExpired = false),
            service = ForegroundServiceSnapshot(continuousCollectionEnabled = true, serviceRunning = true),
            tracking = TrackingPolicySnapshot("power-saving", "", null),
            queues = QueueStatusSnapshot(0, 0, 0, 0, 0, 0),
            diagnostics = DiagnosticSnapshot(
                lastDroppedReason = "some-unknown-reason",
                lastDroppedAtMillis = 42_000L,
                lastLogMessage = null,
                lastHeartbeatStatus = null,
                recentLogMessages = emptyList()
            )
        )

        val issues = StatusIssuePlanner.plan(snapshot)
        assertTrue("unknown dropped reason must produce location-dropped-recent", issues.any { it.code == "location-dropped-recent" })
    }

    @Test
    fun networkDisconnectedTargetsNetworkSettings() {
        val issue = StatusIssue.networkDisconnected()
        assertEquals(StatusActionTarget.NetworkSettings, issue.target)
    }

    @Test
    fun probeBlockedTargetsConnectionCheck() {
        val issue = StatusIssue.probeBlocked()
        assertEquals(StatusActionTarget.ConnectionCheck, issue.target)
        assertEquals("重新检查连接", issue.actionLabel)
    }

    @Test
    fun probePartialTargetsConnectionCheck() {
        val issue = StatusIssue.probePartial()
        assertEquals(StatusActionTarget.ConnectionCheck, issue.target)
        assertEquals("重新检查连接", issue.actionLabel)
    }

    @Test
    fun foregroundServiceNotRunningOpensCollectionSettings() {
        val issue = StatusIssue.foregroundServiceNotRunning()
        assertEquals(StatusActionTarget.Settings, issue.target)
        assertEquals("查看采集设置", issue.actionLabel)
    }

    @Test
    fun locationAccuracyRejectedOpensCollectionSettings() {
        assertEquals(StatusActionTarget.Settings, StatusIssue.locationAccuracyRejected().target)
    }

    @Test
    fun altitudeMissingTimeoutOpensCollectionSettings() {
        assertEquals(StatusActionTarget.Settings, StatusIssue.altitudeMissingTimeout().target)
    }

    @Test
    fun uploadBacklogActionMatchesItsSyncRoute() {
        val issue = StatusIssue.uploadQueueBacklog(18)
        assertEquals("立即同步", issue.actionLabel)
        assertEquals(StatusActionTarget.Queue, issue.target)
        assertEquals(StatusActionRoute.TriggerSync, StatusActionRouter.route(issue.target))
    }

    @Test
    fun recentDroppedLocationHasNoAction() {
        assertEquals(StatusActionTarget.None, StatusIssue.recentDroppedLocation("x", null).target)
    }

    @Test
    fun routeForConnectionCheckReturnsConnectionCheck() {
        assertEquals(StatusActionRoute.ConnectionCheck, StatusActionRouter.route(StatusActionTarget.ConnectionCheck))
    }

    @Test
    fun routeForNetworkSettingsReturnsOpenNetworkSettings() {
        assertEquals(StatusActionRoute.OpenNetworkSettings, StatusActionRouter.route(StatusActionTarget.NetworkSettings))
    }

    @Test
    fun routeForNoneReturnsNone() {
        assertEquals(StatusActionRoute.None, StatusActionRouter.route(StatusActionTarget.None))
    }

    @Test
    fun syncFailureAlwaysUsesFixedSummary() {
        val issue = StatusIssue.syncFailure()
        assertEquals("最近同步出现异常，请导出日志查看详情。", issue.message)
        assertEquals(StatusSeverity.Critical, issue.severity)
        assertEquals(StatusActionTarget.Sync, issue.target)
    }

    @Test
    fun heartbeatFailureAlwaysUsesFixedSummary() {
        val issue = StatusIssue.heartbeatFailure()
        assertEquals("最近一次心跳上报异常。", issue.message)
        assertEquals(StatusSeverity.Warning, issue.severity)
        assertEquals(StatusActionTarget.Sync, issue.target)
    }

    @Test
    fun needAttentionContainsOnlyCriticalAndWarningInSeverityOrder() {
        val issues = listOf(
            StatusIssue.altitudeMissingTimeout(), // Info
            StatusIssue.probeBlocked(), // Critical
            StatusIssue.usageAccessMissing(), // Warning
            StatusIssue.apiAddressMissing(), // Critical
            StatusIssue.heartbeatFailure(), // Warning
            StatusIssue.recentDroppedLocation("test", null), // Info
        )
        val actionable = actionableStatusIssues(issues)
        val info = informationalStatusIssues(issues)

        assertEquals(4, actionable.size)
        assertTrue(actionable.all { it.severity != StatusSeverity.Info })
        assertEquals(
            listOf(
                StatusSeverity.Critical,
                StatusSeverity.Critical,
                StatusSeverity.Warning,
                StatusSeverity.Warning
            ),
            actionable.map { it.severity }
        )
        assertEquals(2, info.size)
        assertTrue(info.all { it.severity == StatusSeverity.Info })
    }

    @Test
    fun statusInformationAppearsOnlyWhenInfoExists() {
        val noInfo = listOf(
            StatusIssue.probeBlocked(), StatusIssue.usageAccessMissing()
        )
        val infoIssues = listOf(
            StatusIssue.altitudeMissingTimeout(),
            StatusIssue.recentDroppedLocation("x", null)
        )

        val info = informationalStatusIssues(noInfo)
        assertEquals(0, info.size)

        val info2 = informationalStatusIssues(infoIssues)
        assertEquals(2, info2.size)
    }

    @Test
    fun pendingUploadSnapshotHasNoPendingLogsField() {
        val hasField = runCatching {
            QueueStatusSnapshot::class.java.getDeclaredField("pendingLogs")
        }.isSuccess
        assertFalse("pendingLogs field must be removed from QueueStatusSnapshot", hasField)

        val q = QueueStatusSnapshot(
            pendingLocationPoints = 10,
            pendingUsageEvents = 5,
            pendingUsageSummaries = 3,
            pendingAppMetadata = 2,
            pendingDeviceProfile = 1,
            pendingSyncBatches = 0
        )
        assertEquals(10 + 5 + 3 + 2 + 1 + 0, q.pendingUploadTotal)
    }

    @Test
    fun `freshCacheNoScheduleIssue`() {
        val snapshot = StatusCenterSnapshot(
            permissions = PermissionStatusSnapshot(true, true, true, true, true, true),
            api = ApiConnectionSnapshot("https://valid.example", isValid = true, reasonCode = null, warnings = emptySet()),
            auth = AuthStatusSnapshot(hasAccessToken = true, isExpired = false),
            service = ForegroundServiceSnapshot(continuousCollectionEnabled = true, serviceRunning = true),
            tracking = TrackingPolicySnapshot("power-saving", "PowerSavingNormal", null),
            queues = QueueStatusSnapshot(0, 0, 0, 0, 0, 0),
            diagnostics = DiagnosticSnapshot(null, null, null, null),
            schedule = ScheduleCacheStatusSnapshot(
                freshness = ScheduleCacheFreshness.Fresh,
                hasCachedWindows = true
            )
        )
        val issues = StatusIssuePlanner.plan(snapshot)
        assertFalse("Fresh cache must not produce schedule issue", issues.any { it.code.startsWith("schedule-cache-") })
    }

    @Test
    fun `staleCacheWithWindowsCreatesWarning`() {
        val snapshot = StatusCenterSnapshot(
            permissions = PermissionStatusSnapshot(true, true, true, true, true, true),
            api = ApiConnectionSnapshot("https://valid.example", isValid = true, reasonCode = null, warnings = emptySet()),
            auth = AuthStatusSnapshot(hasAccessToken = true, isExpired = false),
            service = ForegroundServiceSnapshot(continuousCollectionEnabled = true, serviceRunning = true),
            tracking = TrackingPolicySnapshot("power-saving", "PowerSavingNormal", null),
            queues = QueueStatusSnapshot(0, 0, 0, 0, 0, 0),
            diagnostics = DiagnosticSnapshot(null, null, null, null),
            schedule = ScheduleCacheStatusSnapshot(
                freshness = ScheduleCacheFreshness.Stale,
                hasCachedWindows = true
            )
        )
        val issues = StatusIssuePlanner.plan(snapshot)
        val issue = issues.find { it.code == "schedule-cache-stale" }
        assertEquals("schedule-cache-stale must be Warning", StatusSeverity.Warning, issue!!.severity)
        assertEquals("日程数据可能过期", issue.title)
        assertEquals(StatusActionTarget.ConnectionCheck, issue.target)
    }

    @Test
    fun `missingCacheWithErrorCreatesCritical`() {
        val snapshot = StatusCenterSnapshot(
            permissions = PermissionStatusSnapshot(true, true, true, true, true, true),
            api = ApiConnectionSnapshot("https://valid.example", isValid = true, reasonCode = null, warnings = emptySet()),
            auth = AuthStatusSnapshot(hasAccessToken = true, isExpired = false),
            service = ForegroundServiceSnapshot(continuousCollectionEnabled = true, serviceRunning = true),
            tracking = TrackingPolicySnapshot("power-saving", "PowerSavingNormal", null),
            queues = QueueStatusSnapshot(0, 0, 0, 0, 0, 0),
            diagnostics = DiagnosticSnapshot(null, null, null, null),
            schedule = ScheduleCacheStatusSnapshot(
                freshness = ScheduleCacheFreshness.Missing,
                hasCachedWindows = false,
                lastError = "服务器暂时不可用"
            )
        )
        val issues = StatusIssuePlanner.plan(snapshot)
        val issue = issues.find { it.code == "schedule-cache-error" }
        assertEquals("schedule-cache-error must be Critical", StatusSeverity.Critical, issue!!.severity)
        assertEquals("日程数据暂时不可用", issue.title)
        assertEquals(StatusActionTarget.ConnectionCheck, issue.target)
    }

    @Test
    fun `trackingSnapshotMapsReasonAndIntervalFromRuntime`() {
        val runtime = ForegroundLocationRuntimeState(
            isRunning = true,
            currentPolicyMode = "ScheduleLowFrequency",
            currentPolicyReason = "当前日程时段，降低定位频率",
            requestIntervalMillis = 300_000L,
            nextExpectedLocationAtMillis = 5_000L
        )
        val tracking = StatusTrackingMapper.fromRuntime("power-saving", runtime)
        assertEquals("power-saving", tracking.profile)
        assertEquals("ScheduleLowFrequency", tracking.currentPolicyMode)
        assertEquals("当前日程时段，降低定位频率", tracking.currentPolicyReason)
        assertEquals(300_000L, tracking.requestIntervalMillis)
        assertEquals(5_000L, tracking.nextExpectedLocationAtMillis)
    }

    @Test
    fun `trackingSnapshotKeepsLegacyPositionalNextExpectedField`() {
        val tracking = TrackingPolicySnapshot(
            "power-saving",
            "PowerSavingNormal",
            5_000L
        )

        assertEquals(5_000L, tracking.nextExpectedLocationAtMillis)
        assertEquals(null, tracking.currentPolicyReason)
        assertEquals(null, tracking.requestIntervalMillis)
    }

    @Test
    fun batteryOptimizationGrantedDoesNotProduceIssue() {
        val snapshot = StatusCenterSnapshot(
            permissions = PermissionStatusSnapshot(
                notificationGranted = true,
                preciseLocationGranted = true,
                backgroundLocationGranted = true,
                usageAccessGranted = true,
                activityRecognitionGranted = true,
                batteryOptimizationGranted = true
            ),
            api = ApiConnectionSnapshot(
                address = "https://valid.example",
                isValid = true,
                reasonCode = null,
                warnings = emptySet()
            ),
            auth = AuthStatusSnapshot(hasAccessToken = true, isExpired = false),
            service = ForegroundServiceSnapshot(
                continuousCollectionEnabled = true,
                serviceRunning = true
            ),
            tracking = TrackingPolicySnapshot(
                profile = "power-saving",
                currentPolicyMode = "PowerSavingNormal",
                nextExpectedLocationAtMillis = null
            ),
            queues = QueueStatusSnapshot(0, 0, 0, 0, 0, 0),
            diagnostics = DiagnosticSnapshot(null, null, null, null)
        )

        val issues = StatusIssuePlanner.plan(snapshot).associateBy { it.code }

        assertFalse("battery-optimization-missing must not appear when granted", issues.containsKey("battery-optimization-missing"))
    }

    // ── WO-ANDROID-POLICY-TRANSITION-20260928 REQ-3 ────────────────────────────

    private fun healthySnapshot(writeFailure: PolicyTransitionWriteFailure) = StatusCenterSnapshot(
        permissions = PermissionStatusSnapshot(true, true, true, true, true, true),
        api = ApiConnectionSnapshot(
            address = "https://valid.example",
            isValid = true,
            reasonCode = null,
            warnings = emptySet()
        ),
        auth = AuthStatusSnapshot(hasAccessToken = true, isExpired = false),
        service = ForegroundServiceSnapshot(continuousCollectionEnabled = true, serviceRunning = true),
        tracking = TrackingPolicySnapshot("power-saving", "PowerSavingNormal", null),
        queues = QueueStatusSnapshot(0, 0, 0, 0, 0, 0),
        diagnostics = DiagnosticSnapshot(null, null, null, null),
        policyTransitionWriteFailure = writeFailure
    )

    @Test
    fun `写入失败在告警区产生一条 Warning 且文案逐字一致`() {
        val failedAt = java.time.Instant.parse("2026-09-28T04:30:00Z").toEpochMilli()
        val issue = StatusIssue.policyTransitionWriteFailure(
            consecutiveFailures = 3,
            lastFailureAtMillis = failedAt
        )

        assertEquals("policy-transition-write-failure", issue.code)
        assertEquals(StatusSeverity.Warning, issue.severity)
        assertEquals(
            PolicyTransitionDisplay.writeFailureText(3, failedAt),
            issue.title
        )
        assertEquals(
            "策略切换记录写入失败 3 次 · 最近 09-28 12:30",
            PolicyTransitionDisplay.writeFailureText(3, failedAt, java.time.ZoneId.of("Asia/Shanghai"))
        )
        assertEquals("查看历史", issue.actionLabel)
        assertEquals(StatusActionTarget.PolicyTransitionHistory, issue.target)
        assertEquals(StatusActionRoute.OpenSettings, StatusActionRouter.route(issue.target))
    }

    @Test
    fun `连续失败为零时告警区不出现写入失败提示`() {
        val issues = StatusIssuePlanner.plan(healthySnapshot(PolicyTransitionWriteFailure.None))
        assertFalse(issues.any { it.code == "policy-transition-write-failure" })
    }

    @Test
    fun `连续失败大于零时告警区出现写入失败提示`() {
        val issues = StatusIssuePlanner.plan(
            healthySnapshot(
                PolicyTransitionWriteFailure(
                    consecutiveFailures = 1,
                    lastFailureAtUtcMillis = 1_756_684_800_000L
                )
            )
        )
        val issue = issues.single { it.code == "policy-transition-write-failure" }
        assertEquals(StatusSeverity.Warning, issue.severity)
        assertTrue(
            "文案必须逐字为「策略切换记录写入失败 N 次 · 最近 MM-dd HH:mm」：${issue.title}",
            issue.title.matches(Regex("策略切换记录写入失败 1 次 · 最近 \\d{2}-\\d{2} \\d{2}:\\d{2}"))
        )
    }
}
