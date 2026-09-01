using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Module.Mcp.DTOs;
using Pim.Module.Mcp.Entities;

namespace Pim.Module.Mcp.Services;

public sealed class McpClientService
{
    private readonly PimDbContext _db;
    private readonly IAuditLogService _auditLog;
    private readonly JwtService _jwt;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<McpClientService> _logger;

    public McpClientService(
        PimDbContext db,
        IAuditLogService auditLog,
        JwtService jwt,
        ILogger<McpClientService>? logger = null,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _auditLog = auditLog;
        _jwt = jwt;
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<McpClientService>.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<McpClientCreateResult> CreateAsync(string name, Guid createdBy, CancellationToken ct = default)
    {
        var trimmed = name.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            throw new DomainException(40001, "客户端名称不能为空");
        if (trimmed.Length > 80)
            throw new DomainException(40002, "客户端名称不能超过 80 字符");
        if (await _db.Set<McpClientEntity>().AnyAsync(e => e.CreatedBy == createdBy && e.Name == trimmed, ct))
            throw new DomainException(40003, "客户端名称已存在");

        var token = McpTokenService.GenerateToken();
        var entity = new McpClientEntity
        {
            Id = Guid.NewGuid(),
            Name = trimmed,
            TokenHash = McpTokenService.HashToken(token),
            TokenPrefix = McpTokenService.TokenPrefix(token),
            Permissions = McpToolCatalog.DefaultPermissions(),
            Status = "active",
            CreatedAt = _timeProvider.GetUtcNow(),
            CreatedBy = createdBy,
        };
        _db.Set<McpClientEntity>().Add(entity);
        await _db.SaveChangesAsync(ct);

        return new McpClientCreateResult(ToDto(entity, null), token);
    }

    public async Task<List<McpClientDto>> ListAsync(Guid currentUser, CancellationToken ct = default)
    {
        var entities = await _db.Set<McpClientEntity>()
            .AsNoTracking()
            .Where(e => e.CreatedBy == currentUser)
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);
        var username = await _db.Users.AsNoTracking()
            .Where(u => u.Id == currentUser)
            .Select(u => u.Username)
            .FirstOrDefaultAsync(ct);
        return entities.Select(e => ToDto(e, username)).ToList();
    }

    public async Task<McpClientDto> UpdateAsync(
        Guid id,
        string? name,
        Dictionary<string, Dictionary<string, bool>>? permissions,
        Guid currentUser,
        CancellationToken ct = default)
    {
        var entity = await _db.Set<McpClientEntity>().FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new DomainException(40401, "客户端不存在");
        if (entity.CreatedBy != currentUser)
            throw new DomainException(40301, "无权操作该客户端");

        if (name is not null)
        {
            var trimmed = name.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                throw new DomainException(40001, "客户端名称不能为空");
            if (trimmed.Length > 80)
                throw new DomainException(40002, "客户端名称不能超过 80 字符");
            if (await _db.Set<McpClientEntity>().AnyAsync(e => e.Id != id && e.CreatedBy == currentUser && e.Name == trimmed, ct))
                throw new DomainException(40003, "客户端名称已存在");
            entity.Name = trimmed;
        }

        if (permissions is not null)
        {
            // Merge per key so partial updates never wipe other tools in the same section.
            foreach (var section in SanitizePermissions(permissions))
            {
                if (!entity.Permissions.TryGetValue(section.Key, out var existing))
                {
                    existing = new Dictionary<string, bool>(StringComparer.Ordinal);
                    entity.Permissions[section.Key] = existing;
                }
                foreach (var (toolName, enabled) in section.Value)
                    existing[toolName] = enabled;
            }
        }

        await _db.SaveChangesAsync(ct);
        return ToDto(entity, null);
    }

    public async Task<McpClientDto> RevokeAsync(Guid id, Guid currentUser, CancellationToken ct = default)
    {
        var entity = await _db.Set<McpClientEntity>().FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new DomainException(40401, "客户端不存在");
        if (entity.CreatedBy != currentUser)
            throw new DomainException(40301, "无权操作该客户端");
        if (entity.Status != "revoked")
        {
            entity.Status = "revoked";
            entity.RevokedAt = _timeProvider.GetUtcNow();
            await _db.SaveChangesAsync(ct);
        }
        return ToDto(entity, null);
    }

