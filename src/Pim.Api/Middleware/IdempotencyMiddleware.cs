using System.Text.Json;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
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
/// 首次请求先占位（<c>in_progress</c>）；成功后把状态码、少量响应头与响应体写回（<c>completed</c>，24 小时）；
/// 24 小时内同 key 重放 → 返回缓存的响应，不再执行下游；首次仍在执行中 → 409 / 40902。
/// 首次失败（非 2xx）→ 删除占位、不缓存，客户端可用同 key 重试。
/// </para>
/// <para>
/// 作用域用「路径模板」：<c>/tasks/a</c> 与 <c>/tasks/b</c> 落在同一条记录上（模板相同），
/// 因此客户端必须为**每一次逻辑操作**生成新的 key，不能复用同一个 key 去操作不同资源。
/// 反过来，重试时 URL 里的 id 即使不同（例如创建后不知道 id）也不会让去重失效。
/// </para>
/// <para>
/// 失败降级：幂等存储本身出错（连不上库 / 唯一键竞争以外的异常）时**不影响主操作** ——
/// 记 Warning 后按「没有幂等」继续执行。幂等是锦上添花，不能成为整体可用性的单点。
/// </para>
/// <para>
/// 不参与幂等的端点见 <see cref="NonReplayableRoutes"/>：它们的响应里有一次性凭据，
/// 缓存等于把明文落库 24 小时。
/// </para>
/// </summary>
public sealed class IdempotencyMiddleware
{
    public const string HeaderName = "Idempotency-Key";
    public const string ReplayedHeader = "Idempotent-Replayed";
    public const int KeyInProgressErrorCode = 40902;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>缓存的响应体上限（超出则只缓存状态码，不缓存正文）。</summary>
    public const int MaxResponseBodyBytes = 1024 * 1024;

