using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Storage;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Ai.Asr.Mimo;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Secrets;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Ai.Asr;

public sealed class AiAsrJobServiceTests
{
    [Fact]
    public async Task Repeated_fixtures_do_not_exhaust_EF_provider_cache_before_mobile_contexts()
    {
        for (var i = 0; i < 24; i++)
        {
            await using var fixture = await InMemoryFixture.CreateAsync();
            Assert.NotNull(fixture.Db.Model);
        }

        await using var mobile = await Pim.UnitTests.Mobile.DeviceScopeTestDb
            .CreateAsync(Guid.NewGuid());
        Assert.True(await mobile.Db.Database.CanConnectAsync());
    }

    [Fact]
    public async Task Submit_is_durable_replayable_owner_scoped_cancellable_and_token_preflighted()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var first = fixture.Service(fixture.Owner);
        var request = fixture.Request();

        var accepted = await first.SubmitAsync(request, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Queued, accepted.Status);
        Assert.False(accepted.Replayed);
        Assert.Single(fixture.Dispatcher.Enqueued);

        var replay = await first.SubmitAsync(request, CancellationToken.None);
        Assert.Equal(accepted.ClientRequestId, replay.ClientRequestId);
        Assert.True(replay.Replayed);
        Assert.Single(fixture.Dispatcher.Enqueued);

        var conflict = request with
        {
            Audio = request.Audio with { Name = "different.wav" }
        };
        var rejected = await Assert.ThrowsAsync<DomainException>(() =>
            first.SubmitAsync(conflict, CancellationToken.None));
        Assert.Equal(40901, rejected.ErrorCode);

        var otherOwner = new Owner();
        await fixture.AddUserAsync(otherOwner.UserId!.Value);
        var other = fixture.Service(otherOwner);
        var second = await other.SubmitAsync(fixture.Request(otherOwner), CancellationToken.None);
        Assert.NotEqual(accepted.LogId, second.LogId);
        await Assert.ThrowsAsync<DomainException>(() => other.GetAsync(accepted.ClientRequestId,
            CancellationToken.None));

