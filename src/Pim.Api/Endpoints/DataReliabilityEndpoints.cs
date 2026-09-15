using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Pim.Core.Common;
using Pim.Core.Invariants;
using Pim.Infrastructure.Operations;

namespace Pim.Api.Endpoints;

/// <summary>
/// 数据可信度体检接口（#260）：只读端点，供设置页「数据可信度」面板使用。
/// 默认读取最近一次结果（避免每次进设置都全量扫库），手动触发才真正重新体检。
/// </summary>
public static class DataReliabilityEndpoints
{
    /// <summary>未知尺子编号的错误码。</summary>
    private const int UnknownRuleCode = 40044;

    /// <summary>导出清单的单次上限，避免一次导出把整张表拉进内存。</summary>
    private const int MaxViolationExportLimit = 5000;

    private const int DefaultViolationExportLimit = 2000;

    public static RouteGroupBuilder MapDataReliabilityEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/data-reliability").RequireAuthorization();

        group.MapGet("/inspection", async (
            IDataReliabilityInspectionRunner runner,
            CancellationToken ct) =>
        {
            var report = await runner.GetLatestAsync(ct);
            return Results.Ok(ApiResponse<DataReliabilityInspectionReport>.Ok(report));
        });

        group.MapPost("/inspection/refresh", async (
            IDataReliabilityInspectionRunner runner,
            CancellationToken ct) =>
        {
            var report = await runner.RefreshAsync(ct);
            return Results.Ok(ApiResponse<DataReliabilityInspectionReport>.Ok(report));
        });

        group.MapGet("/rules/{code}/violations", async (
            string code,
            int? limit,
            IDataReliabilityViolationExporter exporter,
            CancellationToken ct) =>
        {
            var definition = DataReliabilityRuleCatalog.Find(code);
            if (definition == null)
            {
                return Results.BadRequest(ApiResponse<string>.Error(UnknownRuleCode, "未知的尺子编号"));
            }

            var effectiveLimit = Math.Clamp(limit ?? DefaultViolationExportLimit, 1, MaxViolationExportLimit);
            var export = await exporter.GetViolationsAsync(definition.Code, effectiveLimit, ct);
            return Results.Ok(ApiResponse<DataReliabilityViolationExport>.Ok(export));
        });

        return group;
    }
}
