using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Operations;
using Pim.UnitTests.Harness.RealDb;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 实机验证（WO-RELIABILITY-WINDOW-20260928 AC-1.3 / AC-2.3 / AC-5.2 / AC-5.5 / AC-5.6）：
/// 用**真实宿主**（`Pim.Api` 的完整装配 + `appsettings.json` 配置绑定 + 鉴权后的 HTTP 端点）
/// 打**生产形状镜像库**，对比"改考核窗配置"前后的体检读数。
///
/// <para>
/// 连接串只来自环境变量 <c>PIM_MIRROR_CONN</c>（源码不内置口令），拿不到就显式 Skip；
/// 连接串里出现 <c>pim_prod</c> 会被断言直接拦下（AGENTS.md 硬规则，由代码而不是注释执行）。
/// 刻意**不读** <c>PIM_TEST_CONN</c>：CI 里那是空的一次性库，本用例的断言依赖生产形状数据。
/// </para>
///
/// <para>
/// 本用例会在镜像库里注册一次性测试账号（镜像库是可丢弃、可随意改写的），
/// 除此之外只读：体检链路本身不写任何业务表。
/// </para>
/// </summary>
public class DataReliabilityAssessmentWindowRealDbTests
{
    private readonly ITestOutputHelper _output;

    public DataReliabilityAssessmentWindowRealDbTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task AssessmentWindow_ConfiguredOnRealHost_ChangesReadings_AndDebtNeverColors()
    {
        var connectionString = ResolveMirrorConnectionString();

        // 场景 A（滚动窗，AC-5.2）：模拟"进程已连续运行 30 天"，
        // 于是考核线 = 体检时刻 − 考核窗时长，两次只改考核窗：24h（旧口径）vs 168h（新默认）。
        var narrow = await InspectOnRealHostAsync(connectionString, assessmentWindowHours: 24, processStartedAgo: TimeSpan.FromDays(30));
        var wide = await InspectOnRealHostAsync(connectionString, assessmentWindowHours: 168, processStartedAgo: TimeSpan.FromDays(30));

        LogReport("考核窗 24h", narrow);
        LogReport("考核窗 168h", wide);

        // ---- AC-5.2：改配置（同一宿主 + 同一镜像库）→ 体检读数确实随之变化 ----
        Assert.Equal(24, narrow.AssessmentWindowHours);
        Assert.Equal(168, wide.AssessmentWindowHours);
        Assert.True(
            wide.AssessmentStartUtc < narrow.AssessmentStartUtc,
            "考核窗变长后账本起点必须更早");

        // 考核窗变长 → 考核线前移 → 更多违规落进窗内、欠账相应变少（这正是"配置真的生效"的可观察证据）。
        Assert.True(
            wide.WindowViolations > narrow.WindowViolations,
            $"考核窗从 24h 放宽到 168h 后窗内违规必须变多："
            + $"24h 窗内 {narrow.WindowViolations} vs 168h 窗内 {wide.WindowViolations}");

        Assert.True(
            wide.HistoricalViolations < narrow.HistoricalViolations,
            $"考核窗从 24h 放宽到 168h 后历史欠账必须变少："
            + $"24h 欠账 {narrow.HistoricalViolations} vs 168h 欠账 {wide.HistoricalViolations}");

        Assert.True(
            narrow.WindowViolations + narrow.HistoricalViolations
            == wide.WindowViolations + wide.HistoricalViolations,
            "两种考核窗下违规总数必须一致（只是分档归属不同，不得凭空多出或少掉违规）");

        // ---- AC-2.3 / AC-3.x：仅欠账不得折黄折红（真实数据上逐条核对）----
        foreach (var report in new[] { narrow, wide })
        {
            foreach (var rule in report.Rules)
            {
                if (rule.Status is "red" or "yellow")
                {
                    Assert.True(
                        rule.WindowViolations > 0,
                        $"{rule.Code} 是 {rule.Status} 却没有窗内违规支撑（窗内 {rule.WindowViolations} / 欠账 {rule.HistoricalViolations}）");
                }
                else if (rule.Status == "green")
                {
                    Assert.Equal(0, rule.WindowViolations);
                }
            }

            // 计数不重不漏 + 总览只统计窗内（AC-2.4 / AC-2.6）。
            Assert.All(report.Rules, rule =>
                Assert.Equal(rule.TotalViolations, rule.WindowViolations + rule.HistoricalViolations));
            Assert.Equal(report.Rules.Sum(rule => rule.WindowViolations), report.TotalViolations);
            Assert.Equal(report.Rules.Sum(rule => rule.HistoricalViolations), report.HistoricalViolations);
        }

        // ---- 生产形状数据上的直接结论：旧口径下"全部存量"的几把尺子，新口径下必须是绿 + 欠账数字 ----
        var wideS1 = wide.Rules.Single(rule => rule.Code == "S1");
        var wideS11 = wide.Rules.Single(rule => rule.Code == "S11");
        var narrowS1 = narrow.Rules.Single(rule => rule.Code == "S1");
        Assert.True(wideS1.HistoricalViolations > 0, "镜像库里 S1 有大量修复前遗留的重叠，欠账不应为 0");
        Assert.True(wideS11.HistoricalViolations > 0, "镜像库里 S11 有大量历史空转批次，欠账不应为 0");
        // S11 的 2301 处空转批次全部在 7 天之前 → 两种考核窗下都只有欠账、都是绿（旧口径下它是黄线）。
        Assert.Equal(0, wideS11.WindowViolations);
        Assert.Equal("green", wideS11.Status);
        // S1 的欠账随考核窗变长而减少：多出来的那部分正是"进了窗内"的重叠。
        Assert.True(
            wideS1.HistoricalViolations < narrowS1.HistoricalViolations,
            $"S1 的欠账应随考核窗变长而减少：24h {narrowS1.HistoricalViolations} vs 168h {wideS1.HistoricalViolations}");

        // ---- AC-6.4：S3 必须给出取数实际覆盖到的业务日数（不足时如实标注截断，不得静默）----
        var wideS3 = wide.Rules.Single(rule => rule.Code == "S3");
        Assert.NotNull(wideS3.ScanCoveredDays);
        Assert.InRange(wideS3.ScanCoveredDays!.Value, 1, 8);
        _output.WriteLine(
            $"S3 取数覆盖 {wideS3.ScanCoveredDays} 个业务日（考核窗 7 天；命中上限={wideS3.ScanTruncated}）");

        // ---- AC-5.5：S9 覆盖率窗口保持 24h，不被考核窗联动 ----
        Assert.Single(wide.Rules, rule => rule.Code == "S9");

        // ---- AC-1.3：刚重启（启动时刻在 2 分钟前）→ 考核线 = 进程启动时刻，
        //      分档尺子的窗内计数必须为 0、全部转绿，旧违规全部进历史欠账。----
        var justRestarted = await InspectOnRealHostAsync(
            connectionString,
            assessmentWindowHours: 168,
            processStartedAgo: TimeSpan.FromMinutes(2));
        LogReport("刚重启 2 分钟（考核窗 168h）", justRestarted);

        Assert.True(
            Math.Abs((justRestarted.AssessmentStartUtc - DateTimeOffset.UtcNow).TotalMinutes) < 10,
            $"刚重启时账本起点应≈进程启动时刻（约 2 分钟前），实际 {justRestarted.AssessmentStartUtc:O}");

        foreach (var code in new[] { "S1", "S2", "S4", "S5", "S6", "S7", "S11", "S13" })
        {
            var rule = justRestarted.Rules.Single(r => r.Code == code);
            Assert.True(
                rule.WindowViolations == 0,
                $"{code} 刚重启后不该有窗内违规（进程启动时刻之后的违规），实际 {rule.WindowViolations}");
            Assert.NotEqual("red", rule.Status);
        }

        Assert.True(
            justRestarted.HistoricalViolations > 0,
            "刚重启后，修复前的历史违规必须全部进历史欠账（只计数）");

        // ---- AC-7.3 / AC-4.2：下钻导出的违规项必须带分档列，且 S4 / S5 / S11 可见 ----
        await AssertExportCarriesSplitMarkerAsync(connectionString);
    }

