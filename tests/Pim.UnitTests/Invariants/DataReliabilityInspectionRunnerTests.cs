using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Invariants;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// 体检运行协调器（#260 验收标准 3：重复调用不产生堆积）。
/// </summary>
public class DataReliabilityInspectionRunnerTests
{
    /// <summary>可计数、可挂起的假取数层，用来观测"真的跑了几次"。</summary>
    private sealed class FakeReportInspector : IDataReliabilityReportInspector
    {
        private readonly TaskCompletionSource? _release;
        private int _runs;

        public FakeReportInspector(TaskCompletionSource? release = null)
        {
            _release = release;
        }

        public int Runs => Volatile.Read(ref _runs);

        public Task<DataReliabilityInspectionReport> InspectReportAsync(
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            Interlocked.Increment(ref _runs);

            if (_release != null)
            {
                return _release.Task.ContinueWith(_ => BuildReport(now), TaskScheduler.Default);
            }

            return Task.FromResult(BuildReport(now));
        }

        public static DataReliabilityInspectionReport BuildReport(DateTimeOffset now) => new(
            InspectedAtUtc: now,
            Version: 0,
            ElapsedMilliseconds: 1,
            Status: "green",
            RedCount: 0,
            YellowCount: 0,
            GreenCount: 13,
            UnknownCount: 0,
            TotalViolations: 0,
            NewViolations: 0,
            HistoricalViolations: 0,
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
                    Status: "green",
                    StatusLabel: "绿",
                    Detail: "PASS",
                    CurrentValue: 0,
                    CurrentValueUnit: "条",
                    CurrentValueLabel: null,
                    Threshold: definition.Threshold,
                    Criterion: definition.Criterion,
                    Rationale: definition.Rationale,
                    RelatedIssues: definition.RelatedIssues,
                    TotalViolations: 0,
                    NewViolations: 0,
                    HistoricalViolations: 0,
                    EarliestOccurrenceUtc: null,
                    LatestOccurrenceUtc: null,
                    Samples: Array.Empty<string>(),
                    ThresholdFallback: false,
                    ThresholdNote: null,
                    CoveredLayers: null,
                    Trend: "unknown",
                    TrendDelta: null,
                    TrendBaselineUtc: null,
                    ThreeState: null,
                    ScanTruncated: false))
                .ToArray(),
            Message: "ok");
    }

    /// <summary>第一次体检抛异常的假取数层，用来验证失败后状态复位。</summary>
    private sealed class FailOnceReportInspector : IDataReliabilityReportInspector
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Task<DataReliabilityInspectionReport> InspectReportAsync(
            DateTimeOffset now,
            CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new InvalidOperationException("第一次体检失败");
            }

            return Task.FromResult(FakeReportInspector.BuildReport(now));
        }
    }

    private static (DataReliabilityInspectionRunner Runner, T Inspector, IDataReliabilityInspectionStore Store)
        BuildRunner<T>(T inspector) where T : class, IDataReliabilityReportInspector
    {
        var store = new InMemoryDataReliabilityInspectionStore();
        var services = new ServiceCollection();
        services.AddScoped<IDataReliabilityReportInspector>(_ => inspector);
        var provider = services.BuildServiceProvider();

        var runner = new DataReliabilityInspectionRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            store,
            TimeProvider.System,
            NullLogger<DataReliabilityInspectionRunner>.Instance);

        return (runner, inspector, store);
    }

    [Fact]
    public async Task GetLatestAsync_WithoutCache_RunsInspectionOnce()
    {
        var (runner, inspector, store) = BuildRunner(new FakeReportInspector());

        var report = await runner.GetLatestAsync();

        Assert.Equal(1, inspector.Runs);
        Assert.Equal(1, report.Version);
        Assert.Same(report, store.Latest);
    }

    [Fact]
    public async Task GetLatestAsync_WithCache_DoesNotRunInspectionAgain()
    {
        var (runner, inspector, _) = BuildRunner(new FakeReportInspector());
        await runner.GetLatestAsync();

        var cached = await runner.GetLatestAsync();

        Assert.Equal(1, inspector.Runs);
        Assert.Equal(1, cached.Version);
    }

    [Fact]
    public async Task RefreshAsync_RerunsAndBumpsVersion()
    {
        var (runner, inspector, _) = BuildRunner(new FakeReportInspector());
        var first = await runner.GetLatestAsync();

        var second = await runner.RefreshAsync();

        Assert.Equal(2, inspector.Runs);
        Assert.Equal(first.Version + 1, second.Version);
    }

    /// <summary>核心防堆积断言：5 个并发重新体检只能触发 1 次全量体检，且拿到同一个 Version。</summary>
    [Fact]
    public async Task ConcurrentRefreshes_ShareSingleInspection()
    {
        var release = new TaskCompletionSource();
        var (runner, inspector, _) = BuildRunner(new FakeReportInspector(release));

        var calls = Enumerable.Range(0, 5).Select(_ => runner.RefreshAsync()).ToList();
        Assert.True(runner.IsRunning);

        release.SetResult();
        var reports = await Task.WhenAll(calls);

        Assert.Equal(1, inspector.Runs);
        Assert.Single(reports.Select(report => report.Version).Distinct());
        Assert.All(reports, report => Assert.Equal(1, report.Version));
        Assert.False(runner.IsRunning);
    }

    [Fact]
    public async Task RefreshAsync_AfterFailure_CanRunAgain()
    {
        var (runner, inspector, _) = BuildRunner(new FailOnceReportInspector());

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RefreshAsync());

        // 失败后必须复位，否则后续调用会永远拿到那个失败的 Task。
        Assert.False(runner.IsRunning);

        var recovered = await runner.RefreshAsync();
        Assert.Equal(1, recovered.Version);
        Assert.Equal(2, inspector.Attempts);
    }
}
