using Pim.Client.Core.Models;
using Pim.Client.Core.Services;
using Xunit;

namespace Pim.UnitTests.ClientWindows;

/// <summary>
/// #310 回归：非浏览器应用（以及浏览器的原生窗口/对话框）不得被归属为「页面」
/// （web-page）事件。
///
/// 现象根因：<see cref="TrackerSessionManager.HandleWindowChange"/> 的「同一应用标题
/// 变化」分支无条件把 <c>_lastHeartbeat.Url</c> 写进新构造的 PageVisit，使得 Telegram /
/// 资源管理器 / 终端等窗口只要标题一变，就产出一条 URL 取自浏览器插件最后心跳的假页面
/// 记录（生产近 7 天 516 条、22 个应用）。
///
/// 这里锁定两条不变量：
/// 1. 只有「前台应用是浏览器」且「心跳与当前窗口标题对得上」时，才允许把心跳的 URL
///    写进 visit；
/// 2. 产事件边界（<see cref="NativeTrackerService.SessionToEvents"/>）对非浏览器会话
///    一律不产出 web-page 事件。
/// </summary>
public sealed class TrackerPageAttributionTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 18, 10, 0, 0, TimeSpan.Zero);

    /// <summary>测试时钟：心跳新鲜度判定必须用注入的时钟，不能用真实 UtcNow（规则 B1）。</summary>
    private sealed class StubTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNowValue { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNowValue;
    }

    private const string GithubUrl = "https://github.com/2746267826/pim-platform/issues/272";

    private static BrowserHeartbeat GithubHeartbeat(string tabTitle = "修复远程缺失日历 · Issue #272")
        => new()
        {
            Url = GithubUrl,
            Title = tabTitle,
            Browser = "edge",
            InstanceId = "ext_edge",
            TabCount = 3,
        };

    private static TrackerWindowInfo Window(string app, string title) => new()
    {
        AppName = app,
        ExePath = $"C:\\{app}.exe",
        WindowTitle = title,
        CapturedAt = T0,
    };

    // ---------- 1. 非浏览器应用 ----------

    [Fact]
    public void HandleWindowChange_NonBrowserAppTitleChange_DoesNotAttachHeartbeatUrl()
    {
        var mgr = new TrackerSessionManager(new TrackerConfig());
        mgr.UpdateBrowserHeartbeat(GithubHeartbeat());

        mgr.HandleWindowChange(Window("telegram", "Hermes – (262209)"), T0);
        // Telegram 收到消息会改标题；这是一次纯粹的窗口标题变化。
        mgr.HandleWindowChange(Window("telegram", "新聊天 – (262246)"), T0.AddSeconds(10));

        var visit = Assert.Single(mgr.Current!.PageVisits);
        Assert.Null(visit.Url);
        Assert.Null(visit.Domain);
        Assert.Equal("新聊天 – (262246)", visit.WindowTitle);
    }

    [Fact]
    public void HandleWindowChange_NonBrowserApp_KeepsBoundaryVisitAndSession()
    {
        var mgr = new TrackerSessionManager(new TrackerConfig());
        mgr.HandleWindowChange(Window("explorer", "下载"), T0);
        var session = mgr.Current;

        mgr.HandleWindowChange(Window("explorer", "文档"), T0.AddSeconds(10));

        // 标题变化仍然只切分 visit、不切分会话（既有行为）。
        Assert.Same(session, mgr.Current);
        Assert.Single(mgr.Current!.PageVisits);
        Assert.Equal(1, mgr.SessionsCreated);
    }

    [Fact]
    public void SessionToEvents_NonBrowserSessionWithHeartbeatUrl_DoesNotEmitWebPageEvent()
    {
        var tracker = new NativeTrackerService(new ApiClient(), new TrackerConfig());
        var end = T0.AddMinutes(5);

        // 即便上游（历史数据、旧版本持久化状态或未来代码变更）把浏览器心跳的 URL
        // 塞进了非浏览器会话，产事件边界也必须拒绝产出 web-page。
        var session = new TrackerSession
        {
            Id = 1,
            AppName = "telegram",
            ExePath = "C:\\telegram.exe",
            WindowTitle = "新聊天 – (262246)",
            StartedAt = T0,
            EndedAt = end,
            DurationSecs = 300,
            PageVisits =
            [
                new TrackerPageVisit
                {
                    StartedAt = T0,
                    EndedAt = end,
                    DurationSecs = 300,
                    Url = GithubUrl,
                    Domain = "github.com",
                    WindowTitle = "新聊天 – (262246)",
                }
            ]
        };

        var events = tracker.SessionToEvents(session, end);

        Assert.DoesNotContain(events, e => e.EventType == "web-page");
        var single = Assert.Single(events);
        Assert.Equal("window", single.EventType);
        Assert.Equal(300.0, single.Duration, precision: 2);
        Assert.Null(single.Url);
    }

    // ---------- 2. 浏览器正常页面（必须继续归属） ----------

    [Fact]
    public void HandleWindowChange_BrowserWindowTitleMatchingHeartbeat_AttachesUrl()
    {
        var mgr = new TrackerSessionManager(new TrackerConfig());
        mgr.UpdateBrowserHeartbeat(GithubHeartbeat());

        mgr.HandleWindowChange(Window("msedge", "修复远程缺失日历 · Issue #272"), T0);
        mgr.HandleWindowChange(
            Window("msedge", "修复远程缺失日历 · Issue #272 - 配置文件 1 - Microsoft Edge"),
            T0.AddSeconds(10));

        var visit = Assert.Single(mgr.Current!.PageVisits);
        Assert.Equal(GithubUrl, visit.Url);
        Assert.Equal("github.com", visit.Domain);
    }

    // ---------- 3. 浏览器的原生窗口 / 对话框 ----------

    [Fact]
    public void HandleWindowChange_BrowserNativeDialogTitle_DoesNotAttachUrl()
    {
        var mgr = new TrackerSessionManager(new TrackerConfig());
        mgr.UpdateBrowserHeartbeat(GithubHeartbeat(tabTitle: "管理员: cmd.exe - python"));

        mgr.HandleWindowChange(Window("firefox", "管理员: cmd.exe - python"), T0);
        // Firefox 的「选择附加组件来安装」文件对话框 / 「附加组件管理器」窗口不是网页。
        mgr.HandleWindowChange(Window("firefox", "选择附加组件来安装"), T0.AddSeconds(10));

        var visit = Assert.Single(mgr.Current!.PageVisits);
        Assert.Null(visit.Url);
    }

    [Fact]
    public void HandleWindowChange_StaleHeartbeat_DoesNotAttachUrl()
    {
        var clock = new StubTimeProvider(T0);
        var mgr = new TrackerSessionManager(new TrackerConfig(), timeProvider: clock);
        mgr.UpdateBrowserHeartbeat(GithubHeartbeat());

        mgr.HandleWindowChange(Window("chrome", "修复远程缺失日历 · Issue #272"), T0);

        // 心跳已静默 5 分钟：陈旧值不得再被归属到新的窗口标题上。
        var later = T0.AddMinutes(5);
        clock.UtcNowValue = later;
        mgr.HandleWindowChange(
            Window("chrome", "修复远程缺失日历 · Issue #272 - Google Chrome"),
            later);

        var visit = Assert.Single(mgr.Current!.PageVisits);
        Assert.Null(visit.Url);
    }

    // ---------- 4. 心跳到达时补齐占位 visit ----------

    [Fact]
    public void UpdateBrowserHeartbeat_OpenPlaceholderVisit_IsFilledInPlace()
    {
        var mgr = new TrackerSessionManager(new TrackerConfig());

        // 用户点开新页面：先看到标题变化（心跳还是上一页的），随后扩展上报新页面。
        mgr.HandleWindowChange(Window("chrome", "旧页面 - Google Chrome"), T0);
        mgr.HandleWindowChange(Window("chrome", "新页面 - Google Chrome"), T0.AddSeconds(10));
        var placeholder = Assert.Single(mgr.Current!.PageVisits);
        Assert.Null(placeholder.Url);

        mgr.UpdateBrowserHeartbeat(GithubHeartbeat(tabTitle: "新页面"));

        // 占位 visit 被就地补齐，而不是关闭后另开一条（否则每次标题变化都会把
        // 页面时间线切成碎片）。
        var visits = mgr.Current!.PageVisits;
        var visit = Assert.Single(visits);
        Assert.Same(placeholder, visit);
        Assert.Equal(T0.AddSeconds(10), visit.StartedAt);
        Assert.Equal(GithubUrl, visit.Url);
        Assert.Equal("github.com", visit.Domain);
    }

    [Fact]
    public void UpdateBrowserHeartbeat_BrowserDialogWindow_LeavesPlaceholderEmpty()
    {
        var mgr = new TrackerSessionManager(new TrackerConfig());

        mgr.HandleWindowChange(Window("firefox", "管理员: cmd.exe - python"), T0);
        mgr.HandleWindowChange(Window("firefox", "选择附加组件来安装"), T0.AddSeconds(10));

        mgr.UpdateBrowserHeartbeat(GithubHeartbeat(tabTitle: "管理员: cmd.exe - python"));

        Assert.Null(mgr.Current!.PageVisits[^1].Url);
    }

    [Fact]
    public void UpdateBrowserHeartbeat_NonBrowserForegroundApp_NeverRecordsVisit()
    {
        var mgr = new TrackerSessionManager(new TrackerConfig());
        mgr.HandleWindowChange(Window("obsidian", "收集箱 - Obsidian"), T0);

        mgr.UpdateBrowserHeartbeat(GithubHeartbeat());

        Assert.Empty(mgr.Current!.PageVisits);
    }

    [Fact]
    public void HeartbeatMatchesWindowTitle_TruncatedWindowTitle_StillMatches()
    {
        // Windows 会截断过长的窗口标题，比对必须只取有界前缀。
        var heartbeat = GithubHeartbeat(tabTitle: "校园网IPv6专用浏览器方案 - 收集箱 - Obsidian 知识库");
        var windowTitle = "校园网IPv6专用浏览器方案 - 收集箱 - Obsidian 知…";

        Assert.True(TrackerSessionManager.HeartbeatMatchesWindowTitle(heartbeat, windowTitle));
    }

    [Theory]
    [InlineData("选择附加组件来安装")]
    [InlineData("附加组件管理器")]
    [InlineData("新聊天 – (262246)")]
    [InlineData("")]
    public void HeartbeatMatchesWindowTitle_UnrelatedWindowTitle_DoesNotMatch(string windowTitle)
    {
        Assert.False(TrackerSessionManager.HeartbeatMatchesWindowTitle(GithubHeartbeat(), windowTitle));
    }
}
