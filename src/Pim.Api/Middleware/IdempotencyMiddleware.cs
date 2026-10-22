using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
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

    /// <summary>
    /// `Idempotency-Key` 超过 <see cref="MaxKeyLength"/> 时的业务码。
    /// 刻意独立：40002 已被认证 / MCP / Ops 多处复用为「参数非法」，
    /// 复用会让客户端把「key 太长」显示成「邮箱必填」之类的错误（review P3-4）。
    /// </summary>
    public const int KeyTooLongErrorCode = 40010;
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    /// <summary>
    /// 「执行中」占位的租约，必须远短于 <see cref="Retention"/>：进程在占位之后、写回之前被杀掉时，
    /// 这个 key 最多被锁 <see cref="InProgressLease"/>，之后会被新请求接管并重新执行，
    /// 而不是锁满 24 小时（早期实现会让客户端同一 key 的合法重试在整天内都吃 409）。
    /// </summary>
    public static readonly TimeSpan InProgressLease = TimeSpan.FromMinutes(10);

    /// <summary>
    /// 捕获响应体的内存上限。超过后**不再缓冲**，改为边写边直通给客户端（不会把大响应整体读进内存），
    /// 且本次请求不写幂等缓存：只缓存状态码会让重放返回「2xx + 空正文」，客户端反而无法恢复。
    /// </summary>
    public const int MaxResponseBodyBytes = 1024 * 1024;

    private const int MaxKeyLength = 128;

    private static readonly HashSet<string> WriteMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete
    };

    /// <summary>
    /// 这些路由的响应包含一次性凭据（明文 token / 预授权 URL / 设备码 / 带一次性 state 的授权地址），
    /// **不能**缓存响应体 —— 缓存即等于把明文凭据落库 24 小时，且凭据过期后的重放会伪装成成功。
    /// 整条幂等链路跳过。契约里这些端点也不会声明 <c>Idempotency-Key</c>
    /// （见 <c>IdempotencyKeyDocumentFilter</c>）。
    /// </summary>
    public static readonly IReadOnlySet<string> NonReplayableRoutes = new HashSet<string>(StringComparer.Ordinal)
    {
        // MCP 客户端创建：响应里是明文 token。
        "/api/v1/mcp/clients",
        // 预授权上传 URL：DTO 约定「分片 PUT 不带 Authorization」，即 bearer 能力 URL。
        "/api/v1/files/items/upload-session",
        // OneDrive 设备码（user code + verification URI）。
        "/api/v1/files/providers/onedrive",
        // Google 授权地址：带一次性 state。
        "/api/v1/files/providers/google/authorize",
        // 分享链接：webUrl 是可直接访问文件的 bearer 能力链接；服务层明确不写日志/审计，
        // 缓存响应体等于把它明文落库 24 小时（review P2-3）。需要时可用 GET /items/{id}/shares 重新取。
        "/api/v1/files/items/{id}/share",
        // Outlook 设备码：user code 是本次授权会话的凭据。
        "/api/v1/calendar/outlook/device-code",
        // 设备码轮询：返回同一个 user code，且是**有状态**轮询（waiting-for-user → authorized）。
        // 缓存 24h 会让客户端永远看到第一次的 waiting 状态，凭据过期后重放还会伪装成成功。
        "/api/v1/calendar/outlook/device-code/poll",
    };

    /// <summary>
    /// 去掉参数段上的约束/可选/默认值，把路由模板归一化成契约写法：
    /// <c>{id:guid}</c> / <c>{id?}</c> / <c>{id=1}</c> → <c>{id}</c>；catch-all 的 <c>*</c> / <c>**</c> 保留。
    /// </summary>
    public static string NormalizeRouteTemplate(string template)
        => RouteParamTemplate.Replace(template,
            match => "{" + match.Groups[1].Value + match.Groups[2].Value + "}");

    // 必须声明在 NonReplayableNormalized 之前：静态字段按文本顺序初始化，
    // 反向声明会让归一化集合在建类时拿到 null 正则（TypeInitializationException）。
    private static readonly Regex RouteParamTemplate =
        new(@"\{(\*{1,2})?([A-Za-z_][A-Za-z0-9_]*)[^{}]*\}", RegexOptions.Compiled);

    /// <summary>
    /// 归一化后的豁免集合。运行时路由模板会保留行内约束
    /// （<c>/api/v1/files/items/{id:guid}/share</c>），而上面按契约写法维护
    /// （<c>{id}</c>）；不归一化就永远比较不上，豁免形同虚设（review P1-1）。
    /// </summary>
    private static readonly HashSet<string> NonReplayableNormalized = new(
        NonReplayableRoutes.Select(NormalizeRouteTemplate), StringComparer.Ordinal);

    /// <summary>该路由模板（运行时形态或契约形态）是否属于不缓存响应的一次性凭据端点。</summary>
    public static bool IsNonReplayableRoute(string route)
        => NonReplayableNormalized.Contains(NormalizeRouteTemplate(route));

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
        var userId = ctx.RequestServices.GetService<ICurrentUserService>()?.UserId;
        if (userId is null)
        {
            // 未认证 / 非 JWT（OpsKey）的写请求没有幂等作用域：行为与未加该头完全一致。
            await _next(ctx);
            return;
        }

        // 匿名端点 / OpsKey 端点不参与幂等。**不能**只看 userId：UseAuthentication 在匿名端点上
        // 同样会解析 Authorization，客户端带着仍未过期的 access token 调 login/refresh 时 userId
        // 非空，会把返回的新 token 明文缓存 24 小时（review P2-2）。
        var endpoint = ctx.GetEndpoint();
        if (endpoint?.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint?.Metadata.GetMetadata<IAuthorizeData>() is null)
        {
            await _next(ctx);
            return;
        }

        var route = IdempotencyKey.RouteOf(endpoint, ctx.Request.Path);
        // 兜底路由的模板是 `/api/{*path}`（Swashbuckle 在文档里把它重建为 `/api/{path}`），
        // 直接比较字符串容易写错一边；按路由段判断 catch-all 才是可靠的。
        if (IsNonReplayableRoute(route) || IdempotencyKey.IsCatchAll(endpoint))
        {
            await _next(ctx);
            return;
        }

        // 长度校验放在豁免判断**之后**：豁免端点的契约承诺是「忽略该请求头」，
        // 不能因为客户端多发了一个超长 key 就直接 400（review P3-1）。
        if (rawKey.Length > MaxKeyLength)
        {
            ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            await ctx.Response.WriteAsJsonAsync(ApiResponse<string>.Error(KeyTooLongErrorCode, $"Idempotency-Key 不能超过 {MaxKeyLength} 个字符"));
            return;
        }

        // 簿记用**独立 scope** 的 DbContext：不能把 handler 尚未保存的变更顺带提交，
        // 也不能让幂等写库与业务事务互相牵连（见 docs/client-contract-semantics.md §2.3）。
        using var scope = ctx.RequestServices.GetRequiredService<IServiceScopeFactory>().CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();

        var method = ctx.Request.Method.ToUpperInvariant();
        var now = DateTimeOffset.UtcNow;
        // 本次执行的租约到期时刻 = 这条记录的 fencing token。
        // Complete/Release 只对「仍是 in_progress 且 ExpiresAt 等于本次租约」的行生效，
        // 因此旧属主在被接管后无法删除或覆盖新属主的占位（review P2-2，无需新增列）。
        var leaseExpiresAt = now.Add(InProgressLease);

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
                // 占位只租一小段时间；成功写回时再延到 Retention（见 CompleteAsync）。
                ExpiresAt = leaseExpiresAt
            };
            db.IdempotencyRecords.Add(placeholder);
            await db.SaveChangesAsync(CancellationToken.None);
            recordId = placeholder.Id;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.ChangeTracker.Clear();
            // 唯一键冲突有两种来源：另一请求正在执行（→409），或一条**已过期**但还没被清理的记录。
            // 后者如果不接管，这个 key 会一直 409 —— 过期行必须能被新请求替换。
            bool taken;
            Guid? takenOver;
            try
            {
                (taken, takenOver) = await TryTakeOverExpiredAsync(
                    db, userId.Value, method, route, rawKey, now, leaseExpiresAt);
            }
            catch (Exception takeoverEx)
            {
                // 接管本身失败（存储处理不了条件更新 / 瞬时错误）：按「没有幂等」放行。
                // 不能把它当成「另一个请求正在执行」——那会让客户端为一次数据库抖动无限退避。
                _logger.LogWarning(takeoverEx, "幂等占位接管失败，按无幂等继续执行：{Method} {Route}", method, route);
                db.ChangeTracker.Clear();
                await _next(ctx);
                return;
            }
            if (!taken)
            {
                await WriteInProgressAsync(ctx);
                return;
            }
            if (takenOver is null)
            {
                // 接管更新成功，但记录随即被清理任务删掉：没有可写回的簿记行。
                // 直接执行下游，比回一个错误的 409 更接近正确行为。
                await _next(ctx);
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
        using var capture = new CappedCaptureStream(originalBody, MaxResponseBodyBytes);
        ctx.Response.Body = capture;
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
                await ReleaseAsync(db, recordId, leaseExpiresAt);
            }
            else if (succeeded && capture.Overflowed)
            {
                // 响应体超过缓存上限：正文已经边写边直通给客户端了。
                // **不缓存**这次结果 —— 只缓存状态码会让重放返回「2xx + 空正文」，客户端无法反序列化，
                // 该 key 也会在 24 小时内既拿不到数据也重新执行不了。释放占位让重试真正重新执行。
                _logger.LogDebug("响应体超过幂等缓存上限，本次不缓存：{RecordId}", recordId);
                await ReleaseAsync(db, recordId, leaseExpiresAt);
            }
            else
            {
                if (succeeded)
                    await CompleteAsync(db, recordId, leaseExpiresAt, ctx, capture);
                else
                    await ReleaseAsync(db, recordId, leaseExpiresAt);

                // 未溢出时正文只在 _buffer 里（从未写给客户端）：必须回写。
                // 即使下游已经 flush/StartAsync 让响应开始，也要写 —— 否则客户端收到 2xx + 空正文，
                // 而缓存里却有正文（review P2-1）。
                if (!capture.Overflowed)
                {
                    try
                    {
                        // 用 None 而不是 RequestAborted：客户端取消时再抛异常会盖掉真实结果。
                        await capture.CopyToClientAsync(originalBody, CancellationToken.None);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "幂等响应回写失败：{RecordId}", recordId);
                    }
                }
            }
        }
    }

    private async Task CompleteAsync(PimDbContext db, Guid recordId, DateTimeOffset leaseExpiresAt,
        HttpContext ctx, CappedCaptureStream body)
    {
        try
        {
            if (db.Database.IsRelational())
            {
                // 单语句条件更新：租约（ExpiresAt）就是 fencing token。条件读取再 SaveChanges
                // 不是原子的 —— 旧属主在「读回」与「写回」之间被接管时，UPDATE 仍会按 id 覆盖
                // 新属主的占位（review P2-1）。这里把围栏放进 WHERE，影响行数为 0 即放弃。
                var updated = await TryCompleteAsync(
                    db, recordId, leaseExpiresAt,
                    ctx.Response.StatusCode, ctx.Response.ContentType,
                    body.CapturedText(), SerializeHeaders(ctx.Response),
                    DateTimeOffset.UtcNow.Add(Retention));
                if (updated == 0)
                    _logger.LogWarning("幂等租约已被接管，丢弃本次结果：{RecordId}", recordId);
                return;
            }

            // InMemory（仅测试宿主）不支持 ExecuteUpdate：退化为条件读 + 写。
            var stored = await db.IdempotencyRecords
                .FirstOrDefaultAsync(r => r.Id == recordId && r.State == "in_progress"
                                          && r.ExpiresAt == leaseExpiresAt, CancellationToken.None);
            if (stored is null)
            {
                _logger.LogWarning("幂等租约已被接管，丢弃本次结果：{RecordId}", recordId);
                return;
            }
            stored.State = "completed";
            stored.StatusCode = ctx.Response.StatusCode;
            stored.ContentType = ctx.Response.ContentType;
            stored.ResponseBody = body.CapturedText();
            stored.ResponseHeaders = SerializeHeaders(ctx.Response);
            // 只有成功写回后才把占位租约延到完整保留期。
            stored.ExpiresAt = DateTimeOffset.UtcNow.Add(Retention);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // 写缓存失败：删掉占位，让客户端下次重试时重新执行（宁可重复执行，也不留一个永远 409 的键）。
            _logger.LogWarning(ex, "幂等响应写入失败，已释放占位键 {RecordId}", recordId);
            await ReleaseAsync(db, recordId, leaseExpiresAt);
        }
    }

    /// <summary>成功的条件写回（单语句）；返回影响行数，0 表示租约已被接管。</summary>
    internal static Task<int> TryCompleteAsync(
        PimDbContext db, Guid recordId, DateTimeOffset leaseExpiresAt,
        int statusCode, string? contentType, string? responseBody, string? responseHeaders,
        DateTimeOffset expiresAt)
        => db.IdempotencyRecords
            .Where(r => r.Id == recordId && r.State == "in_progress" && r.ExpiresAt == leaseExpiresAt)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(r => r.State, "completed")
                .SetProperty(r => r.StatusCode, (int?)statusCode)
                .SetProperty(r => r.ContentType, contentType)
                .SetProperty(r => r.ResponseBody, responseBody)
                .SetProperty(r => r.ResponseHeaders, responseHeaders)
                .SetProperty(r => r.ExpiresAt, expiresAt), CancellationToken.None);

    private async Task ReleaseAsync(PimDbContext db, Guid recordId, DateTimeOffset leaseExpiresAt)
    {
        try
        {
            db.ChangeTracker.Clear();
            if (db.Database.IsRelational())
            {
                // 条件删除同样是单语句：租约已被接管时影响 0 行，不会删掉新属主的占位（review P2-1）。
                var deleted = await TryReleaseAsync(db, recordId, leaseExpiresAt);
                if (deleted == 0)
                    _logger.LogDebug("幂等占位已不属于本次执行（已被接管或已完成），跳过释放：{RecordId}", recordId);
                return;
            }

            // InMemory（仅测试宿主）：条件读 + 删除。
            var stored = await db.IdempotencyRecords
                .FirstOrDefaultAsync(r => r.Id == recordId && r.State == "in_progress"
                                          && r.ExpiresAt == leaseExpiresAt, CancellationToken.None);
            if (stored is null)
            {
                _logger.LogDebug("幂等占位已不属于本次执行（已被接管或已完成），跳过释放：{RecordId}", recordId);
                return;
            }
            db.IdempotencyRecords.Remove(stored);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "幂等占位释放失败（记录会在过期后被清理）：{RecordId}", recordId);
        }
    }

    /// <summary>占位的条件删除（单语句）；返回影响行数，0 表示租约已被接管。</summary>
    internal static Task<int> TryReleaseAsync(PimDbContext db, Guid recordId, DateTimeOffset leaseExpiresAt)
        => db.IdempotencyRecords
            .Where(r => r.Id == recordId && r.State == "in_progress" && r.ExpiresAt == leaseExpiresAt)
            .ExecuteDeleteAsync(CancellationToken.None);

    /// <summary>
    /// 唯一键冲突时接管一条**已过期**的记录（过期 = 已完成的 24h 缓存，或已超租约的 in_progress 占位）。
    /// 返回 (是否接管成功, 记录 id)。第二个值可能为 null：条件更新成功了，但记录随即被清理任务删除。
    /// 用条件更新保证只有一个请求能接管成功；存储失败时**抛出异常**，由调用方降级为「无幂等」。
    /// </summary>
    private static async Task<(bool Taken, Guid? RecordId)> TryTakeOverExpiredAsync(
        PimDbContext db, Guid userId, string method, string route, string key,
        DateTimeOffset now, DateTimeOffset leaseExpiresAt)
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
                // 与新建占位一致：接管后同样只租 InProgressLease，成功写回时再延到 Retention。
                // 这个值同时是接管者的租约 fencing token。
                .SetProperty(r => r.ExpiresAt, leaseExpiresAt), CancellationToken.None);
        if (updated != 1) return (false, null);

        var row = await db.IdempotencyRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.UserId == userId && r.Method == method && r.Route == route && r.Key == key,
                CancellationToken.None);
        return (true, row?.Id);
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

    /// <summary>
    /// 响应体捕获流：未超过 <c>limit</c> 时只缓冲、最后一次性回写真实 Body（保留「下游中途抛异常时
    /// 客户端还没收到半个响应」的性质）；一旦超限就切换为直通模式 —— 已缓冲的部分立刻写出，
    /// 之后的内容边产生边写，内存占用封顶在 limit，且本次请求不缓存（见 <see cref="Overflowed"/>）。
    /// </summary>
    internal sealed class CappedCaptureStream : Stream
    {
        private readonly Stream _inner;
        private readonly int _limit;
        private readonly MemoryStream _buffer = new();
        private bool _passThrough;

        public CappedCaptureStream(Stream inner, int limit)
        {
            _inner = inner;
            _limit = limit;
        }

        /// <summary>true 表示响应体超过上限，本次结果无法缓存。</summary>
        public bool Overflowed { get; private set; }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;

        public override long Position
        {
            get => _buffer.Position;
            set => _buffer.Position = value;
        }

        /// <summary>缓冲的正文（仅在没有溢出时有意义）。</summary>
        public string? CapturedText()
            => Overflowed ? null : Encoding.UTF8.GetString(_buffer.GetBuffer(), 0, (int)_buffer.Length);

        public Task CopyToClientAsync(Stream destination, CancellationToken ct)
        {
            _buffer.Position = 0;
            return _buffer.CopyToAsync(destination, ct);
        }

        public override void Write(byte[] buffer, int offset, int count)
            => Capture(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => Capture(buffer);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (_passThrough)
                return _inner.WriteAsync(buffer, ct);
            if (_buffer.Length + buffer.Length <= _limit)
            {
                _buffer.Write(buffer.Span);
                return ValueTask.CompletedTask;
            }
            return new ValueTask(PassThroughAsync(buffer, ct));
        }

        private void Capture(ReadOnlySpan<byte> data)
        {
            if (_passThrough)
            {
                _inner.Write(data);
                return;
            }
            if (_buffer.Length + data.Length <= _limit)
            {
                _buffer.Write(data);
                return;
            }
            EnterPassThrough();
            _inner.Write(data);
        }

        private async Task PassThroughAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            EnterPassThrough();
            await _inner.WriteAsync(data, ct);
        }

        private void EnterPassThrough()
        {
            Overflowed = true;
            _passThrough = true;
            if (_buffer.Length == 0) return;
            var buffered = _buffer.ToArray();
            _buffer.SetLength(0);
            _inner.Write(buffered);
        }

        // 未溢出时正文只在 _buffer 里：此时 flush 内层只会把 headers 发出去、响应进入 started 状态，
        // 而正文还没有任何字节写给客户端。真正的回写由 IdempotencyMiddleware 在请求结束时统一做。
        public override void Flush()
        {
            if (_passThrough) _inner.Flush();
        }

        public override Task FlushAsync(CancellationToken ct)
            => _passThrough ? _inner.FlushAsync(ct) : Task.CompletedTask;

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _buffer.Dispose();
            // 不能释放 _inner：它是 Kestrel 的响应流，由框架管理。
            base.Dispose(disposing);
        }
    }
}

