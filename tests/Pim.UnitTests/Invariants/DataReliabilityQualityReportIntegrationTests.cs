using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Invariants;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Pim.UnitTests.Calendar;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// #260 第 4 点 / 验收标准 5：质量报告与尺子结论一致 —— 尺子红，报告不得绿。
/// 覆盖 PC 与手机两侧的质量服务；未接入门禁（构造参数为 null）时必须保持原行为。
/// </summary>
public class DataReliabilityQualityReportIntegrationTests
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 14, 12, 0, 0, TimeSpan.Zero);

    private static readonly string[] PcRules = { "S1", "S2", "S3", "S5", "S6", "S7", "S8", "S13" };
    private static readonly string[] MobileRules = { "S4", "S10", "S11", "S12" };

    private sealed class StubGate : IDataReliabilityGate
    {
        private readonly DataReliabilityGateVerdict _verdict;

        public StubGate(PimHealthStatus status, params string[] redRules)
        {
            var red = status == PimHealthStatus.Critical ? redRules : Array.Empty<string>();
            var yellow = status == PimHealthStatus.Warning ? redRules : Array.Empty<string>();
            var unknown = status == PimHealthStatus.Unknown ? Array.Empty<string>() : Array.Empty<string>();
            _verdict = new DataReliabilityGateVerdict(
                status,
                red,
                yellow,
                unknown,
                status == PimHealthStatus.Unknown ? null : FixedNow,
                status switch
                {
                    PimHealthStatus.Critical => $"数据可信度尺子报红：{string.Join("、", red)}",
                    PimHealthStatus.Warning => $"数据可信度尺子报黄：{string.Join("、", yellow)}",
                    PimHealthStatus.Unknown => "数据可信度尺子尚未体检",
                    _ => "数据可信度尺子均通过"
                });
        }

        public IReadOnlyList<string> LastRequestedCodes { get; private set; } = Array.Empty<string>();

        public DataReliabilityGateVerdict Evaluate(IReadOnlyList<string> ruleCodes, TimeSpan? maxAge = null)
        {
            LastRequestedCodes = ruleCodes;
            return _verdict;
        }
    }

    private static PimDbContext CreateDbContext()
    {
        PimDbContext.RegisterModuleAssembly(typeof(AwEventEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(Pim.Module.Mobile.Entities.MobileDeviceEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new PimDbContext(options);
    }

    private static int SeverityRank(PimHealthStatus status) => status switch
    {
        PimHealthStatus.Critical => 3,
        PimHealthStatus.Warning => 2,
        PimHealthStatus.Unknown => 1,
        _ => 0
    };

    [Fact]
    public async Task PcQuality_WhenRulerIsRed_OverallStatusIsNotHealthy()
    {
        await using var db = CreateDbContext();
        var gate = new StubGate(PimHealthStatus.Critical, "S1");
        var service = new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = FixedNow }, gate);

        var result = await service.GetQualityAsync(null, null, null, CancellationToken.None);

        var component = Assert.Single(result.Components, c => c.Key == "data_reliability");
        Assert.Equal(PimHealthStatus.Critical, component.Status);
        Assert.Contains("S1", component.Details["redRules"]);

        var issue = Assert.Single(result.Issues, i => i.Code == "S1");
        Assert.Equal(PimHealthStatus.Critical, issue.Severity);
        Assert.Equal("data_reliability", issue.ComponentKey);

        Assert.NotEqual(PimHealthStatus.Healthy, result.OverallStatus);
        Assert.True(SeverityRank(result.OverallStatus) >= SeverityRank(PimHealthStatus.Critical));
    }

    [Fact]
    public async Task PcQuality_RequestsThePcOwnedRulersOnly()
    {
        await using var db = CreateDbContext();
        var gate = new StubGate(PimHealthStatus.Healthy);
        var service = new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = FixedNow }, gate);

        await service.GetQualityAsync(null, null, null, CancellationToken.None);

        Assert.Equal(PcRules, gate.LastRequestedCodes);
    }

    [Fact]
    public async Task PcQuality_WhenRulerHasNoData_ComponentIsExplicitAndNotSilent()
    {
        await using var db = CreateDbContext();
        var gate = new StubGate(PimHealthStatus.Unknown);
        var service = new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = FixedNow }, gate);

        var result = await service.GetQualityAsync(null, null, null, CancellationToken.None);

        var component = Assert.Single(result.Components, c => c.Key == "data_reliability");
        Assert.Equal(PimHealthStatus.Unknown, component.Status);
        Assert.Contains("尚未体检", component.Message);
    }

    [Fact]
    public async Task PcQuality_WithoutGate_KeepsExistingShape()
    {
        await using var db = CreateDbContext();
        var service = new PcTrackerQualityService(db, new StubTimeProvider { UtcNowValue = FixedNow });

        var result = await service.GetQualityAsync(null, null, null, CancellationToken.None);

        Assert.DoesNotContain(result.Components, c => c.Key == "data_reliability");
    }

    [Fact]
    public async Task MobileQuality_WhenRulerIsRed_OverallStatusIsNotHealthy()
    {
        await using var db = CreateDbContext();
        var gate = new StubGate(PimHealthStatus.Critical, "S11");
        var service = new Pim.Module.Mobile.Services.MobileQualityService(
            db,
            new FixedCurrentUserService(Guid.NewGuid()),
            new StubTimeProvider { UtcNowValue = FixedNow },
            gate);

        var result = await service.GetQualityAsync(null, null, CancellationToken.None);

        var component = Assert.Single(result.Components, c => c.Key == "data_reliability");
        Assert.Equal(PimHealthStatus.Critical, component.Status);
        Assert.Contains("S11", component.Details["redRules"]);

        var issue = Assert.Single(result.Issues, i => i.Code == "S11");
        Assert.Equal(PimHealthStatus.Critical, issue.Severity);

        Assert.NotEqual(PimHealthStatus.Healthy, result.OverallStatus);
        Assert.True(SeverityRank(result.OverallStatus) >= SeverityRank(PimHealthStatus.Critical));
    }

    [Fact]
    public async Task MobileQuality_RequestsTheMobileOwnedRulersOnly()
    {
        await using var db = CreateDbContext();
        var gate = new StubGate(PimHealthStatus.Healthy);
        var service = new Pim.Module.Mobile.Services.MobileQualityService(
            db,
            new FixedCurrentUserService(Guid.NewGuid()),
            new StubTimeProvider { UtcNowValue = FixedNow },
            gate);

        await service.GetQualityAsync(null, null, CancellationToken.None);

        Assert.Equal(MobileRules, gate.LastRequestedCodes);
    }

    private sealed class FixedCurrentUserService : ICurrentUserService
    {
        public FixedCurrentUserService(Guid userId)
        {
            UserId = userId;
        }

        public Guid? UserId { get; }
        public string? Role => "User";
    }

    [Fact]
    public void GateRuleSets_AreDisjointAndCoveredByTheCatalog()
    {
        // PC 与手机各自关心的尺子不得重叠，避免同一条尺子被两处重复报警。
        Assert.Empty(PcRules.Intersect(MobileRules));
        Assert.All(PcRules.Concat(MobileRules), code => Assert.NotNull(DataReliabilityRuleCatalog.Find(code)));
    }
}