    private const int MaxKeyLength = 128;

    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete
    };

    /// <summary>
    /// 这些路由的响应包含一次性凭据（例如 MCP 客户端创建返回的明文 token），
    /// **不能**缓存响应体 —— 缓存即等于把明文凭据落库 24 小时。整条幂等链路跳过。
    /// 契约里这些端点也不会声明 <c>Idempotency-Key</c>（见 <c>IdempotencyKeyDocumentFilter</c>）。
    /// </summary>
    public static readonly IReadOnlySet<string> NonReplayableRoutes = new HashSet<string>(StringComparer.Ordinal)
    {
        "/api/v1/mcp/clients",
    };

    private static readonly string[] ReplayedHeaders = ["Location", "ETag", "Last-Modified"];

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

        var headerValues = ctx.Request.Headers[HeaderName];
        // 同一 header 出现多次时取第一个：不把 "a,b" 当成一个完全不同的 key，也不因此拒绝请求。
        var rawKey = headerValues.Count > 0 ? headerValues[0]?.Trim() : null;
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
            // 未认证 / 非 JWT（OpsKey）的写请求没有幂等作用域：行为与未加该头完全一致。
            await _next(ctx);
            return;
        }

        var route = IdempotencyKey.RouteOf(ctx.GetEndpoint(), ctx.Request.Path);
        if (NonReplayableRoutes.Contains(route) || string.Equals(route, IdempotencyKey.CatchAllRoute, StringComparison.Ordinal))
        {
            await _next(ctx);
            return;
        }

        // 簿记用**独立 scope** 的 DbContext：不能把 handler 尚未保存的变更顺带提交，
        // 也不能让幂等写库与业务事务互相牵连（见 docs/client-contract-semantics.md §2.3）。
        using var scope = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();

        var method = ctx.Request.Method.ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;

        IdempotencyRecordEntity? record;
        try
        {
            record = await db.IdempotencyRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == userId && r.Method == method
                                          && r.Route == route && r.Key == rawKey && r.ExpiresAt > now,
                    CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "幂等记录读取失败，按无幂等继续执行：{Method} {Route}", method, route);
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
        Guid recordId;
        try
        {
            var placeholder = new IdempotencyRecordEntity
            {
                UserId = userId.Value,
                Method = method,
                Route = route,
                Key = rawKey,
                State = "in_progress",
                CreatedAt = now,
                ExpiresAt = now.Add(Retention)
            };
            db.IdempotencyRecords.Add(placeholder);
            await db.SaveChangesAsync(CancellationToken.None);
            recordId = placeholder.Id;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            // 唯一键冲突有两种来源：另一请求正在执行（→409），或一条**已过期**但还没被清理的记录。
            // 后者如果不接管，这个 key 会永远 409 —— 过期行必须能被新请求替换。
            var takenOver = await TryTakeOverExpiredAsync(db, userId.Value, method, route, rawKey, now);
            if (takenOver is null)
            {
                await WriteInProgressAsync(ctx);
                return;
            }
            recordId = takenOver.Value;
        }
        catch (Exception ex)
        {
            // 不是唯一键冲突（连接中断 / 值超长 / 约束失败…）：按无幂等继续，不要把永久错误伪装成 409。
            _logger.LogWarning(ex, "幂等记录占位失败，按无幂等继续执行：{Method} {Route}", method, route);
            db.ChangeTracker.Clear();
            await _next(ctx);
            return;
        }

        var originalBody = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        var succeeded = false;
        var downstreamFailed = false;
        try
        {
            await _next(ctx);
            succeeded = ctx.Response.StatusCode is >= 200 and < 300;
        }
        catch
        {
            downstreamFailed = true;
            throw;
        }
        finally
        {
            ctx.Response.Body = originalBody;
            if (downstreamFailed)
            {
                // 下游已经写了半截响应又抛异常：**不能**把半截内容写回真实 Body
                // （那会启动响应，让外层 ExceptionMiddleware 无法再改写状态码）。
                await ReleaseAsync(db, recordId);
            }
            else
            {
                if (succeeded)
                    await CompleteAsync(db, recordId, ctx, buffer);
                else
                    await ReleaseAsync(db, recordId);

                if (!ctx.Response.HasStarted)
                {
                    try
                    {
                        buffer.Position = 0;
                        // 用 None 而不是 RequestAborted：客户端取消时再抛异常会盖掉真实结果。
                        await buffer.CopyToAsync(originalBody, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "幂等响应回写失败：{RecordId}", recordId);
                    }
                }
            }
        }
    }

    private async Task CompleteAsync(PimDbContext db, Guid recordId, HttpContext ctx, MemoryStream body)
    {
        try
        {
            var stored = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Id == recordId, CancellationToken.None);
            if (stored is null) return;
            stored.State = "completed";
            stored.StatusCode = ctx.Response.StatusCode;
            stored.ContentType = ctx.Response.ContentType;
            stored.ResponseBody = body.Length <= MaxResponseBodyBytes
                ? System.Text.Encoding.UTF8.GetString(body.GetBuffer(), 0, (int)body.Length)
                : null;
            stored.ResponseHeaders = SerializeHeaders(ctx.Response);
            await db.SaveChangesAsync(CancellationToken.None);
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
            db.ChangeTracker.Clear();
            var stored = await db.IdempotencyRecords.FirstOrDefaultAsync(r => r.Id == recordId, CancellationToken.None);
            if (stored is null) return;
            db.IdempotencyRecords.Remove(stored);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "幂等占位释放失败（记录将在 24 小时后过期并被清理）：{RecordId}", recordId);
        }
    }

    /// <summary>
    /// 唯一键冲突时接管一条**已过期**的记录。返回接管到的记录 id；未接管（另一请求仍在执行）返回 null。
    /// 用条件更新保证只有一个请求能接管成功。
    /// </summary>
    private static async Task<Guid?> TryTakeOverExpiredAsync(
        PimDbContext db, Guid userId, string method, string route, string key, DateTimeOffset now)
    {
        try
        {
            var updated = await db.IdempotencyRecords
                .Where(r => r.UserId == userId && r.Method == method && r.Route == route && r.Key == key
                            && r.ExpiresAt <= now)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.State, "in_progress")
                    .SetProperty(r => r.StatusCode, (int?)null)
                    .SetProperty(r => r.ContentType, (string?)null)
                    .SetProperty(r => r.ResponseBody, (string?)null)
                    .SetProperty(r => r.ResponseHeaders, (string?)null)
                    .SetProperty(r => r.CreatedAt, now)
                    .SetProperty(r => r.ExpiresAt, now.Add(Retention)), CancellationToken.None);
            if (updated != 1) return null;

            var row = await db.IdempotencyRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.UserId == userId && r.Method == method && r.Route == route && r.Key == key,
                    CancellationToken.None);
            return row?.Id;
        }
        catch (Exception)
        {
            // 存储不支持条件更新时按「另一请求仍在执行」处理（保守：不重复执行）。
            return null;
        }
    }

    private static string? SerializeHeaders(HttpResponse response)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in ReplayedHeaders)
        {
            if (response.Headers.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
                headers[name] = value.ToString();
        }
        return headers.Count == 0 ? null : JsonSerializer.Serialize(headers);
    }

    private static async Task ReplayAsync(HttpContext ctx, IdempotencyRecordEntity record)
    {
        ctx.Response.StatusCode = record.StatusCode ?? StatusCodes.Status200OK;
        if (!string.IsNullOrEmpty(record.ContentType))
            ctx.Response.ContentType = record.ContentType;
        if (!string.IsNullOrEmpty(record.ResponseHeaders))
        {
            try
            {
                var headers = JsonSerializer.Deserialize<Dictionary<string, string>>(record.ResponseHeaders);
                if (headers is not null)
                    foreach (var (name, value) in headers)
                        ctx.Response.Headers[name] = value;
            }
            catch (JsonException)
            {
                // 历史数据格式异常：忽略响应头，状态码与正文仍可重放。
            }
        }
        ctx.Response.Headers[ReplayedHeader] = "true";
        if (!string.IsNullOrEmpty(record.ResponseBody))
            await ctx.Response.WriteAsync(record.ResponseBody);
    }

    private static async Task WriteInProgressAsync(HttpContext ctx)
    {
        ctx.Response.StatusCode = StatusCodes.Status409Conflict;
        await ctx.Response.WriteAsJsonAsync(ApiResponse<string>.Error(
            KeyInProgressErrorCode, "同一 Idempotency-Key 的请求仍在处理中，请稍后重试"));
    }

    /// <summary>只把 PostgreSQL 唯一键冲突（23505）当成并发占位冲突。</summary>
    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

