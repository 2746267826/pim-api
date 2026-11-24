using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai;

/// <summary>
/// Durable provider-send fence shared by synchronous text, vision and ASR calls. The marker is
/// committed immediately before transport, so a crash after this point cannot be mistaken for a
/// request that never reached the provider.
/// </summary>
internal static class AiProviderAttemptMarker
{
    public static async Task MarkAsync(PimDbContext db, AiRequestLogEntity row,
        CancellationToken ct = default)
    {
        var metadata = AiAsrJobService.SetProviderAttemptedMetadata(row.MetadataJson, true);
        if (db.Database.IsRelational())
        {
            var updated = await db.AiRequestLogs
                .Where(x => x.Id == row.Id && x.UserId == row.UserId && x.Status == "running")
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.MetadataJson, metadata), ct);
            if (updated != 1)
                throw new InvalidOperationException(
                    "Provider attempt marker lost its running audit row; refusing to send");
        }
        else
        {
            var tracked = db.AiRequestLogs.Local.FirstOrDefault(x => x.Id == row.Id) ?? row;
            if (tracked.Status != "running")
                throw new InvalidOperationException(
                    "Provider attempt marker lost its running audit row; refusing to send");
            tracked.MetadataJson = metadata;
            await db.SaveChangesAsync(ct);
        }
        row.MetadataJson = metadata;
    }
}
