using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Auth;

namespace Pim.Api.Endpoints;

public static class AiEndpoints
{
    public static void MapAiEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapAiModelEndpoints();
        var group = endpoints.MapGroup("/api/v1/ai")
            .RequireAuthorization(new AuthorizeAttribute { Roles = "admin" })
            .RequireCurrentActiveAdmin();

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
            [FromServices] IAiUsageService usage,
            CancellationToken ct) =>
        {
            if (!TryParseStatus(status, out var parsedStatus))
            {
                return Results.BadRequest(ApiResponse<string>.Error(400, "AI 请求状态无效。"));
            }

            var filter = new AiRequestLogFilter(from, to, module, purpose, sourceObjectType, sourceObjectId, model, parsedStatus, userId, page ?? 1, pageSize ?? 50);
            return Results.Ok(ApiResponse<PagedResult<AiRequestLogListItemDto>>.Ok(await usage.ListRequestsAsync(filter, ct)));
        })
        .Produces<ApiResponse<PagedResult<AiRequestLogListItemDto>>>(200)
        .Produces(401)
        .Produces(403)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("/requests/{id:guid}", async (Guid id, [FromServices] IAiUsageService usage, CancellationToken ct) =>
        {
            var detail = await usage.GetRequestDetailAsync(id, ct);
            return detail is null
                ? Results.NotFound(ApiResponse<string>.Error(404, "AI 请求日志不存在。"))
                : Results.Ok(ApiResponse<AiRequestLogDetailDto>.Ok(detail));
        })
        .Produces<ApiResponse<AiRequestLogDetailDto>>(200)
        .Produces(401)
        .Produces(403)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(500);

        group.MapGet("/usage/summary", async (DateTimeOffset? from, DateTimeOffset? to, [FromServices] IAiUsageService usage, CancellationToken ct) =>
            Results.Ok(ApiResponse<AiUsageSummaryDto>.Ok(await usage.GetUsageSummaryAsync(from, to, ct))))
        .Produces<ApiResponse<AiUsageSummaryDto>>(200)
        .Produces(401)
        .Produces(403)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

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
            "running" => AiRequestStatus.Running,
            "cancelled" => AiRequestStatus.Cancelled,
            "incomplete" => AiRequestStatus.Incomplete,
            _ => null
        };

        return status is not null;
    }
}
