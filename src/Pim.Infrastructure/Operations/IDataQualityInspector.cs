namespace Pim.Infrastructure.Operations;

public sealed record DataQualityInspectionResult(
    string CheckName,
    bool IsHealthy,
    int IssueCount,
    string Message,
    IReadOnlyDictionary<string, string>? Details = null);

public interface IDataQualityInspector
{
    string CheckName { get; }
    Task<DataQualityInspectionResult> InspectAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>
/// 能产出结构化体检报告（#260）的取数层出口。
/// 抽出接口是为了让运行协调器（<c>DataReliabilityInspectionRunner</c>）可以在测试里替换成假实现，
/// 从而真正验证"并发重复调用只跑一次"这类时序行为。
/// </summary>
public interface IDataReliabilityReportInspector
{
    Task<Pim.Core.Invariants.DataReliabilityInspectionReport> InspectReportAsync(
        DateTimeOffset now,
        CancellationToken ct = default);
}

/// <summary>
/// 单条尺子的完整违规清单导出出口（#261 下钻"导出完整违规清单"）。
/// </summary>
public interface IDataReliabilityViolationExporter
{
    Task<Pim.Core.Invariants.DataReliabilityViolationExport> GetViolationsAsync(
        string ruleCode,
        int limit,
        CancellationToken ct = default);
}