        var cancelled = await first.CancelAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Cancelled, cancelled.Status);
        Assert.Equal("user_cancelled_before_send", cancelled.ErrorCode);
        Assert.True(cancelled.Diagnostics.OutcomeKnown);

        fixture.Profile.MaxOutputTokens = 1;
        fixture.Db.Entry(fixture.Profile).State = EntityState.Modified;
        await fixture.Db.SaveChangesAsync();
        var beforePreflightEnqueues = fixture.Dispatcher.Enqueued.Count;
        var blocked = await first.SubmitAsync(fixture.Request(bytes: BuildWave(8_000)),
            CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Blocked, blocked.Status);
        Assert.Equal("output_limit_preflight", blocked.ErrorCode);
        Assert.True(blocked.Diagnostics.OutputLimitExceeded);
        Assert.Equal(1, blocked.Diagnostics.MaxOutputTokens);
        Assert.Equal(beforePreflightEnqueues, fixture.Dispatcher.Enqueued.Count);

        await using var restarted = await InMemoryFixture.OpenAsync(fixture);
        var reread = await restarted.Service(otherOwner)
            .GetAsync(second.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Queued, reread.Status);
    }

    [Fact]
    public async Task Worker_uses_profile_token_limit_and_records_success_and_provider_timeout_separately()
    {
        await using var success = await InMemoryFixture.CreateAsync();
        var preset = new ScriptedPreset();
        var service = success.Service(success.Owner, preset);
        var accepted = await service.SubmitAsync(success.Request(), CancellationToken.None);

        await service.ProcessAsync(success.Owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);

        var result = await service.GetAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Succeeded, result.Status);
        Assert.Equal("完整后台转写", result.ResponseText);
        Assert.Equal(1024, preset.LastConnection?.MaxOutputTokens);
        Assert.True(result.Diagnostics.ProviderAttempted);
        Assert.True(result.Diagnostics.ResponseBodyComplete);
        Assert.Equal("none", result.Diagnostics.AbortSource);
        Assert.Equal(1, preset.Calls);

        await using var timeout = await InMemoryFixture.CreateAsync();
        var timedOutPreset = new ScriptedPreset
        {
            Failure = new DomainException(50401, "synthetic provider timeout")
        };
        var timeoutService = timeout.Service(timeout.Owner, timedOutPreset);
        var timeoutJob = await timeoutService.SubmitAsync(timeout.Request(), CancellationToken.None);
        await timeoutService.ProcessAsync(timeout.Owner.UserId!.Value, timeoutJob.ClientRequestId,
            CancellationToken.None);

        var timedOut = await timeoutService.GetAsync(timeoutJob.ClientRequestId,
            CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.TimedOut, timedOut.Status);
        Assert.Equal("provider_timeout", timedOut.Diagnostics.AbortSource);
        Assert.False(timedOut.Diagnostics.OutcomeKnown);
        Assert.Equal(504, timedOut.Diagnostics.UpstreamStatus);
        Assert.Equal(1, timedOutPreset.Calls);

        await using var upstream = await InMemoryFixture.CreateAsync();
        var httpPreset = new ScriptedPreset
        {
            Failure = new AiAsrProviderHttpException(429, "synthetic rate limit")
        };
        var httpService = upstream.Service(upstream.Owner, httpPreset);
        var httpJob = await httpService.SubmitAsync(upstream.Request(), CancellationToken.None);
        await httpService.ProcessAsync(upstream.Owner.UserId!.Value, httpJob.ClientRequestId,
            CancellationToken.None);

        var failed = await httpService.GetAsync(httpJob.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Failed, failed.Status);
        Assert.Equal("upstream_http", failed.Diagnostics.AbortSource);
        Assert.Equal(429, failed.Diagnostics.UpstreamStatus);
        Assert.False(failed.Diagnostics.OutcomeKnown);

        await using var httpTimeout = await InMemoryFixture.CreateAsync();
        var timeoutStatusPreset = new ScriptedPreset
        {
            Failure = new AiAsrProviderHttpException(504, "synthetic gateway/provider timeout")
        };
        var timeoutStatusService = httpTimeout.Service(httpTimeout.Owner, timeoutStatusPreset);
        var timeoutStatusJob = await timeoutStatusService.SubmitAsync(httpTimeout.Request(),
            CancellationToken.None);
        await timeoutStatusService.ProcessAsync(httpTimeout.Owner.UserId!.Value,
            timeoutStatusJob.ClientRequestId, CancellationToken.None);

        var timeoutStatus = await timeoutStatusService.GetAsync(timeoutStatusJob.ClientRequestId,
            CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.TimedOut, timeoutStatus.Status);
        Assert.Equal("provider_timeout", timeoutStatus.Diagnostics.AbortSource);
        Assert.Equal(504, timeoutStatus.Diagnostics.UpstreamStatus);
        Assert.False(timeoutStatus.Diagnostics.OutcomeKnown);
    }

    [Fact]
    public async Task UsageLimitAllowsAsyncAsrWorkerAfterItsPersistedQueuePlaceholder()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var limits = new AiUsageLimitService(fixture.Db, fixture.Owner);
        var connection = await limits.CreateConnectionAsync("异步ASR限额连接", default);
        fixture.Profile.ProviderConnectionId = connection.Id;
        await fixture.Db.SaveChangesAsync();
        var rule = await limits.GetRuleAsync(connection.Id, default);
        await limits.SaveRuleAsync(connection.Id,
            new(rule.Revision, true, AiUsageLimitPeriods.Month, 80, 100, null, null,
                "Asia/Shanghai"), default);

        var preset = new ScriptedPreset();
        var markerObservedBeforeProvider = false;
        preset.OnCall = async () =>
        {
            var providerJob = await fixture.Db.AiAsrJobs.AsNoTracking().SingleAsync();
            var providerLog = await fixture.Db.AiRequestLogs.AsNoTracking().SingleAsync();
            Assert.True(providerJob.ProviderAttempted);
            using var metadata = JsonDocument.Parse(providerLog.MetadataJson);
            Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
            markerObservedBeforeProvider = true;
        };
        var service = fixture.Service(fixture.Owner, preset, limits);
        var accepted = await service.SubmitAsync(fixture.Request(), CancellationToken.None);
        var queuedLog = await fixture.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == accepted.LogId);
        using (var metadata = JsonDocument.Parse(queuedLog.MetadataJson))
            Assert.False(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());

        await service.ProcessAsync(fixture.Owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);

        var result = await service.GetAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Succeeded, result.Status);
        Assert.True(markerObservedBeforeProvider);
        Assert.Equal(1, preset.Calls);
    }

    [Fact]
    public async Task UsageLimitRejectionReleasesAsyncAsrGate()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var limits = new AiUsageLimitService(fixture.Db, fixture.Owner);
        var connection = await limits.CreateConnectionAsync("异步ASR门禁连接", default);
        fixture.Profile.ProviderConnectionId = connection.Id;
        await fixture.Db.SaveChangesAsync();
        var rule = await limits.GetRuleAsync(connection.Id, default);
        await limits.SaveRuleAsync(connection.Id,
            new(rule.Revision, true, AiUsageLimitPeriods.Month, 80, 100, null, null,
                "Asia/Shanghai"), default);

        var preset = new ScriptedPreset();
        var gate = new AiTextCallGate();
        var service = fixture.Service(fixture.Owner, preset, limits, gate);
        var accepted = await service.SubmitAsync(fixture.Request(), CancellationToken.None);
        fixture.Db.AiRequestLogs.Add(new AiRequestLogEntity
        {
            UserId = fixture.Owner.UserId,
            Purpose = "ai.asr-job",
            ProviderConnectionId = connection.Id,
            Status = "succeeded",
            StartedAt = DateTimeOffset.UtcNow,
            InitiatedAt = DateTimeOffset.UtcNow,
            CorrelationId = Guid.NewGuid().ToString("N"),
            MetadataJson = """{"providerAttempted":true}""",
            TotalTokens = 100
        });
        await fixture.Db.SaveChangesAsync();
        Assert.True(gate.TryEnter(asr: true));

        await service.ProcessAsync(fixture.Owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);

        var result = await service.GetAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Blocked, result.Status);
        Assert.Equal("usage_limit_reached", result.ErrorCode);
        Assert.Equal(0, preset.Calls);
        Assert.True(gate.TryEnter(asr: true));
        gate.Exit(asr: true);
        gate.Exit(asr: true);
    }

    [Fact]
    public async Task UsageLimitRejectsAsyncAsrModelWithoutProviderConnectionBeforeProvider()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var limits = new AiUsageLimitService(fixture.Db, fixture.Owner);
        fixture.Profile.ProviderConnectionId = null;
        await fixture.Db.SaveChangesAsync();
        var preset = new ScriptedPreset();
        var service = fixture.Service(fixture.Owner, preset, limits);
        var accepted = await service.SubmitAsync(fixture.Request(), CancellationToken.None);

        await service.ProcessAsync(fixture.Owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);

        var result = await service.GetAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Blocked, result.Status);
        Assert.Equal("usage_limit_unbound", result.ErrorCode);
        Assert.Equal(0, preset.Calls);
        Assert.True(fixture.Db.AiRequestLogs.AsNoTracking()
            .Any(x => x.Id == accepted.LogId && x.Status == "blocked"));
    }

    [Fact]
    public async Task ExpiredRunningJobRecoveryCountsAsProviderAttemptedAndFailsClosed()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var limits = new AiUsageLimitService(fixture.Db, fixture.Owner);
        var connection = await limits.CreateConnectionAsync("异步恢复连接", default);
        fixture.Profile.ProviderConnectionId = connection.Id;
        await fixture.Db.SaveChangesAsync();
        var rule = await limits.GetRuleAsync(connection.Id, default);
        await limits.SaveRuleAsync(connection.Id,
            new(rule.Revision, true, AiUsageLimitPeriods.Month, 80, 100, null, null,
                "Asia/Shanghai"), default);
        var service = fixture.Service(fixture.Owner, new ScriptedPreset(), limits);
        var accepted = await service.SubmitAsync(fixture.Request(), CancellationToken.None);
        var job = await fixture.Db.AiAsrJobs.SingleAsync();
        var log = await fixture.Db.AiRequestLogs.SingleAsync(x => x.Id == accepted.LogId);
        var startedAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        job.Status = "running";
        job.StartedAt = startedAt;
        job.ExecutionExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        job.ProviderAttempted = false;
        log.Status = "running";
        log.StartedAt = startedAt;
        log.ExecutionExpiresAt = job.ExecutionExpiresAt;
        log.MetadataJson = """{"providerAttempted":false}""";
        await fixture.Db.SaveChangesAsync();

        await AiAsrJobRecovery.RecoverExpiredAsync(fixture.Db, CancellationToken.None);

        var recoveredJob = await fixture.Db.AiAsrJobs.AsNoTracking().SingleAsync();
        var recoveredLog = await fixture.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == accepted.LogId);
        Assert.Equal("incomplete", recoveredJob.Status);
        Assert.True(recoveredJob.ProviderAttempted);
        using (var metadata = JsonDocument.Parse(recoveredLog.MetadataJson))
            Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
        var decision = await limits.TryBeginAsync(connection.Id, "after-async-recovery",
            "ai.text-call", TimeSpan.FromMinutes(1), default);
        Assert.False(decision.Allowed);
        Assert.Equal("usage_limit_unknown", decision.ErrorCode);
    }

    [Fact]
    public async Task Running_user_cancel_is_not_reported_as_provider_timeout()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var preset = new ScriptedPreset
        {
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            BlockUntilCancelled = true
        };
        var service = fixture.Service(fixture.Owner, preset);
        var accepted = await service.SubmitAsync(fixture.Request(), CancellationToken.None);
        var running = service.ProcessAsync(fixture.Owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);
        await preset.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelService = fixture.Service(fixture.Owner, preset);
        var cancelling = await cancelService.CancelAsync(accepted.ClientRequestId,
            CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Cancelling, cancelling.Status);
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        var result = await service.GetAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Cancelled, result.Status);
        Assert.Equal("user_cancelled", result.Diagnostics.AbortSource);
        Assert.False(result.Diagnostics.OutcomeKnown);
        Assert.True(result.Diagnostics.ProviderAttempted);
        Assert.Equal(1, preset.Calls);
    }

    [Fact]
    public async Task Replay_uses_the_fixed_request_fingerprint_after_current_profile_changes()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var service = fixture.Service(fixture.Owner);
        var request = fixture.Request();
        var accepted = await service.SubmitAsync(request, CancellationToken.None);

        fixture.Profile.Enabled = false;
        fixture.Profile.Revision = Guid.NewGuid();
        fixture.Db.Entry(fixture.Profile).State = EntityState.Modified;
        var runtime = await fixture.Db.AiModelRuntime.SingleAsync();
        runtime.Enabled = false;
        await fixture.Db.SaveChangesAsync();

        var replay = await service.SubmitAsync(request, CancellationToken.None);
        Assert.True(replay.Replayed);
        Assert.Equal(accepted.ClientRequestId, replay.ClientRequestId);
        Assert.Equal(accepted.LogId, replay.LogId);
        Assert.Equal(AiAsrJobStatus.Queued, replay.Status);
        Assert.Single(fixture.Dispatcher.Enqueued);
    }

    [Fact]
    public async Task Dispatcher_outage_allows_existing_replay_but_rejects_new_job()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var service = fixture.Service(fixture.Owner);
        var request = fixture.Request();
        var accepted = await service.SubmitAsync(request, CancellationToken.None);

        fixture.Dispatcher.IsAvailable = false;

        var replay = await service.SubmitAsync(request, CancellationToken.None);
        Assert.True(replay.Replayed);
        Assert.Equal(accepted.ClientRequestId, replay.ClientRequestId);
        Assert.Equal(accepted.LogId, replay.LogId);
        Assert.Single(fixture.Db.AiAsrJobs);

        var rejected = await Assert.ThrowsAsync<DomainException>(() =>
            service.SubmitAsync(fixture.Request(), CancellationToken.None));
        Assert.Equal(50302, rejected.ErrorCode);
        Assert.Single(fixture.Db.AiAsrJobs);
    }

    [Fact]
    public async Task Late_cancel_after_provider_success_preserves_result_with_explicit_evidence()
    {
        await using var fixture = await InMemoryFixture.CreateAsync();
        var preset = new ScriptedPreset
        {
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var service = fixture.Service(fixture.Owner, preset);
        var accepted = await service.SubmitAsync(fixture.Request(), CancellationToken.None);
        var running = service.ProcessAsync(fixture.Owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);
        await preset.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var cancelling = await fixture.Service(fixture.Owner, preset)
            .CancelAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Cancelling, cancelling.Status);

        preset.Release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(10));

        var result = await service.GetAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Succeeded, result.Status);
        Assert.Equal("完整后台转写", result.ResponseText);
        Assert.Equal("user_cancel_late", result.Diagnostics.AbortSource);
        Assert.True(result.Diagnostics.OutcomeKnown);
        var log = await fixture.Db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("succeeded", log.Status);
        Assert.Equal("完整后台转写", log.ResponseText);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_late_provider_success_after_cancel_keeps_job_and_log_consistent()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_cancel_complete");
        await database.MigrateAsync();
        var owner = new Owner();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var preset = new ScriptedPreset
        {
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            IgnoreCancellation = true
        };
        var dispatcher = new RecordingDispatcher();
        var files = new MemoryFiles();
        var registry = new AiAsrPresetRegistry([preset]);
        using var scopes = BuildPostgresScopes(database.ConnectionString);

        AiAsrJobResultDto accepted;
        await using (var submitScope = scopes.CreateAsyncScope())
        {
            accepted = await Service(submitScope.ServiceProvider.GetRequiredService<PimDbContext>(),
                    owner, files, secrets, registry, dispatcher,
                    scopes.GetRequiredService<IServiceScopeFactory>())
                .SubmitAsync(Request(owner, profile), CancellationToken.None);
        }

        await using var workerScope = scopes.CreateAsyncScope();
        var worker = Service(workerScope.ServiceProvider.GetRequiredService<PimDbContext>(), owner,
            files, secrets, registry, dispatcher,
            scopes.GetRequiredService<IServiceScopeFactory>());
        var running = worker.ProcessAsync(owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);
        await preset.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var cancelScope = scopes.CreateAsyncScope())
        {
            var cancelling = await Service(
                    cancelScope.ServiceProvider.GetRequiredService<PimDbContext>(), owner,
                    files, secrets, registry, dispatcher,
                    scopes.GetRequiredService<IServiceScopeFactory>())
                .CancelAsync(accepted.ClientRequestId, CancellationToken.None);
            Assert.Equal(AiAsrJobStatus.Cancelling, cancelling.Status);
        }

        preset.Release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(20));

        await using var check = new PimDbContext(PostgresOptions(database.ConnectionString));
        var job = await check.AiAsrJobs.AsNoTracking().SingleAsync();
        var log = await check.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("succeeded", job.Status);
        Assert.Equal("user_cancel_late", job.AbortSource);
        Assert.Equal("完整后台转写", log.ResponseText);
        Assert.Equal(job.Status, log.Status);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_migration_restart_and_concurrent_claim_allow_only_one_provider_call()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_claim");
        await database.MigrateAsync();
        Assert.True(await database.HasMigrationLikeAsync("AddAiAsrJobs"));
        Assert.True(await database.TableExistsAsync("ai_asr_jobs"));
        Assert.True(await database.IsUniqueIndexAsync(
            "IX_ai_asr_jobs_owner_id_client_request_id"));
        Assert.True(await database.IndexExistsAsync(
            "IX_ai_asr_jobs_status_execution_expires_at"));

        var owner = new Owner();
        var files = new MemoryFiles();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var preset = new ScriptedPreset
        {
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var registry = new AiAsrPresetRegistry([preset]);
        var dispatcher = new RecordingDispatcher();
        using var scopes = BuildPostgresScopes(database.ConnectionString);
        var request = Request(owner, profile, BuildWave(3_200));

        AiAsrJobResultDto accepted;
        await using (var submitScope = scopes.CreateAsyncScope())
        {
            var service = Service(submitScope.ServiceProvider.GetRequiredService<PimDbContext>(),
                owner, files, secrets, registry, dispatcher,
                scopes.GetRequiredService<IServiceScopeFactory>());
            accepted = await service.SubmitAsync(request, CancellationToken.None);
        }

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Task.Run(async () =>
        {
            await started.Task;
            await using var scope = scopes.CreateAsyncScope();
            var service = Service(scope.ServiceProvider.GetRequiredService<PimDbContext>(),
                owner, files, secrets, registry, dispatcher,
                scopes.GetRequiredService<IServiceScopeFactory>());
            await service.ProcessAsync(owner.UserId!.Value, accepted.ClientRequestId,
                CancellationToken.None);
        });
        var second = Task.Run(async () =>
        {
            await started.Task;
            await using var scope = scopes.CreateAsyncScope();
            var service = Service(scope.ServiceProvider.GetRequiredService<PimDbContext>(),
                owner, files, secrets, registry, dispatcher,
                scopes.GetRequiredService<IServiceScopeFactory>());
            await service.ProcessAsync(owner.UserId!.Value, accepted.ClientRequestId,
                CancellationToken.None);
        });
        started.SetResult();
        await preset.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        preset.Release.SetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(1, preset.Calls);
        await using (var check = new PimDbContext(PostgresOptions(database.ConnectionString)))
        {
            var job = await check.AiAsrJobs.AsNoTracking().SingleAsync();
            Assert.Equal("succeeded", job.Status);
            Assert.Equal(1, job.AttemptNumber);
            Assert.Null(job.ExecutionExpiresAt);
        }

        // A fresh context and service represent process restart: result is read from durable rows.
        await using (var restartScope = scopes.CreateAsyncScope())
        {
            var restarted = Service(restartScope.ServiceProvider.GetRequiredService<PimDbContext>(),
                owner, files, secrets, registry, dispatcher,
                scopes.GetRequiredService<IServiceScopeFactory>());
            var reread = await restarted.GetAsync(accepted.ClientRequestId, CancellationToken.None);
            Assert.Equal(AiAsrJobStatus.Succeeded, reread.Status);
            Assert.Equal("完整后台转写", reread.ResponseText);
        }
        Assert.Equal(1, preset.Calls);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_expired_running_lease_becomes_unknown_without_retry_or_late_overwrite()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_recovery");
        await database.MigrateAsync();
        var owner = new Owner();
        var files = new MemoryFiles();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var preset = new ScriptedPreset
        {
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var registry = new AiAsrPresetRegistry([preset]);
        var dispatcher = new RecordingDispatcher();
        using var scopes = BuildPostgresScopes(database.ConnectionString);
        var request = Request(owner, profile, BuildWave(3_200));

        AiAsrJobResultDto accepted;
        await using (var submitScope = scopes.CreateAsyncScope())
        {
            var submitDb = submitScope.ServiceProvider.GetRequiredService<PimDbContext>();
            accepted = await Service(submitDb, owner, files, secrets, registry, dispatcher,
                    scopes.GetRequiredService<IServiceScopeFactory>())
                .SubmitAsync(request, CancellationToken.None);
        }

        await using var workerScope = scopes.CreateAsyncScope();
        var workerDb = workerScope.ServiceProvider.GetRequiredService<PimDbContext>();
        var worker = Service(workerDb, owner, files, secrets, registry, dispatcher,
            scopes.GetRequiredService<IServiceScopeFactory>());
        var running = worker.ProcessAsync(owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);
        await preset.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using (var recoveryDb = new PimDbContext(PostgresOptions(database.ConnectionString)))
        {
            await recoveryDb.AiAsrJobs.Where(x => x.ClientRequestId == accepted.ClientRequestId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.ExecutionExpiresAt,
                    DateTimeOffset.UtcNow.AddSeconds(-1)));
            await AiAsrJobRecovery.RecoverExpiredAsync(recoveryDb, CancellationToken.None);
        }
        preset.Release.SetResult();
        await running.WaitAsync(TimeSpan.FromSeconds(20));

        await using (var check = new PimDbContext(PostgresOptions(database.ConnectionString)))
        {
            var job = await check.AiAsrJobs.AsNoTracking().SingleAsync();
            var log = await check.AiRequestLogs.AsNoTracking().SingleAsync();
            Assert.Equal("incomplete", job.Status);
            Assert.Equal("worker_lost", job.AbortSource);
            Assert.False(job.OutcomeKnown);
            Assert.Equal("incomplete", log.Status);
            Assert.Null(log.ResponseText);
        }

        var reread = await worker.GetAsync(accepted.ClientRequestId, CancellationToken.None);
        Assert.Equal(AiAsrJobStatus.Incomplete, reread.Status);
        await worker.ProcessAsync(owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);
        Assert.Equal(1, preset.Calls);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_preparing_is_not_dispatched_and_recovery_marks_it_unpaid_and_known()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_preparing");
        await database.MigrateAsync();
        var owner = new Owner();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var files = new MemoryFiles(entered, release);
        var preset = new ScriptedPreset();
        var dispatcher = new RecordingDispatcher();
        using var scopes = BuildPostgresScopes(database.ConnectionString);

        AiAsrJobResultDto accepted;
        await using (var submitScope = scopes.CreateAsyncScope())
        {
            var submit = Service(submitScope.ServiceProvider.GetRequiredService<PimDbContext>(),
                owner, files, secrets, new AiAsrPresetRegistry([preset]), dispatcher,
                scopes.GetRequiredService<IServiceScopeFactory>())
                .SubmitAsync(Request(owner, profile), CancellationToken.None);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            await using var inspect = new PimDbContext(PostgresOptions(database.ConnectionString));
            var preparing = await inspect.AiAsrJobs.AsNoTracking().SingleAsync();
            var preparingLog = await inspect.AiRequestLogs.AsNoTracking().SingleAsync();
            Assert.Equal("preparing", preparing.Status);
            Assert.Equal("preparing", preparingLog.Status);

            await using var dispatchDb = new PimDbContext(PostgresOptions(database.ConnectionString));
            await new AiAsrJobDispatchJob(dispatchDb, dispatcher,
                NullLogger<AiAsrJobDispatchJob>.Instance).RunAsync(CancellationToken.None);
            Assert.Empty(dispatcher.Enqueued);

            await inspect.AiAsrJobs.ExecuteUpdateAsync(update => update
                .SetProperty(x => x.ExecutionExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            await AiAsrJobRecovery.RecoverExpiredAsync(inspect, CancellationToken.None);
            release.SetResult();
            accepted = await submit.WaitAsync(TimeSpan.FromSeconds(20));
        }

        Assert.Equal(AiAsrJobStatus.Failed, accepted.Status);
        Assert.True(accepted.Diagnostics.OutcomeKnown);
        Assert.False(accepted.Diagnostics.ProviderAttempted);
        Assert.Equal("preparation_interrupted", accepted.Diagnostics.AbortSource);
        Assert.Empty(dispatcher.Enqueued);
        Assert.Equal(0, preset.Calls);
        await using var check = new PimDbContext(PostgresOptions(database.ConnectionString));
        Assert.Equal("failed", (await check.AiAsrJobs.AsNoTracking().SingleAsync()).Status);
        Assert.Equal("failed", (await check.AiRequestLogs.AsNoTracking().SingleAsync()).Status);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_generic_call_recovery_leaves_expired_asr_audit_to_job_recovery()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_cross_recovery");
        await database.MigrateAsync();
        var owner = new Owner();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var now = DateTimeOffset.UtcNow;
        var log = new AiRequestLogEntity
        {
            UserId = owner.UserId,
            ClientRequestId = Guid.NewGuid(),
            InputHash = "synthetic",
            Module = "system",
            Purpose = AiAsrJobService.AuditPurpose,
            SourceObjectType = "ai-model",
            SourceObjectId = profile.Id.ToString("D"),
            Provider = ScriptedPreset.PresetId,
            Model = profile.Model,
            ModelProfileId = profile.Id,
            ModelRevision = profile.Revision,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "running",
            AttemptNumber = 1,
            MaxAttempts = 1,
            StartedAt = now.AddMinutes(-10),
            InitiatedAt = now.AddMinutes(-10),
            ExecutionExpiresAt = now.AddMinutes(-1),
            MetadataJson = """{"providerAttempted":true}"""
        };
        var job = new AiAsrJobEntity
        {
            OwnerId = owner.UserId!.Value,
            ClientRequestId = log.ClientRequestId!.Value,
            LogId = log.Id,
            Status = "running",
            ModelProfileId = profile.Id,
            ModelRevision = profile.Revision,
            PresetId = ScriptedPreset.PresetId,
            PresetVersion = ScriptedPreset.Dto.Version,
            Model = profile.Model,
            Endpoint = AiAsrPresetRegistry.OperationEndpoint(profile.BaseUrl, ScriptedPreset.Dto),
            ProviderAttempted = true,
            CreatedAt = now.AddMinutes(-10),
            StartedAt = now.AddMinutes(-9),
            ExecutionExpiresAt = now.AddMinutes(-1)
        };
        database.Db.AiRequestLogs.Add(log);
        database.Db.AiAsrJobs.Add(job);
        await database.Db.SaveChangesAsync();

        await AiCallRecovery.RecoverExpiredAsync(database.Db, CancellationToken.None);

        database.Db.ChangeTracker.Clear();
        Assert.Equal("running", (await database.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == log.Id)).Status);
        Assert.Equal("running", (await database.Db.AiAsrJobs.AsNoTracking()
            .SingleAsync(x => x.Id == job.Id)).Status);

        await AiAsrJobRecovery.RecoverExpiredAsync(database.Db, CancellationToken.None);

        database.Db.ChangeTracker.Clear();
        var recoveredJob = await database.Db.AiAsrJobs.AsNoTracking()
            .SingleAsync(x => x.Id == job.Id);
        var recoveredLog = await database.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == log.Id);
        Assert.Equal("incomplete", recoveredJob.Status);
        Assert.Equal("worker_lost", recoveredJob.AbortSource);
        Assert.True(recoveredJob.ProviderAttempted);
        Assert.Equal(recoveredJob.Status, recoveredLog.Status);
        Assert.Equal(recoveredJob.ErrorCode, recoveredLog.ErrorCode);
        using var metadata = JsonDocument.Parse(recoveredLog.MetadataJson);
        Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_job_recovery_overwrites_a_legacy_premature_audit_terminal_state()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_premature_audit");
        await database.MigrateAsync();
        var owner = new Owner();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var now = DateTimeOffset.UtcNow;
        var log = new AiRequestLogEntity
        {
            UserId = owner.UserId,
            ClientRequestId = Guid.NewGuid(),
            InputHash = "synthetic",
            Module = "system",
            Purpose = AiAsrJobService.AuditPurpose,
            SourceObjectType = "ai-model",
            SourceObjectId = profile.Id.ToString("D"),
            Provider = ScriptedPreset.PresetId,
            Model = profile.Model,
            ModelProfileId = profile.Id,
            ModelRevision = profile.Revision,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "incomplete",
            ErrorCode = "outcome_unknown",
            StartedAt = now.AddMinutes(-10),
            InitiatedAt = now.AddMinutes(-10),
            FinishedAt = now.AddMinutes(-1),
            ExecutionExpiresAt = now.AddMinutes(-1),
            MetadataJson = """{"providerAttempted":true}"""
        };
        var job = new AiAsrJobEntity
        {
            OwnerId = owner.UserId!.Value,
            ClientRequestId = log.ClientRequestId!.Value,
            LogId = log.Id,
            Status = "running",
            ModelProfileId = profile.Id,
            ModelRevision = profile.Revision,
            PresetId = ScriptedPreset.PresetId,
            PresetVersion = ScriptedPreset.Dto.Version,
            Model = profile.Model,
            Endpoint = AiAsrPresetRegistry.OperationEndpoint(profile.BaseUrl, ScriptedPreset.Dto),
            ProviderAttempted = true,
            CreatedAt = now.AddMinutes(-10),
            StartedAt = now.AddMinutes(-9),
            ExecutionExpiresAt = now.AddMinutes(-1)
        };
        database.Db.AiRequestLogs.Add(log);
        database.Db.AiAsrJobs.Add(job);
        await database.Db.SaveChangesAsync();

        await AiAsrJobRecovery.RecoverExpiredAsync(database.Db, CancellationToken.None);

        database.Db.ChangeTracker.Clear();
        var recoveredJob = await database.Db.AiAsrJobs.AsNoTracking()
            .SingleAsync(x => x.Id == job.Id);
        var recoveredLog = await database.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == log.Id);
        Assert.Equal("incomplete", recoveredJob.Status);
        Assert.Equal("incomplete", recoveredLog.Status);
        Assert.Equal("worker_lost_outcome_unknown", recoveredJob.ErrorCode);
        Assert.Equal(recoveredJob.ErrorCode, recoveredLog.ErrorCode);
        Assert.Equal("worker_lost_outcome_unknown", recoveredLog.ErrorCode);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_recovery_isolates_a_terminal_audit_from_the_rest_of_the_batch()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_recovery_isolation");
        await database.MigrateAsync();
        var owner = new Owner();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var now = DateTimeOffset.UtcNow;

        var goodLog = NewRecoveryLog(owner, profile, "running", now);
        var goodJob = NewRecoveryJob(owner, profile, goodLog, "running", now);
        var terminalLog = NewRecoveryLog(owner, profile, "succeeded", now);
        terminalLog.TotalTokens = 7;
        var terminalJob = NewRecoveryJob(owner, profile, terminalLog, "running", now);
        database.Db.AiRequestLogs.AddRange(goodLog, terminalLog);
        database.Db.AiAsrJobs.AddRange(goodJob, terminalJob);
        await database.Db.SaveChangesAsync();

        await AiAsrJobRecovery.RecoverExpiredAsync(database.Db, CancellationToken.None);

        database.Db.ChangeTracker.Clear();
        var recoveredJob = await database.Db.AiAsrJobs.AsNoTracking()
            .SingleAsync(x => x.Id == goodJob.Id);
        var recoveredLog = await database.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == goodLog.Id);
        Assert.Equal("incomplete", recoveredJob.Status);
        Assert.Equal("incomplete", recoveredLog.Status);
        Assert.Equal("worker_lost", recoveredJob.AbortSource);

        // The already-terminal audit row cannot be overwritten, but it must not roll back the
        // recovery of later/earlier candidates in the same scan.
        var skippedJob = await database.Db.AiAsrJobs.AsNoTracking()
            .SingleAsync(x => x.Id == terminalJob.Id);
        var skippedLog = await database.Db.AiRequestLogs.AsNoTracking()
            .SingleAsync(x => x.Id == terminalLog.Id);
        Assert.Equal("running", skippedJob.Status);
        Assert.Equal("succeeded", skippedLog.Status);
        Assert.Equal(7, skippedLog.TotalTokens);
    }

    private static AiRequestLogEntity NewRecoveryLog(Owner owner, AiModelProfileEntity profile,
        string status, DateTimeOffset now) => new()
    {
        UserId = owner.UserId,
        ClientRequestId = Guid.NewGuid(),
        InputHash = "synthetic",
        Module = "system",
        Purpose = AiAsrJobService.AuditPurpose,
        SourceObjectType = "ai-model",
        SourceObjectId = profile.Id.ToString("D"),
        Provider = ScriptedPreset.PresetId,
        Model = profile.Model,
        ModelProfileId = profile.Id,
        ModelRevision = profile.Revision,
        CorrelationId = Guid.NewGuid().ToString("N"),
        Status = status,
        AttemptNumber = 1,
        MaxAttempts = 1,
        StartedAt = now.AddMinutes(-10),
        InitiatedAt = now.AddMinutes(-10),
        FinishedAt = status == "running" ? null : now.AddMinutes(-2),
        ExecutionExpiresAt = now.AddMinutes(-1),
        MetadataJson = """{"providerAttempted":true}"""
    };

    private static AiAsrJobEntity NewRecoveryJob(Owner owner, AiModelProfileEntity profile,
        AiRequestLogEntity log, string status, DateTimeOffset now) => new()
    {
        OwnerId = owner.UserId!.Value,
        ClientRequestId = log.ClientRequestId!.Value,
        LogId = log.Id,
        Status = status,
        ModelProfileId = profile.Id,
        ModelRevision = profile.Revision,
        PresetId = ScriptedPreset.PresetId,
        PresetVersion = ScriptedPreset.Dto.Version,
        Model = profile.Model,
        Endpoint = AiAsrPresetRegistry.OperationEndpoint(profile.BaseUrl, ScriptedPreset.Dto),
        ProviderAttempted = true,
        CreatedAt = now.AddMinutes(-10),
        StartedAt = now.AddMinutes(-9),
        ExecutionExpiresAt = now.AddMinutes(-1)
    };

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_generic_recovery_closes_an_orphaned_asr_audit_without_a_job()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_orphan_audit");
        await database.MigrateAsync();
        var owner = new Owner();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var now = DateTimeOffset.UtcNow;
        database.Db.AiRequestLogs.Add(new AiRequestLogEntity
        {
            UserId = owner.UserId,
            ClientRequestId = Guid.NewGuid(),
            InputHash = "synthetic",
            Module = "system",
            Purpose = AiAsrJobService.AuditPurpose,
            SourceObjectType = "ai-model",
            SourceObjectId = profile.Id.ToString("D"),
            Provider = ScriptedPreset.PresetId,
            Model = profile.Model,
            ModelProfileId = profile.Id,
            ModelRevision = profile.Revision,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Status = "running",
            AttemptNumber = 1,
            MaxAttempts = 1,
            StartedAt = now.AddMinutes(-10),
            InitiatedAt = now.AddMinutes(-10),
            ExecutionExpiresAt = now.AddMinutes(-1),
            MetadataJson = """{"providerAttempted":false}"""
        });
        await database.Db.SaveChangesAsync();

        await AiCallRecovery.RecoverExpiredAsync(database.Db, CancellationToken.None);

        database.Db.ChangeTracker.Clear();
        var recovered = await database.Db.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal("incomplete", recovered.Status);
        Assert.Equal("outcome_unknown", recovered.ErrorCode);
        using var metadata = JsonDocument.Parse(recovered.MetadataJson);
        Assert.False(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_cancel_and_claim_races_never_split_job_and_audit_terminal_state()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_cancel_claim");
        await database.MigrateAsync();
        var owner = new Owner();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var preset = new ScriptedPreset();
        var dispatcher = new RecordingDispatcher();
        using var scopes = BuildPostgresScopes(database.ConnectionString);
        var accepted = new List<AiAsrJobResultDto>();
        for (var index = 0; index < 6; index++)
        {
            await using var scope = scopes.CreateAsyncScope();
            accepted.Add(await Service(scope.ServiceProvider.GetRequiredService<PimDbContext>(),
                    owner, new MemoryFiles(), secrets, new AiAsrPresetRegistry([preset]),
                    dispatcher, scopes.GetRequiredService<IServiceScopeFactory>())
                .SubmitAsync(Request(owner, profile), CancellationToken.None));
        }

        await using (var disable = new PimDbContext(PostgresOptions(database.ConnectionString)))
        {
            await disable.Users.Where(x => x.Id == owner.UserId).ExecuteUpdateAsync(update =>
                update.SetProperty(x => x.IsActive, false));
        }

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var races = new List<Task>();
        foreach (var job in accepted)
        {
            races.Add(Task.Run(async () =>
            {
                await start.Task;
                await using var scope = scopes.CreateAsyncScope();
                await Service(scope.ServiceProvider.GetRequiredService<PimDbContext>(), owner,
                    new MemoryFiles(), secrets, new AiAsrPresetRegistry([preset]), dispatcher,
                    scopes.GetRequiredService<IServiceScopeFactory>())
                    .CancelAsync(job.ClientRequestId, CancellationToken.None);
            }));
            races.Add(Task.Run(async () =>
            {
                await start.Task;
                await using var scope = scopes.CreateAsyncScope();
                await Service(scope.ServiceProvider.GetRequiredService<PimDbContext>(), owner,
                    new MemoryFiles(), secrets, new AiAsrPresetRegistry([preset]), dispatcher,
                    scopes.GetRequiredService<IServiceScopeFactory>())
                    .ProcessAsync(owner.UserId!.Value, job.ClientRequestId, CancellationToken.None);
            }));
        }
        start.SetResult();
        await Task.WhenAll(races).WaitAsync(TimeSpan.FromSeconds(30));

        await using var check = new PimDbContext(PostgresOptions(database.ConnectionString));
        var jobs = await check.AiAsrJobs.AsNoTracking().ToListAsync();
        var logs = await check.AiRequestLogs.AsNoTracking()
            .Where(x => x.Purpose == "ai.asr-job").ToDictionaryAsync(x => x.Id);
        Assert.Equal(accepted.Count, jobs.Count);
        foreach (var job in jobs)
        {
            Assert.True(job.Status is "cancelled" or "blocked", job.Status);
            Assert.True(logs.TryGetValue(job.LogId, out var log));
            Assert.Equal(job.Status, log!.Status);
        }
        Assert.Equal(0, preset.Calls);
    }

    [SkippableFact]
    [Trait("DataSource", "RealDb")]
    public async Task Postgres_recovery_and_late_completion_leave_one_consistent_terminal_state()
    {
        await using var database = await TempMigrationDatabase.CreateAsync(
            "test_ai_asr_job_recovery_race");
        await database.MigrateAsync();
        var owner = new Owner();
        ISecretProtector secrets =
            new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
        var profile = await SeedPostgresAsync(database.Db, owner, secrets);
        var preset = new ScriptedPreset
        {
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)
        };
        var dispatcher = new RecordingDispatcher();
        var files = new MemoryFiles();
        using var scopes = BuildPostgresScopes(database.ConnectionString);

        AiAsrJobResultDto accepted;
        await using (var submitScope = scopes.CreateAsyncScope())
        {
            accepted = await Service(submitScope.ServiceProvider.GetRequiredService<PimDbContext>(),
                    owner, files, secrets, new AiAsrPresetRegistry([preset]),
                    dispatcher, scopes.GetRequiredService<IServiceScopeFactory>())
                .SubmitAsync(Request(owner, profile), CancellationToken.None);
        }

        await using var workerScope = scopes.CreateAsyncScope();
        var worker = Service(workerScope.ServiceProvider.GetRequiredService<PimDbContext>(), owner,
            files, secrets,
            new AiAsrPresetRegistry([preset]), dispatcher,
            scopes.GetRequiredService<IServiceScopeFactory>());
        var running = worker.ProcessAsync(owner.UserId!.Value, accepted.ClientRequestId,
            CancellationToken.None);
        await preset.Entered!.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await using (var observed = new PimDbContext(PostgresOptions(database.ConnectionString)))
        {
            var observedJob = await observed.AiAsrJobs.AsNoTracking().SingleAsync();
            var observedLog = await observed.AiRequestLogs.AsNoTracking().SingleAsync();
            Assert.True(observedJob.ProviderAttempted);
            using var metadata = JsonDocument.Parse(observedLog.MetadataJson);
            Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
        }
        await using (var expire = new PimDbContext(PostgresOptions(database.ConnectionString)))
        {
            await expire.AiAsrJobs.ExecuteUpdateAsync(update => update
                .SetProperty(x => x.ExecutionExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }

        var recovery = Task.Run(async () =>
        {
            await using var recoveryDb = new PimDbContext(PostgresOptions(database.ConnectionString));
            await AiAsrJobRecovery.RecoverExpiredAsync(recoveryDb, CancellationToken.None);
        });
        preset.Release!.SetResult();
        await Task.WhenAll(running, recovery).WaitAsync(TimeSpan.FromSeconds(20));

        await using var check = new PimDbContext(PostgresOptions(database.ConnectionString));
        var job = await check.AiAsrJobs.AsNoTracking().SingleAsync();
        var log = await check.AiRequestLogs.AsNoTracking().SingleAsync();
        Assert.Equal(job.Status, log.Status);
        Assert.True(job.Status is "succeeded" or "incomplete", job.Status);
        Assert.True(job.ProviderAttempted);
        using (var metadata = JsonDocument.Parse(log.MetadataJson))
            Assert.True(metadata.RootElement.GetProperty("providerAttempted").GetBoolean());
        Assert.Equal(1, preset.Calls);
    }

    private static AiAsrJobService Service(PimDbContext db, Owner owner, IFileAttachmentStore files,
        ISecretProtector secrets, AiAsrPresetRegistry registry, IAsrJobDispatcher dispatcher,
        IServiceScopeFactory scopes, AiUsageLimitService? usageLimits = null,
        AiTextCallGate? gate = null) =>
        new(db, secrets, owner, gate ?? new AiTextCallGate(), registry,
            new AiAsrMaterialService(db, files, owner, registry), dispatcher, scopes,
            NullLogger<AiAsrJobService>.Instance, usageLimits);

    private static ServiceProvider BuildPostgresScopes(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<PimDbContext>(options => options.UseNpgsql(connectionString));
        return services.BuildServiceProvider();
    }

    private static DbContextOptions<PimDbContext> PostgresOptions(string connectionString) =>
        new DbContextOptionsBuilder<PimDbContext>().UseNpgsql(connectionString).Options;

    private static async Task<AiModelProfileEntity> SeedPostgresAsync(PimDbContext db, Owner owner,
        ISecretProtector secrets)
    {
        db.Users.Add(User(owner));
        var profile = Profile(owner, secrets);
        db.AiModelProfiles.Add(profile);
        db.AiModelRuntime.Add(new AiModelRuntimeEntity
        {
            Id = AiModelRuntimeEntity.SingletonId,
            Enabled = true,
            Revision = Guid.NewGuid(),
            TranscriptionModelId = profile.Id
        });
        await db.SaveChangesAsync();
        return profile;
    }

    private static UserEntity User(Owner owner) => new()
    {
        Id = owner.UserId!.Value,
        Username = "asr-job-" + owner.UserId.Value.ToString("N")[..10],
        Email = "asr-job-synthetic@example.invalid",
        PasswordHash = "synthetic-not-a-login",
        Role = "admin",
        IsActive = true
    };

    private static AiModelProfileEntity Profile(Owner owner, ISecretProtector secrets) => new()
    {
        Name = "asr-job-synthetic-" + Guid.NewGuid().ToString("N")[..8],
        NormalizedName = "asr-job-synthetic-" + Guid.NewGuid().ToString("N"),
        BaseUrl = "https://api.synthetic.invalid/v1/",
        Model = ScriptedPreset.Model,
        Enabled = true,
        Capabilities = AiModelCapabilityFlags.Transcription,
        AsrPresetId = ScriptedPreset.PresetId,
        ApiKeySecret = secrets.Protect("synthetic-asr-job-key"),
        TimeoutSeconds = 10,
        MaxOutputTokens = 1024
    };

    private static AiAsrCallRequest Request(Owner owner, AiModelProfileEntity profile,
        byte[]? bytes = null)
    {
        bytes ??= BuildWave(3_200);
        return new(Guid.NewGuid(), owner.UserId!.Value,
            new AiAsrAudioRequest("synthetic.wav", "wav", Convert.ToBase64String(bytes)),
            new AiAsrDestinationConsent(profile.Id, profile.Revision,
                AiAsrPresetRegistry.OperationEndpoint(profile.BaseUrl, ScriptedPreset.Dto),
                ScriptedPreset.PresetId, ScriptedPreset.Dto.Version, true),
            "zh", profile.Id);
    }

    private static byte[] BuildWave(int dataBytes)
    {
        var size = Math.Max(44, dataBytes + 44);
        var bytes = new byte[size];
        "RIFF"u8.CopyTo(bytes);
        BitConverter.TryWriteBytes(bytes.AsSpan(4), size - 8);
        "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BitConverter.TryWriteBytes(bytes.AsSpan(16), 16);
        BitConverter.TryWriteBytes(bytes.AsSpan(20), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(22), (short)1);
        BitConverter.TryWriteBytes(bytes.AsSpan(24), 16_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(28), 32_000);
        BitConverter.TryWriteBytes(bytes.AsSpan(32), (short)2);
        BitConverter.TryWriteBytes(bytes.AsSpan(34), (short)16);
        "data"u8.CopyTo(bytes.AsSpan(36));
        BitConverter.TryWriteBytes(bytes.AsSpan(40), size - 44);
        return bytes;
    }

    private sealed class InMemoryFixture : IAsyncDisposable
    {
        private static readonly InMemoryDatabaseRoot SharedRoot = new();
        private readonly ServiceProvider _scopes;
        private readonly DbContextOptions<PimDbContext> _options;

        private InMemoryFixture(PimDbContext db, ServiceProvider scopes, Owner owner,
            MemoryFiles files, ISecretProtector secrets, AiModelProfileEntity profile,
            RecordingDispatcher dispatcher, DbContextOptions<PimDbContext> options)
        {
            Db = db;
            _scopes = scopes;
            Owner = owner;
            Files = files;
            Secrets = secrets;
            Profile = profile;
            Dispatcher = dispatcher;
            _options = options;
        }

        public PimDbContext Db { get; }
        public Owner Owner { get; }
        public MemoryFiles Files { get; }
        public ISecretProtector Secrets { get; }
        public AiModelProfileEntity Profile { get; }
        public RecordingDispatcher Dispatcher { get; }

        public static async Task<InMemoryFixture> CreateAsync()
        {
            var name = "asr-job-" + Guid.NewGuid().ToString("N");
            var options = new DbContextOptionsBuilder<PimDbContext>()
                .UseInMemoryDatabase(name, SharedRoot)
                .Options;
            var db = new PimDbContext(options);
            var owner = new Owner();
            var files = new MemoryFiles();
            ISecretProtector secrets =
                new DataProtectionSecretProtector(new EphemeralDataProtectionProvider());
            db.Users.Add(User(owner));
            var profile = Profile(owner, secrets);
            db.AiModelProfiles.Add(profile);
            db.AiModelRuntime.Add(new AiModelRuntimeEntity
            {
                Id = AiModelRuntimeEntity.SingletonId,
                Enabled = true,
                Revision = Guid.NewGuid(),
                TranscriptionModelId = profile.Id
            });
            await db.SaveChangesAsync();

            var services = new ServiceCollection();
            services.AddDbContext<PimDbContext>(builder =>
                builder.UseInMemoryDatabase(name, SharedRoot));
            return new InMemoryFixture(db, services.BuildServiceProvider(), owner, files, secrets,
                profile, new RecordingDispatcher(), options);
        }

        public static Task<InMemoryFixture> OpenAsync(InMemoryFixture source)
        {
            return Task.FromResult(new InMemoryFixture(new PimDbContext(source._options),
                source._scopes,
                source.Owner, source.Files, source.Secrets, source.Profile, source.Dispatcher,
                source._options));
        }

        public AiAsrJobService Service(Owner owner, ScriptedPreset? preset = null,
            AiUsageLimitService? usageLimits = null, AiTextCallGate? gate = null) =>
            AiAsrJobServiceTests.Service(Db, owner, Files, Secrets,
                new AiAsrPresetRegistry([preset ?? new ScriptedPreset()]), Dispatcher,
                _scopes.GetRequiredService<IServiceScopeFactory>(), usageLimits, gate);

        public AiAsrCallRequest Request(Owner? owner = null, byte[]? bytes = null) =>
            AiAsrJobServiceTests.Request(owner ?? Owner, Profile, bytes);

        public async Task AddUserAsync(Guid id)
        {
            Db.Users.Add(new UserEntity
            {
                Id = id,
                Username = "other-" + id.ToString("N")[..10],
                Email = "other-synthetic@example.invalid",
                PasswordHash = "synthetic-not-a-login",
                Role = "user",
                IsActive = true
            });
            await Db.SaveChangesAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await _scopes.DisposeAsync();
        }
    }

    private sealed class ScriptedPreset : IAiAsrPreset
    {
        public const string PresetId = MimoMultimodalPreset.PresetId;
        public const string Model = MimoMultimodalPreset.FlashModel;
        public static readonly AiAsrPresetDto Dto = new(PresetId, "1", "Synthetic durable ASR",
            "https://api.synthetic.invalid/v1/", "chat/completions", Model,
            Array.AsReadOnly(new[] { Model }), Array.AsReadOnly(new[] { "wav" }),
            Array.AsReadOnly(new[] { "auto", "zh", "en" }), 4 * 1024 * 1024, false);

        public AiAsrPresetDto Descriptor => Dto;
        public int Calls { get; private set; }
        public AiModelConnection? LastConnection { get; private set; }
        public Exception? Failure { get; init; }
        public TaskCompletionSource? Entered { get; init; }
        public TaskCompletionSource? Release { get; init; }
        public bool IgnoreCancellation { get; init; }
        public bool BlockUntilCancelled { get; init; }
        public Func<Task>? OnCall { get; set; }

        public string NormalizeEndpoint(string value) =>
            value.EndsWith('/') ? value : value + "/";

        public void ValidateModel(string model)
        {
            if (model != Model) throw new DomainException(40001, "synthetic model mismatch");
        }

        public string AuditRequest(byte[] sdkBody, AiModelConnection connection, AiAsrInput audio,
            Guid audioId, string language, AiAsrPromptSnapshot? prompt = null) => "{}";

        public async Task<AiAsrProviderResult> TranscribeAsync(AiModelConnection connection,
            AiAsrInput audio, string language, CancellationToken ct,
            AiAsrPromptSnapshot? prompt = null)
        {
            if (OnCall is not null) await OnCall();
            Calls++;
            LastConnection = connection;
            AiTextWireCapture.Current?.Attempt();
            Entered?.TrySetResult();
            if (Release is not null)
            {
                if (IgnoreCancellation) await Release.Task;
                else await Release.Task.WaitAsync(ct);
            }
            if (BlockUntilCancelled) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            if (Failure is not null) throw Failure;
            const string raw = """
                {"choices":[{"message":{"content":"完整后台转写"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":6,"total_tokens":10}}
                """;
            AiTextWireCapture.Current?.Response(Encoding.UTF8.GetBytes(raw));
            return new("完整后台转写", "stop", 4, 6, 10, raw, true, 0.2);
        }
    }

    private sealed class RecordingDispatcher : IAsrJobDispatcher
    {
        public bool IsAvailable { get; set; } = true;
        public List<(Guid Owner, Guid ClientRequestId)> Enqueued { get; } = [];

        public string Enqueue(Guid owner, Guid clientRequestId)
        {
            Enqueued.Add((owner, clientRequestId));
            return Guid.NewGuid().ToString("N");
        }
    }

    private sealed class MemoryFiles(TaskCompletionSource? entered = null,
        TaskCompletionSource? release = null) : IFileAttachmentStore
    {
        private readonly Dictionary<(Guid Owner, string Key), byte[]> _objects = [];

        public async Task<FileAttachmentRef> StoreAsync(Guid userId, string key, Stream content,
            string mime, long size, CancellationToken ct = default)
        {
            entered?.TrySetResult();
            if (release is not null) await release.Task.WaitAsync(ct);
            using var memory = new MemoryStream();
            await content.CopyToAsync(memory, ct);
            _objects[(userId, key)] = memory.ToArray();
            return new(key, "synthetic", userId);
        }

        public Task<Stream> OpenReadAsync(Guid userId, FileAttachmentRef reference,
            CancellationToken ct = default) =>
            Task.FromResult<Stream>(new MemoryStream(_objects[(userId, reference.ObjectKey)]));

        public Task<string?> GetDirectLinkAsync(Guid userId, FileAttachmentRef reference,
            CancellationToken ct = default) => Task.FromResult<string?>(null);

        public Task DeleteAsync(Guid userId, FileAttachmentRef reference,
            CancellationToken ct = default)
        {
            _objects.Remove((userId, reference.ObjectKey));
            return Task.CompletedTask;
        }
    }

    private sealed class Owner : ICurrentUserService
    {
        public Guid? UserId { get; } = Guid.NewGuid();
        public string? Username => "asr-job-synthetic";
        public string? Role => "admin";
    }
}
