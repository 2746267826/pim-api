using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.Infrastructure;
using Pim.Api.Infrastructure.Ops;
using Pim.Api.Services;
using Pim.Core.Common;
using Pim.Core.Operations;

namespace Pim.Api.Endpoints;

public static class OpsDbEndpoints
{
    public static void MapOpsDbEndpoints(this IEndpointRouteBuilder e)
    {
        var g = e.MapGroup("/api/v1/ops/db");
        g.MapGet("/tables", async (OpsDbService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var ip = GetIp(ctx);
            var corr = GetCorrelation(ctx);
            try
            {
                var tables = await svc.ListTablesAsync(ct);
                await TryAuditAsync(ctx, ip, corr, sqlHash: "", rowCount: tables.Count, bytes: 0, truncated: false, result: AuditResult.Success, code: null, msg: null);
                return Results.Ok(ApiResponse<IReadOnlyList<TableInfo>>.Ok(tables));
            }
            catch (Exception ex)
            {
                var code = ex is Pim.Core.Exceptions.DomainException de ? de.ErrorCode : (int?)null;
                await TryAuditAsync(ctx, ip, corr, sqlHash: "", rowCount: 0, bytes: 0, truncated: false, result: AuditResult.Failure, code: code, msg: ex.Message);
                throw;
            }
        });
        g.MapGet("/describe", async (string table, OpsDbService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var ip = GetIp(ctx);
            var corr = GetCorrelation(ctx);
            try
            {
                var cols = await svc.DescribeAsync(table, ct);
                await TryAuditAsync(ctx, ip, corr, sqlHash: "", rowCount: cols.Count, bytes: 0, truncated: false, result: AuditResult.Success, code: null, msg: null);
                return Results.Ok(ApiResponse<IReadOnlyList<ColumnInfo>>.Ok(cols));
            }
            catch (Exception ex)
            {
                var code = ex is Pim.Core.Exceptions.DomainException de ? de.ErrorCode : (int?)null;
                await TryAuditAsync(ctx, ip, corr, sqlHash: "", rowCount: 0, bytes: 0, truncated: false, result: AuditResult.Failure, code: code, msg: ex.Message);
                throw;
            }
        });
        g.MapPost("/query", async (OpsDbQueryRequest req, OpsDbService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var ip = GetIp(ctx);
            var corr = GetCorrelation(ctx);
            var sqlHash = ComputeHash(req.Sql);
            try
            {
                var r = await svc.QueryAsync(req.Sql, req.Params, req.MaxRows, ct);
                if (r.Truncated) ctx.Response.Headers["X-Truncated"] = "true";
                var bytes = EstimateBytes(r.Rows);
                await TryAuditAsync(ctx, ip, corr, sqlHash, r.Rows.Count, bytes, r.Truncated, AuditResult.Success, null, null);
                return r.Truncated
                    ? Results.Json(ApiResponse<OpsDbQueryResult>.Ok(r), statusCode: 206)
                    : Results.Ok(ApiResponse<OpsDbQueryResult>.Ok(r));
            }
            catch (Exception ex)
            {
                var code = ex is Pim.Core.Exceptions.DomainException de ? de.ErrorCode : (int?)null;
                await TryAuditAsync(ctx, ip, corr, sqlHash, 0, 0, false, AuditResult.Failure, code, ex.Message);
                throw;
            }
        });
    }

    private static string GetIp(HttpContext ctx) => OpsIpHelper.GetClientIp(ctx);

    private static string? GetCorrelation(HttpContext ctx) => ctx.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? ctx.TraceIdentifier;

    private static string ComputeHash(string sql)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(sql ?? ""));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    private static long EstimateBytes(IReadOnlyList<Dictionary<string, object?>> rows)
    {
        long b = 0;
        foreach (var r in rows) b += Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(r)) + 1;
        return b;
    }

    private static async Task TryAuditAsync(HttpContext ctx, string ip, string? corr, string sqlHash, int rowCount, long bytes, bool truncated, AuditResult result, int? code, string? msg)
    {
        try
        {
            var audit = ctx.RequestServices.GetService<IAuditLogService>();
            if (audit is null) return;
            var meta = new Dictionary<string, string>
            {
                ["ip"] = ip,
                ["sqlHash"] = sqlHash,
                ["rowCount"] = rowCount.ToString(),
                ["bytes"] = bytes.ToString(),
                ["truncated"] = truncated.ToString().ToLowerInvariant()
            };
            await audit.RecordAsync(new CreateAuditLogRequest(
                UserId: null,
                ActorType: AuditActorType.System,
                Action: "ops.db.query",
                ResourceType: "ops",
                ResourceId: null,
                Source: "ops",
                Result: result,
                IpAddress: ip,
                UserAgent: ctx.Request.Headers.UserAgent.ToString(),
                CorrelationId: corr,
                Metadata: meta,
                ErrorCode: code,
                ErrorMessage: msg), ctx.RequestAborted);
        }
        catch { }
    }
}

public record OpsDbQueryRequest(string Sql, Dictionary<string, object?>? Params, int? MaxRows);
