using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

public class KeyStatsFixAdvisorTests
{
    [Fact]
    public void BuildSuggestion_StaleZeroWithForeign_MentionsSessionAndOneClick()
    {
        var health = new KeyStatsHealthResult(
            KeyStatsDetailState.ApiOkButStaleZero,
            "Unavailable",
            CanUpload: false,
            SkipReason: "stale-zero",
            ProcessCount: 2,
            HasForeignSessionProcess: true,
            Snapshot: null,
            SummaryZh: "x");

        var s = KeyStatsFixAdvisor.BuildSuggestion(health);
        Assert.Contains("Session", s.MessageZh);
        Assert.Contains("一键修复", s.MessageZh);
        Assert.True(s.ShowActionHint);
    }

    [Fact]
    public void BuildSuggestion_Healthy_SaysNormal()
    {
        var health = new KeyStatsHealthResult(
            KeyStatsDetailState.Available,
            "Available",
            CanUpload: true,
            SkipReason: null,
            ProcessCount: 1,
            HasForeignSessionProcess: false,
            Snapshot: null,
            SummaryZh: "KeyStats 可用");

        var s = KeyStatsFixAdvisor.BuildSuggestion(health);
        Assert.Contains("运行正常", s.MessageZh);
        Assert.False(s.ShowActionHint);
    }

    [Theory]
    [InlineData(KeyStatsDetailState.MissingProcess, "missing-process", false, "未运行")]
    [InlineData(KeyStatsDetailState.ApiUnreachable, "api-unreachable", false, "不可达")]
    [InlineData(KeyStatsDetailState.ApiOkButStaleZero, "stale-zero", false, "计数")]
    public void BuildSuggestion_CoversCommonSkipReasons(
        KeyStatsDetailState state, string skip, bool foreign, string needle)
    {
        var health = new KeyStatsHealthResult(
            state, "Unavailable", false, skip, 1, foreign, null, "x");
        var s = KeyStatsFixAdvisor.BuildSuggestion(health);
        Assert.Contains(needle, s.MessageZh);
        Assert.True(s.ShowActionHint);
    }

    [Fact]
    public void BuildSuggestion_AvailableWithForeign_SuggestsConverge()
    {
        var health = new KeyStatsHealthResult(
            KeyStatsDetailState.Available, "Available", true, null, 2, true, null, "x");
        var s = KeyStatsFixAdvisor.BuildSuggestion(health);
        Assert.Contains("额外会话", s.MessageZh);
        Assert.True(s.ShowActionHint);
    }

    [Fact]
    public void BuildSuggestion_NullHealth_SafeDefault()
    {
        var s = KeyStatsFixAdvisor.BuildSuggestion(null);
        Assert.False(string.IsNullOrWhiteSpace(s.MessageZh));
    }
}
