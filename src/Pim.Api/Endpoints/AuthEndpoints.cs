using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
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
            CancellationToken ct) =>
        {
            var username = request.Username?.Trim() ?? string.Empty;
            var email = request.Email?.Trim() ?? string.Empty;
            var normalizedEmail = email.ToLowerInvariant();
            var displayName = request.DisplayName?.Trim();
            if (string.IsNullOrEmpty(displayName))
                displayName = username;

            if (string.IsNullOrWhiteSpace(username))
                return Results.BadRequest(ApiResponse<string>.Error(40001, "Username is required"));
            if (string.IsNullOrWhiteSpace(email))
                return Results.BadRequest(ApiResponse<string>.Error(40002, "Email is required"));
            if (!new System.ComponentModel.DataAnnotations.EmailAddressAttribute().IsValid(email))
                return Results.BadRequest(ApiResponse<string>.Error(40003, "Invalid email format"));
            if (string.IsNullOrWhiteSpace(request.Password) || request.Password.Length < 8)
                return Results.BadRequest(ApiResponse<string>.Error(40004, "Password must be at least 8 characters"));
            if (username.Length > 50) return Results.BadRequest(ApiResponse<string>.Error(40005, "Username must not exceed 50 characters"));
            if (email.Length > 255) return Results.BadRequest(ApiResponse<string>.Error(40006, "Email must not exceed 255 characters"));
            if (request.Password.Length > 100) return Results.BadRequest(ApiResponse<string>.Error(40007, "Password must not exceed 100 characters"));
            if (!string.IsNullOrEmpty(displayName) && displayName.Length > 100) return Results.BadRequest(ApiResponse<string>.Error(40008, "DisplayName must not exceed 100 characters"));

            if (await db.Users.AnyAsync(u => u.Username == username, ct))
                return Results.Conflict(ApiResponse<string>.Error(01003, "用户名已存在"));

            if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, ct))
                return Results.Conflict(ApiResponse<string>.Error(01004, "邮箱已存在"));

            // 首个注册用户自动成为管理员（users 表为空时）
            var role = await AdminBootstrap.DetermineRegistrationRoleAsync(db, ct);

            var user = new UserEntity
            {
                Username = username,
                Email = normalizedEmail,
                PasswordHash = PasswordHasher.Hash(request.Password),
                DisplayName = displayName,
                Role = role
            };

            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException pg && pg.SqlState == "23505")
            {
                return Results.Conflict(ApiResponse<string>.Error(01003, "用户名已存在或邮箱已存在"));
            }

            // 并发首注册保护：若竞争中已有更早的管理员，当前用户降级为普通用户
            if (role == AdminBootstrap.AdminRole &&
                !await AdminBootstrap.ConfirmFirstAdminAsync(db, user.Id, user.CreatedAt, ct))
            {
                user.Role = AdminBootstrap.UserRole;
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }

            var accessToken = jwt.GenerateAccessToken(user.Id, user.Username, user.Role);
            var refreshToken = jwt.GenerateRefreshToken();
            var refreshTokenHash = Convert.ToBase64String(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(refreshToken)));

            db.RefreshTokens.Add(new RefreshTokenEntity
            {
                UserId = user.Id,
                TokenHash = refreshTokenHash,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync(ct);

            return Results.Created($"/api/v1/users/{user.Id}",
                ApiResponse<AuthResponse>.Ok(new AuthResponse(
                    accessToken,
                    refreshToken,
                    DateTimeOffset.UtcNow.AddMinutes(15),
                    new UserInfo(user.Id, user.Username, user.DisplayName!, user.Role))));
        });

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

            db.LoginAttempts.Add(new LoginAttemptEntity
            {
                UserId = user.Id,
                IpAddress = ipAddress,
                Success = true
            });

            stepSw.Restart();
            var accessToken = jwt.GenerateAccessToken(user.Id, user.Username, user.Role);
            var jwtMs = stepSw.ElapsedMilliseconds;

            var refreshToken = jwt.GenerateRefreshToken();
            var refreshTokenHash = Convert.ToBase64String(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(refreshToken)));

            db.RefreshTokens.Add(new RefreshTokenEntity
            {
                UserId = user.Id,
                TokenHash = refreshTokenHash,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
            });
            stepSw.Restart();
            await db.SaveChangesAsync(ct);
            var dbSaveMs = stepSw.ElapsedMilliseconds;

            logger.LogInformation("Login succeeded for '{User}': bcrypt={BcryptMs}ms, userLookup={UserLookupMs}ms, rateLimit={RateLimitMs}ms, jwt={JwtMs}ms, dbSave={DbSaveMs}ms, total={TotalMs}ms",
                request.Username, bcryptMs, userLookupMs, rateLimitMs, jwtMs, dbSaveMs, totalSw.ElapsedMilliseconds);

            return Results.Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse(
                accessToken,
                refreshToken,
                DateTimeOffset.UtcNow.AddMinutes(15),
                new UserInfo(user.Id, user.Username, user.DisplayName!, user.Role))));
        });

        group.MapPost("/refresh", async (
            RefreshRequest request,
            PimDbContext db,
            JwtService jwt,
            CancellationToken ct) =>
        {
            var tokenHash = Convert.ToBase64String(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(request.RefreshToken)));

            var stored = await db.RefreshTokens.FirstOrDefaultAsync(
                rt => rt.TokenHash == tokenHash && rt.RevokedAt == null, ct);

            if (stored is null || stored.ExpiresAt < DateTimeOffset.UtcNow)
                return Results.Unauthorized();

            // Revoke old token
            stored.RevokedAt = DateTimeOffset.UtcNow;

            var user = await db.Users.FindAsync(new object[] { stored.UserId }, ct);
            if (user is null || !user.IsActive) return Results.Unauthorized();

            var accessToken = jwt.GenerateAccessToken(user.Id, user.Username, user.Role);
            var newRefreshToken = jwt.GenerateRefreshToken();
            var newTokenHash = Convert.ToBase64String(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(newRefreshToken)));

            db.RefreshTokens.Add(new RefreshTokenEntity
            {
                UserId = user.Id,
                TokenHash = newTokenHash,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7)
            });
            await db.SaveChangesAsync(ct);

            return Results.Ok(ApiResponse<AuthResponse>.Ok(new AuthResponse(
                accessToken,
                newRefreshToken,
                DateTimeOffset.UtcNow.AddMinutes(15),
                new UserInfo(user.Id, user.Username, user.DisplayName!, user.Role))));
        });

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
                new UserInfo(user.Id, user.Username, user.DisplayName!, user.Role)));
        }).RequireAuthorization();
    }
}