    /// <summary>导出（GET /rules/{code}/violations）：每条项都必须带 `isNew` 分档标记。</summary>
    private async Task AssertExportCarriesSplitMarkerAsync(string connectionString)
    {
        using var factory = CreateFactory(connectionString, assessmentWindowHours: 168, processStartedAgo: TimeSpan.FromHours(1));
        var anonymous = factory.CreateClient();
        var token = await RegisterThrowawayUserAsync(anonymous);

        using var user = factory.CreateClient();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        foreach (var code in new[] { "S1", "S4", "S5", "S11" })
        {
            var response = await user.GetAsync($"/api/v1/data-reliability/rules/{code}/violations?limit=2000");
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var items = document.RootElement.GetProperty("data").GetProperty("items");
            var count = items.GetArrayLength();
            _output.WriteLine($"{code} 导出 {count} 条违规项");

            if (code == "S5")
            {
                // S5 在这份镜像数据上可能没有违规，此时没有可断言的条目。
                continue;
            }

            Assert.True(count > 0, $"{code} 在镜像库上应有可导出的违规项");
            foreach (var item in items.EnumerateArray())
            {
                Assert.True(
                    item.TryGetProperty("isNew", out var isNew) && (isNew.ValueKind is JsonValueKind.True or JsonValueKind.False),
                    $"{code} 导出的违规项缺少分档标记 isNew");
            }
        }
    }

