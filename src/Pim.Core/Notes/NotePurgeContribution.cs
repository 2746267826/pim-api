namespace Pim.Core.Notes;

/// <summary>
/// A domain module contributes note-owned derived rows to the native-note permanent purge.
/// Implementations remove the rows from the shared <c>PimDbContext</c> change tracker and
/// return AI call log ids that still have to be removed by the note purge transaction.
/// </summary>
public interface INotePurgeContributor
{
    Task<NotePurgeContribution> StagePurgeAsync(Guid owner, Guid noteId, CancellationToken ct);
}

public sealed record NotePurgeContribution(IReadOnlyCollection<Guid> AiLogIds);
