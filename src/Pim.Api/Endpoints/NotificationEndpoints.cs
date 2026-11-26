using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Pim.Core.Common;
using Pim.Core.Notifications;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Notifications;

namespace Pim.Api.Endpoints;

public static class NotificationEndpoints
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static IEndpointRouteBuilder MapNotificationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/notifications")
            .RequireAuthorization();

        group.MapGet("/capabilities", (
            [FromServices] NotificationSubscriptionService subscriptions) =>
            Results.Ok(ApiResponse<NotificationCapabilitiesDto>.Ok(
                subscriptions.GetCapabilities())))
        .Produces<ApiResponse<NotificationCapabilitiesDto>>(200)
        .Produces(401);

        group.MapGet("/preferences", async (
            HttpContext http,
            [FromServices] NotificationPreferenceService preferences,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NotificationPreferenceDto>.Ok(
                await preferences.GetAsync(DeviceId(http), ct))))
        .Produces<ApiResponse<NotificationPreferenceDto>>(200)
        .Produces(401);

        group.MapPut("/preferences", async (
            HttpContext http,
            [FromBody] UpdateNotificationPreferencesRequest request,
            [FromServices] NotificationPreferenceService preferences,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NotificationPreferenceDto>.Ok(
                await preferences.UpdateAsync(request, DeviceId(http), ct))))
        .Produces<ApiResponse<NotificationPreferenceDto>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces(401);

        group.MapGet("/subscriptions", async (
            [FromServices] NotificationSubscriptionService subscriptions,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<IReadOnlyList<NotificationSubscriptionDto>>.Ok(
                await subscriptions.ListAsync(ct))))
        .Produces<ApiResponse<IReadOnlyList<NotificationSubscriptionDto>>>(200)
        .Produces(401);

        group.MapPost("/subscriptions", async (
            [FromBody] CreateNotificationSubscriptionRequest request,
            [FromServices] NotificationSubscriptionService subscriptions,
            CancellationToken ct) =>
        {
            var result = await subscriptions.UpsertAsync(request, ct);
            return Results.Ok(ApiResponse<NotificationSubscriptionDto>.Ok(result));
        })
        .Produces<ApiResponse<NotificationSubscriptionDto>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces(401);

        group.MapDelete("/subscriptions/{id:guid}", async (
            Guid id,
            [FromServices] NotificationSubscriptionService subscriptions,
            CancellationToken ct) =>
        {
            var result = await subscriptions.RevokeAsync(id, ct);
            return result is null
                ? Results.NotFound(ApiResponse<string>.Error(40401, "订阅不存在"))
                : Results.Ok(ApiResponse<NotificationSubscriptionDto>.Ok(result));
        })
        .Produces<ApiResponse<NotificationSubscriptionDto>>(200)
        .Produces<ApiResponse<string>>(404)
        .Produces(401);

        group.MapGet("", async (
            [FromQuery] int? limit,
            [FromQuery] long? before,
            [FromServices] NotificationQueryService query,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NotificationListResponse>.Ok(
                await query.ListAsync(limit ?? 50, before, ct))))
        .Produces<ApiResponse<NotificationListResponse>>(200)
        .Produces(401);

        group.MapPost("/{id:guid}/read", async (
            Guid id,
            [FromServices] NotificationQueryService query,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NotificationReadResponse>.Ok(
                await query.MarkReadAsync(id, ct))))
        .Produces<ApiResponse<NotificationReadResponse>>(200)
        .Produces<ApiResponse<string>>(404)
        .Produces(401);

        group.MapPost("/read-all", async (
            [FromServices] NotificationQueryService query,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NotificationReadAllResponse>.Ok(
                await query.MarkAllReadAsync(ct))))
        .Produces<ApiResponse<NotificationReadAllResponse>>(200)
        .Produces(401);

        group.MapGet("/deliveries", async (
            [FromQuery] int? limit,
            [FromQuery] string? status,
            [FromServices] NotificationQueryService query,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NotificationDeliveryListResponse>.Ok(
                await query.ListDeliveriesAsync(limit ?? 50, status, ct))))
        .Produces<ApiResponse<NotificationDeliveryListResponse>>(200)
        .Produces<ApiResponse<string>>(400)
        .Produces(401);

        group.MapPost("/deliveries/{id:guid}/retry", async (
            Guid id,
            [FromServices] NotificationQueryService query,
            CancellationToken ct) =>
            Results.Ok(ApiResponse<NotificationDeliveryDto>.Ok(
                await query.RetryDeliveryAsync(id, ct))))
        .Produces<ApiResponse<NotificationDeliveryDto>>(200)
        .Produces<ApiResponse<string>>(404)
        .Produces<ApiResponse<string>>(409)
        .Produces(401);

        group.MapPost("/test", async (
            [FromServices] NotificationEventWriter writer,
            [FromServices] ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId
                ?? throw new Pim.Core.Exceptions.DomainException(40101, "请先登录");
            var outcome = await writer.TryEnqueueAsync(
                userId,
                new NotificationEventDraft(
                    NotificationEventTypes.Test,
                    $"notification-test:{Guid.NewGuid():D}",
                    "测试通知",
                    "这是一条来自 PIM 的测试通知。",
                    "/settings/notifications"),
                ct);
            var enqueued = outcome == NotificationEnqueueOutcome.Enqueued;
            return Results.Ok(ApiResponse<NotificationTestResponse>.Ok(
                new NotificationTestResponse(
                    enqueued,
                    outcome switch
                    {
                        NotificationEnqueueOutcome.Enqueued =>
                            "测试通知已入队；站内通知与已订阅通道会按偏好送达。",
                        NotificationEnqueueOutcome.Duplicate =>
                            "测试通知重复，未再次创建。",
                        NotificationEnqueueOutcome.Disabled =>
                            "通知偏好已关闭全部通道，事件已作为抑制记录保存。",
                        _ => "测试通知入队失败，请稍后重试。"
                    })));
        })
        .Produces<ApiResponse<NotificationTestResponse>>(200)
        .Produces(401);

        group.MapGet("/stream", async (
            HttpContext http,
            [FromQuery] long? after,
            [FromServices] NotificationQueryService query,
            [FromServices] ICurrentUserService currentUser,
            [FromServices] IOptions<NotificationDispatcherOptions> options,
            CancellationToken ct) =>
        {
            var userId = currentUser.UserId;
            if (userId is null)
                return Results.Unauthorized();

            var cursor = after
                ?? ParseLastEventId(http.Request.Headers["Last-Event-ID"].FirstOrDefault())
                ?? await query.LatestCursorAsync(userId.Value, ct);

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";
            http.Response.Headers["X-Accel-Buffering"] = "no";
            await http.Response.WriteAsync("retry: 3000\n\n", ct);
            await http.Response.Body.FlushAsync(ct);

            var deadline = DateTimeOffset.UtcNow.AddSeconds(
                Math.Max(5, options.Value.PollIntervalSeconds * 5));
            var nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(10);
            try
            {
                while (!ct.IsCancellationRequested && DateTimeOffset.UtcNow < deadline)
                {
                    var items = await query.ReadStreamBatchAsync(userId.Value, cursor, 50, ct);
                    foreach (var item in items)
                    {
                        var payload = JsonSerializer.Serialize(item, Json);
                        await http.Response.WriteAsync(
                            $"id: {item.Cursor}\nevent: notification\ndata: {payload}\n\n",
                            ct);
                        cursor = item.Cursor;
                    }
                    if (items.Count > 0)
                    {
                        await http.Response.Body.FlushAsync(ct);
                        nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(10);
                    }
                    else if (DateTimeOffset.UtcNow >= nextHeartbeat)
                    {
                        await http.Response.WriteAsync(": keep-alive\n\n", ct);
                        await http.Response.Body.FlushAsync(ct);
                        nextHeartbeat = DateTimeOffset.UtcNow.AddSeconds(10);
                    }
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }
            return Results.Empty;
        })
        .Produces(200, contentType: "text/event-stream")
        .Produces(401);

        return endpoints;
    }

    private static long? ParseLastEventId(string? value) =>
        long.TryParse(value, out var parsed) && parsed > 0 ? parsed : null;

    private static string? DeviceId(HttpContext http) =>
        http.Request.Headers["X-Pim-Device-Id"].FirstOrDefault();
}
