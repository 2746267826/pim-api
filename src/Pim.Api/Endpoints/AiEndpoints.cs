using Microsoft.AspNetCore.Authorization;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai;

namespace Pim.Api.Endpoints;

public static class AiEndpoints
{
    public static void MapAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/ai")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" });

        group.MapGet("/status", async (IAiUsageService usage, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiStatusDto>.Ok(await usage.GetStatusAsync(ct))));

        group.MapPost("/test", async (IAiGateway gateway, CancellationToken ct) =>
        {
            var result = await gateway.CompleteAsync(new AiGatewayRequest(
                Module: "system",
                Purpose: "ai.test",
                SourceObjectType: "system",
                SourceObjectId: "ai-test",
                Messages: [new AiMessage(AiMessageRole.User, "Reply with the word ok.")],
                Model: null,
                SchemaName: null,
                SchemaVersion: null,
                MaxOutputTokens: 32,
                MaxAttempts: 1,
                Metadata: new Dictionary<string, string> { ["endpoint"] = "/api/v1/ai/test" }), ct);
            return Results.Ok(ApiResponse<AiResult>.Ok(result));
        });

        group.MapGet("/requests", async (
            DateTimeOffset? from,
            DateTimeOffset? to,
            string? module,
            string? purpose,
            string? sourceObjectType,
            string? sourceObjectId,
            string? model,
            string? status,
            Guid? userId,
            int? page,
            int? pageSize,
            IAiUsageService usage,
            CancellationToken ct) =>
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, "AI 请求状态无效。"));
            }

            var filter = new AiRequestLogFilter(from, to, module, purpose, sourceObjectType, sourceObjectId, model, parsedStatus, userId, page ?? 1, pageSize ?? 50);
            return Results.Ok(ApiResponse<PagedResult<AiRequestLogListItemDto>>.Ok(await usage.ListRequestsAsync(filter, ct)));
        });

        group.MapGet("/requests/{id:guid}", async (Guid id, IAiUsageService usage, CancellationToken ct) =>
        {
            var detail = await usage.GetRequestDetailAsync(id, ct);
            return detail is null
                ? Results.NotFound(ApiResponse<string>.Error(404, "AI 请求日志不存在。"))
                : Results.Ok(ApiResponse<AiRequestLogDetailDto>.Ok(detail));
        });

        group.MapGet("/usage/summary", async (DateTimeOffset? from, DateTimeOffset? to, IAiUsageService usage, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiUsageSummaryDto>.Ok(await usage.GetUsageSummaryAsync(from, to, ct))));

        group.MapPost("/health-check", async (IAiProviderHealthService health, IAiUsageService usage, CancellationToken ct) =>
        {
            await health.CheckAsync(ct);
            return Results.Ok(ApiResponse<AiStatusDto>.Ok(await usage.GetStatusAsync(ct)));
        });
    }

    public static bool TryParseStatus(string? value, out AiRequestStatus? status)
    {
        status = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        status = value.Trim().ToLowerInvariant() switch
        {
            "succeeded" => AiRequestStatus.Succeeded,
            "failed" => AiRequestStatus.Failed,
            "blocked" => AiRequestStatus.Blocked,
            "timedout" => AiRequestStatus.TimedOut,
            "timed_out" => AiRequestStatus.TimedOut,
            "failedvalidation" => AiRequestStatus.FailedValidation,
            "failed_validation" => AiRequestStatus.FailedValidation,
            _ => null
        };

        return status is not null;
    }
}
