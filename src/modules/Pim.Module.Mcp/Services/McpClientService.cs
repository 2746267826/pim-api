using Microsoft.EntityFrameworkCore;
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

    public McpClientService(
        PimDbContext db,
        IAuditLogService auditLog,
        JwtService jwt,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _auditLog = auditLog;
        _jwt = jwt;
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

        return new McpClientCreateResult(await ToDtoAsync(entity, ct), token);
    }

    public async Task<List<McpClientDto>> ListAsync(CancellationToken ct = default)
    {
        var entities = await _db.Set<McpClientEntity>()
            .AsNoTracking()
            .OrderByDescending(e => e.CreatedAt)
            .ToListAsync(ct);
        var result = new List<McpClientDto>(entities.Count);
        foreach (var e in entities)
            result.Add(await ToDtoAsync(e, ct));
        return result;
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
            entity.Permissions = SanitizePermissions(permissions);

        await _db.SaveChangesAsync(ct);
        return await ToDtoAsync(entity, ct);
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
        return await ToDtoAsync(entity, ct);
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
    /// Validates a raw token and (optionally) a tool-level permission. On success issues a
    /// short-lived user JWT for REST passthrough and records connection activity + audit.
    /// </summary>
    public async Task<McpVerifyOutcome> VerifyAsync(string rawToken, string? tool, string? paramsSummary, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawToken))
            return McpVerifyOutcome.Unauthorized();
        var hash = McpTokenService.HashToken(rawToken.Trim());
        var client = await _db.Set<McpClientEntity>().FirstOrDefaultAsync(e => e.TokenHash == hash, ct);
        if (client is null || client.Status != "active")
            return McpVerifyOutcome.Unauthorized();

        var isWrite = false;
        if (!string.IsNullOrEmpty(tool))
        {
            if (!McpToolCatalog.Contains(tool!))
                return McpVerifyOutcome.Forbidden(tool!);
            isWrite = McpToolCatalog.IsWrite(tool!);
            var section = isWrite ? "write" : "read";
            var map = client.Permissions.TryGetValue(section, out var m) ? m : new Dictionary<string, bool>();
            var allowed = map.TryGetValue(tool!, out var v) ? v : !isWrite;
            if (!allowed)
                return McpVerifyOutcome.Forbidden(tool!);
        }

        var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == client.CreatedBy, ct);
        if (user is null || !user.IsActive)
            return McpVerifyOutcome.Unauthorized();
        var accessToken = _jwt.GenerateAccessToken(user.Id, user.Username, user.Role);

        client.LastSeenAt = _timeProvider.GetUtcNow();
        client.LastTool = tool;
        client.CallCount++;
        if (isWrite)
            client.WriteCallCount++;
        await _db.SaveChangesAsync(ct);

        if (isWrite && !string.IsNullOrEmpty(tool))
        {
            var metadata = new Dictionary<string, string>
            {
                ["clientId"] = client.Id.ToString(),
                ["clientName"] = client.Name,
                ["tool"] = tool!,
            };
            if (!string.IsNullOrWhiteSpace(paramsSummary))
                metadata["params"] = paramsSummary!.Length > 500 ? paramsSummary[..500] : paramsSummary!;
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

    /// <summary>Keeps only catalog-known tools so arbitrary keys cannot be stored.</summary>
    private static Dictionary<string, Dictionary<string, bool>> SanitizePermissions(
        Dictionary<string, Dictionary<string, bool>> permissions)
    {
        var result = new Dictionary<string, Dictionary<string, bool>>();
        var read = CleanSection(permissions, "read", McpToolCatalog.ReadTools.Select(t => t.Name));
        var write = CleanSection(permissions, "write", McpToolCatalog.WriteTools.Select(t => t.Name));
        result["read"] = read;
        result["write"] = write;
        return result;
    }

    private static Dictionary<string, bool> CleanSection(
        Dictionary<string, Dictionary<string, bool>> permissions,
        string section,
        IEnumerable<string> known)
    {
        var source = permissions.TryGetValue(section, out var m) ? m : new Dictionary<string, bool>();
        var knownSet = known.ToHashSet(StringComparer.Ordinal);
        return source
            .Where(kv => knownSet.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private async Task<McpClientDto> ToDtoAsync(McpClientEntity e, CancellationToken ct = default)
    {
        var now = _timeProvider.GetUtcNow();
        string? createdByUsername = null;
        if (e.CreatedBy != Guid.Empty)
        {
            var user = await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == e.CreatedBy, ct);
            createdByUsername = user?.Username;
        }
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
    public static McpVerifyOutcome Forbidden(string tool) => new(403, null, $"permission denied: {tool}");
    public static McpVerifyOutcome Ok(McpVerifyResult result) => new(0, result, null);
}