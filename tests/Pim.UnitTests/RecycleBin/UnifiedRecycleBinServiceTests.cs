using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Api.DTOs;
using Pim.Api.Services;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Storage;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Providers.Local;
using Pim.Module.Files.Services;
using Pim.Module.NoteTaskSuggestions.Entities;
using Pim.Module.NoteTaskSuggestions.Services;
using Pim.UnitTests.Files;
using Xunit;

namespace Pim.UnitTests.RecycleBin;

public sealed class UnifiedRecycleBinServiceTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T02:00:00Z");

    private readonly Guid _user = Guid.Parse("aaaaaaaa-1111-2222-3333-444444444444");
    private readonly Guid _other = Guid.Parse("bbbbbbbb-1111-2222-3333-444444444444");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-q03-tests", Guid.NewGuid().ToString("N"));
    private readonly PimDbContext _db;
    private readonly NoteMaterialUploadStore _materials;
    private readonly NoteDerivedStore _derived;
    private readonly NoteAsrTaskStore _asr;
    private readonly NativeNoteRecycleService _notes;
    private readonly CalendarRecycleBinService _calendar;
    private readonly AuditLogService _audit;
    private readonly FileWriteService _files;
    private readonly LocalFileStore _localStore;
    private readonly LocalFileProvider _localProvider;
    private readonly ManagedBinaryStore _binaries;
    private readonly FilesProviderHarness _providerHarness;
    private readonly UnifiedRecycleBinService _service;

    public UnifiedRecycleBinServiceTests()
    {
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(NoteTaskSuggestionExtractionEntity).Assembly);
        _db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase("q03-" + Guid.NewGuid())
            .Options);
        var noteOptions = Options.Create(new NoteMaterialUploadOptions
        {
            StorageRoot = _root,
            MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024
        });
        var clock = new FixedClock(Now);
        _materials = new NoteMaterialUploadStore(noteOptions, clock);
        _derived = new NoteDerivedStore(noteOptions, clock);
        _asr = new NoteAsrTaskStore(noteOptions);
        var processing = new NoteProcessingStore(noteOptions, clock);
        _audit = new AuditLogService(_db);
        _notes = new NativeNoteRecycleService(_db, _materials, _derived, _asr, _audit,
            processing: processing,
            purgeContributors: [new NoteTaskSuggestionPurgeContributor(_db)]);
        var user = new FixedCurrentUser(_user);
        _calendar = new CalendarRecycleBinService(_db, user, new CalendarAuditWriter(_audit));

        _binaries = new ManagedBinaryStore(Options.Create(new ManagedBinaryOptions
        {
            Root = Path.Combine(_root, "managed")
        }));
        _localStore = new LocalFileStore(_binaries);
        var grants = new LocalFileGrants(
            new EphemeralDataProtectionProvider(), _localStore, new HttpContextAccessor(), clock);
        _localProvider = new LocalFileProvider(_localStore, grants);
        _providerHarness = new FilesProviderHarness(_db, clock: clock, extraProvider: _localProvider);
        _files = new FileWriteService(
            _db,
            _providerHarness.Sessions,
            _providerHarness.Providers,
            user,
            _audit,
            new SensitivePathPolicy(null),
            NullLogger<FileWriteService>.Instance,
            clock);
        _service = new UnifiedRecycleBinService(
            _db, user, _calendar, _notes, _files, _localStore, _audit,
            NullLogger<UnifiedRecycleBinService>.Instance);
    }

    [Fact]
    public async Task ListAsync_GroupsFourDomains_AndKeepsSourcesAndUsersIsolated()
    {
        var note = await SeedNoteAsync(_user, "回收站里的笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var localFile = await SeedLocalFileAsync("内置报告.txt");
        await _files.DeleteToTrashAsync(localFile.Id);
        var oneDrive = SeedExternalFile(_user, "onedrive", "OneDrive 合同.docx", deleted: true);
        var google = SeedExternalFile(_user, "google", "Google 预算.xlsx", deleted: true);
        var calendar = SeedCalendar(_user, "工作", "calendar", deleted: true);
        var taskBook = SeedCalendar(_user, "任务本", "task", deleted: true);
        var evt = SeedEvent(calendar, "已删日程", deleted: true);
        var task = SeedTask(taskBook, "已删任务", deleted: true);
        SeedEvent(calendar, "活跃日程");
        SeedTask(taskBook, "活跃任务");
        var otherNote = await SeedNoteAsync(_other, "别人的笔记");
        await _notes.SoftDeleteAsync(_other, otherNote.NoteId);
        SeedExternalFile(_other, "onedrive", "别人的文件", deleted: true);
        await _db.SaveChangesAsync();

        var page = await _service.ListAsync(null, null, null, null, 1, 100);

        Assert.Equal(8, page.TotalCount);
        Assert.Equal(4, page.Groups.Count);
        Assert.Equal(1, page.Groups.Single(g => g.Domain == RecycleBinDomains.Note).TotalCount);
        Assert.Equal(3, page.Groups.Single(g => g.Domain == RecycleBinDomains.File).TotalCount);
        Assert.Equal(2, page.Groups.Single(g => g.Domain == RecycleBinDomains.Task).TotalCount);
        Assert.Equal(2, page.Groups.Single(g => g.Domain == RecycleBinDomains.Calendar).TotalCount);
        Assert.DoesNotContain(page.Items, item => item.Title == "别人的笔记" || item.Title == "别人的文件");
        Assert.DoesNotContain(page.Items, item => item.Title is "活跃日程" or "活跃任务");
        Assert.Equal("builtin", page.Items.Single(item => item.Id == localFile.Id).Source);
        Assert.Equal("onedrive", page.Items.Single(item => item.Id == oneDrive.Id).Source);
        Assert.Equal("google", page.Items.Single(item => item.Id == google.Id).Source);
        Assert.True(page.Items.Single(item => item.Id == oneDrive.Id).SourcePending);
        Assert.True(page.Items.Single(item => item.Id == google.Id).SourcePending);
        Assert.False(page.Items.Single(item => item.Id == localFile.Id).SourcePending);
        Assert.Contains(page.Items, item => item.Id == evt.Id && item.Domain == RecycleBinDomains.Calendar);
        Assert.Contains(page.Items, item => item.Id == task.Id && item.Domain == RecycleBinDomains.Task);
        Assert.Contains(page.Items, item => item.Id == taskBook.Id && item.Domain == RecycleBinDomains.Task);
        Assert.Contains(page.Items, item => item.Id == calendar.Id && item.Domain == RecycleBinDomains.Calendar);
    }

    [Fact]
    public async Task BatchRestore_ReturnsPerItemPartialReceipt_AndDoesNotTouchAnotherUsersItem()
    {
        var note = await SeedNoteAsync(_user, "我的笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var external = SeedExternalFile(_user, "onedrive", "云端文件.txt", deleted: true);
        var otherEvent = SeedEvent(SeedCalendar(_other, "别人", "calendar", deleted: true), "别人的日程", deleted: true);
        await _db.SaveChangesAsync();
        var missing = Guid.NewGuid();

        var receipt = await _service.RestoreAsync(Batch(
            (RecycleBinDomains.Note, note.NoteId),
            (RecycleBinDomains.File, external.Id),
            (RecycleBinDomains.Calendar, otherEvent.Id),
            (RecycleBinDomains.File, missing)));

        Assert.Equal(1, receipt.Succeeded);
        Assert.Equal(1, receipt.SourcePending);
        Assert.Equal(2, receipt.Failed);
        Assert.Equal("success", receipt.Items.Single(i => i.Id == note.NoteId).Status);
        Assert.Equal("source_pending", receipt.Items.Single(i => i.Id == external.Id).Status);
        Assert.Equal("failed", receipt.Items.Single(i => i.Id == otherEvent.Id).Status);
        Assert.Equal("failed", receipt.Items.Single(i => i.Id == missing).Status);

        var current = await _materials.GetCurrentAsync(_user, note.NoteId);
        Assert.Equal(note.VersionId, current.Upload.VersionId);
        Assert.True((await _db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(e => e.Id == otherEvent.Id)).DeletedAt is not null);
        Assert.True((await _db.Set<FileItemEntity>().SingleAsync(f => f.Id == external.Id)).IsDeleted);
    }

    [Fact]
    public async Task ListAsync_DomainFilterKeepsAllFourGroupCounters()
    {
        var note = await SeedNoteAsync(_user, "计数笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var file = SeedExternalFile(_user, "onedrive", "计数文件.txt", deleted: true);
        var book = SeedCalendar(_user, "计数日历", "calendar", deleted: true);
        SeedEvent(book, "计数日程", deleted: true);
        var taskBook = SeedCalendar(_user, "计数任务本", "task", deleted: true);
        SeedTask(taskBook, "计数任务", deleted: true);
        await _db.SaveChangesAsync();

        var page = await _service.ListAsync(RecycleBinDomains.File, null, null, null, 1, 50);

        Assert.Equal(1, page.TotalCount);
        Assert.All(page.Items, item => Assert.Equal(RecycleBinDomains.File, item.Domain));
        Assert.Equal(1, page.Groups.Single(group => group.Domain == RecycleBinDomains.Note).TotalCount);
        Assert.Equal(1, page.Groups.Single(group => group.Domain == RecycleBinDomains.File).TotalCount);
        Assert.Equal(2, page.Groups.Single(group => group.Domain == RecycleBinDomains.Task).TotalCount);
        Assert.Equal(2, page.Groups.Single(group => group.Domain == RecycleBinDomains.Calendar).TotalCount);
    }

    [Fact]
    public async Task Purge_ActiveExternalFileFailsClosed()
    {
        var active = SeedExternalFile(_user, "onedrive", "仍在使用的云端文件.txt", deleted: false);
        await _db.SaveChangesAsync();

        var receipt = await _service.PurgeAsync(Batch((RecycleBinDomains.File, active.Id)));

        var result = Assert.Single(receipt.Items);
        Assert.Equal(RecycleBinItemStatuses.Failed, result.Status);
        Assert.False((await _db.Set<FileItemEntity>().SingleAsync(row => row.Id == active.Id)).IsDeleted);
    }

    [Fact]
    public async Task EmptyBatchIsRejectedInsteadOfReturningAConfusingReceipt()
    {
        await Assert.ThrowsAsync<DomainException>(() =>
            _service.RestoreAsync(new RecycleBinBatchRequest([])));
    }

    [Fact]
    public async Task BatchRestore_RetryIsIdempotentAndDeduplicatesSuccesses()
    {
        var note = await SeedNoteAsync(_user, "幂等笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var missing = Guid.NewGuid();
        var request = Batch(
            (RecycleBinDomains.Note, note.NoteId),
            (RecycleBinDomains.Note, note.NoteId),
            (RecycleBinDomains.File, missing));

        var first = await _service.RestoreAsync(request);
        var second = await _service.RestoreAsync(request);

        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Equal("success", first.Items.Single(i => i.Id == note.NoteId).Status);
        Assert.Equal("success", second.Items.Single(i => i.Id == note.NoteId).Status);
        Assert.Contains("无需重复", second.Items.Single(i => i.Id == note.NoteId).Message);
        Assert.Equal("failed", second.Items.Single(i => i.Id == missing).Status);
        var summaries = await _materials.ListNativeNotesAsync(_user, 0, 50);
        Assert.Single(summaries.Items.Where(i => i.NoteId == note.NoteId));
    }

    [Fact]
    public async Task BatchRestore_SameOperationIdReplaysTheStoredReceipt()
    {
        var note = await SeedNoteAsync(_user, "持久幂等回执笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var operationId = Guid.NewGuid();
        var request = new RecycleBinBatchRequest(
            [new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId)],
            operationId);

        var first = await _service.RestoreAsync(request);
        var second = await _service.RestoreAsync(request);

        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(operationId, first.OperationId);
        Assert.Equal(operationId, second.OperationId);
        Assert.Equal(first.Items, second.Items);
        Assert.Single(await _db.RecycleBinOperations.ToListAsync());
        Assert.Equal(1, await _db.AuditLogs.CountAsync(row =>
            row.Action == "recycle_bin.note.restore"
            && row.ResourceId == note.NoteId.ToString("D")));
    }

    [Fact]
    public async Task BatchRestore_SameOperationIdWithDifferentPayloadIsAConflict()
    {
        var note = await SeedNoteAsync(_user, "冲突载荷笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var operationId = Guid.NewGuid();

        var first = await _service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId)
        ], operationId));
        Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(first.Items).Status);

        var conflict = await Assert.ThrowsAsync<DomainException>(() =>
            _service.RestoreAsync(new RecycleBinBatchRequest(
            [
                new RecycleBinItemRef(
                    RecycleBinDomains.Note,
                    note.NoteId,
                    DateTimeOffset.UtcNow.AddMinutes(-1))
            ], operationId)));

        Assert.Equal(40902, conflict.ErrorCode);
        Assert.Single(await _db.RecycleBinOperations.ToListAsync());
    }

    [Fact]
    public async Task BatchRestore_WithoutOperationIdStillMintsANewReceiptPerRequest()
    {
        var note = await SeedNoteAsync(_user, "无幂等键笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);

        var first = await _service.RestoreAsync(Batch((RecycleBinDomains.Note, note.NoteId)));
        var second = await _service.RestoreAsync(Batch((RecycleBinDomains.Note, note.NoteId)));

        Assert.NotEqual(first.ReceiptId, second.ReceiptId);
        Assert.Null(first.OperationId);
        Assert.Null(second.OperationId);
        Assert.Empty(await _db.RecycleBinOperations.ToListAsync());
    }

    [Fact]
    public async Task Operation_StaleProcessingTokenCannotRenewCompleteOrReleaseNewClaim()
    {
        var operationKey = Guid.NewGuid();
        var first = await _service.ClaimOperationAsync(
            operationKey, "restore", "payload", default);
        var staleToken = first.Operation.ProcessingToken;
        Assert.Null(first.Replay);

        first.Operation.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var second = await _service.ClaimOperationAsync(
            operationKey, "restore", "payload", default);
        var newToken = second.Operation.ProcessingToken;
        Assert.NotEqual(staleToken, newToken);

        await _service.ReleaseOperationAsync(first.Operation.Id, staleToken, default);
        var afterRelease = await _db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.Id == first.Operation.Id);
        Assert.Equal(RecycleBinOperationStatuses.Running, afterRelease.Status);
        Assert.Equal(newToken, afterRelease.ProcessingToken);
        Assert.True(afterRelease.LeaseExpiresAt > DateTimeOffset.UtcNow);

        var staleRenew = await Assert.ThrowsAsync<DomainException>(() =>
            _service.RenewOperationLeaseAsync(first.Operation.Id, staleToken, default));
        Assert.Equal(40904, staleRenew.ErrorCode);

        var receipt = new RecycleBinBatchReceiptDto(
            Guid.NewGuid(), operationKey, "restore", 0, 0, 0, []);
        var staleComplete = await Assert.ThrowsAsync<DomainException>(() =>
            _service.CompleteOperationAsync(
                first.Operation.Id, staleToken, receipt, default));
        Assert.Equal(40904, staleComplete.ErrorCode);

        var afterStale = await _db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.Id == first.Operation.Id);
        Assert.Equal(RecycleBinOperationStatuses.Running, afterStale.Status);
        Assert.Equal(newToken, afterStale.ProcessingToken);
        Assert.Null(afterStale.ReceiptJson);

        await _service.RenewOperationLeaseAsync(first.Operation.Id, newToken, default);
        var completed = await _service.CompleteOperationAsync(
            first.Operation.Id, newToken, receipt, default);
        Assert.Equal(receipt.ReceiptId, completed.ReceiptId);
        var final = await _db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.Id == first.Operation.Id);
        Assert.Equal(RecycleBinOperationStatuses.Completed, final.Status);
        Assert.NotNull(final.ReceiptJson);
    }

    [Fact]
    public async Task BatchRestore_StaleExpectedDeletedAtFailsClosed()
    {
        var note = await SeedNoteAsync(_user, "需要刷新的笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var localFile = await SeedLocalFileAsync("需要刷新的文件.txt");
        await _files.DeleteToTrashAsync(localFile.Id);
        var deletedNote = await _materials.GetDeletedAsync(_user, note.NoteId);
        var fileRow = await _db.Set<FileItemEntity>().SingleAsync(row => row.Id == localFile.Id);

        var receipt = await _service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId, deletedNote!.DeletedAt.AddSeconds(-1)),
            new RecycleBinItemRef(RecycleBinDomains.File, localFile.Id, fileRow.DeletedAt!.Value.AddSeconds(-1))
        ]));

        Assert.Equal(2, receipt.Failed);
        Assert.All(receipt.Items, item => Assert.Equal(40901, item.ErrorCode));
        Assert.NotNull(await _materials.GetDeletedAsync(_user, note.NoteId));
        Assert.True((await _db.Set<FileItemEntity>().SingleAsync(row => row.Id == localFile.Id)).IsDeleted);
    }

    [Fact]
    public async Task BatchRestore_LegacyFileWithoutDeletedAt_UsesModifiedAtCasToken()
    {
        var localFile = await SeedLocalFileAsync("历史软删文件.txt");
        await _files.DeleteToTrashAsync(localFile.Id);
        var row = await _db.Set<FileItemEntity>().SingleAsync(candidate => candidate.Id == localFile.Id);
        row.DeletedAt = null;
        await _db.SaveChangesAsync();

        var listed = (await _service.ListAsync(null, null, null, null, 1, 50))
            .Items.Single(item => item.Id == localFile.Id);
        Assert.Equal(row.ModifiedAt, listed.DeletedAt);

        var receipt = await _service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.File, localFile.Id, listed.DeletedAt)
        ]));

        Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(receipt.Items).Status);
        Assert.False((await _db.Set<FileItemEntity>().SingleAsync(candidate => candidate.Id == localFile.Id)).IsDeleted);
        Assert.NotNull(await _localStore.GetAsync(_user, localFile.ProviderId, localFile.ExternalFileId));
    }

    [Fact]
    public async Task BatchRestore_CalendarStaleExpectedDeletedAtFailsClosed()
    {
        var book = SeedCalendar(_user, "需要刷新的日历", "calendar", deleted: false);
        var evt = SeedEvent(book, "需要刷新的日程", deleted: true);
        await _db.SaveChangesAsync();

        var receipt = await _service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(
                RecycleBinDomains.Calendar,
                evt.Id,
                evt.DeletedAt!.Value.AddSeconds(-1))
        ]));

        var result = Assert.Single(receipt.Items);
        Assert.Equal(RecycleBinItemStatuses.Failed, result.Status);
        Assert.Equal(40901, result.ErrorCode);
        Assert.NotNull((await _db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(row => row.Id == evt.Id)).DeletedAt);
    }

    [Fact]
    public async Task BatchRestore_CopyRequestNeverSilentlyRestoresNoteOrFile()
    {
        var note = await SeedNoteAsync(_user, "不能静默恢复原位的笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var localFile = await SeedLocalFileAsync("不能静默恢复原位的文件.txt");
        await _files.DeleteToTrashAsync(localFile.Id);

        var receipt = await _service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId),
            new RecycleBinItemRef(RecycleBinDomains.File, localFile.Id)
        ], RestoreAsCopy: true));

        Assert.Equal(2, receipt.Failed);
        Assert.All(receipt.Items, item => Assert.Equal(40001, item.ErrorCode));
        Assert.NotNull(await _materials.GetDeletedAsync(_user, note.NoteId));
        Assert.True((await _db.Set<FileItemEntity>().SingleAsync(row => row.Id == localFile.Id)).IsDeleted);
    }

    [Fact]
    public async Task BatchPurge_SameOperationIdReplaysWholeReceiptAfterApplied()
    {
        var note = await SeedNoteAsync(_user, "幂等永久删除笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var localFile = await SeedLocalFileAsync("幂等永久删除文件.txt");
        await _files.DeleteToTrashAsync(localFile.Id);
        var book = SeedCalendar(_user, "幂等永久删除日历", "calendar", deleted: false);
        var evt = SeedEvent(book, "幂等永久删除日程", deleted: true);
        await _db.SaveChangesAsync();

        var deletedNote = await _materials.GetDeletedAsync(_user, note.NoteId);
        var fileRow = await _db.Set<FileItemEntity>().SingleAsync(row => row.Id == localFile.Id);
        var request = new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId, deletedNote!.DeletedAt),
            new RecycleBinItemRef(RecycleBinDomains.File, localFile.Id, fileRow.DeletedAt),
            new RecycleBinItemRef(RecycleBinDomains.Calendar, evt.Id, evt.DeletedAt)
        ], Guid.NewGuid());

        var first = await _service.PurgeAsync(request);
        var second = await _service.PurgeAsync(request);

        Assert.Equal(3, first.Succeeded);
        Assert.Equal(3, second.Succeeded);
        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(first.Items, second.Items);
    }

    [Fact]
    public async Task BatchPurge_WithoutProofDoesNotClaimMissingFileOrCalendarWasPurged()
    {
        var note = await SeedNoteAsync(_user, "有完成意图证明的笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var localFile = await SeedLocalFileAsync("没有持久回执的文件.txt");
        await _files.DeleteToTrashAsync(localFile.Id);
        var book = SeedCalendar(_user, "没有持久回执的日历", "calendar", deleted: false);
        var evt = SeedEvent(book, "没有持久回执的日程", deleted: true);
        await _db.SaveChangesAsync();

        // The note purge commits its own durable intent. The file/calendar rows disappear
        // without any purge receipt, so absence alone must not be reported as "purged".
        var noteOnly = await _service.PurgeAsync(Batch((RecycleBinDomains.Note, note.NoteId)));
        Assert.Equal(1, noteOnly.Succeeded);
        _db.Set<FileItemEntity>().Remove(
            await _db.Set<FileItemEntity>().SingleAsync(row => row.Id == localFile.Id));
        _db.Set<EventEntity>().Remove(
            await _db.Set<EventEntity>().IgnoreQueryFilters()
                .SingleAsync(row => row.Id == evt.Id));
        await _db.SaveChangesAsync();

        var second = await _service.PurgeAsync(Batch(
            (RecycleBinDomains.Note, note.NoteId),
            (RecycleBinDomains.File, localFile.Id),
            (RecycleBinDomains.Calendar, evt.Id)));

        var noteRetry = second.Items.Single(item => item.Id == note.NoteId);
        Assert.Equal(RecycleBinItemStatuses.Success, noteRetry.Status);
        Assert.Contains("无需重复", noteRetry.Message);
        var fileRetry = second.Items.Single(item => item.Id == localFile.Id);
        var calendarRetry = second.Items.Single(item => item.Id == evt.Id);
        Assert.Equal(RecycleBinItemStatuses.Failed, fileRetry.Status);
        Assert.Equal(40401, fileRetry.ErrorCode);
        Assert.Equal(RecycleBinItemStatuses.Failed, calendarRetry.Status);
        Assert.Equal(40401, calendarRetry.ErrorCode);
    }

    [Fact]
    public async Task BatchPurge_CrashBeforeReceiptWrite_ReplayReconcilesCommittedItemsWithoutFailure()
    {
        var note = await SeedNoteAsync(_user, "崩溃窗口笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var localFile = await SeedLocalFileAsync("崩溃窗口文件.txt");
        await _files.DeleteToTrashAsync(localFile.Id);
        var book = SeedCalendar(_user, "崩溃窗口日历", "calendar", deleted: false);
        var evt = SeedEvent(book, "崩溃窗口日程", deleted: true);
        await _db.SaveChangesAsync();
        var fileDeletedAt = (await _db.Set<FileItemEntity>()
            .SingleAsync(row => row.Id == localFile.Id)).DeletedAt;
        var eventDeletedAt = evt.DeletedAt;

        var operationId = Guid.NewGuid();
        var request = new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId),
            new RecycleBinItemRef(RecycleBinDomains.File, localFile.Id),
            new RecycleBinItemRef(RecycleBinDomains.Calendar, evt.Id)
        ], operationId);

        // Commit every item's destructive work, then rewind the operation row to exactly the
        // state a crash before CompleteOperationAsync would leave: still Running, no receipt,
        // expired lease. All business rows and per-item receipts stay committed.
        var first = await _service.PurgeAsync(request);
        Assert.Equal(3, first.Succeeded);
        var operationRow = await _db.RecycleBinOperations
            .SingleAsync(row => row.OperationId == operationId);
        operationRow.Status = RecycleBinOperationStatuses.Running;
        operationRow.ReceiptJson = null;
        operationRow.CompletedAt = null;
        operationRow.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var interrupted = await _db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.OperationId == operationId);
        Assert.Equal(RecycleBinOperationStatuses.Running, interrupted.Status);
        Assert.Null(interrupted.ReceiptJson);
        Assert.True(interrupted.LeaseExpiresAt <= DateTimeOffset.UtcNow);
        Assert.Null(await _db.Set<FileItemEntity>().SingleOrDefaultAsync(row => row.Id == localFile.Id));
        Assert.Null(await _db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(row => row.Id == evt.Id));

        var replay = await _service.PurgeAsync(request);

        Assert.Equal(3, replay.Succeeded);
        Assert.Equal(0, replay.Failed);
        Assert.All(replay.Items, item => Assert.Equal(RecycleBinItemStatuses.Success, item.Status));
        Assert.Equal(interrupted.Id, replay.ReceiptId);
        Assert.Equal(operationId, replay.OperationId);

        var fileReceipt = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.Domain == RecycleBinPurgeDomains.File
                && row.ItemId == localFile.Id);
        Assert.Equal(RecycleBinPurgePhases.Completed, fileReceipt.Phase);
        Assert.Equal(
            RecycleBinPurgeGenerations.FromDeletedAt(fileDeletedAt!.Value),
            fileReceipt.Generation);
        var calendarReceipt = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.Domain == RecycleBinPurgeDomains.Calendar
                && row.ItemId == evt.Id);
        Assert.Equal(RecycleBinPurgePhases.Completed, calendarReceipt.Phase);
        Assert.Equal(
            RecycleBinPurgeGenerations.FromDeletedAt(eventDeletedAt!.Value),
            calendarReceipt.Generation);

        // Reconciliation must replay the durable outcome, not delete/audit a second time.
        Assert.Equal(1, await _db.AuditLogs.CountAsync(row =>
            row.Action == "files.recycle_bin.purge"
                && row.ResourceId == localFile.Id.ToString("D")));
        Assert.Equal(1, await _db.AuditLogs.CountAsync(row =>
            row.Action == "calendar.recycle_bin.purge"
                && row.ResourceId == evt.Id.ToString("D")));
        var completed = await _db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.OperationId == operationId);
        Assert.Equal(RecycleBinOperationStatuses.Completed, completed.Status);
        Assert.NotNull(completed.ReceiptJson);
    }

    [Fact]
    public async Task BatchPurge_WithExpectedTokenRequiresProvenState()
    {
        var activeNote = await SeedNoteAsync(_user, "仍在笔记库的笔记");
        var foreignNote = await SeedNoteAsync(_other, "别人的已删笔记");
        await _notes.SoftDeleteAsync(_other, foreignNote.NoteId);
        var foreignDeleted = await _materials.GetDeletedAsync(_other, foreignNote.NoteId);
        var missingNote = Guid.NewGuid();
        var missingFile = Guid.NewGuid();
        var missingCalendar = Guid.NewGuid();
        var expected = Now.AddMinutes(-1);

        var receipt = await _service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, activeNote.NoteId, expected),
            new RecycleBinItemRef(RecycleBinDomains.Note, missingNote, expected),
            new RecycleBinItemRef(RecycleBinDomains.Note, foreignNote.NoteId, foreignDeleted!.DeletedAt),
            new RecycleBinItemRef(RecycleBinDomains.File, missingFile, expected),
            new RecycleBinItemRef(RecycleBinDomains.Calendar, missingCalendar, expected)
        ]));

        Assert.Equal(5, receipt.Failed);
        Assert.All(receipt.Items, item => Assert.Equal(RecycleBinItemStatuses.Failed, item.Status));
        Assert.Equal(40901, receipt.Items.Single(item => item.Id == activeNote.NoteId).ErrorCode);
        Assert.Equal(40401, receipt.Items.Single(item => item.Id == missingNote).ErrorCode);
        Assert.Equal(40401, receipt.Items.Single(item => item.Id == foreignNote.NoteId).ErrorCode);
        Assert.Equal(40401, receipt.Items.Single(item => item.Id == missingFile).ErrorCode);
        Assert.Equal(40401, receipt.Items.Single(item => item.Id == missingCalendar).ErrorCode);
        Assert.Equal(activeNote.VersionId,
            (await _materials.GetCurrentAsync(_user, activeNote.NoteId)).Upload.VersionId);
        Assert.NotNull(await _materials.GetDeletedAsync(_other, foreignNote.NoteId));
    }

    [Fact]
    public async Task ExternalFiles_StaySourcePending_ForBothRestoreAndPurge()
    {
        var oneDrive = SeedExternalFile(_user, "onedrive", "云盘恢复.txt", deleted: true);
        var google = SeedExternalFile(_user, "google", "云盘清理.txt", deleted: true);
        await _db.SaveChangesAsync();

        var restored = await _service.RestoreAsync(Batch(
            (RecycleBinDomains.File, oneDrive.Id),
            (RecycleBinDomains.File, google.Id)));
        var purged = await _service.PurgeAsync(Batch(
            (RecycleBinDomains.File, oneDrive.Id),
            (RecycleBinDomains.File, google.Id)));

        Assert.All(restored.Items, item => Assert.Equal("source_pending", item.Status));
        Assert.All(purged.Items, item => Assert.Equal("source_pending", item.Status));
        Assert.Contains("OneDrive", restored.Items.Single(i => i.Id == oneDrive.Id).Message);
        Assert.Contains("Google Drive", purged.Items.Single(i => i.Id == google.Id).Message);
        Assert.True((await _db.Set<FileItemEntity>().SingleAsync(f => f.Id == oneDrive.Id)).IsDeleted);
        Assert.True((await _db.Set<FileItemEntity>().SingleAsync(f => f.Id == google.Id)).IsDeleted);
    }

    [Fact]
    public async Task LocalFilePurge_RemovesMetadataAndBlob_WhileExternalPurgeLeavesSourcePending()
    {
        var local = await SeedLocalFileAsync("永久清理.bin");
        var providerId = local.ProviderId;
        var externalId = local.ExternalFileId;
        var blobPath = _localStore.Binaries.ObjectPath(
            _user, ManagedBinaryStore.Files, providerId,
            (await _localStore.GetAsync(_user, providerId, externalId)).BlobKey!);
        await _files.DeleteToTrashAsync(local.Id);
        var external = SeedExternalFile(_user, "onedrive", "保留云端.txt", deleted: true);
        await _db.SaveChangesAsync();

        var receipt = await _service.PurgeAsync(Batch(
            (RecycleBinDomains.File, local.Id),
            (RecycleBinDomains.File, external.Id)));

        Assert.Equal("success", receipt.Items.Single(i => i.Id == local.Id).Status);
        Assert.Equal("source_pending", receipt.Items.Single(i => i.Id == external.Id).Status);
        Assert.Null(await _db.Set<FileItemEntity>().SingleOrDefaultAsync(f => f.Id == local.Id));
        Assert.NotNull(await _db.Set<FileItemEntity>().SingleOrDefaultAsync(f => f.Id == external.Id));
        Assert.False(File.Exists(blobPath));
        await Assert.ThrowsAsync<ProviderItemNotFoundException>(() =>
            _localStore.GetAsync(_user, providerId, externalId));
    }

    [Fact]
    public async Task LocalFilePurge_StaleExpectedDeletedAtKeepsCatalogAndBlob()
    {
        var local = await SeedLocalFileAsync("需要刷新的清理.bin");
        var providerId = local.ProviderId;
        var externalId = local.ExternalFileId;
        var item = await _localStore.GetAsync(_user, providerId, externalId);
        var blobPath = _localStore.Binaries.ObjectPath(
            _user, ManagedBinaryStore.Files, providerId, item.BlobKey!);
        await _files.DeleteToTrashAsync(local.Id);
        var row = await _db.Set<FileItemEntity>().SingleAsync(candidate => candidate.Id == local.Id);

        var receipt = await _service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(
                RecycleBinDomains.File,
                local.Id,
                row.DeletedAt!.Value.AddSeconds(-1))
        ]));

        Assert.Equal("failed", Assert.Single(receipt.Items).Status);
        Assert.NotNull(await _db.Set<FileItemEntity>().SingleOrDefaultAsync(candidate => candidate.Id == local.Id));
        Assert.True(File.Exists(blobPath));
        var catalogEntry = await _localStore.TransactionAsync(
            _user,
            providerId,
            entries => Task.FromResult(entries[externalId]),
            write: false,
            CancellationToken.None);
        Assert.True(catalogEntry.Trashed);
    }

    [Fact]
    public async Task NoteSoftDelete_PreservesMaterialsAsrAndDerivedArtifacts_UntilPermanentPurge()
    {
        var note = await SeedNoteAsync(_user, "保留原稿的笔记");
        var asrTaskId = SeedAsrTask(note);
        var derivedTaskId = SeedDerivedTask(note);
        var asrLog = SeedAiLog("native-note-asr-task", asrTaskId);
        var derivedLog = SeedAiLog("native-note-derived-task", derivedTaskId);
        _db.AddRange(
            new AiCallImageEntity { LogId = asrLog.Id, Name = "frame.png", ContentType = "image/png", SizeBytes = 4, Sha256 = "a" },
            new AiCallAudioEntity { LogId = derivedLog.Id, Name = "clip.wav", Format = "wav", SizeBytes = 4, Sha256 = "b" });
        await _db.SaveChangesAsync();

        await _notes.SoftDeleteAsync(_user, note.NoteId);

        Assert.NotNull(await _materials.GetAsync(_user, note.VersionId));
        Assert.True(File.Exists(Path.Combine(_root, "asr-tasks", _user.ToString("D"), asrTaskId + ".json")));
        Assert.True(File.Exists(Path.Combine(_root, "derived", _user.ToString("D"), "tasks", derivedTaskId + ".json")));
        Assert.NotNull(await _materials.GetDeletedAsync(_user, note.NoteId));
        Assert.DoesNotContain((await _materials.ListNativeNotesAsync(_user, 0, 50)).Items,
            item => item.NoteId == note.NoteId);
        await Assert.ThrowsAsync<DomainException>(() => _materials.GetCurrentAsync(_user, note.NoteId));

        var receipt = await _service.PurgeAsync(Batch((RecycleBinDomains.Note, note.NoteId)));

        Assert.Equal("success", Assert.Single(receipt.Items).Status);
        await Assert.ThrowsAsync<DomainException>(() => _materials.GetAsync(_user, note.VersionId));
        Assert.False(File.Exists(Path.Combine(_root, "asr-tasks", _user.ToString("D"),
            asrTaskId.ToString("D") + ".json")));
        Assert.False(File.Exists(Path.Combine(_root, "derived", _user.ToString("D"), "tasks",
            derivedTaskId.ToString("D") + ".json")));
        Assert.Null(await _db.AiRequestLogs.SingleOrDefaultAsync(x => x.Id == asrLog.Id));
        Assert.Null(await _db.AiRequestLogs.SingleOrDefaultAsync(x => x.Id == derivedLog.Id));
        Assert.Empty(await _db.AiCallImages.ToListAsync());
        Assert.Empty(await _db.AiCallAudio.ToListAsync());
        var log = Assert.Single(await _db.AuditLogs
            .Where(x => x.Action == "recycle_bin.note.purge" && x.ResourceId == note.NoteId.ToString())
            .ToListAsync());
        Assert.Equal("System", log.ActorType);
        Assert.DoesNotContain("保留原稿的笔记", log.MetadataJson);
    }

    [Fact]
    public async Task Purge_DoesNotPermanentlyDeleteAnActiveNote()
    {
        var note = await SeedNoteAsync(_user, "仍在使用的笔记");

        var receipt = await _service.PurgeAsync(Batch((RecycleBinDomains.Note, note.NoteId)));

        Assert.Equal("failed", Assert.Single(receipt.Items).Status);
        Assert.NotNull(await _materials.GetCurrentAsync(_user, note.NoteId));
    }

    [Fact]
    public async Task FailedPrecheckThenRestore_StalePurgeRetriesNeverDeleteActiveNote()
    {
        var note = await SeedNoteAsync(_user, "预检失败后已恢复的笔记");
        var asrTaskId = SeedAsrTask(note);
        _db.NoteAsrAttempts.Add(new NoteAsrAttemptEntity
        {
            OwnerId = _user,
            TaskId = asrTaskId,
            NoteId = note.NoteId,
            VersionId = note.VersionId,
            State = "Leased"
        });
        await _db.SaveChangesAsync();
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await _materials.GetDeletedAsync(_user, note.NoteId);

        var precheck = await Assert.ThrowsAsync<DomainException>(() =>
            _notes.PurgeAsync(_user, note.NoteId, deleted!.DeletedAt));
        Assert.Equal(40901, precheck.ErrorCode);

        var pending = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.UserId == _user
                && row.Domain == "note"
                && row.ItemId == note.NoteId);
        Assert.Equal(RecycleBinPurgePhases.Pending, pending.Phase);
        Assert.Null(pending.LeaseToken);
        Assert.NotNull(pending.LastError);

        var restored = await _service.RestoreAsync(Batch((RecycleBinDomains.Note, note.NoteId)));
        Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(restored.Items).Status);
        var abandoned = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.Id == pending.Id);
        Assert.Equal(RecycleBinPurgePhases.Abandoned, abandoned.Phase);
        Assert.Null(abandoned.LeaseToken);

        var expectedRetry = await _service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(
                RecycleBinDomains.Note,
                note.NoteId,
                deleted!.DeletedAt)
        ]));
        var noTokenRetry = await _service.PurgeAsync(
            Batch((RecycleBinDomains.Note, note.NoteId)));

        var expectedResult = Assert.Single(expectedRetry.Items);
        var noTokenResult = Assert.Single(noTokenRetry.Items);
        Assert.Equal(RecycleBinItemStatuses.Failed, expectedResult.Status);
        Assert.Equal(40901, expectedResult.ErrorCode);
        Assert.Equal(RecycleBinItemStatuses.Failed, noTokenResult.Status);
        Assert.Equal(40901, noTokenResult.ErrorCode);

        var current = await _materials.GetCurrentAsync(_user, note.NoteId);
        Assert.Equal(note.VersionId, current.Upload.VersionId);
        Assert.NotNull(await _materials.GetAsync(_user, note.VersionId));
        Assert.NotNull(await _asr.ReadAsync(_user, asrTaskId, default));
        Assert.True(File.Exists(Path.Combine(
            _root, "asr-tasks", _user.ToString("D"), asrTaskId + ".json")));
        Assert.NotNull(await _db.NoteAsrAttempts.SingleOrDefaultAsync(
            row => row.TaskId == asrTaskId && row.NoteId == note.NoteId));
        Assert.Equal(0, await _db.AuditLogs.CountAsync(row =>
            row.Action == "recycle_bin.note.purge"
            && row.ResourceId == note.NoteId.ToString("D")));
        Assert.Equal(RecycleBinPurgePhases.Abandoned,
            (await _db.RecycleBinPurgeIntents.AsNoTracking()
                .SingleAsync(row => row.Id == pending.Id)).Phase);
    }

    [Fact]
    public async Task NotePurge_ContributorFailureThenRestore_OldTokenRetryFailsClosed()
    {
        var note = await SeedNoteAsync(_user, "贡献者预检失败后恢复的笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await _materials.GetDeletedAsync(_user, note.NoteId);
        var failingNotes = new NativeNoteRecycleService(
            _db,
            _materials,
            _derived,
            _asr,
            _audit,
            purgeContributors: [new ThrowingPurgeContributor()]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            failingNotes.PurgeAsync(_user, note.NoteId, deleted!.DeletedAt));

        var pending = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.UserId == _user
                && row.Domain == "note"
                && row.ItemId == note.NoteId);
        Assert.Equal(RecycleBinPurgePhases.Pending, pending.Phase);
        Assert.Null(pending.LeaseToken);
        Assert.NotNull(pending.LastError);

        var restored = await _service.RestoreAsync(
            Batch((RecycleBinDomains.Note, note.NoteId)));
        Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(restored.Items).Status);

        var withOldToken = await _service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId, deleted.DeletedAt)
        ]));
        var withoutToken = await _service.PurgeAsync(
            Batch((RecycleBinDomains.Note, note.NoteId)));
        var oldTokenResult = Assert.Single(withOldToken.Items);
        var tokenlessResult = Assert.Single(withoutToken.Items);
        Assert.Equal(RecycleBinItemStatuses.Failed, oldTokenResult.Status);
        Assert.Equal(40901, oldTokenResult.ErrorCode);
        Assert.Equal(RecycleBinItemStatuses.Failed, tokenlessResult.Status);
        Assert.Equal(40901, tokenlessResult.ErrorCode);

        var current = await _materials.GetCurrentAsync(_user, note.NoteId);
        Assert.Equal(note.VersionId, current.Upload.VersionId);
        Assert.NotNull(await _materials.GetAsync(_user, note.VersionId));
        Assert.Contains((await _materials.ListNativeNotesAsync(_user, 0, 50)).Items,
            item => item.NoteId == note.NoteId);
        Assert.Empty(await _db.AuditLogs
            .Where(row => row.Action == "recycle_bin.note.purge"
                && row.ResourceId == note.NoteId.ToString("D"))
            .ToListAsync());
        Assert.Equal(RecycleBinPurgePhases.Abandoned,
            (await _db.RecycleBinPurgeIntents.AsNoTracking()
                .SingleAsync(row => row.Id == pending.Id)).Phase);
    }

    [Fact]
    public async Task PurgeIntent_StaleLeaseTokenCannotRenewCompleteOrReleaseNewClaim()
    {
        var note = await SeedNoteAsync(_user, "清理租约令牌回归");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await _materials.GetDeletedAsync(_user, note.NoteId);

        var first = await _notes.ClaimIntentAsync(
            _user, note.NoteId, deleted!.DeletedAt, deleted.Title, default);
        var staleToken = first.LeaseToken!.Value;
        first.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var second = await _notes.ClaimIntentAsync(
            _user, note.NoteId, deleted.DeletedAt, deleted.Title, default);
        var newToken = second.LeaseToken!.Value;
        Assert.NotEqual(staleToken, newToken);

        await _notes.ReleaseIntentAsync(first.Id, staleToken, "stale release", default);
        var afterRelease = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.Id == first.Id);
        Assert.Equal(RecycleBinPurgePhases.Pending, afterRelease.Phase);
        Assert.Equal<Guid?>(newToken, afterRelease.LeaseToken);
        Assert.True(afterRelease.LeaseExpiresAt > DateTimeOffset.UtcNow);
        Assert.Null(afterRelease.LastError);

        var staleRenew = await Assert.ThrowsAsync<DomainException>(() =>
            _notes.RenewIntentLeaseAsync(first.Id, staleToken, default));
        Assert.Equal(40901, staleRenew.ErrorCode);
        var staleComplete = await Assert.ThrowsAsync<DomainException>(() =>
            _notes.MarkCompletedAsync(first.Id, staleToken, default));
        Assert.Equal(40901, staleComplete.ErrorCode);

        var afterStale = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.Id == first.Id);
        Assert.Equal(RecycleBinPurgePhases.Pending, afterStale.Phase);
        Assert.Equal<Guid?>(newToken, afterStale.LeaseToken);

        await _notes.RenewIntentLeaseAsync(first.Id, newToken, default);
        await _notes.MarkCompletedAsync(first.Id, newToken, default);
        var completed = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.Id == first.Id);
        Assert.Equal(RecycleBinPurgePhases.Completed, completed.Phase);
        Assert.Null(completed.LeaseToken);
    }

    [Fact]
    public async Task PurgeIntent_LeaseWithoutExpiryCannotBeStolen()
    {
        var note = await SeedNoteAsync(_user, "无过期时间的清理租约");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await _materials.GetDeletedAsync(_user, note.NoteId);
        var intent = await _notes.ClaimIntentAsync(
            _user, note.NoteId, deleted!.DeletedAt, deleted.Title, default);

        intent.LeaseExpiresAt = null;
        await _db.SaveChangesAsync();

        var stolen = await Assert.ThrowsAsync<DomainException>(() =>
            _notes.ClaimIntentAsync(
                _user, note.NoteId, deleted.DeletedAt, deleted.Title, default));
        Assert.Equal(40901, stolen.ErrorCode);
        var after = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.Id == intent.Id);
        Assert.Equal(intent.LeaseToken, after.LeaseToken);
        Assert.Null(after.LeaseExpiresAt);
    }

    [Fact]
    public async Task NoteRestore_ActivePurgeLeaseFailsClosed()
    {
        var note = await SeedNoteAsync(_user, "永久清理进行中不能恢复");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await _materials.GetDeletedAsync(_user, note.NoteId);
        var intent = await _notes.ClaimIntentAsync(
            _user, note.NoteId, deleted!.DeletedAt, deleted.Title, default);
        Assert.NotNull(intent.LeaseToken);

        var blocked = await _service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId, deleted.DeletedAt)
        ]));
        var blockedResult = Assert.Single(blocked.Items);
        Assert.Equal(RecycleBinItemStatuses.Failed, blockedResult.Status);
        Assert.Equal(40901, blockedResult.ErrorCode);
        Assert.NotNull(await _materials.GetDeletedAsync(_user, note.NoteId));
        Assert.NotNull(await _materials.GetAsync(_user, note.VersionId));

        await _notes.ReleaseIntentAsync(
            intent.Id, intent.LeaseToken!.Value, "synthetic failed purge", default);
        var restored = await _service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId, deleted.DeletedAt)
        ]));
        Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(restored.Items).Status);
        Assert.Equal(note.VersionId,
            (await _materials.GetCurrentAsync(_user, note.NoteId)).Upload.VersionId);
    }

    [Fact]
    public async Task NoteRestore_ExpiredPurgeLeaseFencesOffStaleWorker()
    {
        var note = await SeedNoteAsync(_user, "过期清理租约不能阻塞恢复");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await _materials.GetDeletedAsync(_user, note.NoteId);
        var intent = await _notes.ClaimIntentAsync(
            _user, note.NoteId, deleted!.DeletedAt, deleted.Title, default);
        var staleToken = intent.LeaseToken!.Value;
        intent.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await _db.SaveChangesAsync();

        var restored = await _service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId, deleted.DeletedAt)
        ]));
        Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(restored.Items).Status);
        Assert.Equal(note.VersionId,
            (await _materials.GetCurrentAsync(_user, note.NoteId)).Upload.VersionId);

        var fenced = await _db.RecycleBinPurgeIntents.AsNoTracking()
            .SingleAsync(row => row.Id == intent.Id);
        Assert.Equal(RecycleBinPurgePhases.Abandoned, fenced.Phase);
        Assert.Null(fenced.LeaseToken);
        await Assert.ThrowsAsync<DomainException>(() =>
            _notes.RenewIntentLeaseAsync(intent.Id, staleToken, default));
    }

    [Fact]
    public async Task NotePurge_AfterPartialFailureBlocksRestore_AndRetryCompletes()
    {
        var note = await SeedNoteAsync(_user, "清理中断后不能恢复成残缺笔记");
        await _notes.SoftDeleteAsync(_user, note.NoteId);
        var deleted = await _materials.GetDeletedAsync(_user, note.NoteId);

        await _materials.MarkPurgeStartedAsync(_user, note.NoteId, deleted!.DeletedAt);

        var restore = await _service.RestoreAsync(Batch((RecycleBinDomains.Note, note.NoteId)));
        var failed = Assert.Single(restore.Items);
        Assert.Equal(RecycleBinItemStatuses.Failed, failed.Status);
        Assert.Equal(40901, failed.ErrorCode);
        Assert.NotNull(await _materials.GetDeletedAsync(_user, note.NoteId));

        var purge = await _service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Note, note.NoteId, deleted.DeletedAt)
        ]));
        Assert.Equal(RecycleBinItemStatuses.Success, Assert.Single(purge.Items).Status);
        Assert.Null(await _materials.GetDeletedAsync(_user, note.NoteId));
        await Assert.ThrowsAsync<DomainException>(() => _materials.GetAsync(_user, note.VersionId));
    }

    [Fact]
    public async Task NotePurge_RemovesAsrRoutingBatchAndTaskSuggestionEvidence()
    {
        var note = await SeedNoteAsync(_user, "深度清理的笔记");
        var asrTaskId = SeedAsrTask(note);
        var derivedTaskId = SeedDerivedTask(note);
        var asrLog = SeedAiLog("native-note-asr-task", asrTaskId);
        _ = SeedAiLog("native-note-derived-task", derivedTaskId);
        var artifactVersionId = Guid.NewGuid();
        var suggestionLog = SeedAiLog("note-derived-version", artifactVersionId);
        var attempt = new NoteAsrAttemptEntity
        {
            Id = Guid.NewGuid(),
            OwnerId = _user,
            TaskId = asrTaskId,
            NoteId = note.NoteId,
            VersionId = note.VersionId,
            State = "Succeeded"
        };
        var lease = new NoteAsrLeaseEntity
        {
            OwnerId = _user,
            AttemptId = attempt.Id,
            NodeId = Guid.NewGuid(),
            Status = "Expired",
            ExpiresAt = Now.AddMinutes(-1)
        };
        var receipt = new NoteAsrAckReceiptEntity
        {
            OwnerId = _user,
            AttemptId = attempt.Id,
            LeaseId = lease.Id,
            Kind = "segment",
            Result = "Succeeded",
            ResultText = "敏感转写内容"
        };
        var evidence = new NoteAsrSegmentEvidenceEntity
        {
            OwnerId = _user,
            AttemptId = attempt.Id,
            TaskId = asrTaskId,
            VersionId = note.VersionId,
            ResultText = "敏感分片证据"
        };
        var batch = new NoteAsrBatchJobEntity
        {
            OwnerId = _user,
            TaskId = asrTaskId,
            AttemptId = attempt.Id,
            Status = "Completed",
            CredentialSecret = "protected-credential",
            ResultJson = "{\"text\":\"敏感批量结果\"}"
        };
        var extraction = new NoteTaskSuggestionExtractionEntity
        {
            UserId = _user,
            RequestId = Guid.NewGuid(),
            NoteId = note.NoteId,
            ArtifactId = Guid.NewGuid(),
            ArtifactVersionId = artifactVersionId,
            SourceContentMarkdown = "敏感任务建议正文",
            Status = NoteTaskSuggestionExtractionStatuses.Succeeded,
            AiCallLogId = suggestionLog.Id
        };
        var suggestion = new NoteTaskSuggestionEntity
        {
            UserId = _user,
            ExtractionId = extraction.Id,
            NoteId = note.NoteId,
            ArtifactId = extraction.ArtifactId,
            ArtifactVersionId = artifactVersionId,
            Title = "敏感任务标题",
            Description = "敏感任务描述"
        };
        _db.AddRange(lease, receipt, evidence, batch, extraction, suggestion, attempt);
        _db.AiAsrJobs.Add(new AiAsrJobEntity
        {
            OwnerId = _user,
            LogId = asrLog.Id,
            ClientRequestId = Guid.NewGuid()
        });
        await _db.SaveChangesAsync();
        await _notes.SoftDeleteAsync(_user, note.NoteId);

        var result = await _service.PurgeAsync(Batch((RecycleBinDomains.Note, note.NoteId)));

        Assert.Equal("success", Assert.Single(result.Items).Status);
        Assert.Empty(await _db.NoteAsrAttempts.ToListAsync());
        Assert.Empty(await _db.NoteAsrLeases.ToListAsync());
        Assert.Empty(await _db.NoteAsrAckReceipts.ToListAsync());
        Assert.Empty(await _db.NoteAsrSegmentEvidence.ToListAsync());
        Assert.Empty(await _db.NoteAsrBatchJobs.ToListAsync());
        Assert.Empty(await _db.AiAsrJobs.ToListAsync());
        Assert.Empty(await _db.Set<NoteTaskSuggestionExtractionEntity>().ToListAsync());
        Assert.Empty(await _db.Set<NoteTaskSuggestionEntity>().ToListAsync());
        Assert.Empty(await _db.AiRequestLogs.ToListAsync());
        Assert.False(File.Exists(Path.Combine(_root, "asr-tasks", _user.ToString("D"), asrTaskId + ".json")));
        Assert.False(File.Exists(Path.Combine(_root, "derived", _user.ToString("D"), "tasks", derivedTaskId + ".json")));
        await Assert.ThrowsAsync<DomainException>(() => _materials.GetAsync(_user, note.VersionId));
        await Assert.ThrowsAsync<DomainException>(() => _asr.ReadAsync(_user, asrTaskId, default));
        await Assert.ThrowsAsync<DomainException>(() => _derived.ReadTaskAsync(_user, derivedTaskId, default));
        var audit = Assert.Single(await _db.AuditLogs
            .Where(row => row.Action == "recycle_bin.note.purge").ToListAsync());
        Assert.DoesNotContain("敏感", audit.MetadataJson);
        Assert.DoesNotContain("protected-credential", audit.MetadataJson);
    }

    [Fact]
    public async Task CalendarPurge_ReusesCalendarRules_AndRemovesBooksWithChildren()
    {
        var book = SeedCalendar(_user, "工作日历", "calendar", deleted: true);
        var evt = SeedEvent(book, "已删日程", deleted: true);
        var taskBook = SeedCalendar(_user, "任务本", "task", deleted: true);
        var task = SeedTask(taskBook, "已删任务", deleted: true);
        var otherBook = SeedCalendar(_other, "别人的日历", "calendar", deleted: true);
        var otherEvent = SeedEvent(otherBook, "别人的日程", deleted: true);
        await _db.SaveChangesAsync();

        var receipt = await _service.PurgeAsync(Batch(
            (RecycleBinDomains.Calendar, evt.Id),
            (RecycleBinDomains.Task, task.Id),
            (RecycleBinDomains.Calendar, book.Id),
            (RecycleBinDomains.Task, taskBook.Id),
            (RecycleBinDomains.Calendar, otherEvent.Id)));

        Assert.Equal(4, receipt.Succeeded);
        Assert.Equal(1, receipt.Failed);
        Assert.Equal("failed", receipt.Items.Single(i => i.Id == otherEvent.Id).Status);
        Assert.Null(await _db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(e => e.Id == evt.Id));
        Assert.Null(await _db.Set<TaskEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(t => t.Id == task.Id));
        Assert.Null(await _db.Set<CalendarEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(c => c.Id == book.Id));
        Assert.Null(await _db.Set<CalendarEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(c => c.Id == taskBook.Id));
        Assert.NotNull(await _db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(e => e.Id == otherEvent.Id));
        Assert.Equal(4, await _db.AuditLogs.CountAsync(x => x.Action == "calendar.recycle_bin.purge"));
    }

    private RecycleBinBatchRequest Batch(params (string Domain, Guid Id)[] items)
        => new(items.Select(item => new RecycleBinItemRef(item.Domain, item.Id)).ToArray());

    private async Task<(Guid NoteId, Guid VersionId)> SeedNoteAsync(Guid owner, string title)
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
        await _materials.BeginAsync(owner, request);
        await _materials.UploadPartAsync(owner, versionId, 0, 0, Hash(noteJson), new MemoryStream(noteBytes));
        await _materials.UploadPartAsync(owner, versionId, 1, 0, Hash(document), new MemoryStream(document));
        await _materials.CompleteAsync(owner, versionId);
        return (noteId, versionId);
    }

    private Guid SeedAsrTask((Guid NoteId, Guid VersionId) note)
    {
        var taskId = Guid.NewGuid();
        var folder = Path.Combine(_root, "asr-tasks", _user.ToString("D"));
        Directory.CreateDirectory(folder);
        var json = JsonSerializer.Serialize(new
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
        }, Json);
        File.WriteAllText(Path.Combine(folder, taskId.ToString("D") + ".json"), json);
        return taskId;
    }

    private Guid SeedDerivedTask((Guid NoteId, Guid VersionId) note)
    {
        var taskId = Guid.NewGuid();
        var folder = Path.Combine(_root, "derived", _user.ToString("D"), "tasks");
        Directory.CreateDirectory(folder);
        var json = JsonSerializer.Serialize(new
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
        }, Json);
        File.WriteAllText(Path.Combine(folder, taskId.ToString("D") + ".json"), json);
        return taskId;
    }

    private AiRequestLogEntity SeedAiLog(string sourceType, Guid sourceId)
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
        _db.AiRequestLogs.Add(log);
        return log;
    }

    private async Task<FileItemEntity> SeedLocalFileAsync(string name)
    {
        var provider = await BuiltinLocalAccount.EnsureAsync(_db, _user, default);
        var session = await _localProvider.OpenSessionAsync(new FileProviderContext(
            provider.Id, _user, provider.Provider, provider.ClientId, provider.DriveId, provider.AccountId,
            _ => throw new InvalidOperationException("local files must not request OAuth")));
        var bytes = Encoding.UTF8.GetBytes("永久清理内容");
        var externalId = await session.Writer.CreateFileAsync(
            new("root", "/"), name, bytes, "text/plain");
        var row = new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = externalId,
            Path = "/" + name,
            Name = name,
            ItemType = "file",
            MimeType = "text/plain",
            Size = bytes.Length
        };
        _db.Set<FileItemEntity>().Add(row);
        await _db.SaveChangesAsync();
        return row;
    }

    private FileItemEntity SeedExternalFile(Guid owner, string providerKey, string name, bool deleted)
    {
        var provider = new FileProviderEntity
        {
            UserId = owner,
            Provider = providerKey,
            AccountId = providerKey + "-account",
            AccountName = providerKey,
            Status = "connected"
        };
        var item = new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = Guid.NewGuid().ToString("N"),
            Path = "/" + name,
            Name = name,
            ItemType = "file",
            MimeType = "text/plain",
            IsDeleted = deleted,
            DeletedAt = deleted ? Now : null
        };
        _db.Set<FileProviderEntity>().Add(provider);
        _db.Set<FileItemEntity>().Add(item);
        return item;
    }

    private CalendarEntity SeedCalendar(Guid owner, string name, string kind, bool deleted)
    {
        var calendar = new CalendarEntity
        {
            UserId = owner,
            Name = name,
            Kind = kind,
            Color = "#2563EB",
            DeletedAt = deleted ? Now : null,
            DeletedByOperationId = deleted ? Guid.NewGuid() : null,
            DeletedByOperationKind = deleted ? "single" : null
        };
        _db.Set<CalendarEntity>().Add(calendar);
        return calendar;
    }

    private EventEntity SeedEvent(CalendarEntity calendar, string title, bool deleted = false)
    {
        var evt = new EventEntity
        {
            Calendar = calendar,
            CalendarId = calendar.Id,
            Title = title,
            Uid = Guid.NewGuid().ToString("N"),
            DtStart = Now.AddHours(1),
            DtEnd = Now.AddHours(2),
            DeletedAt = deleted ? Now : null,
            DeletedByOperationId = deleted ? Guid.NewGuid() : null,
            DeletedByOperationKind = deleted ? "single" : null
        };
        _db.Set<EventEntity>().Add(evt);
        return evt;
    }

    private TaskEntity SeedTask(CalendarEntity book, string title, bool deleted = false)
    {
        var task = new TaskEntity
        {
            UserId = book.UserId,
            Calendar = book,
            CalendarId = book.Id,
            Title = title,
            Uid = Guid.NewGuid().ToString("N"),
            DtStart = Now.AddHours(1),
            DeletedAt = deleted ? Now : null,
            DeletedByOperationId = deleted ? Guid.NewGuid() : null,
            DeletedByOperationKind = deleted ? "single" : null
        };
        _db.Set<TaskEntity>().Add(task);
        return task;
    }

    private static string Hash(string text) => Hash(Encoding.UTF8.GetBytes(text));

    private static string Hash(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        _providerHarness.Dispose();
        _db.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class FixedCurrentUser(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role { get; } = "user";
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class ThrowingPurgeContributor : INotePurgeContributor
    {
        public Task<NotePurgeContribution> StagePurgeAsync(Guid owner, Guid noteId,
            CancellationToken ct) =>
            throw new InvalidOperationException(
                "Synthetic preflight failure before purge inventory is persisted.");
    }
}
