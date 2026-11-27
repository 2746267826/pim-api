using System.Globalization;
using Pim.Core.Exceptions;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

/// <summary>
/// Guards the relationship between the URL event id and the optional
/// <c>originalEventId</c> query parameter. The latter exists only for expanded
/// occurrence ids that are derived from a stored series master and are therefore
/// not present in the events table. Accepting an arbitrary originalEventId would
/// let one request write to a different event of the same user.
/// </summary>
internal static class OutlookWriteTargetIdentity
{
    public static bool IsSeriesMasterLike(EventEntity evt)
        => evt.IsSeriesMaster
            || !string.IsNullOrEmpty(evt.RRule)
            || string.Equals(evt.OutlookEventType, "seriesMaster", StringComparison.OrdinalIgnoreCase);

    public static void Validate(
        Guid urlEventId,
        EventEntity? urlEvent,
        EventEntity? originalEvent,
        string? scope,
        string? recurrenceId)
    {
        if (urlEvent is not null)
        {
            if (originalEvent is not null && originalEvent.Id != urlEvent.Id)
            {
                throw new DomainException(
                    02009,
                    "originalEventId 与 URL 事件不一致，已拒绝写回以避免修改另一条日程。");
            }
            return;
        }

        if (originalEvent is null)
            throw new DomainException(02009, "事件不存在。");

        var normalizedScope = scope?.Trim().ToLowerInvariant();
        if (normalizedScope is not ("this" or "instance" or "series"))
        {
            throw new DomainException(
                02009,
                "originalEventId 仅用于重复日程的展开 occurrence，必须同时提供 scope=this/instance 或 scope=series。");
        }

        if (!IsSeriesMasterLike(originalEvent))
        {
            throw new DomainException(
                02009,
                "originalEventId 必须指向重复日程的系列主事件，不能用于普通日程或其他事件。");
        }

        if (string.IsNullOrWhiteSpace(recurrenceId))
        {
            throw new DomainException(
                02009,
                "使用 originalEventId 时必须提供 recurrenceId，以证明 URL occurrence 属于该系列。");
        }

        if (!TryParseRecurrenceStart(recurrenceId, out var occurrenceStart))
            throw new DomainException(02009, "RecurrenceId 格式无效。");

        if (MatchesDerivedOccurrence(originalEvent.Id, occurrenceStart, urlEventId))
            return;

        // All-day expansion derives ids from wall-clock digits while recurrenceId
        // keeps its original offset. Accept the UTC-normalized derivation too so a
        // legitimate all-day occurrence is not rejected.
        if (MatchesDerivedOccurrence(originalEvent.Id, occurrenceStart.ToUniversalTime(), urlEventId))
            return;

        throw new DomainException(
            02009,
            "originalEventId 与 URL occurrence 不匹配，已拒绝写回以避免修改另一条日程。");
    }

    private static bool MatchesDerivedOccurrence(
        Guid masterId, DateTimeOffset occurrenceStart, Guid urlEventId)
        => RecurrenceService.DeriveOccurrenceId(masterId, occurrenceStart) == urlEventId;

    private static bool TryParseRecurrenceStart(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out parsed)
            || DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out parsed);
}
