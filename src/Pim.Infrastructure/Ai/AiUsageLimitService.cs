using System.Globalization;
using System.Data.Common;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai;

/// <summary>
/// J09 connection-scoped usage limits. The only accounting facts are the already persisted
/// <c>TotalTokens</c>, <c>EstimatedCost</c> and <c>Currency</c> on <see cref="AiRequestLogEntity"/>.
/// This service never derives a price from a model rate or treats a missing value as zero.
/// </summary>
public sealed class AiUsageLimitService(
    PimDbContext db,
    ICurrentUserService currentUser,
    TimeProvider? clock = null,
    DbContextOptions<PimDbContext>? isolatedOptions = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string DefaultTimeZone = "Asia/Shanghai";
    private const long LockSalt = 0x4A30395F55534147L; // "J09_USAG"

    private DateTimeOffset Now => (clock ?? TimeProvider.System).GetUtcNow();

    public async Task<IReadOnlyList<AiProviderConnectionDto>> ListConnectionsAsync(CancellationToken ct = default)
    {
        EnsureAdmin();
        var connections = await db.AiProviderConnections.AsNoTracking()
            .OrderBy(x => x.Name).ThenBy(x => x.Id).ToListAsync(ct);
        var rules = await db.AiUsageLimitRules.AsNoTracking()
            .ToDictionaryAsync(x => x.ConnectionId, ct);
        var counts = await db.AiModelProfiles.AsNoTracking()
            .Where(x => x.ProviderConnectionId != null)
            .GroupBy(x => x.ProviderConnectionId!.Value)
            .Select(x => new { ConnectionId = x.Key, Count = x.Count() })
            .ToDictionaryAsync(x => x.ConnectionId, x => x.Count, ct);
        return connections.Select(connection => Map(connection,
            rules.GetValueOrDefault(connection.Id) ?? DefaultRule(connection.Id),
            counts.GetValueOrDefault(connection.Id))).ToArray();
    }

    public async Task<AiProviderConnectionDto> CreateConnectionAsync(string name,
        CancellationToken ct = default)
    {
        EnsureAdmin();
        var clean = CleanName(name);
        var normalized = clean.ToUpperInvariant();
        if (await db.AiProviderConnections.CountAsync(ct) >= 64)
            throw Invalid("最多保存64个AI服务连接");
        if (await db.AiProviderConnections.AnyAsync(x => x.NormalizedName == normalized, ct))
            throw Conflict("已有同名AI服务连接");
        var now = Now;
        var connection = new AiProviderConnectionEntity
        {
            Name = clean, NormalizedName = normalized, CreatedAt = now, UpdatedAt = now
        };
        var rule = DefaultRule(connection.Id);
        rule.Revision = Guid.NewGuid();
        rule.UpdatedAt = now;
        db.AiProviderConnections.Add(connection);
        db.AiUsageLimitRules.Add(rule);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            throw Conflict("已有同名AI服务连接");
        }
        return Map(connection, rule, 0);
    }

    public async Task<AiProviderConnectionDto> UpdateConnectionAsync(Guid id,
        SaveAiProviderConnectionRequest request, CancellationToken ct = default)
    {
        EnsureAdmin();
        var connection = await db.AiProviderConnections.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new DomainException(40401, "AI服务连接不存在");
        if (connection.Revision != (request.ExpectedRevision ?? Guid.Empty)) throw Stale();
        var clean = CleanName(request.Name);
        var normalized = clean.ToUpperInvariant();
        if (await db.AiProviderConnections.AnyAsync(x => x.Id != id && x.NormalizedName == normalized, ct))
            throw Conflict("已有同名AI服务连接");
        connection.Name = clean;
        connection.NormalizedName = normalized;
        connection.Revision = Guid.NewGuid();
        connection.UpdatedAt = Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Stale(); }
        catch (DbUpdateException error) when (IsUniqueViolation(error)) { throw Conflict("已有同名AI服务连接"); }
        var rule = await db.AiUsageLimitRules.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == id, ct) ?? DefaultRule(id);
        var count = await db.AiModelProfiles.AsNoTracking().CountAsync(x => x.ProviderConnectionId == id, ct);
        return Map(connection, rule, count);
    }

    public async Task<AiUsageLimitRuleDto> GetRuleAsync(Guid connectionId,
        CancellationToken ct = default)
    {
        EnsureAdmin();
        if (!await db.AiProviderConnections.AnyAsync(x => x.Id == connectionId, ct))
            throw new DomainException(40401, "AI服务连接不存在");
        var rule = await db.AiUsageLimitRules.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId, ct) ?? DefaultRule(connectionId);
        return Map(rule);
    }

    public async Task<AiUsageLimitRuleDto> SaveRuleAsync(Guid connectionId,
        SaveAiUsageLimitRuleRequest request, CancellationToken ct = default)
    {
        EnsureAdmin();
        if (!await db.AiProviderConnections.AnyAsync(x => x.Id == connectionId, ct))
            throw new DomainException(40401, "AI服务连接不存在");

        var rule = await db.AiUsageLimitRules.SingleOrDefaultAsync(x => x.ConnectionId == connectionId, ct);
        if (rule is null)
        {
            if (request.ExpectedRevision != Guid.Empty) throw Stale();
            rule = DefaultRule(connectionId);
            rule.Revision = Guid.NewGuid();
            db.AiUsageLimitRules.Add(rule);
        }
        else if (rule.Revision != request.ExpectedRevision) throw Stale();

        var period = ValidatePeriod(request.Period);
        if (request.WarningPercent is < 1 or > 99) throw Invalid("提前提醒比例须为1–99的整数");
        if (request.TokenLimit is <= 0) throw Invalid("Token提醒值须为正整数");
        if (request.CostLimit is <= 0) throw Invalid("费用提醒值须为正数");
        if (request.Enabled && request.TokenLimit is null && request.CostLimit is null)
            throw Invalid("启用提醒时至少填写Token或费用提醒值");
        var timeZone = ValidateTimeZone(request.TimeZoneId);
        var currency = NormalizeCurrency(request.Currency);
        if (request.CostLimit is not null && currency is null)
            throw Invalid("填写费用提醒值时必须选择3位大写币种代码");
        if (request.CostLimit is null) currency = null;

        rule.Enabled = request.Enabled;
        rule.Period = period;
        rule.WarningPercent = request.WarningPercent;
        rule.TokenLimit = request.TokenLimit;
        rule.CostLimit = request.CostLimit;
        rule.Currency = currency;
        rule.TimeZoneId = timeZone;
        rule.Revision = Guid.NewGuid();
        rule.UpdatedAt = Now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Stale(); }
        catch (DbUpdateException error) when (IsUniqueViolation(error)) { throw Stale(); }

        if (rule.Enabled) await EvaluateAsync(connectionId, ct);
        return Map(rule);
    }

    public async Task<AiUsageLimitUsageDto> GetUsageAsync(Guid connectionId, string? period,
        string? cycleKey, CancellationToken ct = default)
    {
        EnsureAdmin();
        var connection = await db.AiProviderConnections.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == connectionId, ct)
            ?? throw new DomainException(40401, "AI服务连接不存在");
        var rule = await db.AiUsageLimitRules.AsNoTracking()
            .SingleOrDefaultAsync(x => x.ConnectionId == connectionId, ct) ?? DefaultRule(connectionId);
        if (rule.Enabled) await EvaluateAsync(connectionId, ct);
        var scope = ResolvePeriod(period ?? rule.Period, cycleKey, Now, rule.TimeZoneId);
        return await ComputeUsageAsync(connection.Id, connection.Name, rule, scope, ct);
    }

    public async Task<IReadOnlyList<AiUsageReminderDto>> ListRemindersAsync(Guid? connectionId,
        bool unreadOnly, int limit, CancellationToken ct = default)
    {
        EnsureAdmin();
        var userId = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        var enabled = await db.AiUsageLimitRules.AsNoTracking()
            .Where(x => x.Enabled && (connectionId == null || x.ConnectionId == connectionId))
            .Select(x => x.ConnectionId).ToListAsync(ct);
        foreach (var id in enabled) await EvaluateAsync(id, ct);

        var take = Math.Clamp(limit, 1, 200);
        var query = db.AiUsageReminderEvents.AsNoTracking();
        if (connectionId is not null) query = query.Where(x => x.ConnectionId == connectionId);
        if (unreadOnly)
            query = query.Where(x => !db.AiUsageReminderEventReads.Any(r =>
                r.EventId == x.Id && r.UserId == userId));
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Take(take).ToListAsync(ct);
        var eventIds = rows.Select(x => x.Id).ToArray();
        var read = (await db.AiUsageReminderEventReads.AsNoTracking()
            .Where(x => x.UserId == userId && eventIds.Contains(x.EventId))
            .Select(x => x.EventId).ToListAsync(ct)).ToHashSet();
        var names = await db.AiProviderConnections.AsNoTracking()
            .ToDictionaryAsync(x => x.Id, x => x.Name, ct);

        return rows.Select(x => Map(x, names.GetValueOrDefault(x.ConnectionId, "已删除连接"), read.Contains(x.Id)))
            .ToArray();
    }

    public async Task<bool> MarkReminderReadAsync(Guid eventId, CancellationToken ct = default)
    {
        EnsureAdmin();
        var userId = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        if (!await db.AiUsageReminderEvents.AsNoTracking().AnyAsync(x => x.Id == eventId, ct))
            throw new DomainException(40401, "用量提醒不存在");
        if (await db.AiUsageReminderEventReads.AsNoTracking()
                .AnyAsync(x => x.EventId == eventId && x.UserId == userId, ct))
            return true;
        db.AiUsageReminderEventReads.Add(new AiUsageReminderEventReadEntity
        {
            EventId = eventId, UserId = userId, ReadAt = Now
        });
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException error) when (IsUniqueViolation(error))
        {
            db.ChangeTracker.Clear();
        }
        return true;
    }

    /// <summary>
    /// Atomic pre-provider check. When a limit is enabled this also takes a connection-wide
    /// lease, so two requests cannot both read "under the limit" and send concurrently.
    /// </summary>
    public async Task<AiUsageLimitDecision> TryBeginAsync(Guid? connectionId, string requestKey,
        string purpose, TimeSpan ttl, CancellationToken ct = default)
    {
        if (connectionId is not { } id) return AiUsageLimitDecision.Allow();
        var key = CleanLeaseText(requestKey, 200, "请求键");
        var leasePurpose = CleanLeaseText(purpose, 128, "用途");
        if (ttl <= TimeSpan.Zero || ttl > TimeSpan.FromHours(26))
            throw Invalid("用量租约有效期无效");
        var now = Now;

        try
        {
            return await WithIsolatedContextAsync(lockDb => AdvisoryLock.RunAsync(lockDb, LockKey(id), async () =>
            {
                var rule = await lockDb.AiUsageLimitRules.AsNoTracking()
                    .SingleOrDefaultAsync(x => x.ConnectionId == id, ct);
                if (rule is null || !rule.Enabled ||
                    (rule.TokenLimit is null && rule.CostLimit is null))
                    return AiUsageLimitDecision.Allow();

                var scope = ResolvePeriod(rule.Period, null, now, rule.TimeZoneId);
                var usage = await ComputeUsageAsync(lockDb, id, "", rule, scope, key, ct);
                if (rule.TokenLimit is { } tokenLimit && usage.TotalTokens >= tokenLimit)
                    return AiUsageLimitDecision.Deny("usage_limit_reached",
                        "本周期已记录的Token用量已达到该连接上限，未发送模型请求");
                if (rule.CostLimit is { } costLimit && usage.EstimatedCost >= costLimit)
                    return AiUsageLimitDecision.Deny("usage_limit_reached",
                        "本周期已记录的费用已达到该连接上限，未发送模型请求");
                if (rule.TokenLimit is not null && usage.TokenUnknownRequestCount > 0)
                    return AiUsageLimitDecision.Deny("usage_limit_unknown",
                        "本周期存在未上报Token的调用记录，无法确认是否低于上限；本次未发送模型请求");
                if (rule.CostLimit is not null && usage.CostUnknownRequestCount > 0)
                    return AiUsageLimitDecision.Deny("usage_limit_unknown",
                        "本周期存在缺少所选币种费用的调用记录，无法确认是否低于上限；本次未发送模型请求");

                await RemoveExpiredLeasesAsync(lockDb, now, ct);
                if (await lockDb.AiUsageLimitLeases.AsNoTracking()
                        .AnyAsync(x => x.ConnectionId == id && x.ExpiresAt > now, ct))
                    return AiUsageLimitDecision.Deny("usage_limit_busy",
                        "该连接已有进行中的AI请求；为避免并发请求共同突破用量上限，本次未发送");

                var lease = new AiUsageLimitLeaseEntity
                {
                    ConnectionId = id, RequestKey = key, Purpose = leasePurpose,
                    CreatedAt = now, ExpiresAt = now.Add(ttl)
                };
                lockDb.AiUsageLimitLeases.Add(lease);
                await lockDb.SaveChangesAsync(ct);
                return AiUsageLimitDecision.Allow(new(lease.Id, id, key));
            }, ct));
        }
        catch (DbUpdateException error) when (IsLeaseConflict(error))
        {
            return AiUsageLimitDecision.Deny("usage_limit_busy",
                "该连接已有进行中的AI请求；为避免并发请求共同突破用量上限，本次未发送");
        }
        catch (Exception error) when (error is DbUpdateException or DbException or TimeoutException)
        {
            return AiUsageLimitDecision.Deny("usage_limit_unavailable",
                "AI用量限制暂时无法核对；为避免超出上限，本次未发送模型请求");
        }
    }

    public async Task EndAsync(AiUsageLimitLease? lease, CancellationToken ct = default)
    {
        if (lease is null) return;
        await WithIsolatedContextAsync(async releaseDb =>
        {
            if (releaseDb.Database.IsRelational())
            {
                await releaseDb.AiUsageLimitLeases.Where(x => x.Id == lease.Id).ExecuteDeleteAsync(ct);
                return true;
            }
            var row = await releaseDb.AiUsageLimitLeases.SingleOrDefaultAsync(x => x.Id == lease.Id, ct);
            if (row is not null)
            {
                releaseDb.AiUsageLimitLeases.Remove(row);
                await releaseDb.SaveChangesAsync(ct);
            }
            return true;
        });
    }

    public async Task EvaluateAsync(Guid connectionId, CancellationToken ct = default)
    {
        var now = Now;
        await WithIsolatedContextAsync(lockDb => AdvisoryLock.RunAsync(lockDb, LockKey(connectionId), async () =>
        {
            var rule = await lockDb.AiUsageLimitRules.AsNoTracking()
                .SingleOrDefaultAsync(x => x.ConnectionId == connectionId, ct);
            if (rule is null || !rule.Enabled) return true;
            var scope = ResolvePeriod(rule.Period, null, now, rule.TimeZoneId);
            var usage = await ComputeUsageAsync(lockDb, connectionId, "", rule, scope, null, ct);
            var tokenLevel = Level(usage.TotalTokens, rule.TokenLimit, rule.WarningPercent);
            var costLevel = Level(usage.EstimatedCost, rule.CostLimit, rule.WarningPercent);
            var highest = Highest(tokenLevel, costLevel);
            if (highest is null) return true;

            if (highest == AiUsageLimitLevels.Warning &&
                await lockDb.AiUsageReminderEvents.AsNoTracking().AnyAsync(x =>
                    x.ConnectionId == connectionId && x.Period == rule.Period &&
                    x.CycleKey == usage.CycleKey && x.Level == AiUsageLimitLevels.Reached, ct))
                return true;

            var metrics = MetricsFor(highest, tokenLevel, costLevel, usage.Currency);
            var snapshot = Snapshot(usage);
            var existing = await lockDb.AiUsageReminderEvents.SingleOrDefaultAsync(x =>
                x.ConnectionId == connectionId && x.Period == rule.Period &&
                x.CycleKey == usage.CycleKey && x.Level == highest, ct);
            if (existing is null)
            {
                var created = new AiUsageReminderEventEntity
                {
                    ConnectionId = connectionId, Period = rule.Period, CycleKey = usage.CycleKey,
                    Level = highest, WarningPercent = rule.WarningPercent,
                    TimeZoneId = rule.TimeZoneId, MetricsJson = JsonSerializer.Serialize(metrics, Json),
                    SnapshotJson = JsonSerializer.Serialize(snapshot, Json), CreatedAt = now, UpdatedAt = now
                };
                lockDb.AiUsageReminderEvents.Add(created);
                try { await lockDb.SaveChangesAsync(ct); }
                catch (DbUpdateException error) when (IsUniqueViolation(error))
                {
                    // The unique index already holds the authoritative event. PostgreSQL aborts the
                    // surrounding transaction on this error, so no follow-up query may run here.
                    lockDb.Entry(created).State = EntityState.Detached;
                }
                return true;
            }
            await MergeEventAsync(lockDb, existing, metrics, snapshot, now, ct);
            return true;
        }, ct));
    }

    internal async Task<AiProviderConnectionEntity> CreateDedicatedConnectionForModelAsync(
        string modelName, Guid modelId, CancellationToken ct = default)
    {
        var basis = CleanName(modelName);
        var name = basis;
        var normalized = name.ToUpperInvariant();
        if (await db.AiProviderConnections.AnyAsync(x => x.NormalizedName == normalized, ct))
        {
            var suffix = " #" + modelId.ToString("N")[..8];
            name = basis.Length + suffix.Length <= 120 ? basis + suffix : basis[..(120 - suffix.Length)] + suffix;
            normalized = name.ToUpperInvariant();
        }
        var now = Now;
        var connection = new AiProviderConnectionEntity
        {
            Id = modelId, Name = name, NormalizedName = normalized, CreatedAt = now, UpdatedAt = now
        };
        var rule = DefaultRule(connection.Id);
        rule.Revision = Guid.NewGuid();
        rule.UpdatedAt = now;
        db.AiProviderConnections.Add(connection);
        db.AiUsageLimitRules.Add(rule);
        return connection;
    }

    private async Task<AiUsageLimitUsageDto> ComputeUsageAsync(Guid connectionId, string connectionName,
        AiUsageLimitRuleEntity rule, PeriodScope scope, CancellationToken ct)
        => await ComputeUsageAsync(db, connectionId, connectionName, rule, scope, null, ct);

    private static async Task<AiUsageLimitUsageDto> ComputeUsageAsync(PimDbContext usageDb, Guid connectionId,
        string connectionName, AiUsageLimitRuleEntity rule, PeriodScope scope, string? excludedRequestKey,
        CancellationToken ct)
    {
        var rows = await usageDb.AiRequestLogs.AsNoTracking()
            .Where(x => x.ProviderConnectionId == connectionId && x.Status != "blocked" &&
                        (excludedRequestKey == null || x.CorrelationId != excludedRequestKey) &&
                        (x.InitiatedAt ?? x.StartedAt) >= scope.StartsAt &&
                        (x.InitiatedAt ?? x.StartedAt) < scope.EndsAt)
            .Select(x => new UsageRow(x.Status, x.TotalTokens, x.EstimatedCost, x.Currency, x.MetadataJson))
            .ToListAsync(ct);

        var providerRows = rows.Where(x => ProviderAttempted(x.MetadataJson) != false).ToList();
        var tokenKnown = providerRows.Where(x => x.TotalTokens is not null).ToList();
        var totalTokens = tokenKnown.Count == 0 ? null : (long?)tokenKnown.Sum(x => (long)x.TotalTokens!);
        var costKnown = rule.Currency is null
            ? new List<UsageRow>()
            : providerRows.Where(x => x.EstimatedCost is not null &&
                              string.Equals(x.Currency, rule.Currency, StringComparison.OrdinalIgnoreCase)).ToList();
        var costUsed = costKnown.Count == 0 ? null : (decimal?)costKnown.Sum(x => x.EstimatedCost!.Value);
        var running = rows.Count(x => x.Status is "running" or "preparing" or "queued" or "cancelling");
        var incomplete = rows.Count(x => x.Status is "incomplete" or "timed_out" or "cancelled");
        var tokenUnknown = providerRows.Count - tokenKnown.Count;
        var costUnknown = providerRows.Count - costKnown.Count;
        var tokenStatus = rule.TokenLimit is not null && tokenUnknown > 0
            ? AiUsageLimitStatuses.Unknown
            : Status(totalTokens, rule.TokenLimit, rule.WarningPercent);
        var costStatus = rule.CostLimit is not null && costUnknown > 0
            ? AiUsageLimitStatuses.Unknown
            : Status(costUsed, rule.CostLimit, rule.WarningPercent);
        var notes = new List<string>();
        if (tokenUnknown > 0) notes.Add("部分请求未上报Token，累计仅包含已上报值");
        if (costUnknown > 0) notes.Add("部分请求未提供所选币种的EstimatedCost，累计仅包含已知金额");
        if (running > 0) notes.Add("进行中或排队请求的用量尚待确认");
        if (rule.Currency is not null && providerRows.Count > costKnown.Count)
            notes.Add("其他币种或缺失币种费用不计入" + rule.Currency);
        notes.Add("费用只使用调用记录中的EstimatedCost，不做模型费率估算");

        return new AiUsageLimitUsageDto(
            connectionId, connectionName, scope.Period, scope.CycleKey, scope.StartsAt, scope.EndsAt,
            rule.TimeZoneId, rows.Count, running, incomplete, tokenKnown.Count, tokenUnknown, totalTokens,
            costKnown.Count, costUnknown, costUsed, rule.Currency, rule.TokenLimit, rule.CostLimit,
            rule.WarningPercent, tokenStatus, costStatus, HighestStatus(tokenStatus, costStatus), notes);
    }

    private static async Task MergeEventAsync(PimDbContext eventDb, AiUsageReminderEventEntity existing,
        IReadOnlyList<string> metrics, AiUsageLimitSnapshotDto snapshot, DateTimeOffset now,
        CancellationToken ct)
    {
        var merged = ParseMetrics(existing.MetricsJson).ToList();
        foreach (var metric in metrics)
            if (!merged.Contains(metric, StringComparer.Ordinal)) merged.Add(metric);
        merged.Sort(StringComparer.Ordinal);
        var metricsJson = JsonSerializer.Serialize(merged, Json);
        var snapshotJson = JsonSerializer.Serialize(snapshot, Json);
        if (existing.MetricsJson == metricsJson && existing.SnapshotJson == snapshotJson) return;
        existing.MetricsJson = metricsJson;
        existing.SnapshotJson = snapshotJson;
        existing.UpdatedAt = now;
        await eventDb.SaveChangesAsync(ct);
    }

    private static async Task RemoveExpiredLeasesAsync(PimDbContext leaseDb, DateTimeOffset now,
        CancellationToken ct)
    {
        if (leaseDb.Database.IsRelational())
        {
            await leaseDb.AiUsageLimitLeases.Where(x => x.ExpiresAt <= now).ExecuteDeleteAsync(ct);
            return;
        }
        var expired = await leaseDb.AiUsageLimitLeases.Where(x => x.ExpiresAt <= now).ToListAsync(ct);
        if (expired.Count == 0) return;
        leaseDb.AiUsageLimitLeases.RemoveRange(expired);
        await leaseDb.SaveChangesAsync(ct);
    }

    /// <summary>
    /// AdvisoryLock clears the change tracker before each retry. Limit accounting must never
    /// clear the caller's pending audit/result entities, so use a fresh context when available.
    /// </summary>
    private async Task<T> WithIsolatedContextAsync<T>(Func<PimDbContext, Task<T>> operation)
    {
        if (isolatedOptions is null) return await operation(db);
        await using var isolated = new PimDbContext(isolatedOptions, currentUser);
        return await operation(isolated);
    }

    private static long LockKey(Guid connectionId)
    {
        var bytes = connectionId.ToByteArray();
        return BitConverter.ToInt64(bytes, 0) ^ BitConverter.ToInt64(bytes, 8) ^ LockSalt;
    }

    private static PeriodScope ResolvePeriod(string period, string? cycleKey, DateTimeOffset nowUtc,
        string timeZoneId)
    {
        period = ValidatePeriod(period);
        var zone = FindTimeZone(timeZoneId);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, zone);
        if (string.IsNullOrWhiteSpace(cycleKey))
            cycleKey = period == AiUsageLimitPeriods.Month
                ? localNow.ToString("yyyy-MM", CultureInfo.InvariantCulture)
                : localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (period == AiUsageLimitPeriods.Month)
        {
            if (!DateTime.TryParseExact(cycleKey, "yyyy-MM", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var month) || month.Year is < 2000 or > 2200)
                throw Invalid("月度周期须为有效的YYYY-MM");
            var startLocal = new DateTime(month.Year, month.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
            return new(period, cycleKey, ToUtc(startLocal, zone), ToUtc(startLocal.AddMonths(1), zone));
        }
        if (!DateTime.TryParseExact(cycleKey, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day) || day.Year is < 2000 or > 2200)
            throw Invalid("每日周期须为有效的YYYY-MM-DD");
        var start = new DateTime(day.Year, day.Month, day.Day, 0, 0, 0, DateTimeKind.Unspecified);
        return new(period, cycleKey, ToUtc(start, zone), ToUtc(start.AddDays(1), zone));
    }

    private static DateTimeOffset ToUtc(DateTime local, TimeZoneInfo zone)
    {
        try
        {
            var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
            return new DateTimeOffset(utc, TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            throw Invalid("周期时区边界无效");
        }
    }

    private static TimeZoneInfo FindTimeZone(string value)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(value); }
        catch (TimeZoneNotFoundException) { throw Invalid("时区不受服务器支持"); }
        catch (InvalidTimeZoneException) { throw Invalid("时区无效"); }
    }

    private static string ValidateTimeZone(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128 || value.Any(char.IsControl))
            throw Invalid("时区不能为空或含控制字符");
        var trimmed = value.Trim();
        _ = FindTimeZone(trimmed);
        return trimmed;
    }

    private static string ValidatePeriod(string? value)
    {
        var period = value?.Trim().ToLowerInvariant();
        return period is AiUsageLimitPeriods.Day or AiUsageLimitPeriods.Month
            ? period
            : throw Invalid("周期仅支持每日或每月");
    }

    private static string? NormalizeCurrency(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var currency = value.Trim().ToUpperInvariant();
        if (currency.Length != 3 || currency.Any(c => c is < 'A' or > 'Z'))
            throw Invalid("币种须为3位大写字母代码，例如CNY或USD");
        return currency;
    }

    private static string CleanName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 120 || value.Any(char.IsControl))
            throw Invalid("连接名称不能为空、超过120字符或含控制字符");
        return value.Trim();
    }

    private static string CleanLeaseText(string? value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max || value.Any(char.IsControl))
            throw Invalid(field + "无效");
        return value.Trim();
    }

    private static string Status(long? used, long? limit, int warningPercent)
    {
        if (used is null || limit is null) return AiUsageLimitStatuses.Unknown;
        var level = Level(used, limit, warningPercent);
        return level ?? AiUsageLimitStatuses.Normal;
    }

    private static string Status(decimal? used, decimal? limit, int warningPercent)
    {
        if (used is null || limit is null) return AiUsageLimitStatuses.Unknown;
        var level = Level(used, limit, warningPercent);
        return level ?? AiUsageLimitStatuses.Normal;
    }

    private static string? Level(long? used, long? limit, int warningPercent)
        => used is null || limit is null ? null
            : used >= limit ? AiUsageLimitLevels.Reached
            : (decimal)used * 100 >= (decimal)limit * warningPercent ? AiUsageLimitLevels.Warning
            : null;

    private static string? Level(decimal? used, decimal? limit, int warningPercent)
        => used is null || limit is null ? null
            : used >= limit ? AiUsageLimitLevels.Reached
            : used * 100 >= limit * warningPercent ? AiUsageLimitLevels.Warning
            : null;

    private static string? Highest(string? first, string? second)
    {
        if (first == AiUsageLimitLevels.Reached || second == AiUsageLimitLevels.Reached)
            return AiUsageLimitLevels.Reached;
        if (first == AiUsageLimitLevels.Warning || second == AiUsageLimitLevels.Warning)
            return AiUsageLimitLevels.Warning;
        return null;
    }

    private static string HighestStatus(string first, string second)
    {
        static int Rank(string value) => value switch
        {
            AiUsageLimitStatuses.Reached => 3,
            AiUsageLimitStatuses.Warning => 2,
            AiUsageLimitStatuses.Normal => 1,
            _ => 0
        };
        return Math.Max(Rank(first), Rank(second)) switch
        {
            3 => AiUsageLimitStatuses.Reached,
            2 => AiUsageLimitStatuses.Warning,
            1 => AiUsageLimitStatuses.Normal,
            _ => AiUsageLimitStatuses.Unknown
        };
    }

    private static IReadOnlyList<string> MetricsFor(string level, string? tokenLevel,
        string? costLevel, string? currency)
    {
        var metrics = new List<string>();
        var tokenTriggered = level == AiUsageLimitLevels.Reached
            ? tokenLevel is AiUsageLimitLevels.Warning or AiUsageLimitLevels.Reached
            : tokenLevel == AiUsageLimitLevels.Warning;
        var costTriggered = level == AiUsageLimitLevels.Reached
            ? costLevel is AiUsageLimitLevels.Warning or AiUsageLimitLevels.Reached
            : costLevel == AiUsageLimitLevels.Warning;
        if (tokenTriggered) metrics.Add("tokens");
        if (costTriggered && currency is not null) metrics.Add("cost-" + currency);
        metrics.Sort(StringComparer.Ordinal);
        return metrics;
    }

    private static AiUsageLimitSnapshotDto Snapshot(AiUsageLimitUsageDto usage) => new(
        usage.TotalTokens, usage.TokenLimit, usage.EstimatedCost, usage.CostLimit, usage.Currency,
        usage.RequestCount, usage.TokenUnknownRequestCount, usage.CostUnknownRequestCount,
        usage.StartsAt, usage.EndsAt);

    private static AiProviderConnectionDto Map(AiProviderConnectionEntity connection,
        AiUsageLimitRuleEntity rule, int modelCount) => new(
            connection.Id, connection.Revision, connection.Name, modelCount, Map(rule), connection.UpdatedAt);

    private static AiUsageLimitRuleDto Map(AiUsageLimitRuleEntity rule) => new(
        rule.ConnectionId, rule.Revision, rule.Enabled, rule.Period, rule.WarningPercent,
        rule.TokenLimit, rule.CostLimit, rule.Currency, rule.TimeZoneId);

    private static AiUsageReminderDto Map(AiUsageReminderEventEntity entity, string connectionName, bool read) =>
        new(entity.Id, entity.ConnectionId, connectionName, entity.Period, entity.CycleKey, entity.Level,
            entity.WarningPercent, entity.TimeZoneId, ParseMetrics(entity.MetricsJson),
            ParseSnapshot(entity.SnapshotJson), entity.CreatedAt, entity.UpdatedAt, read);

    private static IReadOnlyList<string> ParseMetrics(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "[]", Json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static AiUsageLimitSnapshotDto ParseSnapshot(string? json)
    {
        try
        {
            return JsonSerializer.Deserialize<AiUsageLimitSnapshotDto>(json ?? "{}", Json)
                ?? EmptySnapshot();
        }
        catch (JsonException) { return EmptySnapshot(); }
    }

    private static AiUsageLimitSnapshotDto EmptySnapshot() =>
        new(null, null, null, null, null, 0, 0, 0, default, default);

    private static bool? ProviderAttempted(string? metadata)
    {
        try
        {
            using var document = JsonDocument.Parse(metadata ?? "{}");
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            if (!document.RootElement.TryGetProperty("providerAttempted", out var value))
                return null;
            return value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => null
            };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static AiUsageLimitRuleEntity DefaultRule(Guid connectionId) => new()
    {
        ConnectionId = connectionId, Revision = Guid.Empty, Enabled = false,
        Period = AiUsageLimitPeriods.Month, WarningPercent = 80, TimeZoneId = DefaultTimeZone
    };

    private void EnsureAdmin()
    {
        if (!string.Equals(currentUser.Role, "admin", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(40301, "仅管理员可管理AI用量提醒");
    }

    private static bool IsUniqueViolation(DbUpdateException error) =>
        error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static bool IsLeaseConflict(DbUpdateException error) =>
        error.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: "IX_ai_usage_limit_leases_connection_id_request_key"
        };

    private static DomainException Invalid(string message) => new(40001, message);
    private static DomainException Conflict(string message) => new(40901, message);
    private static DomainException Stale() => new(40901, "用量设置已被修改，请刷新核对后再保存");

    private sealed record PeriodScope(string Period, string CycleKey, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
    private sealed record UsageRow(string Status, int? TotalTokens, decimal? EstimatedCost, string? Currency,
        string MetadataJson);
}
