using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Ai;
using Pim.Core.Common;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai;

public sealed class AiUsageService(PimDbContext db, IOptions<AiOptions> options) : IAiUsageService
{
    public async Task<AiStatusDto> GetStatusAsync(CancellationToken ct = default)
    {
        var ai = options.Value;
        var recentSuccess = await db.AiRequestLogs
            .Where(l => l.Status == "succeeded")
            .OrderByDescending(l => l.StartedAt)
            .ThenByDescending(l => l.Id)
            .Select(l => (DateTimeOffset?)l.StartedAt)
            .FirstOrDefaultAsync(ct);

        var settings = await db.AiProviderSettings.SingleOrDefaultAsync(s => s.Provider == "litellm", ct);

        return new AiStatusDto(
            ai.Enabled,
            ai.Provider,
            ai.BaseUrl,
            ai.DefaultModel,
            settings?.LastHealthCheckAt,
            settings?.LastError,
            recentSuccess);
    }

    public async Task<PagedResult<AiRequestLogListItemDto>> ListRequestsAsync(
        AiRequestLogFilter filter,
        CancellationToken ct = default)
    {
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var query = ApplyFilter(db.AiRequestLogs.AsNoTracking(), filter);
        var total = await query.CountAsync(ct);
        var page = Math.Max(1, filter.Page);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var items = await query
            .OrderByDescending(l => l.StartedAt)
            .ThenByDescending(l => l.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(l => new AiRequestLogListItemDto(
                l.Id,
                l.StartedAt,
                l.Module,
                l.Purpose,
                l.Model,
                FromStorageStatus(l.Status),
                l.TotalTokens,
                l.EstimatedCost,
                l.DurationMs,
                l.SourceObjectType,
                l.SourceObjectId,
                l.ErrorMessage,
                l.ModelProfileId,
                l.ModelProfileName,
                l.FinishReason,
                l.ContentRedacted))
            .ToListAsync(ct);

        return new PagedResult<AiRequestLogListItemDto>(
            items,
            page,
            pageSize,
            total,
            total == 0 ? 0 : (int)Math.Ceiling(total / (double)pageSize));
    }

    public async Task<AiRequestLogDetailDto?> GetRequestDetailAsync(Guid id, CancellationToken ct = default)
    {
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var l = await db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (l is null)
        {
            return null;
        }

        return new AiRequestLogDetailDto(
            l.Id,
            l.UserId,
            l.Module,
            l.Purpose,
            l.SourceObjectType,
            l.SourceObjectId,
            l.Provider,
            l.Model,
            l.LiteLlmRequestId,
            l.CorrelationId,
            FromStorageStatus(l.Status),
            l.AttemptNumber,
            l.MaxAttempts,
            l.StartedAt,
            l.FinishedAt,
            l.DurationMs,
            l.RequestMessagesJson,
            l.RequestPayloadJson,
            l.ResponseRawJson,
            l.ResponseText,
            l.ParsedOutputJson,
            l.SchemaName,
            l.SchemaVersion,
            l.SchemaJsonSnapshot,
            l.SchemaValidationErrorsJson,
            new AiTokenUsage(l.PromptTokens, l.CompletionTokens, l.TotalTokens, l.EstimatedCost, l.Currency),
            l.ErrorCode,
            l.ErrorMessage,
            l.MetadataJson,
            l.ClientRequestId,
            l.ModelProfileId,
            l.ModelProfileName,
            l.ModelEndpoint,
            l.ModelRevision,
            l.FinishReason,
            l.ContentRedacted);
    }

    public async Task<AiUsageSummaryDto> GetUsageSummaryAsync(
        DateTimeOffset? from,
        DateTimeOffset? to,
        CancellationToken ct = default)
    {
        await AiCallRecovery.RecoverExpiredAsync(db, ct);
        var filter = new AiRequestLogFilter(from, to, null, null, null, null, null, null, null);
        var logs = await ApplyFilter(db.AiRequestLogs.AsNoTracking(), filter)
            .Select(l => new AiUsageSummaryRow(
                l.Module,
                l.Purpose,
                l.Model,
                l.Status,
                l.PromptTokens,
                l.CompletionTokens,
                l.TotalTokens,
                l.EstimatedCost,
                l.DurationMs))
            .ToListAsync(ct);

        return new AiUsageSummaryDto(
            logs.Count,
            logs.Count(IsSuccess),
            logs.Count(l => !IsSuccess(l) && l.Status != "running"),
            logs.Sum(l => l.PromptTokens ?? 0),
            logs.Sum(l => l.CompletionTokens ?? 0),
            logs.Sum(l => l.TotalTokens ?? 0),
            logs.Sum(l => l.EstimatedCost ?? 0),
            Group(logs, l => l.Module),
            Group(logs, l => l.Purpose),
            Group(logs, l => l.Model),
            Group(logs, l => l.Status),
            logs.Count(l => l.Status == "running"),
            logs.Count(l => l.PromptTokens is null || l.CompletionTokens is null || l.TotalTokens is null),
            logs.Count(l => l.EstimatedCost is not null),
            logs.Average(l => (double?)l.DurationMs));
    }

    private static IQueryable<AiRequestLogEntity> ApplyFilter(
        IQueryable<AiRequestLogEntity> query,
        AiRequestLogFilter filter)
    {
        if (filter.From is not null)
        {
            query = query.Where(l => l.StartedAt >= filter.From);
        }

        if (filter.To is not null)
        {
            query = query.Where(l => l.StartedAt <= filter.To);
        }

        if (!string.IsNullOrWhiteSpace(filter.Module))
        {
            query = query.Where(l => l.Module == filter.Module);
        }

        if (!string.IsNullOrWhiteSpace(filter.Purpose))
        {
            query = query.Where(l => l.Purpose == filter.Purpose);
        }

        if (!string.IsNullOrWhiteSpace(filter.SourceObjectType))
        {
            query = query.Where(l => l.SourceObjectType == filter.SourceObjectType);
        }

        if (!string.IsNullOrWhiteSpace(filter.SourceObjectId))
        {
            query = query.Where(l => l.SourceObjectId == filter.SourceObjectId);
        }

        if (!string.IsNullOrWhiteSpace(filter.Model))
        {
            query = query.Where(l => l.Model == filter.Model);
        }

        if (filter.Status is not null)
        {
            query = query.Where(l => l.Status == ToStorageStatus(filter.Status.Value));
        }

        if (filter.UserId is not null)
        {
            query = query.Where(l => l.UserId == filter.UserId);
        }

        return query;
    }

    private sealed record AiUsageSummaryRow(
        string Module,
        string Purpose,
        string Model,
        string Status,
        int? PromptTokens,
        int? CompletionTokens,
        int? TotalTokens,
        decimal? EstimatedCost,
        long? DurationMs);

    private static IReadOnlyList<AiUsageGroupDto> Group(
        IEnumerable<AiUsageSummaryRow> logs,
        Func<AiUsageSummaryRow, string> keySelector)
        => logs.GroupBy(keySelector)
            .OrderByDescending(g => g.Count())
            .Select(g => new AiUsageGroupDto(
                g.Key,
                g.Count(),
                g.Count(IsSuccess),
                g.Count(l => !IsSuccess(l) && l.Status != "running"),
                g.Sum(l => l.PromptTokens ?? 0),
                g.Sum(l => l.CompletionTokens ?? 0),
                g.Sum(l => l.TotalTokens ?? 0),
                g.Sum(l => l.EstimatedCost ?? 0),
                g.Count(l => l.Status == "running"),
                g.Count(l => l.PromptTokens is null || l.CompletionTokens is null || l.TotalTokens is null),
                g.Count(l => l.EstimatedCost is not null),
                g.Average(l => (double?)l.DurationMs)))
            .ToList();

    private static bool IsSuccess(AiRequestLogEntity log) => log.Status == "succeeded";

    private static bool IsSuccess(AiUsageSummaryRow log) => log.Status == "succeeded";

    private static string ToStorageStatus(AiRequestStatus status) => status switch
    {
        AiRequestStatus.Succeeded => "succeeded",
        AiRequestStatus.Failed => "failed",
        AiRequestStatus.Blocked => "blocked",
        AiRequestStatus.TimedOut => "timed_out",
        AiRequestStatus.FailedValidation => "failed_validation",
        AiRequestStatus.Running => "running",
        AiRequestStatus.Cancelled => "cancelled",
        AiRequestStatus.Incomplete => "incomplete",
        _ => "failed"
    };

    private static AiRequestStatus FromStorageStatus(string status) => status switch
    {
        "succeeded" => AiRequestStatus.Succeeded,
        "blocked" => AiRequestStatus.Blocked,
        "timed_out" => AiRequestStatus.TimedOut,
        "failed_validation" => AiRequestStatus.FailedValidation,
        "running" => AiRequestStatus.Running,
        "cancelled" => AiRequestStatus.Cancelled,
        "incomplete" => AiRequestStatus.Incomplete,
        _ => AiRequestStatus.Failed
    };
}
