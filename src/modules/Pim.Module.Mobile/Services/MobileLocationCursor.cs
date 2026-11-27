using System.Globalization;
using System.Text;
using Pim.Core.Exceptions;
using Pim.Module.Mobile.Entities;

namespace Pim.Module.Mobile.Services;

internal enum MobileLocationCursorKind
{
    Encoded,
    LegacyPointId
}

internal readonly record struct MobileLocationCursorValue(
    MobileLocationCursorKind Kind,
    DateTimeOffset RecordedAtUtc,
    Guid Id);

/// <summary>
/// 原始定位点分页游标。游标保存真实排序键（recorded_at_utc, id），
/// 查询端据此使用 keyset 边界，而不是先把整个区间载入内存再 Skip/Take。
/// 仍接受旧版裸 point id 游标，避免已打开的页面在升级后失效。
/// </summary>
internal static class MobileLocationCursor
{
    private const string Prefix = "v1:";
    private const int MaxCursorLength = 512;

    public static string Encode(DateTimeOffset recordedAtUtc, Guid id)
    {
        var payload = string.Concat(
            recordedAtUtc.UtcDateTime.Ticks.ToString(CultureInfo.InvariantCulture),
            ":",
            id.ToString("N"));
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        return string.Concat(Prefix, encoded);
    }

    public static MobileLocationCursorValue Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException(6208, "分页游标不能为空。");

        var cursor = value.Trim();
        if (cursor.Length > MaxCursorLength)
            throw new DomainException(6208, "分页游标无效。");

        if (Guid.TryParse(cursor, out var legacyId))
            return new MobileLocationCursorValue(MobileLocationCursorKind.LegacyPointId, default, legacyId);

        if (!cursor.StartsWith(Prefix, StringComparison.Ordinal))
            throw new DomainException(6208, "分页游标无效。");

        var payload = cursor[Prefix.Length..];
        try
        {
            var normalized = payload.Replace('-', '+').Replace('_', '/');
            normalized += new string('=', (4 - normalized.Length % 4) % 4);
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(normalized));
            var separator = text.IndexOf(':');
            if (separator <= 0
                || !long.TryParse(text[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || !Guid.TryParseExact(text[(separator + 1)..], "N", out var id))
            {
                throw new DomainException(6208, "分页游标无效。");
            }

            return new MobileLocationCursorValue(
                MobileLocationCursorKind.Encoded,
                new DateTimeOffset(ticks, TimeSpan.Zero),
                id);
        }
        catch (Exception error) when (error is FormatException or ArgumentOutOfRangeException)
        {
            throw new DomainException(6208, "分页游标无效。");
        }
    }

    /// <summary>
    /// 把游标边界下推到查询，避免服务层先装载整个区间再 Skip/Take。
    /// 排序键必须与调用方的 OrderBy(recordedAtUtc).ThenBy(id) 完全一致。
    /// </summary>
    internal static IQueryable<MobileLocationPointEntity> Apply(
        IQueryable<MobileLocationPointEntity> query,
        MobileLocationCursorValue cursor)
        => cursor.Kind == MobileLocationCursorKind.Encoded
            ? query.Where(point => point.RecordedAtUtc > cursor.RecordedAtUtc
                || (point.RecordedAtUtc == cursor.RecordedAtUtc && point.Id.CompareTo(cursor.Id) > 0))
            : query;
}
