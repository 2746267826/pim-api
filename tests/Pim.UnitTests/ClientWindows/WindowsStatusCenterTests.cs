using Xunit;
using Pim.Client.Core.Services;

namespace Pim.UnitTests.ClientWindows;

public class WindowsStatusCenterTests
{
    [Fact]
    public void StatusWindow_DeclaresFourSectionsAndKeyStatsActions()
    {
        var xaml = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "StatusWindow.xaml"));
        var code = File.ReadAllText(RepoPath("src", "client-windows", "Pim.Client.App", "StatusWindow.xaml.cs"));

        Assert.Contains("概览", xaml);
        Assert.Contains("数据源", xaml);
        Assert.Contains("上传", xaml);
        Assert.Contains("设置", xaml);
        Assert.Contains("重启 KeyStats", xaml);
        Assert.Contains("复制诊断", xaml);
        Assert.Contains("在浏览器打开 Web", xaml);
        Assert.Contains("KeyStatsProcessManager", code);
        Assert.Contains("一键修复", xaml);
        Assert.Contains("修复建议", xaml);
        Assert.Contains("修复结果", xaml);
        Assert.Contains("KeyStatsOneClickFixButton", xaml);
        Assert.Contains("KeyStatsFixSuggestionText", xaml);
        Assert.Contains("KeyStatsFixResultText", xaml);
        Assert.Contains("OnOneClickFixKeyStats", code);
        Assert.Contains("fix-keystats-session.ps1", code);
    }

    [Theory]
    [InlineData(true, "Available", "Available", null, 0, "正常")]
    [InlineData(true, "Available", "Unavailable", "stale-zero", 0, "部分异常")]
    [InlineData(false, "Unavailable", "Unavailable", "missing-process", 0, "不可用")]
    public void RateOverall_MatchesExpected(
        bool authenticated,
        string awState,
        string ksState,
        string? ksSkip,
        int queue,
        string expected)
    {
        var rating = StatusCenterEvaluator.Rate(authenticated, awState, ksState, ksSkip, queue);
        Assert.Equal(expected, rating);
    }

    private static string RepoPath(params string[] parts)
    {
        var current = AppContext.BaseDirectory;
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(new[] { current }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new FileNotFoundException(Path.Combine(parts));
    }
}