    public async Task DeleteAsync(Guid id, Guid currentUser, CancellationToken ct = default)
    {
        var entity = await _db.Set<McpClientEntity>().FirstOrDefaultAsync(e => e.Id == id, ct)
            ?? throw new DomainException(40401, "客户端不存在");
        if (entity.CreatedBy != currentUser)
            throw new DomainException(40301, "无权操作该客户端");
        _db.Set<McpClientEntity>().Remove(entity);
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Validates a raw token and a tool-level permission. A tool name is REQUIRED: without it the
    /// endpoint would issue a JWT that bypasses the tool-level permission model. On success issues a
    /// short-lived user JWT for REST passthrough and records connection activity + audit.
    /// </summary>
    public async Task<McpVerifyOutcome> VerifyAsync(string rawToken, string? tool, string? paramsSummary, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return McpVerifyOutcome.Unauthorized();
        if (string.IsNullOrWhiteSpace(tool))
            return McpVerifyOutcome.InvalidRequest("tool is required");

        var hash = McpTokenService.HashToken(rawToken.Trim());
        var client = await _db.Set<McpClientEntity>().FirstOrDefaultAsync(e => e.TokenHash == hash, ct);
        if (client is null || client.Status != "active")
            return McpVerifyOutcome.Unauthorized();
        // Defense in depth: constant-time comparison of the stored hash with the derived one.
        var storedBytes = Convert.FromHexString(client.TokenHash);
        var derivedBytes = Convert.FromHexString(hash);
        if (!CryptographicOperations.FixedTimeEquals(storedBytes, derivedBytes))
            return McpVerifyOutcome.Unauthorized();

        if (!McpToolCatalog.Contains(tool!))
            return McpVerifyOutcome.Forbidden(tool!);
        var isWrite = McpToolCatalog.IsWrite(tool!);
        var section = isWrite ? "write" : "read";
        var map = client.Permissions.TryGetValue(section, out var m) ? m : new Dictionary<string, bool>();
        var allowed = map.TryGetValue(tool!, out var v) ? v : !isWrite;
        if (!allowed)
            return McpVerifyOutcome.Forbidden(tool!);

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == client.CreatedBy, ct);
        if (user is null || !user.IsActive)
            return McpVerifyOutcome.Unauthorized();
        var accessToken = _jwt.GenerateAccessToken(user.Id, user.Username, user.Role);

        // Atomically bump connection stats. InMemory provider (tests) does not support
        // ExecuteUpdateAsync, so fall back to a tracked read-modify-write there.
        var now = _timeProvider.GetUtcNow();
        if (_db.Database.ProviderName?.Contains("InMemory") == true)
        {
            client.LastSeenAt = now;
            client.LastTool = tool;
            client.CallCount++;
            if (isWrite)
                client.WriteCallCount++;
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            await _db.Set<McpClientEntity>()
                .Where(e => e.Id == client.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.LastSeenAt, now)
                    .SetProperty(e => e.LastTool, tool)
                    .SetProperty(e => e.CallCount, e => e.CallCount + 1)
                    .SetProperty(e => e.WriteCallCount, e => e.WriteCallCount + (isWrite ? 1 : 0)),
                    ct);
        }

        if (isWrite)
        {
            var metadata = new Dictionary<string, string>
            {
                ["clientId"] = client.Id.ToString(),
                ["clientName"] = client.Name,
                ["tool"] = tool!,
            };
            if (!string.IsNullOrWhiteSpace(paramsSummary))
                metadata["params"] = paramsSummary!.Length > 500 ? paramsSummary[..500] : paramsSummary!;
            try
            {
                await _auditLog.RecordAsync(new CreateAuditLogRequest(
                    user.Id,
                    AuditActorType.User,
                    $"mcp.write.{tool}",
                    "mcp_tool",
                    client.Id.ToString(),
                    "mcp",
                    AuditResult.Success,
                    null,
                    null,
                    null,
                    metadata,
                    null,
                    null), ct);
            }
            catch (Exception ex)
            {
                // Audit must never fail a verified write; counters are already bumped.
                _logger.LogWarning(ex, "MCP write audit failed for client {ClientId}", client.Id);
            }
        }

        return McpVerifyOutcome.Ok(new McpVerifyResult(
            client.Id,
            client.Name,
            user.Id,
            client.Permissions,
            accessToken,
            isWrite));
    }

    public McpCatalogDto Catalog() => new(
        McpToolCatalog.ReadTools.ToList(),
        McpToolCatalog.WriteTools.ToList());

    /// <summary>Keeps only catalog-known tools. Sections absent from the input are left untouched.</summary>
    private static Dictionary<string, Dictionary<string, bool>> SanitizePermissions(
        Dictionary<string, Dictionary<string, bool>> permissions)
    {
        var result = new Dictionary<string, Dictionary<string, bool>>();
        if (permissions.TryGetValue("read", out var read))
            result["read"] = CleanSection(read, McpToolCatalog.ReadTools.Select(t => t.Name));
        if (permissions.TryGetValue("write", out var write))
            result["write"] = CleanSection(write, McpToolCatalog.WriteTools.Select(t => t.Name));
        return result;
    }

    private static Dictionary<string, bool> CleanSection(
        Dictionary<string, bool> source,
        IEnumerable<string> known)
    {
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        return source
            .Where(kv => knownSet.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private McpClientDto ToDto(McpClientEntity e, string? createdByUsername = null)
    {
        var now = _timeProvider.GetUtcNow();
        return new McpClientDto(
            e.Id,
            e.Name,
            e.Status,
            e.TokenPrefix,
            e.Permissions,
            e.CreatedAt,
            e.RevokedAt,
            e.LastSeenAt,
            e.CallCount,
            e.WriteCallCount,
            e.LastTool,
            e.LastSeenAt is { } last && now - last <= TimeSpan.FromMinutes(5),
            createdByUsername);
    }
}

public sealed record McpVerifyOutcome(int HttpStatus, McpVerifyResult? Result, string? Error)
{
    public static McpVerifyOutcome Unauthorized() => new(401, null, "invalid or revoked token");
    public static McpVerifyOutcome InvalidRequest(string message) => new(400, null, message);
    public static McpVerifyOutcome Forbidden(string tool)
    {
        var name = tool.Replace("\n", "").Replace("\r", "");
        if (name.Length > 120)
            name = name[..120];
        return new(403, null, $"permission denied: {name}");
    }
    public static McpVerifyOutcome Ok(McpVerifyResult result) => new(0, result, null);
}
