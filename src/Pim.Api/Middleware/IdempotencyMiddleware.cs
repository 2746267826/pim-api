using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Common;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Api.Middleware;

/// <summary>
/// 写操作幂等（契约语义见 <c>docs/client-contract-semantics.md</c> §2）。
///
/// <para>
/// 语义：请求头 <c>Idempotency-Key</c>（可选）。作用域是「用户 + 方法 + **路径模板** + key」。
/// 首次请求先占位（<c>in_progress</c>）；成功后把状态码与响应体写回（<c>completed</c>，24 小时）；
/// 24 小时内同 key 重放 → 原样返回缓存的响应，不再执行下游；首次仍在执行中 → 409 / 40902。
/// 首次失败（非 2xx）→ 删除占位、不缓存，客户端可用同 key 重试。
/// </para>
/// <para>
/// 为什么用「路径模板」而不是具体 URL：同一个客户端 key 在 <c>/tasks/{a}</c> 与 <c>/tasks/{b}</c>
/// 上不能被当成同一操作，否则重放会返回另一条资源；也不要让路径里的可变 id 让去重失效。
/// </para>
/// <para>
/// 失败降级：幂等存储本身出错（连不上库 / 唯一键竞争以外的异常）时**不影响主操作** ——
/// 记 Warning 后按「没有幂等」继续执行。幂等是锦上添花，不能成为整体可用性的单点。
/// </para>
/// </summary>
public sealed class IdempotencyMiddleware
{
    public const string HeaderName = "Idempotency-Key";
    public const int KeyInProgressErrorCode = 40902;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);
    private const int MaxKeyLength = 128;

    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete
    };

    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext ctx)
    {
        if (!WriteMethods.Contains(ctx.Request.Method) || !ctx.Request.Path.StartsWithSegments("/api"))
        {
            await _next(ctx);
            return;
        }

        var rawKey = ctx.Request.Headers[HeaderName].ToString()?.Trim();
        if (string.IsNullOrEmpty(rawKey))
        {
            await _next(ctx);
            return;
        }
        if (rawKey.Length > MaxKeyLength)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(ApiResponse<string>.Error(40002, $"Idempotency-Key 不能超过 {MaxKeyLength} 个字符"));
            return;
        }

        var userId = ctx.RequestServices.GetService<ICurrentUserService>()?.UserId;
        if (userId is null)
        {
            // 未认证的写请求由下游鉴权处理（401）；没有用户就没有幂等作用域。
            await _next(ctx);
            return;
        }

        var db = ctx.RequestServices.GetRequiredService<PimDbContext>();
        var now = DateTimeOffset.UtcNow;
        var route = IdempotencyKey.RouteOf(ctx.GetEndpoint(), ctx.Request.Path);

        IdempotencyRecordEntity? record;
        try
        {
            record = await db.IdempotencyRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == userId && r.Method == ctx.Request.Method.ToUpperInvariant()
                                          && r.Route == route && r.Key == rawKey && r.ExpiresAt > now);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "幂等记录读取失败，按无幂等继续执行：{Method} {Route}", ctx.Request.Method, route);
            await _next(ctx);
            return;
        }

        if (record is not null)
        {
            if (record.State == "in_progress")
            {
                await WriteInProgressAsync(ctx);
                return;
            }
            await ReplayAsync(ctx, record);
            return;
        }

        // 占位：唯一索引保证并发下只有一个请求能拿到「首次」。
        var placeholder = new IdempotencyRecordEntity
        {
            UserId = userId.Value,
            Method = ctx.Request.Method.ToUpperInvariant(),
            Route = route,
            Key = rawKey,
            State = "in_progress",
            ExpiresAt = now.Add(Retention)
        };
        try
        {
            db.IdempotencyRecords.Add(placeholder);
            await db.SaveChangesAsync(ctx.RequestAborted);
        }
        catch (DbUpdateException)
        {
            // 唯一键冲突：另一并发请求已占位。
            db.Entry(placeholder).State = EntityState.Detached;
            await WriteInProgressAsync(ctx);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "幂等记录占位失败，按无幂等继续执行：{Method} {Route}", ctx.Request.Method, route);
            db.Entry(placeholder).State = EntityState.Detached;
            await _next(ctx);
            return;
        }

        var originalBody = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        var succeeded = false;
        try
        {
            await _next(ctx);
            succeeded = ctx.Response.StatusCode is >= 200 and < 300;
        }
        finally
        {
            ctx.Response.Body = originalBody;
            if (succeeded)
            {
                await CompleteAsync(db, placeholder.Id, ctx, buffer);
            }
            else
            {
                await ReleaseAsync(db, placeholder.Id);
            }
            buffer.Position = 0;
            await buffer.CopyToAsync(originalBody, ctx.RequestAborted);
        }
    }

    private async Task CompleteAsync(PimDbContext db, Guid recordId, HttpContext ctx, MemoryStream body)
    {
        try
        {
            var stored = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Id == recordId);
            if (stored is null) return;
            stored.State = "completed";
            stored.StatusCode = ctx.Response.StatusCode;
            stored.ContentType = ctx.Response.ContentType;
            stored.ResponseBody = System.Text.Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            // 写缓存失败：删掉占位，让客户端下次重试时重新执行（宁可重复执行，也不留一个永远 409 的键）。
            _logger.LogWarning(ex, "幂等响应写入失败，已释放占位键 {RecordId}", recordId);
            await ReleaseAsync(db, recordId);
        }
    }

    private async Task ReleaseAsync(PimDbContext db, Guid recordId)
    {
        try
        {
            var stored = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Id == recordId);
            if (stored is null) return;
            db.IdempotencyRecords.Remove(stored);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "幂等占位释放失败（记录将在 24 小时后过期）：{RecordId}", recordId);
        }
    }

    private static async Task ReplayAsync(HttpContext ctx, IdempotencyRecordEntity record)
    {
        ctx.Response.StatusCode = record.StatusCode ?? StatusCodes.Status200OK;
        if (!string.IsNullOrEmpty(record.ContentType))
            ctx.Response.ContentType = record.ContentType;
        ctx.Response.Headers["Idempotent-Replayed"] = "true";
        if (!string.IsNullOrEmpty(record.ResponseBody))
            await ctx.Response.WriteAsync(record.ResponseBody);
    }

    private static async Task WriteInProgressAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(ApiResponse<string>.Error(
            KeyInProgressErrorCode, "同一 Idempotency-Key 的请求仍在处理中，请稍后重试"));
    }
}

