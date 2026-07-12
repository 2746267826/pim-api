using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class WindowsDaemonPrimaryPathTests
{
    [Fact]
    public void TrayMenu_IsDaemonFocused()
    {
        var trayCode = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "TrayIcon.cs"));
        foreach (var banned in new[] { "任务 / 日历", "报告中心", "Outlook 同步", "Data Center", "审计中心", "通知中心" })
        {
            Assert.DoesNotContain(banned, trayCode);
        }

        Assert.Contains("打开状态中心", trayCode);
        Assert.Contains("立即同步", trayCode);
        Assert.Contains("回填最近 14 天 ActivityWatch", trayCode);
        Assert.Contains("在浏览器打开 Web 工作台", trayCode);
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
