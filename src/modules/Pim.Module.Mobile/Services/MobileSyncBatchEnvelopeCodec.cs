using System.Text.Json;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Services;

public sealed record MobileSyncBatchEnvelope(
    int SchemaVersion,
    IReadOnlyList<MobileIngestItemResult> ItemResults,
    IReadOnlyList<string> BatchErrors,
    string? PayloadSha256 = null,
    DateTimeOffset? LeaseUntilUtc = null,
    int AttemptCount = 0,
    string? FailureCode = null,
    string? LeaseId = null,
    DateTimeOffset? RetryAfterUtc = null);

public static class MobileSyncBatchEnvelopeCodec
{
    public const int CurrentSchemaVersion = 1;
    public static readonly TimeSpan DefaultProcessingLease = TimeSpan.FromMinutes(15);

    public static string Serialize(
        IReadOnlyList<MobileIngestItemResult> itemResults,
        IReadOnlyList<string> batchErrors,
        string? payloadSha256 = null,
        DateTimeOffset? leaseUntilUtc = null,
        int attemptCount = 0,
        string? failureCode = null,
        string? leaseId = null,
        DateTimeOffset? retryAfterUtc = null)
        => JsonSerializer.Serialize(new MobileSyncBatchEnvelope(
            CurrentSchemaVersion,
            itemResults,
            batchErrors,
            payloadSha256,
            leaseUntilUtc,
            attemptCount,
            failureCode,
            leaseId,
            retryAfterUtc));

    public static bool TryDeserialize(
        string? value,
        out MobileSyncBatchEnvelope envelope)
    {
        envelope = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        try
        {
            var candidate = JsonSerializer.Deserialize<MobileSyncBatchEnvelope>(value);
            if (candidate is null
                || candidate.SchemaVersion != CurrentSchemaVersion
                || candidate.ItemResults is null
                || candidate.BatchErrors is null
                || candidate.AttemptCount < 0)
                return false;

            envelope = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static MobileSyncBatchEnvelope DeserializeOrEmpty(string? value)
        => TryDeserialize(value, out var envelope)
            ? envelope
            : new MobileSyncBatchEnvelope(CurrentSchemaVersion, [], []);

    public static string? ErrorMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "{}")
            return null;

        if (!TryDeserialize(value, out var envelope))
            return value;

        var batchErrors = envelope.BatchErrors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .ToList();
        if (batchErrors.Count > 0)
            return string.Join("; ", batchErrors);

        return string.IsNullOrWhiteSpace(envelope.FailureCode)
            ? null
            : envelope.FailureCode;
    }

    public static DateTimeOffset EffectiveLeaseUntil(
        DateTimeOffset createdAt,
        string? errorJson)
        => EffectiveLeaseUntil(
            createdAt,
            DeserializeOrEmpty(errorJson));

    public static DateTimeOffset EffectiveLeaseUntil(
        DateTimeOffset createdAt,
        MobileSyncBatchEnvelope envelope)
        => envelope.LeaseUntilUtc
            ?? (envelope.LeaseId is null && envelope.AttemptCount == 0
                ? createdAt.Add(DefaultProcessingLease)
                : DateTimeOffset.MinValue);

    public static bool HasActiveLease(
        DateTimeOffset createdAt,
        string? errorJson,
        DateTimeOffset now)
    {
        var envelope = DeserializeOrEmpty(errorJson);
        return EffectiveLeaseUntil(createdAt, envelope) > now
            || envelope.RetryAfterUtc is { } retryAfter && retryAfter > now;
    }
}
