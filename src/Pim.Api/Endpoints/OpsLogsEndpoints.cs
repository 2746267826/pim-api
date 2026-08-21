using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Pim.Api.Infrastructure;
using Pim.Api.Infrastructure.Ops;
using Pim.Api.Services;
using Pim.Core.Common;
using Pim.Core.Operations;

namespace Pim.Api.Endpoints;

public static class OpsLogsEndpoints
{
    public static void MapOpsLogsEndpoints(this IEndpointRouteBuilder e)
    {
        var g = e.MapGroup("/api/v1/ops/logs");
        g.MapGet("/files", async (OpsLogsService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var ip = GetIp(ctx);
            var corr = GetCorrelation(ctx);
            try
            {
                var files = await svc.ListFilesAsync(ct);
                await TryAuditAsync(ctx, "ops.logs.query", ip, corr, new Dictionary<string, string>
                {
                    ["ip"] = ip,
                    ["rowCount"] = files.Count.ToString(),
                    ["bytes"] = "0",
                    ["truncated"] = "false"
                }, AuditResult.Success, null, null);
                return Results.Ok(ApiResponse<IReadOnlyList<LogFileInfo>>.Ok(files));
            }
            catch (Exception ex)
            {
                var code = ex is Pim.Core.Exceptions.DomainException de ? de.ErrorCode : (int?)null;
                await TryAuditAsync(ctx, "ops.logs.query", ip, corr, new Dictionary<string, string>
                {
                    ["ip"] = ip,
                    ["truncated"] = "false"
                }, AuditResult.Failure, code, ex.Message);
                throw;
            }
        });
        g.MapGet("/tail", async (string file, int? lines, string? level, string? keyword, OpsLogsService svc, HttpContext ctx, CancellationToken ct) =>
        {
            var ip = GetIp(ctx);
            var corr = GetCorrelation(ctx);
            try
            {
                var r = await svc.TailAsync(file, lines ?? 50, level, keyword, ct);
                if (r.Truncated) ctx.Response.Headers["X-Truncated"] = "true";
                var bytes = EstimateBytes(r.Lines);
                await TryAuditAsync(ctx, "ops.logs.query", ip, corr, new Dictionary<string, string>
                {
                    ["file"] = file,
                    ["ip"] = ip,
                    ["rowCount"] = r.Lines.Count.ToString(),
                    ["bytes"] = bytes.ToString(),
                    ["truncated"] = r.Truncated.ToString().ToLowerInvariant()
                }, AuditResult.Success, null, null);
                return r.Truncated
                    ? Results.Json(ApiResponse<OpsLogsResult>.Ok(r), statusCode: 206)
                    : Results.Ok(ApiResponse<OpsLogsResult>.Ok(r));
            }
            catch (Exception ex)
            {
                var code = ex is Pim.Core.Exceptions.DomainException de ? de.ErrorCode : (int?)null;
                await TryAuditAsync(ctx, "ops.logs.query", ip, corr, new Dictionary<string, string>
                {
                    ["file"] = file,
                    ["ip"] = ip,
                    ["truncated"] = "false"
                }, AuditResult.Failure, code, ex.Message);
                throw;
            }
        });
        g.MapGet("/query", async ([AsParameters] OpsLogsQuery q, OpsLogsService svc, HttpContext ctx, CancellationToken ct) =>
        {
            // default limit if not provided
            if (q.Limit == 0) q.Limit = 50;
            var ip = GetIp(ctx);
            var corr = GetCorrelation(ctx);
            try
            {
                var r = await svc.QueryAsync(q, ct);
                if (r.Truncated) ctx.Response.Headers["X-Truncated"] = "true";
                var bytes = EstimateBytes(r.Lines);
                await TryAuditAsync(ctx, "ops.logs.query", ip, corr, new Dictionary<string, string>
                {
                    ["file"] = q.File ?? "",
                    ["ip"] = ip,
                    ["rowCount"] = r.Lines.Count.ToString(),
                    ["bytes"] = bytes.ToString(),
                    ["truncated"] = r.Truncated.ToString().ToLowerInvariant()
                }, AuditResult.Success, null, null);
                if (r.Truncated) return Results.Json(ApiResponse<OpsLogsResult>.Ok(r), statusCode: 206);
                return Results.Ok(ApiResponse<OpsLogsResult>.Ok(r));
            }
            catch (Exception ex)
            {
                var code = ex is Pim.Core.Exceptions.DomainException de ? de.ErrorCode : (int?)null;
                await TryAuditAsync(ctx, "ops.logs.query", ip, corr, new Dictionary<string, string>
                {
                    ["file"] = q.File ?? "",
                    ["ip"] = ip,
                    ["truncated"] = "false"
                }, AuditResult.Failure, code, ex.Message);
                throw;
            }
        });
    }

    private static string GetIp(HttpContext ctx) => OpsIpHelper.GetClientIp(ctx);

    private static string? GetCorrelation(HttpContext ctx) => ctx.Items[CorrelationIdMiddleware.HeaderName]?.ToString() ?? ctx.TraceIdentifier;

    private static long EstimateBytes(IReadOnlyList<string> lines)
    {
        long b = 0;
        foreach (var l in lines) b += Encoding.UTF8.GetByteCount(l) + 1;
        return b;
    }

    private static async Task TryAuditAsync(HttpContext ctx, string action, string ip, string? corr, Dictionary<string, string> meta, AuditResult result, int? code, string? msg)
    {
        try
        {
            var audit = ctx.RequestServices.GetService<IAuditLogService>();
            if (audit is null) return;
            await audit.RecordAsync(new CreateAuditLogRequest(
                UserId: null,
                ActorType: AuditActorType.System,
                Action: action,
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
        catch { /* audit must not break request */ }
    }
}