/// <summary>幂等作用域的纯粹逻辑（可单测，不依赖 HTTP）。</summary>
public static class IdempotencyKey
{
    /// <summary>JSON 404 兜底路由：不是业务端点，不参与幂等。</summary>
    public const string CatchAllRoute = "/api/{path}";

    /// <summary>路由里的动态段（GUID / 纯数字）：只在「整段」匹配时才替换，避免把 <c>v1</c> 之类改坏。</summary>
    private static readonly System.Text.RegularExpressions.Regex VolatileSegment =
        new(@"^(?:[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|\d+)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// 取端点的**路由模板**（如 <c>/api/v1/calendar/tasks/{id}</c>）。最小 API 的路由模板在
    /// <see cref="RouteEndpoint.RoutePattern"/> 上；取不到时退回实际路径并做一次「形态化」
    /// （整段是 GUID / 纯数字的段替换为 <c>{}</c>），避免 id 让作用域漂移。
    /// </summary>
    public static string RouteOf(Endpoint? endpoint, PathString? actualPath = null)
    {
        if (endpoint is RouteEndpoint { RoutePattern.RawText: { Length: > 0 } raw })
            return raw;
        return FallbackRoute(actualPath?.Value ?? string.Empty);
    }

    public static string FallbackRoute(string path) =>
        string.Join('/', path.Split('/').Select(segment =>
            VolatileSegment.IsMatch(segment) ? "{}" : segment));
}
