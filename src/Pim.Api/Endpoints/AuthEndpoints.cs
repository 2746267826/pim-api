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
            if (await db.Users.AnyAsync(u => u.Username == request.Username, ct))
                return Results.Conflict(ApiResponse<string>.Error(01003, "Username already exists"));

            if (await db.Users.AnyAsync(u => u.Email == request.Email, ct))
                return Results.Conflict(ApiResponse<string>.Error(01004, "Email already exists"));

            var user = new UserEntity
            {
                Username = request.Username,
                Email = request.Email,
                PasswordHash = PasswordHasher.Hash(request.Password),
                DisplayName = request.DisplayName ?? request.Username,
                Role = "user"
            };

            db.Users.Add(user);
            await db.SaveChangesAsync(ct);

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

            if (!passwordValid)
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
            if (user is null) return Results.Unauthorized();

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
    }
}
