using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pim.Api.Endpoints;
using Pim.Core.Invariants;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 体检接口契约测试（#260 验收标准 3）：路由、鉴权、未知编号 400、limit 钳制、响应字段与前端契约一致。
/// </summary>
public class DataReliabilityEndpointContractTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubRunner : IDataReliabilityInspectionRunner
    {
        public int RefreshCount { get; private set; }

        public bool IsRunning => false;

        public Task<DataReliabilityInspectionReport> GetLatestAsync(System.Threading.CancellationToken ct = default)
            => Task.FromResult(BuildReport());

        public Task<DataReliabilityInspectionReport> RefreshAsync(System.Threading.CancellationToken ct = default)
        {
            RefreshCount++;
            return Task.FromResult(BuildReport());
        }

        private static DataReliabilityInspectionReport BuildReport() => new(
            InspectedAtUtc: Now,
            Version: 7,
            ElapsedMilliseconds: 42,
            Status: "red",
            RedCount: 1,
            YellowCount: 0,
            GreenCount: 12,
            UnknownCount: 0,
            TotalViolations: 3,
            NewViolations: 1,
            HistoricalViolations: 2,
            Notices: new Dictionary<string, string>(),
            Rules: DataReliabilityRuleCatalog.All
                .Select(definition => new DataReliabilityRuleReport(
                    Code: definition.Code,
                    InvariantCode: definition.InvariantCode,
                    Key: DataReliabilityRuleCatalog.BuildKey(definition),
                    Order: definition.Order,
                    Name: definition.Name,
                    Group: definition.Group.ToString(),
                    GroupLabel: definition.GroupLabel,
                    Status: definition.Code == "S1" ? "red" : "green",
                    StatusLabel: definition.Code == "S1" ? "红" : "绿",
                    Detail: "INV detail",
                    CurrentValue: 3,
                    CurrentValueUnit: "条",
                    CurrentValueLabel: null,
                    Threshold: definition.Threshold,
                    Criterion: definition.Criterion,
                    Rationale: definition.Rationale,
                    RelatedIssues: definition.RelatedIssues,
                    TotalViolations: definition.Code == "S1" ? 3 : 0,
                    NewViolations: definition.Code == "S1" ? 1 : 0,
                    HistoricalViolations: definition.Code == "S1" ? 2 : 0,
                    EarliestOccurrenceUtc: null,
                    LatestOccurrenceUtc: null,
                    Samples: Array.Empty<string>(),
                    ThresholdFallback: false,
                    ThresholdNote: null,
                    CoveredLayers: definition.Code == "S8" ? "DataField" : null,
                    Trend: "unknown",
                    TrendDelta: null,
                    TrendBaselineUtc: null,
                    ThreeState: null,
                    ScanTruncated: false))
                .ToArray(),
            Message: "体检完成");

        public DateTimeOffset InspectedAt => Now;
    }

    private sealed class StubExporter : IDataReliabilityViolationExporter
    {
        public string? LastRuleCode { get; private set; }
        public int LastLimit { get; private set; }

        public Task<DataReliabilityViolationExport> GetViolationsAsync(string ruleCode, int limit, System.Threading.CancellationToken ct = default)
        {
            LastRuleCode = ruleCode;
            LastLimit = limit;
            return Task.FromResult(new DataReliabilityViolationExport(
                ruleCode,
                Now,
                0,
                false,
                Array.Empty<DataReliabilityViolationItem>()));
        }
    }

    /// <summary>最简单的测试鉴权：带 X-Test-User 头即视为已登录。</summary>
    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestScheme";

        public TestAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("X-Test-User"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "qa") }, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
        }
    }

    private static async Task<(HttpClient Client, StubRunner Runner, StubExporter Exporter)> CreateServerAsync()
    {
        var runner = new StubRunner();
        var exporter = new StubExporter();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IDataReliabilityInspectionRunner>(runner);
        builder.Services.AddSingleton<IDataReliabilityViolationExporter>(exporter);

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapDataReliabilityEndpoints();
        await app.StartAsync();

        return (app.GetTestClient(), runner, exporter);
    }

    private static async Task<JsonElement> ReadDataAsync(HttpResponseMessage response)
    {
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return document.RootElement.GetProperty("data").Clone();
    }

    [Fact]
    public async Task Inspection_WithoutAuthentication_ReturnsUnauthorized()
    {
        var (client, _, _) = await CreateServerAsync();

        var response = await client.GetAsync("/api/v1/data-reliability/inspection");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Inspection_ReturnsLatestReportWithFrontendContractFields()
    {
        var (client, _, _) = await CreateServerAsync();
        client.DefaultRequestHeaders.Add("X-Test-User", "qa");

        var response = await client.GetAsync("/api/v1/data-reliability/inspection");
        response.EnsureSuccessStatusCode();

        var data = await ReadDataAsync(response);

        Assert.Equal(7, data.GetProperty("version").GetInt64());
        Assert.Equal(13, data.GetProperty("rules").GetArrayLength());
        Assert.True(data.TryGetProperty("inspectedAtUtc", out _));
        Assert.True(data.TryGetProperty("redCount", out _));
        Assert.True(data.TryGetProperty("yellowCount", out _));
        Assert.True(data.TryGetProperty("greenCount", out _));
        Assert.True(data.TryGetProperty("unknownCount", out _));
        Assert.True(data.TryGetProperty("totalViolations", out _));
        Assert.True(data.TryGetProperty("newViolations", out _));
        Assert.True(data.TryGetProperty("historicalViolations", out _));
        Assert.True(data.TryGetProperty("notices", out _));
        Assert.True(data.TryGetProperty("elapsedMilliseconds", out _));
        Assert.True(data.TryGetProperty("message", out _));

        var first = data.GetProperty("rules")[0];
        foreach (var field in new[]
        {
            "code", "invariantCode", "key", "order", "name", "group", "groupLabel", "status", "statusLabel",
            "detail", "currentValue", "currentValueUnit", "currentValueLabel", "threshold", "criterion",
            "rationale", "relatedIssues", "totalViolations", "newViolations", "historicalViolations",
            "earliestOccurrenceUtc", "latestOccurrenceUtc", "samples", "thresholdFallback", "thresholdNote",
            "coveredLayers", "trend", "trendDelta", "trendBaselineUtc", "threeState", "scanTruncated"
        })
        {
            Assert.True(first.TryGetProperty(field, out _), $"rules[0] 缺少字段 {field}");
        }
    }

    [Fact]
    public async Task Refresh_TriggersRunnerAndReturnsReport()
    {
        var (client, runner, _) = await CreateServerAsync();
        client.DefaultRequestHeaders.Add("X-Test-User", "qa");

        var response = await client.PostAsync("/api/v1/data-reliability/inspection/refresh", content: null);
        response.EnsureSuccessStatusCode();

        Assert.Equal(1, runner.RefreshCount);
        var data = await ReadDataAsync(response);
        Assert.Equal(13, data.GetProperty("rules").GetArrayLength());
    }

    [Fact]
    public async Task Violations_WithUnknownRuleCode_ReturnsBadRequest()
    {
        var (client, _, _) = await CreateServerAsync();
        client.DefaultRequestHeaders.Add("X-Test-User", "qa");

        var response = await client.GetAsync("/api/v1/data-reliability/rules/S99/violations");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        Assert.Equal(40044, document.RootElement.GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Violations_ClampsLimitIntoAllowedRange()
    {
        var (client, _, exporter) = await CreateServerAsync();
        client.DefaultRequestHeaders.Add("X-Test-User", "qa");

        await client.GetAsync("/api/v1/data-reliability/rules/S1/violations?limit=99999");
        Assert.Equal(5000, exporter.LastLimit);
        Assert.Equal("S1", exporter.LastRuleCode);

        await client.GetAsync("/api/v1/data-reliability/rules/s2/violations?limit=0");
        Assert.Equal(1, exporter.LastLimit);
        Assert.Equal("S2", exporter.LastRuleCode);

        await client.GetAsync("/api/v1/data-reliability/rules/S3/violations");
        Assert.Equal(2000, exporter.LastLimit);
    }

    [Fact]
    public async Task Violations_WithoutAuthentication_ReturnsUnauthorized()
    {
        var (client, _, _) = await CreateServerAsync();

        var response = await client.GetAsync("/api/v1/data-reliability/rules/S1/violations");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