/// <summary>幂等作用域与清理的纯粹逻辑（可单测，不依赖 HTTP）。</summary>
public static class IdempotencyKey
{
    private static readonly System.Text.RegularExpressions.Regex VolatileSegment =
        new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\d+",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 取端点的**路由模板**（如 <c>/api/v1/calendar/tasks/{id}</c>）。最小 API 的路由模板在
    /// <see cref="RouteEndpoint.RoutePattern"/> 上；取不到时退回实际路径并做一次「形态化」
    /// （GUID / 数字段替换为 <c>{}</c>），避免 id 让作用域漂移。
    /// </summary>
    public static string RouteOf(Endpoint? endpoint, PathString? actualPath = null)
    {
        if (endpoint is RouteEndpoint route && route.RoutePattern is { } pattern)
        {
            var raw = pattern.RawText ?? string.Empty;
            // 用「模板参数名」替换具体段：/tasks/123 → /tasks/{id}
            var segments = raw.Split('/');
            var pathSegments = (actualPath?.Value ?? string.Empty).Split('/');
            if (segments.Length == pathSegments.Length && raw.Contains('{'))
            {
                return raw;
            }
            if (!string.IsNullOrEmpty(raw)) return raw;
        }
        return FallbackRoute(actualPath?.Value ?? string.Empty);
    }

    public static string FallbackRoute(string path) => VolatileSegment.Replace(path, "{}");
}
