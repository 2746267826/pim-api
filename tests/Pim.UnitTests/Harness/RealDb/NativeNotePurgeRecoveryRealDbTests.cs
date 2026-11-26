using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Operations;
using Pim.Module.NoteTaskSuggestions.Services;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// Q03 PostgreSQL evidence for permanent note purge recovery. The failure is injected at
/// the single SaveChanges that commits the deleted rows, audit row and db_committed phase,
/// after material/ASR/derived files have already been removed. A retry must converge from
/// the durable intent solely; no cleanup handle may depend on the deleted task files.
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class NativeNotePurgeRecoveryRealDbTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T02:00:00Z");

    private readonly Guid _user = Guid.Parse("eeeeeeee-1111-2222-3333-444444444444");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pim-q03-purge-recovery", Guid.NewGuid().ToString("N"));

    private TempMigrationDatabase? _temp;

    [SkippableFact]
    public async Task Purge_CommitRollbackAfterFilesDeleted_RetryUsesPersistedHandles()
    {
        RealDbTestConnection.SkipIfUnavailable();
        _temp = await TempMigrationDatabase.CreateAsync("wo_q03_note_purge_recovery");
        await _temp.MigrateAsync();
        SeedUser(_temp.Db);

        var options = Options.Create(new NoteMaterialUploadOptions
        {
            StorageRoot = _root,
            MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024
        });
        var materials = new NoteMaterialUploadStore(options, new FixedClock(Now));
        var derived = new NoteDerivedStore(options, new FixedClock(Now));
        var asr = new NoteAsrTaskStore(options);
        var note = await SeedNoteAsync(materials, _user, "回滚后重试的笔记");
        var asrTaskId = SeedAsrTask(note);
        var derivedTaskId = SeedDerivedTask(note);
        var asrLog = SeedAiLog(_temp.Db, "native-note-asr-task", asrTaskId);
        var derivedLog = SeedAiLog(_temp.Db, "native-note-derived-task", derivedTaskId);
        _temp.Db.AiCallImages.Add(new AiCallImageEntity
        {
            LogId = asrLog.Id,
            Name = "frame.png",
            ContentType = "image/png",
            SizeBytes = 4,
            Sha256 = "a"
        });
        _temp.Db.AiCallAudio.Add(new AiCallAudioEntity
        {
            LogId = derivedLog.Id,
            Name = "clip.wav",
            Format = "wav",
            SizeBytes = 4,
            Sha256 = "b"
        });
        await _temp.Db.SaveChangesAsync();
        await materials.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await materials.GetDeletedAsync(_user, note.NoteId);
        Assert.NotNull(deleted);

        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(_temp.ConnectionString)
            .Options;
        await using (var failingDb = new FailOnSavePimDbContext(dbOptions, failOnSave: 3))
        {
            var notes = new NativeNoteRecycleService(
                failingDb,
                materials,
                derived,
                asr,
                new AuditLogService(failingDb),
                purgeContributors: [new NoteTaskSuggestionPurgeContributor(failingDb)]);
            var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                notes.PurgeAsync(_user, note.NoteId, deleted!.DeletedAt));
            Assert.Contains("synthetic purge commit failure", failure.Message);
        }

        // Destructive work already happened, but PostgreSQL rolled back and the ledger
        // tombstone remains so a retry still has an anchor.
        await Assert.ThrowsAsync<DomainException>(() => materials.GetAsync(_user, note.VersionId));
        Assert.False(File.Exists(AsrPath(asrTaskId)));
        Assert.False(File.Exists(DerivedPath(derivedTaskId)));
        Assert.NotNull(await materials.GetDeletedAsync(_user, note.NoteId));

        await using (var verifyDb = new PimDbContext(dbOptions))
        {
            Assert.NotNull(await verifyDb.AiRequestLogs.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == asrLog.Id));
            Assert.NotNull(await verifyDb.AiRequestLogs.AsNoTracking()
                .SingleOrDefaultAsync(row => row.Id == derivedLog.Id));
            Assert.Equal(0, await verifyDb.AuditLogs.CountAsync(row =>
                row.Action == "recycle_bin.note.purge"));
            var intent = await verifyDb.RecycleBinPurgeIntents.AsNoTracking()
                .SingleAsync(row => row.UserId == _user
                    && row.ItemId == note.NoteId
                    && row.Domain == "note");
            Assert.Equal(RecycleBinPurgePhases.Pending, intent.Phase);
            Assert.Null(intent.LeaseToken);
            Assert.Contains(asrTaskId.ToString("D"), intent.AsrTaskIdsJson);
            Assert.Contains(derivedTaskId.ToString("D"), intent.DerivedTaskIdsJson);
            Assert.Contains(asrLog.Id.ToString("D"), intent.AiLogIdsJson);
            Assert.Contains(derivedLog.Id.ToString("D"), intent.AiLogIdsJson);
            Assert.Equal(1, intent.MaterialVersions);
            Assert.Equal(1, intent.AsrTasks);
            Assert.Equal(1, intent.DerivedTasks);
            Assert.Equal(2, intent.AiCalls);
        }

        await using var retryDb = new PimDbContext(dbOptions);
        var retryNotes = new NativeNoteRecycleService(
            retryDb,
            new NoteMaterialUploadStore(options, new FixedClock(Now)),
            new NoteDerivedStore(options, new FixedClock(Now)),
            new NoteAsrTaskStore(options),
            new AuditLogService(retryDb),
            purgeContributors: [new NoteTaskSuggestionPurgeContributor(retryDb)]);
        var result = await retryNotes.PurgeAsync(_user, note.NoteId, deleted!.DeletedAt);

        Assert.Equal(1, result.MaterialVersions);
        Assert.Equal(1, result.AsrTasks);
        Assert.Equal(1, result.DerivedTasks);
        Assert.Equal(2, result.AiCalls);
        Assert.Equal(1, result.AiImages);
        Assert.Equal(1, result.AiAudio);

        await using var finalDb = new PimDbContext(dbOptions);
        Assert.Null(await finalDb.AiRequestLogs.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == asrLog.Id));
        Assert.Null(await finalDb.AiRequestLogs.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == derivedLog.Id));
        Assert.Equal(0, await finalDb.AiCallImages.CountAsync());
        Assert.Equal(0, await finalDb.AiCallAudio.CountAsync());
        Assert.Equal(1, await finalDb.AuditLogs.CountAsync(row =>
            row.Action == "recycle_bin.note.purge"
            && row.ResourceId == note.NoteId.ToString("D")));
        Assert.Null(await materials.GetDeletedAsync(_user, note.NoteId));
        var completed = await finalDb.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.UserId == _user
                && row.ItemId == note.NoteId
                && row.Domain == "note");
        Assert.Equal(RecycleBinPurgePhases.Completed, completed.Phase);
        Assert.NotNull(completed.CompletedAt);
    }

    [SkippableFact]
    public async Task Purge_LedgerHandleWrittenButTombstoneNotCompleted_RetryFinishes()
    {
        RealDbTestConnection.SkipIfUnavailable();
        _temp = await TempMigrationDatabase.CreateAsync("wo_q03_note_ledger_recovery");
        await _temp.MigrateAsync();
        SeedUser(_temp.Db);

        var options = Options.Create(new NoteMaterialUploadOptions
        {
            StorageRoot = _root,
            MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024
        });
        var materials = new NoteMaterialUploadStore(options, new FixedClock(Now));
        var note = await SeedNoteAsync(materials, _user, "台账完成前中断的笔记");
        var asrTaskId = SeedAsrTask(note);
        var asrLog = SeedAiLog(_temp.Db, "native-note-asr-task", asrTaskId);
        await _temp.Db.SaveChangesAsync();
        await materials.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await materials.GetDeletedAsync(_user, note.NoteId);

        // Simulate a crash after ledger handles and material files were handled but before
        // CompletePurgeAsync removed the recycle-bin tombstone.
        var ledgerResult = await materials.PurgeDeletedAsync(
            _user, note.NoteId, deleted!.DeletedAt);
        Assert.Equal(1, ledgerResult.MaterialVersions);
        Assert.NotNull(await materials.GetDeletedAsync(_user, note.NoteId));
        await Assert.ThrowsAsync<DomainException>(() => materials.GetAsync(_user, note.VersionId));

        var dbOptions = new DbContextOptionsBuilder<PimDbContext>()
            .UseNpgsql(_temp.ConnectionString)
            .Options;
        await using var db = new PimDbContext(dbOptions);
        var notes = new NativeNoteRecycleService(
            db,
            new NoteMaterialUploadStore(options, new FixedClock(Now)),
            new NoteDerivedStore(options, new FixedClock(Now)),
            new NoteAsrTaskStore(options),
            new AuditLogService(db),
            purgeContributors: [new NoteTaskSuggestionPurgeContributor(db)]);

        var result = await notes.PurgeAsync(_user, note.NoteId, deleted.DeletedAt);

        Assert.Equal(1, result.MaterialVersions);
        Assert.Null(await materials.GetDeletedAsync(_user, note.NoteId));
        Assert.Null(await db.AiRequestLogs.AsNoTracking()
            .SingleOrDefaultAsync(row => row.Id == asrLog.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(row =>
            row.Action == "recycle_bin.note.purge"
            && row.ResourceId == note.NoteId.ToString("D")));
    }

    private void SeedUser(PimDbContext db)
    {
        db.Users.Add(new UserEntity
        {
            Id = _user,
            Username = "q03-purge-recovery",
            Email = "q03-purge-recovery@example.com",
            PasswordHash = "hash",
            Role = "user"
        });
    }

    private async Task<(Guid NoteId, Guid VersionId)> SeedNoteAsync(
        NoteMaterialUploadStore materials, Guid owner, string title)
    {
        var noteId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        var noteJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            id = noteId.ToString("D"),
            title,
            revision = 1,
            createdAtMillis = 1,
            blocks = new[] { new { id = blockId.ToString("D"), kind = "markdown", text = "正文" } }
        }, Json);
        var inputJson =
            $"{{\"schemaVersion\":1,\"id\":\"{versionId:D}\",\"createdAtMillis\":1,\"note\":{noteJson},\"recordings\":[]}}";
        var document = Encoding.UTF8.GetBytes("正文");
        var noteBytes = Encoding.UTF8.GetBytes(noteJson);
        var manifestJson = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            versionId = versionId.ToString("D"),
            noteId = noteId.ToString("D"),
            revision = 1,
            inputSha256 = Hash(inputJson),
            files = new object[]
            {
                new { path = "note.json", kind = "Document", sha256 = Hash(noteJson), sizeBytes = noteBytes.Length,
                    blockId = (string?)null, recordingId = (string?)null, segment = (int?)null,
                    chunkIndex = (int?)null, samples = (long?)null },
                new { path = "document.md", kind = "Markdown", sha256 = Hash(document), sizeBytes = document.Length,
                    blockId = (string?)null, recordingId = (string?)null, segment = (int?)null,
                    chunkIndex = (int?)null, samples = (long?)null }
            }
        }, Json);
        var request = new BeginNoteMaterialUploadRequest(
            versionId, noteId, null, inputJson, manifestJson, true);
        await materials.BeginAsync(owner, request);
        await materials.UploadPartAsync(owner, versionId, 0, 0, Hash(noteBytes), new MemoryStream(noteBytes));
        await materials.UploadPartAsync(owner, versionId, 1, 0, Hash(document), new MemoryStream(document));
        await materials.CompleteAsync(owner, versionId);
        return (noteId, versionId);
    }

    private Guid SeedAsrTask((Guid NoteId, Guid VersionId) note)
    {
        var taskId = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(AsrPath(taskId))!);
        File.WriteAllText(AsrPath(taskId), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            owner = _user,
            request = new
            {
                taskId,
                expectedUserId = _user,
                versionId = note.VersionId,
                consent = (object?)null,
                language = "auto"
            },
            fingerprint = "asr-fingerprint",
            noteId = note.NoteId,
            inputSha256 = "asr-input",
            manifestSha256 = "asr-manifest",
            steps = Array.Empty<object>()
        }, Json));
        return taskId;
    }

    private Guid SeedDerivedTask((Guid NoteId, Guid VersionId) note)
    {
        var taskId = Guid.NewGuid();
        Directory.CreateDirectory(Path.GetDirectoryName(DerivedPath(taskId))!);
        File.WriteAllText(DerivedPath(taskId), JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            owner = _user,
            request = new
            {
                taskId,
                expectedUserId = _user,
                versionId = note.VersionId,
                asrTaskId = (Guid?)null,
                consent = new { }
            },
            fingerprint = "derived-fingerprint",
            noteId = note.NoteId,
            inputSha256 = "derived-input",
            manifestSha256 = "derived-manifest",
            steps = Array.Empty<object>()
        }, Json));
        return taskId;
    }

    private AiRequestLogEntity SeedAiLog(PimDbContext db, string sourceType, Guid sourceId)
    {
        var log = new AiRequestLogEntity
        {
            UserId = _user,
            Module = "notes",
            Purpose = sourceType,
            SourceObjectType = sourceType,
            SourceObjectId = sourceId.ToString("D"),
            Status = "succeeded",
            Model = "test-model",
            CorrelationId = Guid.NewGuid().ToString("N")
        };
        db.AiRequestLogs.Add(log);
        return log;
    }

    private string AsrPath(Guid taskId) =>
        Path.Combine(_root, "asr-tasks", _user.ToString("D"), taskId.ToString("D") + ".json");

    private string DerivedPath(Guid taskId) =>
        Path.Combine(_root, "derived", _user.ToString("D"), "tasks", taskId.ToString("D") + ".json");

    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_temp is not null) await _temp.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FailOnSavePimDbContext(
        DbContextOptions<PimDbContext> options, int failOnSave) : PimDbContext(options)
    {
        private int _saveCount;

        public override async Task<int> SaveChangesAsync(
            CancellationToken cancellationToken = default)
        {
            var attempt = Interlocked.Increment(ref _saveCount);
            if (attempt == failOnSave)
                throw new InvalidOperationException(
                    "Synthetic purge commit failure after destructive file work.");
            return await base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