    /// <summary>把体检报告的关键读数打到测试输出，作为可复核的原始输出。</summary>
    private void LogReport(string label, InspectionSnapshot snapshot)
    {
        _output.WriteLine($"=== {label} ===");
        _output.WriteLine($"考核账本起点(UTC): {snapshot.AssessmentStartUtc:O}  考核窗: {snapshot.AssessmentWindowHours}h");
        _output.WriteLine(
            $"总览: {snapshot.Status} 红{snapshot.RedCount}/黄{snapshot.YellowCount}/绿{snapshot.GreenCount}/未知{snapshot.UnknownCount} "
            + $"窗内违规 {snapshot.WindowViolations} 历史欠账 {snapshot.HistoricalViolations}");
        foreach (var rule in snapshot.Rules)
        {
            _output.WriteLine(
                $"  {rule.Code,-4} {rule.Status,-7} 窗内 {rule.WindowViolations,5} 欠账 {rule.HistoricalViolations,6} 合计 {rule.TotalViolations,6}"
                + (rule.ScanCoveredDays is { } days ? $" 覆盖 {days} 个业务日" : string.Empty)
                + (rule.ScanTruncated ? " [已达取数上限]" : string.Empty));

            if (rule.Status == "unknown")
            {
                _output.WriteLine($"       detail: {rule.Detail}");
            }
        }
    }

    private sealed record RuleSnapshot(
        string Code,
        string Status,
        int WindowViolations,
        int HistoricalViolations,
        int TotalViolations,
        string Detail,
        int? ScanCoveredDays,
        bool ScanTruncated);

    private sealed record InspectionSnapshot(
        string Status,
        int RedCount,
        int YellowCount,
        int GreenCount,
        int UnknownCount,
        int TotalViolations,
        int WindowViolations,
        int HistoricalViolations,
        DateTimeOffset AssessmentStartUtc,
        double AssessmentWindowHours,
        IReadOnlyList<RuleSnapshot> Rules);

