using System.Text.Json;
using Pim.Module.Mobile.DTOs;

namespace Pim.Module.Mobile.Services;

public sealed record MobileSyncBatchEnvelope(
    int SchemaVersion,
    IReadOnlyList<MobileIngestItemResult> ItemResults,
    IReadOnlyList<string> BatchErrors);

public static class MobileSyncBatchEnvelopeCodec
{
    public const int CurrentSchemaVersion = 1;

    public static string Serialize(
        IReadOnlyList<MobileIngestItemResult> itemResults,
        IReadOnlyList<string> batchErrors)
        => JsonSerializer.Serialize(new MobileSyncBatchEnvelope(
            CurrentSchemaVersion,
            itemResults,
            batchErrors));

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
                || candidate.BatchErrors is null)
                return false;

            envelope = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string? ErrorMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "{}")
            return null;

        if (!TryDeserialize(value, out var envelope))
            return value;

        var batchErrors = envelope.BatchErrors
            .Where(error => !string.IsNullOrWhiteSpace(error))
            .ToList();
        return batchErrors.Count == 0 ? null : string.Join("; ", batchErrors);
    }
}
