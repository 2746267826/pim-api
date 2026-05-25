using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Module.PcTracker.DTOs;

namespace Pim.Module.PcTracker.Services;

public static class ActivityClassificationRecordKey
{
    public static string FromRecord(PcDetailRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var sourceEventIdsJson = SourceEventIdsJson(record);
        var payload = string.Join(
            "\n",
            record.RecordType ?? string.Empty,
            record.DeviceId ?? string.Empty,
            record.Start ?? string.Empty,
            record.End ?? record.Start ?? string.Empty,
            record.AppName ?? string.Empty,
            record.Domain ?? string.Empty,
            record.Path ?? string.Empty,
            record.Title ?? string.Empty,
            sourceEventIdsJson);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return $"{record.RecordType}:{hex[..32]}";
    }

    public static string SourceEventIdsJson(PcDetailRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var sourceIds = record.SourceWebEventIds is { Count: > 0 }
            ? record.SourceWebEventIds
            : record.SourceWindowEventIds is { Count: > 0 }
                ? record.SourceWindowEventIds
                : [];

        return JsonSerializer.Serialize(sourceIds.OrderBy(id => id));
    }
}