/// <summary>幂等作用域的纯粹逻辑（可单测，不依赖 HTTP）。</summary>
public static class IdempotencyKey
{
    /// <summary>
    /// JSON 404 兜底路由在 **OpenAPI 文档**里的写法：Swashbuckle 丢掉 catch-all 的 <c>*</c>，
    /// 只剩 <c>{path}</c>。运行时模板是 <see cref="RuntimeCatchAllRoute"/>，两者不要混用。
    /// </summary>
    public const string CatchAllRoute = "/api/{path}";

    /// <summary>JSON 404 兜底路由在**运行时**的真实模板（见 <c>Program.cs</c> 的 <c>MapMethods</c>）。</summary>
    public const string RuntimeCatchAllRoute = "/api/{*path}";

    /// <summary>
    /// 是否是 JSON 404 兜底端点。按路由段的 catch-all 标记判断，避免文档模板与运行时模板写法不同
    /// 导致字符串比较永远不成立（那会让未知路径也写幂等记录、把 404 缓存 24 小时）。
    /// </summary>
    public static bool IsCatchAll(Endpoint? endpoint)
        => endpoint is RouteEndpoint routeEndpoint
           && routeEndpoint.RoutePattern.PathSegments.Count > 0
           && routeEndpoint.RoutePattern.PathSegments[^1].Parts
               .Any(part => part is RoutePatternParameterPart { IsCatchAll: true });

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
