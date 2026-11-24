using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Reconciles completed current material versions with their account preference. This closes the
/// crash window between publishing a material version and persisting its processing intent.
/// </summary>
public sealed class NoteProcessingDispatchJob(
    PimDbContext db,
    NoteMaterialUploadStore materials,
    NoteProcessingService processing,
    ILogger<NoteProcessingDispatchJob> logger)
{
    private const int PageSize = 50;

    [AutomaticRetry(Attempts = 0)]
    public async Task RunAsync(CancellationToken ct)
    {
        var owners = await db.NoteProcessingPreferences.AsNoTracking()
            .Where(x => x.AutoNoteProcessingEnabled)
            .Join(db.Users.AsNoTracking().Where(x => x.IsActive),
                preference => preference.OwnerId,
                user => user.Id,
                (preference, user) => preference.OwnerId)
            .ToListAsync(ct);

        foreach (var owner in owners)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                for (var offset = 0; ; offset += PageSize)
                {
                    var page = await materials.ListNativeNotesAsync(owner, offset, PageSize, ct);
                    foreach (var note in page.Items)
                        _ = await processing.TryAutoStartAfterCompletionAsync(
                            owner, note.CurrentVersionId, ct);
                    if (offset + page.Items.Count >= page.Total) break;
                }
            }
            catch (Exception error) when (error is IOException or DomainException or
                System.Text.Json.JsonException or InvalidDataException)
            {
                logger.LogWarning(
                    "Automatic native-note processing reconciliation failed for owner {Owner} ({ErrorType})",
                    owner, error.GetType().Name);
            }
        }
    }
}
