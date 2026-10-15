using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pim.Core.Common;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Api.DTOs;

namespace Pim.Api.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/auth");

        group.MapPost("/register", async (
            RegisterRequest request,
            PimDbContext db,
            JwtService jwt,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            // REQ-1 / REQ-2：关闭判定必须**先于**参数校验。
            // 库非空且未开开关时，非法载荷同样返回 403/40304（不得落到 400），也不写库。
            var hasAnyUser = await db.Users.IgnoreQueryFilters().AnyAsync(ct);
            if (hasAnyUser && !PublicRegistrationPolicy.IsEnabled(configuration))
            {
                return Results.Json(
                    ApiResponse<string>.Error(
                        PublicRegistrationPolicy.ClosedErrorCode,
                        PublicRegistrationPolicy.ClosedMessage),
                    statusCode: 403);
            }

            /*
             * 物理空库 = 全新部署引导。这条路径必须**全局串行化**：
             * 两个并发匿名首注册会同时看到空库，各自都通过上面的判定，于是双双建号 ——
             * 第二个账号是在库已经非空之后创建的，正好绕过核心验收口径
             * 「库非空时未登录第三方无法通过公开端点创建账号」。
             * （`AdminBootstrap.ConfirmFirstAdminAsync` 的降级只解决"谁是管理员"，
             *  不阻止第二个账号被创建，因此不能替代这里的串行化。）
             */
            var isBootstrap = !hasAnyUser;

            var username = request.Username?.Trim() ?? string.Empty;
            var email = request.Email?.Trim() ?? string.Empty;
            var normalizedEmail = email.ToLowerInvariant();
            var displayName = request.DisplayName?.Trim();
            if (string.IsNullOrEmpty(displayName))
                displayName = username;

            // 校验口径与管理员建号共用同一份实现（40001–40008）
            if (UserCredentialsValidator.Validate(username, email, request.Password, displayName) is { } error)
                return Results.BadRequest(ApiResponse<string>.Error(error.Code, error.Message));

            async Task<IResult> CreateUserAsync()
            {
                // 引导路径：等锁期间可能已被别的请求抢先完成引导 —— 库一旦非空就按关闭判定处理，
                // 否则就要按"库非空"的角色口径发号（下面 DetermineRegistrationRoleAsync 会给出 user）。
                if (isBootstrap && !await PublicRegistrationPolicy.IsAllowedAsync(db, configuration, ct))
                {
                    return Results.Json(
                        ApiResponse<string>.Error(
                            PublicRegistrationPolicy.ClosedErrorCode,
                            PublicRegistrationPolicy.ClosedMessage),
                        statusCode: 403);
                }

                if (await db.Users.AnyAsync(u => u.Username == username, ct))
                    return Results.Conflict(ApiResponse<string>.Error(01003, "用户名已存在"));

                if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct))
                    return Results.Conflict(ApiResponse<string>.Error(01004, "邮箱已存在"));

                // 首个注册用户自动成为管理员（users 表**物理为空**时）
                var role = await AdminBootstrap.DetermineRegistrationRoleAsync(db, ct);

                var user = new UserEntity
                {
                    Username = username,
                    Email = normalizedEmail,
                    PasswordHash = PasswordHasher.Hash(request.Password),
                    DisplayName = displayName,
                    Role = role
                };

                /*
                 * 会话令牌与用户行**同一次提交**。分两次写会留下一个窗口：用户行已可见、令牌行还没写，
                 * 此时管理员重置该用户（撤销"当时全部会话"，实际为空）之后，注册流程再插入一条
                 * 从未被撤销的令牌 —— 与 refresh 逃逸是同一类缺口。
                 * user.Id 是客户端生成的 GUID，令牌行不需要用户行先落库，因此两条 INSERT 可以
                 * 走同一个 SaveChanges（EF 会把多条改动包进一个事务），窗口直接消失，无需额外加锁。
                 */
                var refreshToken = jwt.GenerateRefreshToken();
                db.Users.Add(user);
                db.RefreshTokens.Add(new RefreshTokenEntity
                {
                    UserId = user.Id,
                    TokenHash = RefreshTokenSessions.HashSecret(refreshToken),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
                });
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
                {
                    return Results.Conflict(ApiResponse<string>.Error(01003, "用户名已存在或邮箱已存在"));
                }

                // 并发首注册保护（保留）：若竞争中已有更早的管理员，当前用户降级为普通用户
                if (role == AdminBootstrap.AdminRole &&
                    !await AdminBootstrap.ConfirmFirstAdminAsync(db, user.Id, user.CreatedAt, ct))
                {
                    user.Role = AdminBootstrap.UserRole;
                    user.UpdatedAt = DateTimeOffset.UtcNow;
                    await db.SaveChangesAsync(ct);
                }

                var accessToken = jwt.GenerateAccessToken(user.Id, user.Username, user.Role);

                return Results.Created($"/api/v1/users/{user.Id}",
                    ApiResponse<AuthResponse>.Ok(new AuthResponse(
                        accessToken,
                        refreshToken,
                        DateTimeOffset.UtcNow.AddMinutes(15),
                        new UserInfo(user.Id, user.Username, user.DisplayName, user.Role))));
            }

            // 只有引导路径需要全局串行化；库非空时的普通注册不受影响（避免无谓争用）
            return isBootstrap
                ? await AdvisoryLock.RunAsync(db, AdvisoryLock.BootstrapRegistrationKey, CreateUserAsync, ct)
                : await CreateUserAsync();
        })
        .Produces<ApiResponse<AuthResponse>>(201)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(403)
        .Produces<ApiResponse<string>>(409)
        .Produces<ApiResponse<string>>(500);

        group.MapPost("/login", async (
            LoginRequest request,
            PimDbContext db,
            JwtService jwt,
            HttpContext httpContext,
            ILogger<Program> logger,
            CancellationToken ct) =>
        {
            var totalSw = Stopwatch.StartNew();
            var ipAddress = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

            // Rate limiting check
            var stepSw = Stopwatch.StartNew();
            var recentFailures = await db.LoginAttempts.CountAsync(
                la => la.IpAddress == ipAddress && !la.Success &&
                      la.AttemptedAt > DateTimeOffset.UtcNow.AddMinutes(-15), ct);
            var rateLimitMs = stepSw.ElapsedMilliseconds;

            if (recentFailures >= 5)
            {
                httpContext.Response.Headers.RetryAfter = "900";
                return Results.StatusCode(429);
            }

            stepSw.Restart();
            var user = await db.Users.FirstOrDefaultAsync(
                u => u.Username == request.Username || u.Email == request.Username, ct);
            var userLookupMs = stepSw.ElapsedMilliseconds;

            stepSw.Restart();
            var passwordValid = user is not null && PasswordHasher.Verify(request.Password, user.PasswordHash);
            var bcryptMs = stepSw.ElapsedMilliseconds;

            if (passwordValid && user is not null && !user.IsActive)
            {
                logger.LogInformation("Login blocked for disabled account '{User}'", request.Username);
                return Results.Json(ApiResponse<string>.Error(40030, "账号已停用，请联系管理员"), statusCode: 403);
            }

            if (!passwordValid || user is null)
            {
                db.LoginAttempts.Add(new LoginAttemptEntity
                {
                    IpAddress = ipAddress,
                    Success = false
                });
                await db.SaveChangesAsync(ct);
                logger.LogInformation("Login failed for '{User}': bcrypt={BcryptMs}ms, userLookup={UserLookupMs}ms, rateLimit={RateLimitMs}ms, total={TotalMs}ms",
                    request.Username, bcryptMs, userLookupMs, rateLimitMs, totalSw.ElapsedMilliseconds);
                return Results.Unauthorized();
            }

            /*
             * 发会话前在该用户的会话锁内**复核**：口令校验通过之后、插入会话之前若发生
             * 改密 / 管理员重置，这条新会话会逃过撤销（攻击者持旧口令也能留住一个会话）。
             * 锁内重读用户并比对「我们实际校验过的那个哈希」即可挡住这种交错。
             * bcrypt 校验本身留在锁外：它耗时约百毫秒，不该占着用户锁。
             */
            var verifiedHash = user.PasswordHash;
            // 计时按原语义分别测「签发」与「落库」（都发生在锁内）；锁等待只体现在 total
            var jwtMs = 0L;
            var dbSaveMs = 0L;
            var issued = await UserSessionLock.RunAsync(db, user.Id, async () =>
            {
                var fresh = await db.Users.FirstOrDefaultAsync(u => u.Id == user.Id, ct);
                if (fresh is null || !fresh.IsActive || fresh.PasswordHash != verifiedHash)
                    return (AuthResponse?)null;

                db.LoginAttempts.Add(new LoginAttemptEntity
                {
                    UserId = fresh.Id,
                    IpAddress = ipAddress,
                    Success = true
                });

                stepSw.Restart();
                var accessToken = jwt.GenerateAccessToken(fresh.Id, fresh.Username, fresh.Role);
                var refreshToken = jwt.GenerateRefreshToken();
                jwtMs = stepSw.ElapsedMilliseconds;

                db.RefreshTokens.Add(new RefreshTokenEntity
                {
                    UserId = fresh.Id,
                    TokenHash = RefreshTokenSessions.HashSecret(refreshToken),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
                });
                stepSw.Restart();
                await db.SaveChangesAsync(ct);
                dbSaveMs = stepSw.ElapsedMilliseconds;

                return new AuthResponse(
                    accessToken,
                    refreshToken,
                    DateTimeOffset.UtcNow.AddMinutes(15),
                    new UserInfo(fresh.Id, fresh.Username, fresh.DisplayName, fresh.Role));
            }, ct);

            if (issued is null)
            {
                // 校验通过后账号被改（口令已变）或停用：这条会话不得发出
                logger.LogInformation("Login rejected for '{User}': account changed during login", request.Username);
                return Results.Unauthorized();
            }

            logger.LogInformation("Login succeeded for '{User}': bcrypt={BcryptMs}ms, userLookup={UserLookupMs}ms, rateLimit={RateLimitMs}ms, jwt={JwtMs}ms, dbSave={DbSaveMs}ms, total={TotalMs}ms",
                request.Username, bcryptMs, userLookupMs, rateLimitMs, jwtMs, dbSaveMs, totalSw.ElapsedMilliseconds);

            return Results.Ok(ApiResponse<AuthResponse>.Ok(issued));
        })
        .Produces<ApiResponse<AuthResponse>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(403)
        .Produces(429)
        .Produces<ApiResponse<string>>(500);

        group.MapPost("/refresh", async (
            RefreshRequest request,
            PimDbContext db,
            JwtService jwt,
            CancellationToken ct) =>
        {
            var tokenHash = RefreshTokenSessions.HashSecret(request.RefreshToken);

            // 锁必须按用户取，所以先只读定位令牌归属（认领人是谁与并发无关）
            // 只认「未撤销且未过期」的令牌：早已失效的令牌在锁外直接 401，
            // 否则重放死令牌的请求会去抢合法用户的排他锁（无谓争用）。
            var now = DateTimeOffset.UtcNow;
            var ownerId = await db.RefreshTokens.IgnoreQueryFilters().AsNoTracking()
                .Where(rt => rt.TokenHash == tokenHash && rt.RevokedAt == null && rt.ExpiresAt > now)
                .Select(rt => (Guid?)rt.UserId)
                .FirstOrDefaultAsync(ct);
            if (ownerId is not Guid ownerUserId)
                return Results.Unauthorized();

            /*
             * 轮换必须与「撤销该用户其它会话」互斥：否则在"读到有效令牌"与"插入新令牌"之间，
             * 改密 / 管理员重置把全部会话撤销并提交，这次轮换仍会插入一条**从未被撤销**的新令牌，
             * 被踢的设备保留登录态（REQ-6 落空）。锁内重读后这种交错不可能发生：
             * 撤销一旦提交，这里就读不到未撤销的令牌 → 401。
             */
            var issued = await UserSessionLock.RunAsync(db, ownerUserId, async () =>
            {
                var stored = await db.RefreshTokens.IgnoreQueryFilters().FirstOrDefaultAsync(
                    rt => rt.TokenHash == tokenHash && rt.UserId == ownerUserId && rt.RevokedAt == null, ct);

                if (stored is null || stored.ExpiresAt < DateTimeOffset.UtcNow)
                    return (AuthResponse?)null;

                // Revoke old token
                stored.RevokedAt = DateTimeOffset.UtcNow;

                var user = await db.Users.FindAsync(new object[] { stored.UserId }, ct);
                if (user is null || !user.IsActive) return (AuthResponse?)null;

                var accessToken = jwt.GenerateAccessToken(user.Id, user.Username, user.Role);
                var newRefreshToken = jwt.GenerateRefreshToken();

                db.RefreshTokens.Add(new RefreshTokenEntity
                {
                    UserId = user.Id,
                    TokenHash = RefreshTokenSessions.HashSecret(newRefreshToken),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
                });
                await db.SaveChangesAsync(ct);

                return new AuthResponse(
                    accessToken,
                    newRefreshToken,
                    DateTimeOffset.UtcNow.AddMinutes(15),
                    new UserInfo(user.Id, user.Username, user.DisplayName, user.Role));
            }, ct);

            return issued is null
                ? Results.Unauthorized()
                : Results.Ok(ApiResponse<AuthResponse>.Ok(issued));
        })
        .Produces<ApiResponse<AuthResponse>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        // REQ-5 / REQ-6：用户修改自己密码。
        // 只踢其它设备——请求携带的 refreshToken 即「当前设备」，那条会话保留（决策记录 §3.3）。
        group.MapPost("/password", async (
            ChangePasswordRequest request,
            ICurrentUserService currentUser,
            IUserPasswordService passwordService,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not Guid userId)
                return Results.Unauthorized();

            // 纯入参校验先行：新密码 8–100，沿用注册的既有码
            if (UserCredentialsValidator.ValidatePassword(request.NewPassword) is { } error)
                return Results.BadRequest(ApiResponse<string>.Error(error.Code, error.Message));

            var status = await passwordService.ChangeOwnPasswordAsync(
                userId, request.CurrentPassword, request.NewPassword, request.RefreshToken, ct);

            IResult response = status switch
            {
                ChangeOwnPasswordStatus.Ok =>
                    Results.Ok(ApiResponse<string>.Ok("密码已修改")),
                // 端点已先行拦下长度不合规的新密码，这里是纵深防御（同一批错误码）
                ChangeOwnPasswordStatus.InvalidNewPassword =>
                    Results.BadRequest(ApiResponse<string>.Error(
                        UserCredentialsValidator.PasswordTooShortCode,
                        UserCredentialsValidator.PasswordTooShortMessage)),
                ChangeOwnPasswordStatus.UserNotFound => Results.Unauthorized(),
                ChangeOwnPasswordStatus.InvalidSessionToken =>
                    Results.BadRequest(ApiResponse<string>.Error(
                        PasswordChangeErrors.InvalidSessionCode, PasswordChangeErrors.InvalidSessionMessage)),
                ChangeOwnPasswordStatus.WrongCurrentPassword =>
                    Results.BadRequest(ApiResponse<string>.Error(
                        PasswordChangeErrors.WrongCurrentPasswordCode, PasswordChangeErrors.WrongCurrentPasswordMessage)),
                ChangeOwnPasswordStatus.SameAsCurrentPassword =>
                    Results.BadRequest(ApiResponse<string>.Error(
                        PasswordChangeErrors.SameAsCurrentCode, PasswordChangeErrors.SameAsCurrentMessage)),
                _ => Results.BadRequest(ApiResponse<string>.Error(
                    PasswordChangeErrors.InvalidSessionCode, PasswordChangeErrors.InvalidSessionMessage))
            };
            return response;
        })
        .RequireAuthorization()
        .Produces<ApiResponse<string>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(400)
        .Produces<ApiResponse<string>>(500);

        // 当前登录用户信息（前端刷新页面后恢复用户名与角色）
        group.MapGet("/me", async (
            PimDbContext db,
            ICurrentUserService currentUser,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not Guid userId)
                return Results.Unauthorized();

            var user = await db.Users.FindAsync(new object[] { userId }, ct);
            if (user is null || !user.IsActive) return Results.Unauthorized();

            return Results.Ok(ApiResponse<UserInfo>.Ok(
                new UserInfo(user.Id, user.Username, user.DisplayName, user.Role)));
        }).RequireAuthorization()
        .Produces<ApiResponse<UserInfo>>(200)
        .Produces(401)
        .Produces<ApiResponse<string>>(500);
    }
}
