using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai;

namespace Pim.Api.Endpoints;

public static class AiEndpoints
{
    public static void MapAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/ai").RequireAuthorization();

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
            AiRequestStatus? status,
            Guid? userId,
            int? page,
            int? pageSize,
            IAiUsageService usage,
            CancellationToken ct) =>
        {
            var filter = new AiRequestLogFilter(from, to, module, purpose, sourceObjectType, sourceObjectId, model, status, userId, page ?? 1, pageSize ?? 50);
            return Results.Ok(ApiResponse<PagedResult<AiRequestLogListItemDto>>.Ok(await usage.ListRequestsAsync(filter, ct)));
        });

        group.MapGet("/requests/{id:guid}", async (Guid id, IAiUsageService usage, CancellationToken ct) =>
        {
            var detail = await usage.GetRequestDetailAsync(id, ct);
            return detail is null
                ? Results.NotFound(ApiResponse<string>.Error(404, "AI request log not found."))
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
}
