using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Data;
using Pim.Module.NoteTaskSuggestions.Entities;

namespace Pim.Module.NoteTaskSuggestions.Services;

internal sealed class NoteTaskSuggestionPurgeContributor(PimDbContext db) : INotePurgeContributor
{
    public async Task<NotePurgeContribution> StagePurgeAsync(Guid owner, Guid noteId,
        CancellationToken ct)
    {
        var extractions = await db.Set<NoteTaskSuggestionExtractionEntity>()
            .Where(row => row.UserId == owner && row.NoteId == noteId)
            .ToListAsync(ct);
        // A queued row has not called a provider and a concurrent claim CAS will simply lose;
        // only a running extraction may be in flight, so only that state blocks the purge.
        if (extractions.Any(row => row.Status == NoteTaskSuggestionExtractionStatuses.Running))
            throw new DomainException(40901,
                "笔记的任务建议仍在生成，请等待任务结束后重试永久删除");

        var suggestions = await db.Set<NoteTaskSuggestionEntity>()
            .Where(row => row.UserId == owner && row.NoteId == noteId)
            .ToListAsync(ct);
        var logIds = extractions
            .Where(row => row.AiCallLogId.HasValue)
            .Select(row => row.AiCallLogId!.Value)
            .ToHashSet();
        var artifactVersionIds = extractions.Select(row => row.ArtifactVersionId)
            .Distinct().Select(id => id.ToString("D")).ToArray();
        if (artifactVersionIds.Length > 0)
        {
            var logs = await db.AiRequestLogs.AsNoTracking()
                .Where(log => log.UserId == owner
                    && log.SourceObjectType == "note-derived-version"
                    && artifactVersionIds.Contains(log.SourceObjectId))
                .Select(log => log.Id)
                .ToListAsync(ct);
            logIds.UnionWith(logs);
        }

        db.Set<NoteTaskSuggestionEntity>().RemoveRange(suggestions);
        db.Set<NoteTaskSuggestionExtractionEntity>().RemoveRange(extractions);
        return new NotePurgeContribution(logIds);
    }
}
