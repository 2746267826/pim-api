using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pim.Core.Operations;
using Pim.Infrastructure.Metrics;

namespace Pim.Infrastructure.Operations;

/// <summary>
/// 全面数据质量巡检任务（原 Stage0 空转任务实质化）。
/// 周期性执行核心数据质量巡检（心跳新鲜度、同步批次积压、待打标分类积压、AI 网关错误率等），
/// 联动系统审计日志、警告记录与 Prometheus 质量指标。
/// </summary>
public sealed class Stage0DiagnosticJob
{
    private readonly IEnumerable<IDataQualityInspector> _inspectors;
    private readonly IAuditLogService _auditLogs;
    private readonly ILogger<Stage0DiagnosticJob> _logger;
    private readonly TimeProvider _timeProvider;

    public Stage0DiagnosticJob(
        IEnumerable<IDataQualityInspector> inspectors,
        IAuditLogService auditLogs,
        ILogger<Stage0DiagnosticJob> logger,
        TimeProvider? timeProvider = null)
    {
        _inspectors = inspectors;
        _auditLogs = auditLogs;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<DataQualityInspectionResult>> RunAsync(CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        _logger.LogInformation("Stage0 data quality diagnostic job started at {ExecutedAt}", now);

        var results = new List<DataQualityInspectionResult>();
        int totalIssues = 0;

        foreach (var inspector in _inspectors)
        {
            try
            {
                var res = await inspector.InspectAsync(now, ct);
                results.Add(res);

                if (!res.IsHealthy)
                {
                    totalIssues += res.IssueCount > 0 ? res.IssueCount : 1;
                    _logger.LogWarning(
                        "Data quality check failed: [{CheckName}] {Message} (IssueCount: {Count})",
                        res.CheckName,
                        res.Message,
                        res.IssueCount);

                    try
                    {
                        PimMetrics.DataQualityIssuesTotal.WithLabels(res.CheckName, "warning").Inc(res.IssueCount > 0 ? res.IssueCount : 1);
                    }
                    catch { }
                }
                else
                {
                    _logger.LogInformation("Data quality check passed: [{CheckName}] {Message}", res.CheckName, res.Message);
                }
            }
            catch (Exception ex)
            {
                totalIssues++;
                _logger.LogError(ex, "Data quality inspector [{CheckName}] threw unhandled exception", inspector.CheckName);
                results.Add(new DataQualityInspectionResult(
                    inspector.CheckName,
                    false,
                    1,
                    $"Unhandled inspector exception: {ex.Message}"));

                try
                {
                    PimMetrics.DataQualityIssuesTotal.WithLabels(inspector.CheckName, "critical").Inc();
                }
                catch { }
            }
        }

        var isAllHealthy = totalIssues == 0;
        var summaryDict = new Dictionary<string, string>
        {
            ["inspectedAt"] = now.ToString("O"),
            ["totalChecks"] = results.Count.ToString(),
            ["healthyChecks"] = results.Count(r => r.IsHealthy).ToString(),
            ["failedChecks"] = results.Count(r => !r.IsHealthy).ToString(),
            ["totalIssues"] = totalIssues.ToString()
        };

        foreach (var r in results)
        {
            summaryDict[$"check_{r.CheckName}_status"] = r.IsHealthy ? "healthy" : "unhealthy";
            summaryDict[$"check_{r.CheckName}_message"] = r.Message;
        }

        try
        {
            await _auditLogs.RecordAsync(new CreateAuditLogRequest(
                UserId: null,
                ActorType: AuditActorType.System,
                Action: "DataQualityPatrol",
                ResourceType: "Diagnostic",
                ResourceId: "stage0",
                Source: "Stage0DiagnosticJob",
                Result: isAllHealthy ? AuditResult.Success : AuditResult.Failure,
                IpAddress: null,
                UserAgent: null,
                CorrelationId: Guid.NewGuid().ToString("N"),
                Metadata: summaryDict,
                ErrorCode: isAllHealthy ? null : 500,
                ErrorMessage: isAllHealthy
                    ? "All data quality checks passed successfully."
                    : $"{totalIssues} data quality issue(s) detected across {results.Count(r => !r.IsHealthy)} check(s)."),
                ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record audit log for Stage0DiagnosticJob");
        }

        _logger.LogInformation(
            "Stage0 data quality diagnostic job finished. {Healthy}/{Total} checks healthy, {Issues} total issues detected.",
            results.Count(r => r.IsHealthy),
            results.Count,
            totalIssues);

        return results;
    }
}
