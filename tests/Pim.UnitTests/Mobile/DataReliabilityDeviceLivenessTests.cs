using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Core.Invariants;
using Pim.Core.Liveness;
using Pim.Infrastructure.Operations;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// REQ-10：数据可信度体检新增「设备存活」数据项，**本版本不判档**（R4-P1 / AC-10.3）。
/// </summary>
public sealed class DataReliabilityDeviceLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 12, 0, 0, TimeSpan.Zero);

    private sealed class StubProvider : IDeviceLivenessInspectionProvider
    {
        private readonly IReadOnlyList<DeviceLivenessInspectionItem> _items;
        private readonly Exception? _failure;

        public StubProvider(IReadOnlyList<DeviceLivenessInspectionItem> items, Exception? failure = null)
        {
            _items = items;
            _failure = failure;
        }

        public Task<IReadOnlyList<DeviceLivenessInspectionItem>> GetLivenessForInspectionAsync(
            DateTimeOffset rangeStartUtc,
            DateTimeOffset rangeEndUtc,
            CancellationToken ct = default)
        {
            if (_failure is not null)
            {
                throw _failure;
            }

            return Task.FromResult(_items);
        }
    }

    private static DeviceLivenessInspectionItem Item(string deviceId, bool hasData)
    {
        var summary = DeviceLivenessCalculator.Summarize(
            hasData
                ? new[] { new LivenessEvidence(Now.AddHours(-1), LivenessEvidenceSources.Heartbeat) }
                : Array.Empty<LivenessEvidence>(),
            null,
            Now.AddDays(-7),
            Now);

        return new DeviceLivenessInspectionItem(deviceId, deviceId, "phone", summary);
    }

    private static DataReliabilityQualityInspector Inspector(params IDeviceLivenessInspectionProvider[] providers) =>
        new(
            db: null,
            options: Options.Create(new InvariantOptions()),
            logger: NullLogger<DataReliabilityQualityInspector>.Instance,
            store: null,
            timeProvider: new FixedTimeProvider(Now),
            livenessProviders: providers);

    [Fact]
    public async Task Report_ContainsDeviceLivenessSectionWithDataFields()
    {
        // AC-10.1：体检结果含该项，且能读到结论 / 两个覆盖率 / 最长静默 / 死因汇总 / 最近事件时间。
        var report = await Inspector(new StubProvider(new[] { Item("android-phone", hasData: true) }))
            .InspectReportAsync(Now);

        var item = Assert.Single(report.DeviceLiveness!);
        Assert.Equal("android-phone", item.DeviceId);
        Assert.True(item.Summary.HasData);
        Assert.NotNull(item.Summary.Conclusion);
        Assert.NotNull(item.Summary.CoverageByHour);
        Assert.NotNull(item.Summary.CoverageByExpectedHeartbeat);
        Assert.NotNull(item.Summary.Causes);
        Assert.NotNull(item.Summary.LastEventAtUtc);
    }

    [Fact]
    public async Task Report_DeviceWithoutData_ShowsNoDataConclusion()
    {
        // AC-10.2：无数据设备显示"无数据/未上报"，不得给出"无异常"结论。
        var report = await Inspector(new StubProvider(new[] { Item("android-tablet", hasData: false) }))
            .InspectReportAsync(Now);

        var item = Assert.Single(report.DeviceLiveness!);
        Assert.False(item.Summary.HasData);
        Assert.Equal(DeviceLivenessRules.NoDataConclusion, item.Summary.Conclusion);
        Assert.Null(item.Summary.CoverageByHour);
        Assert.DoesNotContain("无异常", item.Summary.Conclusion);
    }

    [Fact]
    public async Task Report_DeviceLivenessDoesNotChangeRedYellowGreenVerdict()
    {
        // AC-10.3：设备存活是独立数据区块，不参与也不改变 13 条尺子的红/黄/绿档位结论。
        var withoutProvider = await Inspector().InspectReportAsync(Now);
        var withProvider = await Inspector(new StubProvider(new[]
        {
            Item("android-phone", hasData: true),
            Item("android-tablet", hasData: false),
        })).InspectReportAsync(Now);

        Assert.Equal(withoutProvider.Status, withProvider.Status);
        Assert.Equal(withoutProvider.RedCount, withProvider.RedCount);
        Assert.Equal(withoutProvider.YellowCount, withProvider.YellowCount);
        Assert.Equal(withoutProvider.GreenCount, withProvider.GreenCount);
        Assert.Equal(withoutProvider.UnknownCount, withProvider.UnknownCount);
        Assert.Equal(withoutProvider.Rules.Count, withProvider.Rules.Count);
    }

    [Fact]
    public void DeviceLivenessItem_HasNoHealthGradeField()
    {
        // AC-10.3 的结构性保证：该项的数据契约里根本不存在"红/黄/绿档位"字段
        // （静默时段的标色属于 REQ-7 AC-7.4，是另一回事）。
        var propertyNames = typeof(DeviceLivenessInspectionItem)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(property => property.Name)
            .ToList();

        Assert.DoesNotContain("Status", propertyNames);
        Assert.DoesNotContain("Grade", propertyNames);
        Assert.DoesNotContain("Level", propertyNames);
        Assert.DoesNotContain("IsHealthy", propertyNames);
    }

    [Fact]
    public async Task Report_NoProvider_KeepsSectionPresentWithVisibleNotice()
    {
        // 取数源缺席不能变成"设备一切正常"：区块仍在，且 notices 里写明原因。
        var report = await Inspector().InspectReportAsync(Now);

        Assert.NotNull(report.DeviceLiveness);
        Assert.Empty(report.DeviceLiveness!);
        Assert.True(report.Notices.ContainsKey("device_liveness_unavailable"));
    }

    [Fact]
    public async Task Report_ProviderFailure_IsVisibleAndDoesNotFailInspection()
    {
        // REQ-28：失败要有可见出口，且不能把整次体检打成失败。
        var report = await Inspector(new StubProvider(Array.Empty<DeviceLivenessInspectionItem>(),
                new InvalidOperationException("provider exploded")))
            .InspectReportAsync(Now);

        Assert.True(report.Notices.ContainsKey("device_liveness_error"));
        Assert.Contains("provider exploded", report.Notices["device_liveness_error"]);
        Assert.NotNull(report.Rules);
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
