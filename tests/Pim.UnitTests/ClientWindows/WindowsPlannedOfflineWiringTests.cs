using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class WindowsPlannedOfflineWiringTests
{
    [Fact]
    public void AppWiresPlannedOfflineListeners()
    {
        var source = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "App.xaml.cs"));
        Assert.Contains("SystemEvents.SessionEnding", source);
        Assert.Contains("PowerModeChanged", source);
        Assert.Contains("TryReportPlannedOffline", source);
        Assert.Contains("Interlocked", source);
        Assert.Contains("_plannedOfflineTask", source);
        Assert.Contains("_plannedOfflineLock", source);
        Assert.Contains("_shutdown.Cancel()", source);
        Assert.Contains("PowerModes.Resume", source);
        Assert.Contains("StopHeartbeatLoopAndWait", source);
        Assert.Contains("ReportHeartbeatOnceAsync(CancellationToken.None)", source);
        Assert.Contains("wait: true", source);
        Assert.Contains("TimeSpan.FromSeconds(2)", source);
        Assert.Contains("PlannedOfflineReporter", source);
        Assert.Contains("\"shutdown\"", source);
        Assert.Contains("\"suspend\"", source);
        Assert.Contains("\"exit\"", source);
    }

    [Fact]
    public void SuspendHookReportsPlannedOfflineSynchronouslyBeforeClosingTheSession()
    {
        // WO-ISSUES-396-400-20261007 REQ-4 ①：休眠钩子的执行窗口随时可能被系统冻结，
        // 声明必须在这个窗口内**同步**送达（wait: true），且要在关会话/刷队列之前先发出去。
        var source = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "App.xaml.cs"));

        var suspendBranchStart = source.IndexOf("if (e.Mode == PowerModes.Suspend)", StringComparison.Ordinal);
        Assert.True(suspendBranchStart > 0, "未找到休眠分支");
        var resumeBranchStart = source.IndexOf("else if (e.Mode == PowerModes.Resume)", StringComparison.Ordinal);
        Assert.True(resumeBranchStart > suspendBranchStart, "未找到唤醒分支");
        var suspendBranch = source[suspendBranchStart..resumeBranchStart];

        var reportIndex = suspendBranch.IndexOf("TryReportPlannedOffline(\"suspend\", wait: true", StringComparison.Ordinal);
        Assert.True(reportIndex >= 0, "休眠分支必须同步上报计划内下线（wait: true）");

        var handleSuspendIndex = suspendBranch.IndexOf("HandleSuspend()", StringComparison.Ordinal);
        Assert.True(handleSuspendIndex >= 0, "休眠分支必须关会话");
        Assert.True(
            reportIndex < handleSuspendIndex,
            "计划内下线声明必须先于关会话/刷队列发出：后者最长阻塞数秒，会挤掉冻结前仅剩的时间窗");

        // OccurredAt 取钩子入口时刻（= 休眠起点），而不是上报那一刻。
        Assert.Contains("var suspendAt = DateTimeOffset.UtcNow;", suspendBranch);
        Assert.Contains("occurredAt: suspendAt", suspendBranch);
    }

    [Fact]
    public void ResumeHookRetriesAnUndeliveredSuspendDeclarationBeforeTheWakeHeartbeat()
    {
        // App.xaml.cs 属于 WPF 工程，Linux 上不参与编译，因此这里只能做结构断言（真机结论仍以 AC-4.1~4.3 为准）：
        // ① 休眠前那次声明若没送达（网络未就绪 / 被 2 秒 CTS 取消 / 抢不到心跳信号量），
        //    唤醒时必须用**同一个休眠起点**补发一次；
        // ② 补发必须早于唤醒后的第一跳心跳（心跳会把 received_at 推到唤醒时刻，顺序反了声明就"比心跳还旧"）。
        var source = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "App.xaml.cs"));

        Assert.Contains("_plannedOfflineDelivered", source);
        Assert.Contains("_plannedOfflineOccurredAt", source);

        var resumeStart = source.IndexOf("else if (e.Mode == PowerModes.Resume)", StringComparison.Ordinal);
        Assert.True(resumeStart > 0, "未找到唤醒分支");
        var resumeBranch = source[resumeStart..];

        var retryIndex = resumeBranch.IndexOf("TryReportPlannedOffline(\"suspend\", wait: true, occurredAt: retryAt)", StringComparison.Ordinal);
        Assert.True(retryIndex >= 0, "唤醒分支必须用同一个休眠起点补发未送达的声明");

        var heartbeatIndex = resumeBranch.IndexOf("ReportHeartbeatOnceAsync(CancellationToken.None)", StringComparison.Ordinal);
        Assert.True(heartbeatIndex > retryIndex, "补发声明必须早于唤醒后的第一跳心跳");
    }

    [Fact]
    public void StartupRegistersPlannedOfflineReporter()
    {
        var startup = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "Startup.cs"));
        Assert.Contains("PlannedOfflineReporter", startup);
    }

    [Fact]
    public void CoreReporterTargetsPlannedOfflineEndpoint()
    {
        var reporter = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.Core", "Services", "PlannedOfflineReporter.cs"));
        Assert.Contains("daemon/planned-offline", reporter);
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new FileNotFoundException($"Could not find repository file {Path.Combine(parts)}.");
    }
}