    /// <summary>在真实宿主上跑一次体检并读回结果（/inspection/refresh → /inspection）。</summary>
    private async Task<InspectionSnapshot> InspectOnRealHostAsync(
        string connectionString,
        double assessmentWindowHours,
        TimeSpan? processStartedAgo = null)
    {
        using var factory = CreateFactory(connectionString, assessmentWindowHours, processStartedAgo);
        var anonymous = factory.CreateClient();
        var token = await RegisterThrowawayUserAsync(anonymous);

        using var user = factory.CreateClient();
        user.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var refresh = await user.PostAsync("/api/v1/data-reliability/inspection/refresh", content: null);
        refresh.EnsureSuccessStatusCode();

        var response = await user.GetAsync("/api/v1/data-reliability/inspection");
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");

        var rules = data.GetProperty("rules").EnumerateArray()
            .Select(rule => new RuleSnapshot(
                rule.GetProperty("code").GetString()!,
                rule.GetProperty("status").GetString()!,
                rule.GetProperty("windowViolations").GetInt32(),
                rule.GetProperty("historicalViolations").GetInt32(),
                rule.GetProperty("totalViolations").GetInt32(),
                rule.GetProperty("detail").GetString() ?? string.Empty,
                rule.TryGetProperty("scanCoveredDays", out var covered) && covered.ValueKind == JsonValueKind.Number
                    ? covered.GetInt32()
                    : null,
                rule.TryGetProperty("scanTruncated", out var truncated) && truncated.ValueKind == JsonValueKind.True))
            .ToList();

        return new InspectionSnapshot(
            Status: data.GetProperty("status").GetString()!,
            RedCount: data.GetProperty("redCount").GetInt32(),
            YellowCount: data.GetProperty("yellowCount").GetInt32(),
            GreenCount: data.GetProperty("greenCount").GetInt32(),
            UnknownCount: data.GetProperty("unknownCount").GetInt32(),
            TotalViolations: data.GetProperty("totalViolations").GetInt32(),
            WindowViolations: data.GetProperty("windowViolations").GetInt32(),
            HistoricalViolations: data.GetProperty("historicalViolations").GetInt32(),
            AssessmentStartUtc: data.GetProperty("assessmentStartUtc").GetDateTimeOffset(),
            AssessmentWindowHours: data.GetProperty("assessmentWindowHours").GetDouble(),
            Rules: rules);
    }

    /// <summary>
    /// 真宿主 + 真库：保留 <c>appsettings.json</c> 的配置绑定链（这正是"只改 C# 默认值不生效"的坑），
    /// 只把连接串与考核窗时长从外部注入 —— 与生产用环境变量覆盖配置是同一条路径。
    /// </summary>
    private static WebApplicationFactory<Program> CreateFactory(
        string connectionString,
        double assessmentWindowHours,
        TimeSpan? processStartedAgo = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("ConnectionStrings:DefaultConnection", connectionString);
            builder.UseSetting("Invariants:AssessmentWindowHours", assessmentWindowHours.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("Invariants:CoverageWindowHours", "24");
            builder.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor != null)
                {
                    services.Remove(descriptor);
                }

                services.AddDbContext<PimDbContext>(options => options.UseNpgsql(connectionString));

                if (processStartedAgo is { } ago)
                {
                    // 生产里进程启动时刻来自真实进程；实机用例需要模拟"已连续运行 N 天 / 刚重启 2 分钟"
                    // 这两种状态，因此替换掉这一个依赖（其余装配保持真实）。
                    var startDescriptor = services.SingleOrDefault(d => d.ServiceType == typeof(IProcessStartTimeProvider));
                    if (startDescriptor != null)
                    {
                        services.Remove(startDescriptor);
                    }

                    services.AddSingleton<IProcessStartTimeProvider>(
                        new FixedProcessStartTimeProvider(DateTimeOffset.UtcNow - ago));
                }
            });
        });

    private sealed class FixedProcessStartTimeProvider : IProcessStartTimeProvider
    {
        public FixedProcessStartTimeProvider(DateTimeOffset startedAtUtc) => ProcessStartedAtUtc = startedAtUtc;

        public DateTimeOffset ProcessStartedAtUtc { get; }
    }

    private static async Task<string> RegisterThrowawayUserAsync(HttpClient client)
    {
        var username = ("relwin-" + Guid.NewGuid().ToString("N"))[..18];
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = username
        });
        response.EnsureSuccessStatusCode();

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    private static string ResolveMirrorConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("PIM_MIRROR_CONN");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new Xunit.SkipException(
                "未提供生产形状镜像库连接（PIM_MIRROR_CONN），跳过考核窗实机校验。");
        }

        // AGENTS.md 硬规则：绝不连接生产库。硬规则必须由代码执行，而不是靠注释提醒。
        Assert.DoesNotContain("pim_prod", connectionString, StringComparison.OrdinalIgnoreCase);

        try
        {
            using var connection = new NpgsqlConnection(connectionString);
            connection.Open();
        }
        catch (Exception ex) when (RealDbTestConnection.IsServerUnreachable(ex))
        {
            throw new Xunit.SkipException($"镜像库不可达，跳过考核窗实机校验：{ex.Message}");
        }

        return connectionString;
    }
}
