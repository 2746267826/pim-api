using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Pim.Module.PcTracker.DTOs;

namespace Pim.Module.PcTracker.Services;

public sealed record PcActivityRecordKeyResult(
    string RecordKey,
    string KeyVersion,
    string Stability,
    string SourceType,
    string SourceEventIdsJson,
    string SourceBucketIdsJson);

public sealed class PcActivityRecordKeyService
{
    public PcActivityRecordKeyResult BuildKey(PcDetailRecord record) => Build(record);

    public static PcActivityRecordKeyResult Build(PcDetailRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var sourceIds = SourceEventIds(record);
        var bucketIds = SourceBucketIds(record);
        if (sourceIds.Count > 0 && bucketIds.Count > 0)
        {
            var eventPart = string.Join('-', sourceIds);
            var bucketPart = bucketIds.Count == 1
                ? bucketIds[0]
                : HashPart(string.Join('|', bucketIds));

            return new PcActivityRecordKeyResult(
                $"pc-aw-v1:{bucketPart}:{eventPart}",
                "pc-aw-v1",
                "stable",
                "aw",
                JsonSerializer.Serialize(sourceIds),
                JsonSerializer.Serialize(bucketIds));
        }

        var fallbackPayload = string.Join(
            "\n",
            record.RecordType ?? string.Empty,
            record.DeviceId ?? string.Empty,
            record.Start ?? string.Empty,
            record.End ?? record.Start ?? string.Empty,
            record.AppName ?? string.Empty,
            record.Domain ?? string.Empty,
            record.Path ?? string.Empty,
            record.Title ?? string.Empty);

        return new PcActivityRecordKeyResult(
            $"pc-fallback-v1:{HashPart(fallbackPayload)}",
            "pc-fallback-v1",
            "low",
            "fallback",
            JsonSerializer.Serialize(sourceIds),
            JsonSerializer.Serialize(bucketIds));
    }

    public static IReadOnlyList<long> SourceEventIds(PcDetailRecord record)
    {
        var ids = record.SourceWebEventIds is { Count: > 0 }
            ? record.SourceWebEventIds
            : record.SourceWindowEventIds is { Count: > 0 }
                ? record.SourceWindowEventIds
                : [];

        return ids
            .Distinct()
            .OrderBy(id => id)
            .ToList();
    }

    public static IReadOnlyList<string> SourceBucketIds(PcDetailRecord record)
    {
        return (record.SourceBucketIds ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    private static string HashPart(string payload)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant()[..32];
    }
}
