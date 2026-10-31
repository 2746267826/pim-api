using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Text.Json;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Secrets;
using Xunit;

namespace Pim.UnitTests.Notes;

/// <summary>
/// Real relational concurrency proof for #516 phase 1. Requires a disposable synthetic
/// PostgreSQL cluster via PIM_ASR516_PG; never points at production or pim_test.
/// </summary>
public sealed class NoteAsrRoutingConcurrencyTests
{
    private static readonly string StorageRoot = Path.Combine(Path.GetTempPath(), "pim-asr516-tests");
    private static string? ConnectionString => Environment.GetEnvironmentVariable("PIM_ASR516_PG");

    [SkippableFact]
    public async Task Two_nodes_claiming_same_attempt_produce_exactly_one_active_lease()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var nodeB = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, nodeB);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dbA = NewDb(owner);
        await using var dbB = NewDb(owner);
        var serviceA = NewService(dbA, owner);
        var serviceB = NewService(dbB, owner);
        var claimA = Task.Run(async () => { await gate.Task; return await serviceA.ClaimAsync(
            new ClaimNoteAsrTaskRequest(nodeA, "claim-a"), CancellationToken.None); });
        var claimB = Task.Run(async () => { await gate.Task; return await serviceB.ClaimAsync(
            new ClaimNoteAsrTaskRequest(nodeB, "claim-b"), CancellationToken.None); });
        gate.SetResult();
        var results = await Task.WhenAll(claimA, claimB);

        Assert.Equal(1, results.Count(x => x.Status == "claimed"));
        Assert.Equal(1, results.Count(x => x.Status == "contended"));
        await using var check = NewDb(owner);
        Assert.Equal(1, await check.NoteAsrLeases.CountAsync(x => x.OwnerId == owner && x.Status == "Active"));
        Assert.Equal(1, await check.NoteAsrLeases.CountAsync(x => x.OwnerId == owner && x.AttemptId == attemptId));
        Assert.Equal(1, await check.NoteAsrAckReceipts.CountAsync(x => x.OwnerId == owner && x.Kind == "claim"));
        Assert.Equal(1L, await check.NoteAsrAttempts.Where(x => x.Id == attemptId).Select(x => x.FencingToken).SingleAsync());
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Replaying_same_idempotency_key_returns_same_lease_without_extra_fence()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dbA = NewDb(owner);
        await using var dbB = NewDb(owner);
        var claimA = Task.Run(async () => { await gate.Task; return await NewService(dbA, owner).ClaimAsync(
            new ClaimNoteAsrTaskRequest(nodeA, "same-key"), CancellationToken.None); });
        var claimB = Task.Run(async () => { await gate.Task; return await NewService(dbB, owner).ClaimAsync(
            new ClaimNoteAsrTaskRequest(nodeA, "same-key"), CancellationToken.None); });
        gate.SetResult();
        var results = await Task.WhenAll(claimA, claimB);

        Assert.All(results, x => Assert.Equal("claimed", x.Status));
        Assert.Single(results.Select(x => x.Lease!.LeaseId).Distinct());
        Assert.All(results, x => Assert.Equal(1L, x.Lease!.FencingToken));
        await using var check = NewDb(owner);
        Assert.Equal(1, await check.NoteAsrLeases.CountAsync(x => x.OwnerId == owner));
        Assert.Equal(1, await check.NoteAsrAckReceipts.CountAsync(x => x.OwnerId == owner && x.Kind == "claim"));
        Assert.Equal(1L, await check.NoteAsrAttempts.Where(x => x.Id == attemptId).Select(x => x.FencingToken).SingleAsync());
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Stale_ack_after_newer_fence_cannot_mutate_attempt()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(new ClaimNoteAsrTaskRequest(nodeA, "stale-claim"), CancellationToken.None);
        await using (var db = NewDb(owner))
        {
            var attempt = await db.NoteAsrAttempts.SingleAsync(x => x.Id == attemptId);
            attempt.FencingToken = 99; attempt.State = "Leased";
            var oldLease = await db.NoteAsrLeases.SingleAsync(x => x.Id == claim.LeaseId);
            oldLease.Status = "Expired"; oldLease.EndedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        await using var staleDb = NewDb(owner);
        var error = await Assert.ThrowsAsync<DomainException>(() => NewService(staleDb, owner).AcknowledgeAsync(attemptId,
            new NoteAsrAckRequest(claim.LeaseId!.Value, claim.Lease.LeaseToken, claim.Lease.FencingToken,
                "complete", "Succeeded", 0, "provider-1", null, null), CancellationToken.None));
        Assert.Equal(40901, error.ErrorCode);
        await using var check = NewDb(owner);
        var row = await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal(99L, row.FencingToken);
        Assert.Equal(0, row.CompletedSegments);
        Assert.Equal("Leased", row.State);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Complete_requires_all_segments_and_is_idempotent_without_double_counting()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(new ClaimNoteAsrTaskRequest(nodeA, "ack-claim"), CancellationToken.None);
        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner);
            for (var i = 0; i < 3; i++)
                await service.AcknowledgeAsync(attemptId, SegmentAck(claim, i, "Succeeded"), CancellationToken.None);
        }
        var ack = new NoteAsrAckRequest(claim.LeaseId!.Value, claim.Lease.LeaseToken, claim.Lease.FencingToken,
            "complete", "Succeeded", 0, "provider-1", null, null);
        await using (var db = NewDb(owner))
        {
            var first = await NewService(db, owner).AcknowledgeAsync(attemptId, ack, CancellationToken.None);
            Assert.Equal("accepted", first.Status);
        }
        await using (var db = NewDb(owner))
            await db.NoteAsrLeases.Where(x => x.Id == claim.LeaseId).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        await using (var db = NewDb(owner))
        {
            var second = await NewService(db, owner).AcknowledgeAsync(attemptId, ack, CancellationToken.None);
            Assert.True(second.Duplicate);
        }
        await using var check = NewDb(owner);
        var row = await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Succeeded", row.State);
        Assert.Equal(3, row.CompletedSegments);
        Assert.Equal(3, await check.NoteAsrAckReceipts.CountAsync(x => x.OwnerId == owner && x.AttemptId == attemptId && x.Kind == "segment"));
        Assert.Equal(1, await check.NoteAsrAckReceipts.CountAsync(x => x.OwnerId == owner && x.AttemptId == attemptId && x.Kind == "complete"));
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Complete_with_missing_segment_is_rejected_and_partial_results_remain()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(new ClaimNoteAsrTaskRequest(nodeA, "missing-claim"), CancellationToken.None);
        await using var db2 = NewDb(owner);
        var service = NewService(db2, owner);
        await service.AcknowledgeAsync(attemptId, SegmentAck(claim, 0, "Succeeded"), CancellationToken.None);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.AcknowledgeAsync(attemptId,
            TerminalAck(claim, "complete", "Succeeded"), CancellationToken.None));
        Assert.Equal(40901, error.ErrorCode);
        Assert.Contains("1", error.Message);
        var row = await db2.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Leased", row.State);
        Assert.Equal(1, row.CompletedSegments);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Complete_with_failed_segment_is_rejected_and_keeps_partial_results()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(new ClaimNoteAsrTaskRequest(nodeA, "failed-segment"), CancellationToken.None);
        await using var work = NewDb(owner);
        var service = NewService(work, owner);
        await service.AcknowledgeAsync(attemptId, SegmentAck(claim, 0, "Succeeded"), CancellationToken.None);
        await service.AcknowledgeAsync(attemptId, SegmentAck(claim, 1, "Failed"), CancellationToken.None);
        await service.AcknowledgeAsync(attemptId, SegmentAck(claim, 2, "Succeeded"), CancellationToken.None);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.AcknowledgeAsync(attemptId,
            TerminalAck(claim, "complete", "Succeeded"), CancellationToken.None));
        Assert.Equal(40901, error.ErrorCode);
        var row = await work.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Leased", row.State);
        Assert.Equal(2, row.CompletedSegments);
        Assert.Equal(1, row.FailedSegments);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Complete_after_lease_expiry_is_rejected_without_terminal_receipt()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeA, "expired-terminal"), CancellationToken.None);
        await using var work = NewDb(owner);
        var service = NewService(work, owner);
        for (var i = 0; i < 3; i++)
            await service.AcknowledgeAsync(attemptId, SegmentAck(claim, i, "Succeeded"), CancellationToken.None);
        await work.NoteAsrLeases.Where(x => x.Id == claim.LeaseId).ExecuteUpdateAsync(setters => setters
            .SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));

        var error = await Assert.ThrowsAsync<DomainException>(() => service.AcknowledgeAsync(attemptId,
            TerminalAck(claim, "complete", "Succeeded"), CancellationToken.None));
        Assert.Equal(40901, error.ErrorCode);
        var row = await work.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Leased", row.State);
        Assert.Equal(3, row.CompletedSegments);
        Assert.Equal(0, await work.NoteAsrAckReceipts.CountAsync(x => x.OwnerId == owner &&
            x.AttemptId == attemptId && x.Kind == "complete"));
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Exact_terminal_replay_with_wrong_token_is_rejected_before_duplicate_path()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(new ClaimNoteAsrTaskRequest(nodeA, "identity-first"), CancellationToken.None);
        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner);
            for (var i = 0; i < 3; i++)
                await service.AcknowledgeAsync(attemptId, SegmentAck(claim, i, "Succeeded"), CancellationToken.None);
            var terminal = TerminalAck(claim, "complete", "Succeeded");
            Assert.Equal("accepted", (await service.AcknowledgeAsync(attemptId, terminal, CancellationToken.None)).Status);
            var forged = terminal with { LeaseToken = Guid.NewGuid() };
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                service.AcknowledgeAsync(attemptId, forged, CancellationToken.None));
            Assert.Equal(40901, error.ErrorCode);
        }
        await using var check = NewDb(owner);
        var row = await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Succeeded", row.State);
        Assert.Equal(3, row.CompletedSegments);
        Assert.Equal(1, await check.NoteAsrAckReceipts.CountAsync(x => x.OwnerId == owner &&
            x.AttemptId == attemptId && x.Kind == "complete"));
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Concurrent_identical_terminal_acks_are_idempotent()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(new ClaimNoteAsrTaskRequest(nodeA, "terminal-claim"), CancellationToken.None);
        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner);
            for (var i = 0; i < 3; i++)
                await service.AcknowledgeAsync(attemptId, SegmentAck(claim, i, "Succeeded"), CancellationToken.None);
        }
        var ack = TerminalAck(claim, "complete", "Succeeded");
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dbA = NewDb(owner);
        await using var dbB = NewDb(owner);
        var one = Task.Run(async () => { await gate.Task; return await NewService(dbA, owner).AcknowledgeAsync(attemptId, ack, CancellationToken.None); });
        var two = Task.Run(async () => { await gate.Task; return await NewService(dbB, owner).AcknowledgeAsync(attemptId, ack, CancellationToken.None); });
        gate.SetResult();
        var results = await Task.WhenAll(one, two);
        Assert.Equal(1, results.Count(x => x.Status == "accepted"));
        Assert.Equal(1, results.Count(x => x.Duplicate));
        await using var check = NewDb(owner);
        var row = await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Succeeded", row.State);
        Assert.Equal(3, row.CompletedSegments);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Renew_and_withdraw_race_cannot_resurrect_lease_or_attempt()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var seed = NewDb(owner))
            claim = await NewService(seed, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeA, "renew-withdraw"), CancellationToken.None);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var renewDb = NewDb(owner);
        await using var withdrawDb = NewDb(owner);
        var renew = Task.Run(async () =>
        {
            await gate.Task;
            return await Capture(() => NewService(renewDb, owner).RenewLeaseAsync(claim.LeaseId!.Value,
                new NoteAsrLeaseHeartbeatRequest(claim.LeaseId.Value, claim.Lease.LeaseToken,
                    claim.Lease.FencingToken), CancellationToken.None));
        });
        var withdraw = Task.Run(async () =>
        {
            await gate.Task;
            return await Capture(() => NewService(withdrawDb, owner).WithdrawNodeAsync(nodeA, CancellationToken.None));
        });
        gate.SetResult();
        var outcomes = await Task.WhenAll(renew, withdraw);
        Assert.Null(outcomes[1]);
        Assert.True(outcomes[0] is null or DomainException { ErrorCode: 40901 });

        await using var check = NewDb(owner);
        Assert.Equal("Expired", await check.NoteAsrLeases.Where(x => x.Id == claim.LeaseId)
            .Select(x => x.Status).SingleAsync());
        var attempt = await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal("Unknown", attempt.State);
        Assert.Equal("node_withdrawn", attempt.LastErrorCode);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Concurrent_complete_and_withdraw_never_overwrite_succeeded_attempt()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeA, "complete-withdraw"), CancellationToken.None);
        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner);
            for (var i = 0; i < 3; i++)
                await service.AcknowledgeAsync(attemptId, SegmentAck(claim, i, "Succeeded"), CancellationToken.None);
        }

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var completeDb = NewDb(owner);
        await using var withdrawDb = NewDb(owner);
        var terminal = TerminalAck(claim, "complete", "Succeeded");
        var complete = Task.Run(async () =>
        {
            await gate.Task;
            return await Capture(() => NewService(completeDb, owner)
                .AcknowledgeAsync(attemptId, terminal, CancellationToken.None));
        });
        var withdraw = Task.Run(async () =>
        {
            await gate.Task;
            return await Capture(() => NewService(withdrawDb, owner).WithdrawNodeAsync(nodeA, CancellationToken.None));
        });
        gate.SetResult();
        var outcomes = await Task.WhenAll(complete, withdraw);
        Assert.Null(outcomes[1]);
        Assert.True(outcomes[0] is null or DomainException { ErrorCode: 40901 });

        await using var check = NewDb(owner);
        var attempt = await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        var lease = await check.NoteAsrLeases.AsNoTracking().SingleAsync(x => x.Id == claim.LeaseId);
        if (outcomes[0] is null)
        {
            Assert.Equal("Succeeded", attempt.State);
            Assert.Equal("Completed", lease.Status);
        }
        else
        {
            Assert.Equal("Unknown", attempt.State);
            Assert.Equal("Expired", lease.Status);
        }
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Conflicting_payload_for_same_segment_is_rejected_not_treated_as_duplicate()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(new ClaimNoteAsrTaskRequest(nodeA, "conflict-claim"), CancellationToken.None);
        await using var db2 = NewDb(owner);
        var service = NewService(db2, owner);
        await service.AcknowledgeAsync(attemptId, SegmentAck(claim, 0, "Succeeded"), CancellationToken.None);
        var error = await Assert.ThrowsAsync<DomainException>(() => service.AcknowledgeAsync(attemptId,
            SegmentAck(claim, 0, "Failed"), CancellationToken.None));
        Assert.Equal(40901, error.ErrorCode);
        var row = await db2.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
        Assert.Equal(1, row.CompletedSegments);
        Assert.Equal(0, row.FailedSegments);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Claim_expires_this_nodes_stale_active_lease_before_claiming_new_attempt()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeA = Guid.NewGuid();
        var oldAttemptId = await SeedAsync(owner, nodeA, Guid.NewGuid());
        NoteAsrClaimDto oldClaim;
        await using (var db = NewDb(owner))
            oldClaim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeA, "stale-node-lease"), CancellationToken.None);

        Guid newAttemptId;
        await using (var db = NewDb(owner))
        {
            var oldAttempt = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == oldAttemptId);
            await db.NoteAsrLeases.Where(x => x.Id == oldClaim.LeaseId).ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            var taskId = Guid.NewGuid();
            newAttemptId = Guid.NewGuid();
            db.NoteAsrAttempts.Add(new NoteAsrAttemptEntity
            {
                Id = newAttemptId, OwnerId = owner, TaskId = taskId, NoteId = oldAttempt.NoteId,
                VersionId = oldAttempt.VersionId, InputSha256 = oldAttempt.InputSha256,
                Route = NoteAsrRoute.Local, State = "Queued", Language = oldAttempt.Language,
                ExpectedSegments = oldAttempt.ExpectedSegments, LocalModelId = oldAttempt.LocalModelId,
                LocalModelRevision = oldAttempt.LocalModelRevision, ConfigRevision = oldAttempt.ConfigRevision,
                ConsentVersion = oldAttempt.ConsentVersion, CreatedAt = oldAttempt.CreatedAt.AddSeconds(1)
            });
            await db.SaveChangesAsync();
            await WriteTaskStateAsync(owner, taskId, oldAttempt.VersionId, oldAttempt.NoteId, oldAttempt.InputSha256);
        }

        NoteAsrClaimDto next;
        await using (var db = NewDb(owner))
            next = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeA, "new-independent-attempt"), CancellationToken.None);
        Assert.Equal("claimed", next.Status);
        Assert.Equal(newAttemptId, next.Lease!.AttemptId);
        Assert.Equal(1L, next.Lease.FencingToken);

        await using var check = NewDb(owner);
        var oldRow = await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == oldAttemptId);
        Assert.Equal("Unknown", oldRow.State);
        Assert.Equal("lease_expired", oldRow.LastErrorCode);
        Assert.Equal("Expired", await check.NoteAsrLeases.Where(x => x.Id == oldClaim.LeaseId)
            .Select(x => x.Status).SingleAsync());
        Assert.Equal("Leased", (await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == newAttemptId)).State);
        Assert.Equal("Active", await check.NoteAsrLeases.Where(x => x.AttemptId == newAttemptId)
            .Select(x => x.Status).SingleAsync());
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Concurrent_route_config_update_has_exactly_one_winner()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        await using (var seed = NewDb(owner))
        {
            await seed.Database.EnsureCreatedAsync();
            await seed.Users.AddAsync(NewUser(owner));
            await seed.SaveChangesAsync();
        }
        NoteAsrRouteConfigDto initial;
        await using (var db = NewDb(owner))
            initial = await NewService(db, owner).UpdateConfigAsync(new UpdateNoteAsrRouteConfigRequest(
                Guid.Empty, NoteAsrRoute.Local, "local-model", "r1", null, "consent-1"), CancellationToken.None);

        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var dbA = NewDb(owner);
        await using var dbB = NewDb(owner);
        var update = new UpdateNoteAsrRouteConfigRequest(initial.Revision, NoteAsrRoute.Local, "local-model", "r2", null, "consent-2");
        var one = Task.Run(async () => { await gate.Task; return await NewService(dbA, owner).UpdateConfigAsync(update, CancellationToken.None); });
        var two = Task.Run(async () => { await gate.Task; return await NewService(dbB, owner).UpdateConfigAsync(update, CancellationToken.None); });
        gate.SetResult();
        var outcomes = await Task.WhenAll(Capture(() => one), Capture(() => two));
        Assert.Equal(1, outcomes.Count(x => x is not null));
        Assert.Equal(1, outcomes.Count(x => x is DomainException { ErrorCode: 40901 }));
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Node_owned_by_another_account_cannot_claim()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var other = Guid.NewGuid();
        var node = Guid.NewGuid();
        await SeedAsync(owner, node, Guid.NewGuid());
        await using var db = NewDb(other);
        await db.Database.EnsureCreatedAsync();
        await db.Users.AddAsync(NewUser(other));
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<DomainException>(() => NewService(db, other)
            .ClaimAsync(new ClaimNoteAsrTaskRequest(node, "cross-owner"), CancellationToken.None));
        Assert.Equal(40401, error.ErrorCode);
        await CleanupAsync(owner);
        await CleanupAsync(other);
    }

    [SkippableFact]
    public async Task Segment_content_sha_is_hash_of_returned_slice_not_whole_source()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        await CleanupAsync(owner);
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot });
        var materials = new NoteMaterialUploadStore(options);
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 1_000);
        await NoteDerivedFixture.UploadAsync(materials, owner, material);
        var taskId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var sourceSha = NoteDerivedFixture.Digest(material.Content[5]);
        var inputSha = NoteMaterialManifestValidator.Hash(material.Request.InputJson);
        await using (var db = NewDb(owner))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Users.AddAsync(NewUser(owner));
            await db.NoteAsrNodes.AddAsync(Node(owner, nodeId, "slice-node", DateTimeOffset.UtcNow));
            await db.NoteAsrAttempts.AddAsync(new NoteAsrAttemptEntity
            {
                Id = attemptId, OwnerId = owner, TaskId = taskId, NoteId = material.NoteId,
                VersionId = material.Request.VersionId, InputSha256 = inputSha, Route = NoteAsrRoute.Local,
                State = "Queued", Language = "zh", ExpectedSegments = 1, LocalModelId = "local-model",
                LocalModelRevision = "r1", ConfigRevision = Guid.NewGuid(), ConsentVersion = "consent-1"
            });
            await db.SaveChangesAsync();
        }
        await WriteTaskStateAsync(new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, material.Request.VersionId,
                new AiAsrDestinationConsent(Guid.NewGuid(), Guid.NewGuid(), "synthetic", "local", "1", true), "zh"),
            Fingerprint = new string('e', 64), NoteId = material.NoteId, InputSha256 = inputSha,
            ManifestSha256 = NoteMaterialManifestValidator.Hash(material.Request.ManifestJson),
            Steps =
            [
                new NoteAsrStep
                {
                    Index = 0, RecordingId = material.RecordingId, RecordingSegment = 1, ChunkIndex = 1,
                    FileIndex = 5, Path = $"recordings/{material.RecordingId:D}/s0001-c000001.wav",
                    Sha256 = sourceSha, SourceOffset = 0, RecordingOffset = 0, TimelineOffset = 0, Samples = 500,
                    Attempts = [new()]
                }
            ]
        });

        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeId, "slice-hash"), CancellationToken.None);
        NoteAsrSegmentContent content;
        await using (var db = NewDb(owner))
            content = await NewService(db, owner).ReadSegmentInputAsync(claim.LeaseId!.Value, 0,
                claim.Lease!.LeaseToken, claim.Lease.FencingToken, CancellationToken.None);

        Assert.Equal(44 + 500 * 2, content.Bytes.Length);
        Assert.Equal(NoteDerivedFixture.Digest(content.Bytes), content.Sha256);
        Assert.Equal(sourceSha, content.SourceSha256);
        Assert.NotEqual(sourceSha, content.Sha256);
        Assert.Equal(owner, claim.Execution!.OwnerId);
        Assert.Equal(attemptId, content.AttemptId);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Stop_request_is_visible_on_renew_and_failure_ack_closes_the_lease()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeId, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeId, "stop-claim"), CancellationToken.None);
        await using (var db = NewDb(owner))
            await NewService(db, owner).StopAttemptAsync(attemptId, CancellationToken.None);
        NoteAsrLeaseDto renewed;
        await using (var db = NewDb(owner))
            renewed = await NewService(db, owner).RenewLeaseAsync(claim.LeaseId!.Value,
                new NoteAsrLeaseHeartbeatRequest(claim.LeaseId.Value, claim.Lease!.LeaseToken,
                    claim.Lease.FencingToken), CancellationToken.None);
        Assert.True(renewed.StopRequested);

        await using (var db = NewDb(owner))
        {
            var ack = await NewService(db, owner).AcknowledgeAsync(attemptId,
                TerminalAck(claim, "fail", "Failed") with
                {
                    ErrorCode = "stop_requested",
                    ErrorMessage = "worker confirmed stop"
                }, CancellationToken.None);
            Assert.Equal("accepted", ack.Status);
        }
        await using var check = NewDb(owner);
        Assert.Equal("Failed", (await check.NoteAsrAttempts.AsNoTracking()
            .SingleAsync(x => x.Id == attemptId)).State);
        Assert.Equal("Failed", (await check.NoteAsrLeases.AsNoTracking()
            .SingleAsync(x => x.Id == claim.LeaseId)).Status);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Exact_duplicate_ack_repairs_a_missing_task_projection()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var attemptId = await SeedAsync(owner, nodeId, Guid.NewGuid());
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeId, "projection-claim"), CancellationToken.None);
        var ack = SegmentAck(claim, 0, "Succeeded");
        await using (var db = NewDb(owner))
            await NewService(db, owner).AcknowledgeAsync(attemptId, ack, CancellationToken.None);
        await using (var db = NewDb(owner))
        {
            var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == attemptId);
            var tasks = new NoteAsrTaskStore(Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot }));
            await tasks.UpdateAsync(owner, attempt.TaskId, state =>
            {
                state.Steps[0].Current.Status = "Queued";
                state.Steps[0].Current.EvidenceId = null;
            }, CancellationToken.None);
            var repaired = await NewService(db, owner).AcknowledgeAsync(attemptId, ack, CancellationToken.None);
            Assert.True(repaired.Duplicate);
            var persisted = await tasks.ReadAsync(owner, attempt.TaskId, CancellationToken.None);
            Assert.Equal("Succeeded", persisted.Steps[0].Current.Status);
            Assert.NotNull(persisted.Steps[0].Current.EvidenceId);
        }
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Get_repairs_committed_ack_after_projection_failure_without_replaying_ack()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        await CleanupAsync(owner);
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot });
        var materials = new NoteMaterialUploadStore(options);
        var tasks = new NoteAsrTaskStore(options);
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 1_000);
        await NoteDerivedFixture.UploadAsync(materials, owner, material);
        var taskId = Guid.NewGuid();
        var inputSha = NoteMaterialManifestValidator.Hash(material.Request.InputJson);
        var sourceSha = NoteDerivedFixture.Digest(material.Content[5]);
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = NewDb(owner))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Users.AddAsync(NewUser(owner));
            await db.NoteAsrNodes.AddAsync(Node(owner, nodeId, "repair-node", now));
            await db.NoteAsrAttempts.AddAsync(new NoteAsrAttemptEntity
            {
                Id = attemptId, OwnerId = owner, TaskId = taskId, NoteId = material.NoteId,
                VersionId = material.Request.VersionId, InputSha256 = inputSha, Route = NoteAsrRoute.Local,
                State = "Queued", Language = "zh", ExpectedSegments = 1, LocalModelId = "local-model",
                LocalModelRevision = "r1", ConfigRevision = Guid.NewGuid(), ConsentVersion = "consent-1",
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }
        await WriteTaskStateAsync(new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, material.Request.VersionId,
                new AiAsrDestinationConsent(Guid.NewGuid(), Guid.NewGuid(), "synthetic", "local", "1", true), "zh"),
            Fingerprint = new string('f', 64), NoteId = material.NoteId, InputSha256 = inputSha,
            ManifestSha256 = NoteMaterialManifestValidator.Hash(material.Request.ManifestJson),
            AttemptId = attemptId, Route = NoteAsrRoute.Local, ExecutionMode = NoteAsrExecutionMode.Realtime,
            Steps =
            [
                new NoteAsrStep
                {
                    Index = 0, RecordingId = material.RecordingId, RecordingSegment = 1, ChunkIndex = 1,
                    FileIndex = 5, Path = $"recordings/{material.RecordingId:D}/s0001-c000001.wav",
                    Sha256 = sourceSha, SourceOffset = 0, RecordingOffset = 0, TimelineOffset = 0, Samples = 500,
                    Attempts = [new()]
                }
            ]
        });
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeId, "repair-claim"), CancellationToken.None);
        var ack = SegmentAck(claim, 0, "Succeeded");
        await using (var db = NewDb(owner))
            await NewService(db, owner).AcknowledgeAsync(attemptId, ack, CancellationToken.None);

        // Simulate the durable ACK succeeding and only the later writable JSON projection being lost.
        WriteRawTask(taskId, new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, material.Request.VersionId,
                new AiAsrDestinationConsent(Guid.NewGuid(), Guid.NewGuid(), "synthetic", "local", "1", true), "zh"),
            Fingerprint = new string('f', 64), NoteId = material.NoteId, InputSha256 = inputSha,
            ManifestSha256 = NoteMaterialManifestValidator.Hash(material.Request.ManifestJson),
            AttemptId = attemptId, Route = NoteAsrRoute.Local, ExecutionMode = NoteAsrExecutionMode.Realtime,
            Steps =
            [
                new NoteAsrStep
                {
                    Index = 0, RecordingId = material.RecordingId, RecordingSegment = 1, ChunkIndex = 1,
                    FileIndex = 5, Path = $"recordings/{material.RecordingId:D}/s0001-c000001.wav",
                    Sha256 = sourceSha, SourceOffset = 0, RecordingOffset = 0, TimelineOffset = 0, Samples = 500,
                    Attempts = [new()]
                }
            ]
        });
        await using (var db = NewDb(owner))
        {
            var service = new NoteAsrService(tasks, materials, db, new FixedUser(owner),
                new AiAsrPresetRegistry(Array.Empty<IAiAsrPreset>()), new ConfigurationBuilder().Build(),
                new PassthroughSecrets(), NewService(db, owner));
            var repaired = await service.GetAsync(taskId, CancellationToken.None);
            Assert.Equal(1, repaired.CompletedSegments);
            await using var check = NewDb(owner);
            var receipt = await check.NoteAsrAckReceipts.AsNoTracking()
                .SingleAsync(x => x.OwnerId == owner && x.AttemptId == attemptId && x.Kind == "segment");
            var evidence = await check.NoteAsrSegmentEvidence.AsNoTracking()
                .SingleAsync(x => x.OwnerId == owner && x.AttemptId == attemptId);
            var view = await service.SegmentsAsync(taskId, 0, 20, CancellationToken.None);
            var segment = Assert.Single(view.Items);
            Assert.Equal("Succeeded", segment.Status);
            Assert.Equal("synthetic transcript 0", segment.Transcript);
            Assert.Equal(receipt.ResultText, segment.Transcript);
            Assert.Equal(evidence.Id, segment.EvidenceId);
            Assert.Equal(1, await check.NoteAsrAckReceipts.CountAsync(x => x.OwnerId == owner &&
                x.AttemptId == attemptId && x.Kind == "segment"));
            Assert.Equal(1, await check.NoteAsrSegmentEvidence.CountAsync(x => x.OwnerId == owner &&
                x.AttemptId == attemptId));
            Assert.Equal(0, await check.AiRequestLogs.CountAsync(x => x.UserId == owner));
        }
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Public_attempt_creation_binds_existing_task_version_count_and_language()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot });
        var materials = new NoteMaterialUploadStore(options);
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 1_000);
        await NoteDerivedFixture.UploadAsync(materials, owner, material);
        var taskId = Guid.NewGuid();
        var inputSha = NoteMaterialManifestValidator.Hash(material.Request.InputJson);
        await using (var db = NewDb(owner))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Users.AddAsync(NewUser(owner));
            await db.SaveChangesAsync();
        }
        var config = await UpdateLocalConfigAsync(owner, "local-model", "r1");
        await WriteTaskStateAsync(new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, material.Request.VersionId,
                new AiAsrDestinationConsent(Guid.NewGuid(), Guid.NewGuid(), "synthetic", "local", "1", true), "zh"),
            Fingerprint = new string('f', 64), NoteId = material.NoteId, InputSha256 = inputSha,
            ManifestSha256 = NoteMaterialManifestValidator.Hash(material.Request.ManifestJson),
            Steps =
            [
                new NoteAsrStep
                {
                    Index = 0, RecordingId = material.RecordingId, RecordingSegment = 1, ChunkIndex = 1,
                    FileIndex = 5, Path = $"recordings/{material.RecordingId:D}/s0001-c000001.wav",
                    Sha256 = NoteDerivedFixture.Digest(material.Content[5]), SourceOffset = 0,
                    RecordingOffset = 0, TimelineOffset = 0, Samples = 500
                }
            ]
        });
        var consent = new NoteAsrRouteConsent(NoteAsrRoute.Local, config.Revision, config.ConsentVersion, true);
        await using (var db = NewDb(owner))
        {
            var service = NewService(db, owner);
            await Assert.ThrowsAsync<DomainException>(() => service.CreateAttemptAsync(
                Guid.NewGuid(), material.Request.VersionId, 1, "zh", consent, CancellationToken.None));
            await Assert.ThrowsAsync<DomainException>(() => service.CreateAttemptAsync(
                taskId, material.Request.VersionId, 2, "zh", consent, CancellationToken.None));
            await Assert.ThrowsAsync<DomainException>(() => service.CreateAttemptAsync(
                taskId, material.Request.VersionId, 1, "en", consent, CancellationToken.None));
            var created = await service.CreateAttemptAsync(
                taskId, material.Request.VersionId, 1, "zh", consent, CancellationToken.None);
            Assert.Equal(material.NoteId, created.NoteId);
            Assert.Equal(inputSha, created.InputSha256);
            Assert.Equal(NoteAsrRoute.Local, created.Route);
        }
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Local_task_override_needs_no_cloud_consent_profile_or_runtime_and_cloud_consent_is_rejected()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot });
        var materials = new NoteMaterialUploadStore(options);
        var tasks = new NoteAsrTaskStore(options);
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 1_000);
        await NoteDerivedFixture.UploadAsync(materials, owner, material);
        await using (var db = NewDb(owner))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Users.AddAsync(NewUser(owner));
            await db.SaveChangesAsync();
        }
        // Give the account an invalid cloud default and no profile/runtime/key at all: an explicit
        // local override must still work from local model configuration alone.
        var configRevision = Guid.NewGuid();
        await using (var db = NewDb(owner))
        {
            db.NoteAsrRouteConfigs.Add(new NoteAsrRouteConfigEntity
            {
                OwnerId = owner, Revision = configRevision, DefaultRoute = NoteAsrRoute.CloudAsr,
                LocalModelId = "local-model", LocalModelRevision = "r1",
                CloudModelId = Guid.NewGuid(), ConsentVersion = "consent-local"
            });
            await db.SaveChangesAsync();
        }
        var taskId = Guid.NewGuid();
        var request = new StartNoteAsrRequest(taskId, owner, material.Request.VersionId, null, "zh",
            new NoteAsrRouteConsent(NoteAsrRoute.Local, configRevision, "consent-local", true));
        NoteAsrTaskDto started;
        await using (var db = NewDb(owner))
        {
            var service = new NoteAsrService(tasks, materials, db, new FixedUser(owner),
                new AiAsrPresetRegistry(Array.Empty<IAiAsrPreset>()), new ConfigurationBuilder().Build(),
                new PassthroughSecrets(), NewService(db, owner));
            started = await service.StartAsync(request, CancellationToken.None);
            Assert.Equal(NoteAsrRoute.Local, started.Route);
            Assert.Null(started.Destination);
            Assert.NotNull(started.AttemptId);
            var read = await service.GetAsync(taskId, CancellationToken.None);
            Assert.Null(read.Destination);
            Assert.Equal(started.AttemptId, read.AttemptId);
            Assert.Equal("Queued", read.Phase);
            var state = await tasks.ReadAsync(owner, taskId, CancellationToken.None);
            var attempt = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == state.AttemptId);
            Assert.Equal("local-model", attempt.LocalModelId);
            Assert.Null(attempt.ModelId);
            Assert.Equal(0, await db.AiModelRuntime.CountAsync(x => x.Enabled));
            var duplicate = await service.StartAsync(request, CancellationToken.None);
            Assert.Equal(started.AttemptId, duplicate.AttemptId);
            var error = await Assert.ThrowsAsync<DomainException>(() => service.RetryAsync(taskId,
                new RetryNoteAsrRequest(Guid.NewGuid(), owner, read.Revision, [0],
                    new AiAsrDestinationConsent(Guid.NewGuid(), Guid.NewGuid(), "https://example.invalid/v1/chat/completions",
                        "mimo-asr", "1", true), true), CancellationToken.None));
            Assert.Equal(40901, error.ErrorCode);
            // A real local resume needs no cloud consent, no cloud profile and no runtime key.
            var resumed = await service.RetryAsync(taskId,
                new RetryNoteAsrRequest(Guid.NewGuid(), owner, read.Revision, [0], null, false), CancellationToken.None);
            Assert.Equal(NoteAsrRoute.Local, resumed.Route);
            Assert.Null(resumed.Destination);
            var resumedState = await tasks.ReadAsync(owner, taskId, CancellationToken.None);
            Assert.Single(resumedState.Retries);
            Assert.Equal(0, await db.AiModelRuntime.CountAsync(x => x.Enabled));
            // A rejected resume must not mutate the task file at all.
            await db.NoteAsrAttempts.Where(x => x.Id == resumedState.AttemptId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.State, "Succeeded"));
            var beforeReject = await tasks.ReadAsync(owner, taskId, CancellationToken.None);
            var attemptBeforeReject = await db.NoteAsrAttempts.AsNoTracking()
                .SingleAsync(x => x.Id == resumedState.AttemptId);
            var rejected = await Assert.ThrowsAsync<DomainException>(() => service.RetryAsync(taskId,
                new RetryNoteAsrRequest(Guid.NewGuid(), owner, beforeReject.Revision, [0], null, false),
                CancellationToken.None));
            Assert.Equal(40901, rejected.ErrorCode);
            var attemptAfterReject = await db.NoteAsrAttempts.AsNoTracking()
                .SingleAsync(x => x.Id == resumedState.AttemptId);
            Assert.Equal("Succeeded", attemptAfterReject.State);
            Assert.Equal(attemptBeforeReject.FencingToken, attemptAfterReject.FencingToken);
            Assert.Equal(attemptBeforeReject.StopRequested, attemptAfterReject.StopRequested);
            Assert.Equal(attemptBeforeReject.UpdatedAt, attemptAfterReject.UpdatedAt);
            var afterReject = await tasks.ReadAsync(owner, taskId, CancellationToken.None);
            Assert.Equal(beforeReject.Revision, afterReject.Revision);
            Assert.Equal(beforeReject.Retries.Count, afterReject.Retries.Count);
            Assert.Equal(beforeReject.Steps[0].Current.Status, afterReject.Steps[0].Current.Status);
            Assert.Equal(beforeReject.Steps[0].Attempts.Count, afterReject.Steps[0].Attempts.Count);
        }
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Explicit_cloud_profile_is_frozen_from_task_consent_not_global_default()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot });
        var materials = new NoteMaterialUploadStore(options);
        var tasks = new NoteAsrTaskStore(options);
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 1_000);
        await NoteDerivedFixture.UploadAsync(materials, owner, material);
        var preset = new MimoAsrPreset(new UnusedChatClientFactory());
        var registry = new AiAsrPresetRegistry([preset]);
        var selectedId = Guid.NewGuid();
        var otherId = Guid.NewGuid();
        Guid selectedRevision;
        string selectedEndpoint;
        await using (var db = NewDb(owner))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Users.AddAsync(NewUser(owner));
            db.AiModelProfiles.AddRange(
                new AiModelProfileEntity
                {
                    Id = selectedId, Name = $"selected-{owner:N}", NormalizedName = $"selected-{owner:N}",
                    BaseUrl = "https://selected.example.invalid/v1/", Model = "mimo-v2.5-asr", Enabled = true,
                    Capabilities = (int)AiModelCapabilityFlags.Transcription, AsrPresetId = "mimo-asr",
                    AsrPrompt = "Selected prompt {{language}}", ApiKeySecret = "synthetic"
                },
                new AiModelProfileEntity
                {
                    Id = otherId, Name = $"default-{owner:N}", NormalizedName = $"default-{owner:N}",
                    BaseUrl = "https://default.example.invalid/v1/", Model = "mimo-v2.5-asr", Enabled = true,
                    Capabilities = (int)AiModelCapabilityFlags.Transcription, AsrPresetId = "mimo-asr",
                    AsrPrompt = "Default prompt {{language}}", ApiKeySecret = "synthetic"
                });
            await db.SaveChangesAsync();
            db.NoteAsrRouteConfigs.Add(new NoteAsrRouteConfigEntity
            {
                OwnerId = owner, Revision = Guid.NewGuid(), DefaultRoute = NoteAsrRoute.CloudAsr,
                LocalModelId = "local-model", LocalModelRevision = "r1", CloudModelId = otherId,
                ConsentVersion = "consent-cloud"
            });
            await db.SaveChangesAsync();
            var selected = await db.AiModelProfiles.AsNoTracking().SingleAsync(x => x.Id == selectedId);
            selectedRevision = selected.Revision;
            selectedEndpoint = new Uri(new Uri(selected.BaseUrl), "chat/completions").AbsoluteUri;
        }
        var taskId = Guid.NewGuid();
        await WriteTaskStateAsync(new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, material.Request.VersionId,
                new AiAsrDestinationConsent(selectedId, selectedRevision, selectedEndpoint,
                    "mimo-asr", "1", true), "zh"),
            Fingerprint = new string('f', 64), NoteId = material.NoteId,
            InputSha256 = NoteMaterialManifestValidator.Hash(material.Request.InputJson),
            ManifestSha256 = NoteMaterialManifestValidator.Hash(material.Request.ManifestJson),
            Steps =
            [
                new NoteAsrStep
                {
                    Index = 0, RecordingId = material.RecordingId, RecordingSegment = 1, ChunkIndex = 1,
                    FileIndex = 5, Path = $"recordings/{material.RecordingId:D}/s0001-c000001.wav",
                    Sha256 = NoteDerivedFixture.Digest(material.Content[5]), SourceOffset = 0,
                    RecordingOffset = 0, TimelineOffset = 0, Samples = 500, Attempts = [new()]
                }
            ]
        });
        await using (var db = NewDb(owner))
        {
            var config = await db.NoteAsrRouteConfigs.AsNoTracking().SingleAsync(x => x.OwnerId == owner);
            var consent = new NoteAsrRouteConsent(NoteAsrRoute.CloudAsr, config.Revision, config.ConsentVersion, true);
            var created = await NewService(db, owner, registry).CreateAttemptAsync(
                taskId, material.Request.VersionId, 1, "zh", consent, CancellationToken.None);
            Assert.Equal(selectedId, created.ModelId);
            Assert.Equal(selectedRevision, created.ModelRevision);
            Assert.False(string.IsNullOrWhiteSpace(created.PromptRevision));
            Assert.StartsWith("https://selected.example.invalid/", created.Endpoint);
        }
        await using (var db = NewDb(owner))
        {
            db.AiModelProfiles.Single(x => x.Id == selectedId).Revision = Guid.NewGuid();
            db.AiModelProfiles.Single(x => x.Id == selectedId).AsrPrompt = "Changed prompt {{language}}";
            await db.SaveChangesAsync();
            var existing = await db.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.OwnerId == owner &&
                x.TaskId == taskId);
            Assert.StartsWith("Selected prompt", existing.PromptTemplate);
            Assert.StartsWith("https://selected.example.invalid/", existing.Endpoint);
        }
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Cloud_route_without_consent_is_rejected_with_domain_error_not_null_reference()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        await CleanupAsync(owner);
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot });
        var materials = new NoteMaterialUploadStore(options);
        var tasks = new NoteAsrTaskStore(options);
        var material = NoteDerivedFixture.CreateMaterial(audioSamples: 1_000);
        await NoteDerivedFixture.UploadAsync(materials, owner, material);
        await using (var db = NewDb(owner))
        {
            await db.Database.EnsureCreatedAsync();
            await db.Users.AddAsync(NewUser(owner));
            db.NoteAsrRouteConfigs.Add(new NoteAsrRouteConfigEntity
            {
                OwnerId = owner, Revision = Guid.NewGuid(), DefaultRoute = NoteAsrRoute.CloudAsr,
                LocalModelId = "local-model", LocalModelRevision = "r1",
                CloudModelId = Guid.NewGuid(), ConsentVersion = "consent-cloud"
            });
            await db.SaveChangesAsync();
        }
        var taskId = Guid.NewGuid();
        await using (var db = NewDb(owner))
        {
            var service = new NoteAsrService(tasks, materials, db, new FixedUser(owner),
                new AiAsrPresetRegistry(Array.Empty<IAiAsrPreset>()), new ConfigurationBuilder().Build(),
                new PassthroughSecrets(), NewService(db, owner));
            var request = new StartNoteAsrRequest(taskId, owner, material.Request.VersionId, null, "zh");
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                service.StartAsync(request, CancellationToken.None));
            Assert.Equal(40001, error.ErrorCode);
            Assert.Equal(0, await db.NoteAsrAttempts.CountAsync(x => x.OwnerId == owner));
        }
        await Assert.ThrowsAsync<DomainException>(async () =>
            await tasks.ReadAsync(owner, taskId, CancellationToken.None));
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Wrong_model_first_row_does_not_starve_later_compatible_task()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var wrongAttemptId = await SeedAsync(owner, nodeId, Guid.NewGuid());
        Guid compatibleAttemptId;
        Guid compatibleTaskId;
        await using (var db = NewDb(owner))
        {
            var wrong = await db.NoteAsrAttempts.SingleAsync(x => x.Id == wrongAttemptId);
            wrong.LocalModelId = "other-model";
            compatibleAttemptId = Guid.NewGuid();
            compatibleTaskId = Guid.NewGuid();
            db.NoteAsrAttempts.Add(new NoteAsrAttemptEntity
            {
                Id = compatibleAttemptId, OwnerId = owner, TaskId = compatibleTaskId, NoteId = wrong.NoteId,
                VersionId = wrong.VersionId, InputSha256 = wrong.InputSha256, Route = NoteAsrRoute.Local,
                State = "Queued", Language = "zh", ExpectedSegments = wrong.ExpectedSegments,
                LocalModelId = "local-model", LocalModelRevision = "r1", ConfigRevision = wrong.ConfigRevision,
                ConsentVersion = wrong.ConsentVersion, CreatedAt = wrong.CreatedAt.AddSeconds(1)
            });
            await db.SaveChangesAsync();
            await WriteTaskStateAsync(owner, compatibleTaskId, wrong.VersionId, wrong.NoteId,
                wrong.InputSha256);
        }
        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeId, "compatible-model"), CancellationToken.None);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(compatibleAttemptId, claim.Lease!.AttemptId);
        await CleanupAsync(owner);
    }

    [SkippableFact]
    public async Task Poisoned_first_attempt_is_quarantined_without_lease_and_does_not_starve_the_queue()
    {
        Skip.If(string.IsNullOrWhiteSpace(ConnectionString), "PIM_ASR516_PG is not configured");
        var owner = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var poisonedId = await SeedAsync(owner, nodeId, Guid.NewGuid());
        Guid compatibleId;
        Guid compatibleTaskId;
        await using (var db = NewDb(owner))
        {
            var poisoned = await db.NoteAsrAttempts.SingleAsync(x => x.Id == poisonedId);
            // The poisoned attempt has no matching task snapshot on disk and must be quarantined
            // inside the claim transaction before the compatible attempt is considered.
            File.Delete(Path.Combine(StorageRoot, "asr-tasks", owner.ToString("D"),
                poisoned.TaskId.ToString("D") + ".json"));
            compatibleId = Guid.NewGuid();
            compatibleTaskId = Guid.NewGuid();
            db.NoteAsrAttempts.Add(new NoteAsrAttemptEntity
            {
                Id = compatibleId, OwnerId = owner, TaskId = compatibleTaskId, NoteId = poisoned.NoteId,
                VersionId = poisoned.VersionId, InputSha256 = poisoned.InputSha256, Route = NoteAsrRoute.Local,
                State = "Queued", Language = "zh", ExpectedSegments = poisoned.ExpectedSegments,
                LocalModelId = "local-model", LocalModelRevision = "r1", ConfigRevision = poisoned.ConfigRevision,
                ConsentVersion = poisoned.ConsentVersion, CreatedAt = poisoned.CreatedAt.AddSeconds(1)
            });
            await db.SaveChangesAsync();
            await WriteTaskStateAsync(owner, compatibleTaskId, poisoned.VersionId, poisoned.NoteId,
                poisoned.InputSha256);
        }

        NoteAsrClaimDto claim;
        await using (var db = NewDb(owner))
            claim = await NewService(db, owner).ClaimAsync(
                new ClaimNoteAsrTaskRequest(nodeId, "poison-claim"), CancellationToken.None);
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(compatibleId, claim.Lease!.AttemptId);
        await using var check = NewDb(owner);
        var poisonedRow = await check.NoteAsrAttempts.AsNoTracking().SingleAsync(x => x.Id == poisonedId);
        Assert.Equal("Failed", poisonedRow.State);
        Assert.Equal("task_binding_missing", poisonedRow.LastErrorCode);
        Assert.Equal(0, await check.NoteAsrLeases.CountAsync(x => x.AttemptId == poisonedId));
        Assert.Equal(0, await check.NoteAsrAckReceipts.CountAsync(x => x.OwnerId == owner &&
            x.AttemptId == poisonedId));
        await CleanupAsync(owner);
    }

    private static async Task<Exception?> Capture(Func<Task<NoteAsrRouteConfigDto>> task)
    {
        try { await task(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<Exception?> Capture(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception error) { return error; }
    }

    private static async Task<Guid> SeedAsync(Guid owner, Guid nodeA, Guid nodeB)
    {
        await CleanupAsync(owner);
        await using var db = NewDb(owner);
        await db.Database.EnsureCreatedAsync();
        var now = DateTimeOffset.UtcNow;
        var attemptId = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var noteId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var inputSha = new string('a', 64);
        await db.Users.AddAsync(NewUser(owner));
        await db.NoteAsrNodes.AddRangeAsync(Node(owner, nodeA, "node-a", now), Node(owner, nodeB, "node-b", now));
        await db.NoteAsrAttempts.AddAsync(new NoteAsrAttemptEntity
        {
            Id = attemptId, OwnerId = owner, TaskId = taskId, NoteId = noteId,
            VersionId = versionId, InputSha256 = inputSha, Route = NoteAsrRoute.Local,
            State = "Queued", Language = "zh", ExpectedSegments = 3, LocalModelId = "local-model",
            LocalModelRevision = "r1", ConfigRevision = Guid.NewGuid(), ConsentVersion = "consent-1"
        });
        await db.SaveChangesAsync();
        var task = new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, versionId,
                new AiAsrDestinationConsent(Guid.NewGuid(), Guid.NewGuid(), "synthetic", "local", "1", true), "zh"),
            Fingerprint = new string('b', 64), NoteId = noteId, InputSha256 = inputSha,
            ManifestSha256 = new string('c', 64), AttemptId = attemptId,
            Steps = Enumerable.Range(0, 3).Select(i => new NoteAsrStep
            {
                Index = i, RecordingId = Guid.NewGuid(), RecordingSegment = 1, ChunkIndex = i,
                FileIndex = i, Path = $"recordings/s0001-c{i:D6}.wav", Sha256 = new string('d', 64),
                SourceOffset = 0, RecordingOffset = i, TimelineOffset = i, Samples = 1_000,
                Attempts = [new()]
            }).ToList()
        };
        var taskDir = Path.Combine(StorageRoot, "asr-tasks", owner.ToString("D"));
        Directory.CreateDirectory(taskDir);
        await File.WriteAllTextAsync(Path.Combine(taskDir, taskId.ToString("D") + ".json"),
            JsonSerializer.Serialize(task, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return attemptId;
    }

    private static async Task WriteTaskStateAsync(Guid owner, Guid taskId, Guid versionId, Guid noteId, string inputSha)
    {
        var task = new NoteAsrState
        {
            Owner = owner,
            Request = new StartNoteAsrRequest(taskId, owner, versionId,
                new AiAsrDestinationConsent(Guid.NewGuid(), Guid.NewGuid(), "synthetic", "local", "1", true), "zh"),
            Fingerprint = new string('b', 64), NoteId = noteId, InputSha256 = inputSha,
            ManifestSha256 = new string('c', 64),
            Steps = Enumerable.Range(0, 3).Select(i => new NoteAsrStep
            {
                Index = i, RecordingId = Guid.NewGuid(), RecordingSegment = 1, ChunkIndex = i,
                FileIndex = i, Path = $"recordings/s0001-c{i:D6}.wav", Sha256 = new string('d', 64),
                SourceOffset = 0, RecordingOffset = i, TimelineOffset = i, Samples = 1_000
            }).ToList()
        };
        await WriteTaskStateAsync(task);
    }

    private static async Task WriteTaskStateAsync(NoteAsrState task)
    {
        var owner = task.Owner;
        var taskId = task.Request.TaskId;
        var taskDir = Path.Combine(StorageRoot, "asr-tasks", owner.ToString("D"));
        Directory.CreateDirectory(taskDir);
        await File.WriteAllTextAsync(Path.Combine(taskDir, taskId.ToString("D") + ".json"),
            JsonSerializer.Serialize(task, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static void WriteRawTask(Guid taskId, NoteAsrState task)
    {
        var taskDir = Path.Combine(StorageRoot, "asr-tasks", task.Owner.ToString("D"));
        Directory.CreateDirectory(taskDir);
        File.WriteAllText(Path.Combine(taskDir, taskId.ToString("D") + ".json"),
            JsonSerializer.Serialize(task, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static async Task<NoteAsrRouteConfigDto> UpdateLocalConfigAsync(Guid owner, string modelId, string revision)
    {
        await using var db = NewDb(owner);
        return await NewService(db, owner).UpdateConfigAsync(new UpdateNoteAsrRouteConfigRequest(
            Guid.Empty, NoteAsrRoute.Local, modelId, revision, null, "consent-local"), CancellationToken.None);
    }

    private static UserEntity NewUser(Guid id) => new()
    {
        Id = id, Username = "synthetic-" + id.ToString("N")[..8], Email = id.ToString("N")[..8] + "@example.invalid",
        PasswordHash = "synthetic", IsActive = true
    };

    private static NoteAsrAckRequest SegmentAck(NoteAsrClaimDto claim, int index, string result) =>
        new(claim.LeaseId!.Value, claim.Lease!.LeaseToken, claim.Lease.FencingToken, "segment", result,
            index, $"provider-{index}", result == "Failed" ? "segment_failed" : null,
            result == "Failed" ? "synthetic segment failure" : null,
            result == "Succeeded" ? $"synthetic transcript {index}" : null);

    private static NoteAsrAckRequest TerminalAck(NoteAsrClaimDto claim, string kind, string result) =>
        new(claim.LeaseId!.Value, claim.Lease!.LeaseToken, claim.Lease.FencingToken, kind, result,
            0, "provider-terminal", result == "Failed" ? "batch_failed" : null,
            result == "Failed" ? "synthetic terminal failure" : null);

    private static NoteAsrNodeEntity Node(Guid owner, Guid id, string name, DateTimeOffset now) => new()
    {
        Id = id, OwnerId = owner, NodeId = name, Enabled = true, ModelId = "local-model",
        ModelRevision = "r1", ModelVerified = true, CapabilitiesJson = "[]", LastHeartbeatAt = now
    };

    private static PimDbContext NewDb(Guid owner) => new(
        new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(ConnectionString!).Options,
        new FixedUser(owner));

    private static NoteAsrRoutingService NewService(PimDbContext db, Guid owner,
        AiAsrPresetRegistry? presets = null)
    {
        var options = Options.Create(new NoteMaterialUploadOptions { StorageRoot = StorageRoot });
        var materials = new NoteMaterialUploadStore(options);
        var tasks = new NoteAsrTaskStore(options);
        return new NoteAsrRoutingService(db, new FixedUser(owner), materials,
            presets ?? new AiAsrPresetRegistry(Array.Empty<IAiAsrPreset>()), tasks,
            new NoteAsrEvidenceService(db, tasks, new FixedUser(owner)),
            new PassthroughSecrets(), Options.Create(new NoteAsrRoutingOptions()));
    }

    private static async Task CleanupAsync(Guid owner)
    {
        if (string.IsNullOrWhiteSpace(ConnectionString)) return;
        await using var db = NewDb(owner);
        try
        {
            await db.NoteAsrAckReceipts.Where(x => x.OwnerId == owner).ExecuteDeleteAsync();
            await db.NoteAsrSegmentEvidence.Where(x => x.OwnerId == owner).ExecuteDeleteAsync();
            await db.NoteAsrLeases.Where(x => x.OwnerId == owner).ExecuteDeleteAsync();
            await db.NoteAsrAttempts.Where(x => x.OwnerId == owner).ExecuteDeleteAsync();
            await db.NoteAsrNodes.Where(x => x.OwnerId == owner).ExecuteDeleteAsync();
            await db.NoteAsrRouteConfigs.Where(x => x.OwnerId == owner).ExecuteDeleteAsync();
            await db.Users.Where(x => x.Id == owner).ExecuteDeleteAsync();
        }
        catch (Exception error) when (error is Npgsql.PostgresException { SqlState: "42P01" }) { }
        var tasks = Path.Combine(StorageRoot, "asr-tasks", owner.ToString("D"));
        if (Directory.Exists(tasks))
        {
            var full = Path.GetFullPath(tasks);
            var root = Path.GetFullPath(StorageRoot);
            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                Directory.Delete(full, recursive: true);
        }
    }

    private sealed class FixedUser(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Role => "user";
    }

    private sealed class PassthroughSecrets : ISecretProtector
    {
        public string Protect(string value) => value;
        public string Unprotect(string protectedValue) => protectedValue;
    }

    private sealed class UnusedChatClientFactory : IAiProfileChatClientFactory
    {
        public AiProfileClientLease Create(AiModelConnection connection) =>
            throw new NotSupportedException("This routing test never executes a cloud chat call");
    }
}
