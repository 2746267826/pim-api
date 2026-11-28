using System.Text;
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
using Pim.UnitTests.Files;
using Xunit;

namespace Pim.UnitTests.Harness.RealDb;

/// <summary>
/// Q03 focused PostgreSQL evidence: the unified list must be composed from real
/// provider/calendar joins, and a stale CAS token must fail before any row changes.
/// This is not an HTTP/browser test and does not touch production data.
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class UnifiedRecycleBinRealDbTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-25T02:00:00Z");
    private readonly Guid _user = Guid.Parse("cccccccc-1111-2222-3333-444444444444");
    private readonly Guid _other = Guid.Parse("dddddddd-1111-2222-3333-444444444444");
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "pim-q03-realdb", Guid.NewGuid().ToString("N"));

    private TempMigrationDatabase? _temp;
    private FilesProviderHarness? _harness;
    private LocalFileProvider? _localProvider;
    private LocalFileStore? _localStore;
    private ManagedBinaryStore? _binaries;
    private FileWriteService? _files;

    [SkippableFact]
    public async Task ListAndCalendarCas_OnRealPostgres_KeepOwnerIsolationAndFailClosed()
    {
        var service = await CreateServiceAsync("wo_q03_realdb");
        var db = _temp!.Db;

        SeedUsers(db);
        var provider = SeedProvider(_user, "onedrive");
        SeedFile(provider, "mine.docx", deleted: true);
        var otherProvider = SeedProvider(_other, "onedrive");
        SeedFile(otherProvider, "other.docx", deleted: true);

        var book = SeedCalendar(_user, "工作日历", "calendar");
        var deletedEvent = SeedEvent(book, "我的已删日程", deleted: true);
        var taskBook = SeedCalendar(_user, "任务本", "task");
        SeedTask(taskBook, "我的已删任务", deleted: true);
        var otherBook = SeedCalendar(_other, "别人的日历", "calendar");
        SeedEvent(otherBook, "别人的日程", deleted: true);
        await db.SaveChangesAsync();

        var page = await service.ListAsync(null, null, null, null, 1, 100);

        Assert.Equal(3, page.TotalCount);
        Assert.Equal(0, page.Groups.Single(group => group.Domain == RecycleBinDomains.Note).TotalCount);
        Assert.Equal(1, page.Groups.Single(group => group.Domain == RecycleBinDomains.File).TotalCount);
        Assert.Equal(1, page.Groups.Single(group => group.Domain == RecycleBinDomains.Calendar).TotalCount);
        Assert.Equal(1, page.Groups.Single(group => group.Domain == RecycleBinDomains.Task).TotalCount);
        Assert.DoesNotContain(page.Items, item => item.Title is "other.docx" or "别人的日程");

        var stale = await service.RestoreAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(
                RecycleBinDomains.Calendar,
                deletedEvent.Id,
                deletedEvent.DeletedAt!.Value.AddSeconds(-1))
        ]));
        Assert.Equal(1, stale.Failed);
        Assert.Equal(40901, Assert.Single(stale.Items).ErrorCode);
        Assert.NotNull((await db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleAsync(row => row.Id == deletedEvent.Id)).DeletedAt);

        var purged = await service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(
                RecycleBinDomains.Calendar,
                deletedEvent.Id,
                deletedEvent.DeletedAt)
        ]));
        Assert.Equal(1, purged.Succeeded);
        Assert.Null(await db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(row => row.Id == deletedEvent.Id));
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            row => row.Action == "calendar.recycle_bin.purge"
                && row.ResourceId == deletedEvent.Id.ToString("D")));
    }

    [SkippableFact]
    public async Task InboxTaskPurge_OnRealPostgres_HandlesNullCalendarIdAndRetry()
    {
        var service = await CreateServiceAsync("wo_q03_inbox_purge");
        var db = _temp!.Db;
        SeedUsers(db);
        var task = new TaskEntity
        {
            UserId = _user,
            CalendarId = null,
            IsInbox = true,
            Uid = Guid.NewGuid().ToString("N"),
            Title = "收件箱已删任务",
            DtStart = Now.AddHours(1),
            DeletedAt = Now,
            DeletedByOperationId = Guid.NewGuid(),
            DeletedByOperationKind = "single"
        };
        db.Set<TaskEntity>().Add(task);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var request = new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Task, task.Id, Now)
        ], operationId);
        var first = await service.PurgeAsync(request);
        Assert.Equal(1, first.Succeeded);
        Assert.Null(await db.Set<TaskEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(row => row.Id == task.Id));

        var second = await service.PurgeAsync(request);
        Assert.Equal(1, second.Succeeded);
        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            row => row.Action == "calendar.recycle_bin.purge"
                && row.ResourceId == task.Id.ToString("D")));

        // A fresh operationId still sees the durable per-generation purge receipt. Reporting
        // "already purged" is truthful here; the absence is not guessed.
        var provenGone = await service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Task, task.Id, Now)
        ]));
        var provenItem = Assert.Single(provenGone.Items);
        Assert.Equal(RecycleBinItemStatuses.Success, provenItem.Status);
        Assert.Contains("无需重复", provenItem.Message);
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            row => row.Action == "calendar.recycle_bin.purge"
                && row.ResourceId == task.Id.ToString("D")));

        var neverExisted = await service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Task, Guid.NewGuid(), Now)
        ]));
        var missingItem = Assert.Single(neverExisted.Items);
        Assert.Equal(RecycleBinItemStatuses.Failed, missingItem.Status);
        Assert.Equal(40401, missingItem.ErrorCode);
    }

    [SkippableFact]
    public async Task OutlookBoundCalendarPurge_OnRealPostgres_RemovesBindingBeforeCalendarAndIsIdempotent()
    {
        var service = await CreateServiceAsync("wo_q03_outlook_purge");
        var db = _temp!.Db;
        SeedUsers(db);
        var connection = new OutlookConnectionEntity
        {
            UserId = _user,
            Status = "connected",
            Provider = "outlook"
        };
        var calendar = SeedCalendar(_user, "Outlook 日历", "calendar");
        calendar.DeletedAt = Now;
        calendar.DeletedByOperationId = Guid.NewGuid();
        calendar.DeletedByOperationKind = "single";
        var binding = new OutlookCalendarBindingEntity
        {
            ConnectionId = connection.Id,
            PimCalendarId = calendar.Id,
            GraphCalendarId = Guid.NewGuid().ToString("N"),
            Name = "Outlook 日历",
            RemoteState = "active",
            SyncStrategy = "window-reconcile"
        };
        db.Set<OutlookConnectionEntity>().Add(connection);
        db.Set<OutlookCalendarBindingEntity>().Add(binding);
        await db.SaveChangesAsync();

        var evt = SeedEvent(calendar, "Outlook 已删日程", deleted: true);
        evt.OutlookCalendarBindingId = binding.Id;
        evt.OutlookConnectionId = connection.Id;
        evt.Source = "outlook";
        var task = SeedTask(calendar, "Outlook 已删任务", deleted: true);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var request = new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Calendar, calendar.Id, Now)
        ], operationId);
        var first = await service.PurgeAsync(request);
        Assert.Equal(1, first.Succeeded);
        Assert.Null(await db.Set<CalendarEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(row => row.Id == calendar.Id));
        Assert.Null(await db.Set<OutlookCalendarBindingEntity>()
            .SingleOrDefaultAsync(row => row.Id == binding.Id));
        Assert.Null(await db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(row => row.Id == evt.Id));
        Assert.Null(await db.Set<TaskEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(row => row.Id == task.Id));
        Assert.NotNull(await db.Set<OutlookConnectionEntity>()
            .SingleOrDefaultAsync(row => row.Id == connection.Id));

        var second = await service.PurgeAsync(request);
        Assert.Equal(1, second.Succeeded);
        Assert.Equal(first.ReceiptId, second.ReceiptId);
        Assert.Equal(1, await db.AuditLogs.CountAsync(
            row => row.Action == "calendar.recycle_bin.purge"
                && row.ResourceId == calendar.Id.ToString("D")));
    }

    [SkippableFact]
    public async Task FileFolderTreePurge_OnRealPostgres_RemovesVersionsBlobsAndChildOrder()
    {
        var service = await CreateServiceAsync("wo_q03_file_tree_purge");
        var db = _temp!.Db;
        var localStore = _localStore!;
        var binaries = _binaries!;
        var files = _files!;
        SeedUsers(db);
        await db.SaveChangesAsync();

        var provider = await BuiltinLocalAccount.EnsureAsync(db, _user, default);
        var session = await _localProvider!.OpenSessionAsync(new FileProviderContext(
            provider.Id, _user, provider.Provider, provider.ClientId, provider.DriveId,
            provider.AccountId,
            _ => throw new InvalidOperationException("local files must not request OAuth")));

        var folderExternalId = await session.Writer.CreateFolderAsync(new("root", "/"), "工程资料");
        var childBytes = Encoding.UTF8.GetBytes("子文件内容-版本一");
        var childExternalId = await session.Writer.CreateFileAsync(
            new(folderExternalId, "/工程资料"), "说明.txt", childBytes, "text/plain");
        var nestedFolderId = await session.Writer.CreateFolderAsync(
            new(folderExternalId, "/工程资料"), "草稿");
        var nestedBytes = Encoding.UTF8.GetBytes("更深一层的内容");
        _ = await session.Writer.CreateFileAsync(
            new(nestedFolderId, "/工程资料/草稿"), "深处.txt", nestedBytes, "text/plain");

        var folder = new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = folderExternalId,
            Path = "/工程资料",
            Name = "工程资料",
            ItemType = "folder"
        };
        var child = new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = childExternalId,
            Path = "/工程资料/说明.txt",
            Name = "说明.txt",
            ItemType = "file",
            MimeType = "text/plain",
            Size = childBytes.Length
        };
        var nestedFolder = new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = nestedFolderId,
            Path = "/工程资料/草稿",
            Name = "草稿",
            ItemType = "folder"
        };
        db.Set<FileItemEntity>().AddRange(folder, child, nestedFolder);
        await db.SaveChangesAsync();

        // A file can legitimately carry a current version (Restrict FK to file_versions).
        // Deleting the item before its version would be rejected by PostgreSQL, so this
        // pins the purge order rather than trusting EF's statement ordering.
        var historyVersion = new FileVersionEntity
        {
            FileItemId = child.Id,
            ExternalVersionId = "v1",
            Source = "history",
            IsCurrent = false,
            Size = childBytes.Length
        };
        var currentVersion = new FileVersionEntity
        {
            FileItemId = child.Id,
            ExternalVersionId = "v2",
            Source = "history",
            IsCurrent = true,
            Size = childBytes.Length
        };
        db.Set<FileVersionEntity>().AddRange(historyVersion, currentVersion);
        child.CurrentVersionId = currentVersion.Id;
        var indexJob = new FileIndexJobEntity
        {
            FileItemId = child.Id,
            VersionId = currentVersion.Id,
            Status = "pending"
        };
        var chunk = new FileChunkEntity
        {
            FileItemId = child.Id,
            VersionId = currentVersion.Id,
            ChunkIndex = 0,
            Text = "分片",
            TextHash = "hash",
            StartOffset = 0,
            EndOffset = 2
        };
        db.Set<FileIndexJobEntity>().Add(indexJob);
        db.Set<FileChunkEntity>().Add(chunk);
        await db.SaveChangesAsync();

        var catalog = await localStore.GetAsync(
            _user, provider.Id, folderExternalId, default);
        var childBlobKey = (await localStore.GetAsync(
            _user, provider.Id, childExternalId, default)).BlobKey!;
        Assert.True(catalog.IsFolder);
        Assert.NotNull(childBlobKey);
        var childBlobPath = binaries.ObjectPath(_user, ManagedBinaryStore.Files, provider.Id, childBlobKey);
        Assert.True(File.Exists(childBlobPath));

        await files.DeleteToTrashAsync(folder.Id);
        var deletedFolder = await db.Set<FileItemEntity>().IgnoreQueryFilters()
            .SingleAsync(row => row.Id == folder.Id);
        Assert.True(deletedFolder.IsDeleted);
        Assert.True((await db.Set<FileItemEntity>().IgnoreQueryFilters()
            .SingleAsync(row => row.Id == child.Id)).IsDeleted);
        Assert.True((await db.Set<FileItemEntity>().IgnoreQueryFilters()
            .SingleAsync(row => row.Id == nestedFolder.Id)).IsDeleted);

        var receipt = await service.PurgeAsync(new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(
                RecycleBinDomains.File, folder.Id, deletedFolder.DeletedAt)
        ]));

        Assert.Equal(1, receipt.Succeeded);
        Assert.Empty(await db.Set<FileItemEntity>().IgnoreQueryFilters()
            .Where(row => row.Id == folder.Id
                || row.Id == child.Id
                || row.Id == nestedFolder.Id
                || row.Path.StartsWith("/工程资料/"))
            .ToListAsync());
        Assert.Empty(await db.Set<FileVersionEntity>()
            .Where(row => row.FileItemId == child.Id)
            .ToListAsync());
        Assert.Empty(await db.Set<FileIndexJobEntity>()
            .Where(row => row.FileItemId == child.Id)
            .ToListAsync());
        Assert.Empty(await db.Set<FileChunkEntity>()
            .Where(row => row.FileItemId == child.Id)
            .ToListAsync());
        Assert.False(File.Exists(childBlobPath));
        await Assert.ThrowsAsync<ProviderItemNotFoundException>(() =>
            localStore.GetAsync(_user, provider.Id, folderExternalId, default));
        Assert.Equal(1, await db.AuditLogs.CountAsync(row =>
            row.Action == "files.recycle_bin.purge"
                && row.ResourceId == folder.Id.ToString("D")));
        Assert.Equal(3, await db.RecycleBinPurgeIntents.CountAsync(row =>
            row.Domain == RecycleBinPurgeDomains.File
                && (row.ItemId == folder.Id
                    || row.ItemId == child.Id
                    || row.ItemId == nestedFolder.Id)
                && row.Phase == RecycleBinPurgePhases.Completed));
    }

    [SkippableFact]
    public async Task Purge_CrashBeforeReceiptWrite_ReplayReconcilesOnRealPostgres()
    {
        var service = await CreateServiceAsync("wo_q03_crash_receipt");
        var db = _temp!.Db;
        SeedUsers(db);
        var book = SeedCalendar(_user, "崩溃回执日历", "calendar");
        var evt = SeedEvent(book, "崩溃回执日程", deleted: true);
        await db.SaveChangesAsync();

        var operationId = Guid.NewGuid();
        var request = new RecycleBinBatchRequest(
        [
            new RecycleBinItemRef(RecycleBinDomains.Calendar, evt.Id, evt.DeletedAt)
        ], operationId);

        // Commit the calendar purge, then rewind only the operation row to the crash state:
        // domain rows and the per-generation receipt are committed, the batch receipt is not.
        var first = await service.PurgeAsync(request);
        Assert.Equal(1, first.Succeeded);
        var operationRow = await db.RecycleBinOperations
            .SingleAsync(row => row.OperationId == operationId);
        operationRow.Status = RecycleBinOperationStatuses.Running;
        operationRow.ReceiptJson = null;
        operationRow.CompletedAt = null;
        operationRow.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        Assert.Null(await db.Set<EventEntity>().IgnoreQueryFilters()
            .SingleOrDefaultAsync(row => row.Id == evt.Id));
        var interrupted = await db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.OperationId == operationId);
        Assert.Equal(RecycleBinOperationStatuses.Running, interrupted.Status);
        Assert.Null(interrupted.ReceiptJson);

        var replay = await service.PurgeAsync(request);

        Assert.Equal(1, replay.Succeeded);
        Assert.Equal(0, replay.Failed);
        Assert.Equal(
            RecycleBinItemStatuses.Success,
            Assert.Single(replay.Items).Status);
        Assert.Equal(interrupted.Id, replay.ReceiptId);
        Assert.Equal(1, await db.AuditLogs.CountAsync(row =>
            row.Action == "calendar.recycle_bin.purge"
                && row.ResourceId == evt.Id.ToString("D")));
        var completed = await db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.OperationId == operationId);
        Assert.Equal(RecycleBinOperationStatuses.Completed, completed.Status);
        Assert.NotNull(completed.ReceiptJson);
    }

    [SkippableFact]
    public async Task Operation_StaleProcessingTokenCannotCompleteOrClearNewLease()
    {
        var service = await CreateServiceAsync("wo_q03_operation_token");
        var db = _temp!.Db;
        SeedUsers(db);
        await db.SaveChangesAsync();

        var staleToken = Guid.NewGuid();
        var newToken = Guid.NewGuid();
        var operation = new RecycleBinOperationEntity
        {
            UserId = _user,
            OperationId = Guid.NewGuid(),
            Action = "restore",
            PayloadHash = "payload",
            Status = RecycleBinOperationStatuses.Running,
            ProcessingToken = staleToken,
            LeaseExpiresAt = Now.AddMinutes(-1),
            AttemptCount = 1,
            CreatedAt = Now.AddMinutes(-2),
            UpdatedAt = Now.AddMinutes(-2)
        };
        db.RecycleBinOperations.Add(operation);
        await db.SaveChangesAsync();

        operation.ProcessingToken = newToken;
        operation.LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5);
        operation.AttemptCount = 2;
        operation.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var receipt = new RecycleBinBatchReceiptDto(
            Guid.NewGuid(),
            operation.OperationId,
            "restore",
            0,
            0,
            0,
            []);
        var stale = await Assert.ThrowsAsync<DomainException>(() =>
            service.CompleteOperationAsync(operation.Id, staleToken, receipt, default));
        Assert.Equal(40904, stale.ErrorCode);
        var staleRenew = await Assert.ThrowsAsync<DomainException>(() =>
            service.RenewOperationLeaseAsync(operation.Id, staleToken, default));
        Assert.Equal(40904, staleRenew.ErrorCode);
        await service.ReleaseOperationAsync(operation.Id, staleToken, default);

        var afterStale = await db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.Id == operation.Id);
        Assert.Equal(RecycleBinOperationStatuses.Running, afterStale.Status);
        Assert.Equal(newToken, afterStale.ProcessingToken);
        Assert.True(afterStale.LeaseExpiresAt > DateTimeOffset.UtcNow);
        Assert.Null(afterStale.ReceiptJson);

        await service.RenewOperationLeaseAsync(operation.Id, newToken, default);
        var completed = await service.CompleteOperationAsync(
            operation.Id, newToken, receipt, default);
        Assert.Equal(receipt.ReceiptId, completed.ReceiptId);
        var final = await db.RecycleBinOperations.AsNoTracking()
            .SingleAsync(row => row.Id == operation.Id);
        Assert.Equal(RecycleBinOperationStatuses.Completed, final.Status);
        Assert.Equal(newToken, final.ProcessingToken);
        Assert.NotNull(final.ReceiptJson);
    }

    private async Task<UnifiedRecycleBinService> CreateServiceAsync(string prefix)
    {
        RealDbTestConnection.SkipIfUnavailable();
        PimDbContext.RegisterModuleAssembly(typeof(CalendarEntity).Assembly);
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        _temp = await TempMigrationDatabase.CreateAsync(prefix);
        await _temp.MigrateAsync();

        var db = _temp.Db;
        var noteOptions = Options.Create(new NoteMaterialUploadOptions
        {
            StorageRoot = _root,
            MaxVersionBytes = 32 * 1024 * 1024,
            MaxUserReservedBytes = 64 * 1024 * 1024
        });
        var clock = new FixedClock(Now);
        var audit = new AuditLogService(db);
        var materials = new NoteMaterialUploadStore(noteOptions, clock);
        var notes = new NativeNoteRecycleService(
            db, materials, new NoteDerivedStore(noteOptions, clock),
            new NoteAsrTaskStore(noteOptions), audit);
        var user = new FixedCurrentUser(_user);
        var calendar = new CalendarRecycleBinService(
            db, user, new CalendarAuditWriter(audit));

        var binaries = new ManagedBinaryStore(Options.Create(new ManagedBinaryOptions
        {
            Root = Path.Combine(_root, "managed")
        }));
        var localStore = new LocalFileStore(binaries);
        var grants = new LocalFileGrants(
            new EphemeralDataProtectionProvider(), localStore, new HttpContextAccessor(), clock);
        var localProvider = new LocalFileProvider(localStore, grants);
        _harness = new FilesProviderHarness(
            db, clock: clock, extraProvider: localProvider);
        var files = new FileWriteService(
            db, _harness.Sessions, _harness.Providers, user, audit,
            new SensitivePathPolicy(null), NullLogger<FileWriteService>.Instance, clock);
        _binaries = binaries;
        _localStore = localStore;
        _localProvider = localProvider;
        _files = files;
        return new UnifiedRecycleBinService(
            db, user, calendar, notes, files, localStore, audit,
            NullLogger<UnifiedRecycleBinService>.Instance);
    }

    private void SeedUsers(PimDbContext db)
    {
        db.Users.AddRange(
            new UserEntity
            {
                Id = _user,
                Username = "q03-owner",
                Email = "q03-owner@example.com",
                PasswordHash = "hash",
                Role = "user"
            },
            new UserEntity
            {
                Id = _other,
                Username = "q03-other",
                Email = "q03-other@example.com",
                PasswordHash = "hash",
                Role = "user"
            });
    }

    private FileProviderEntity SeedProvider(Guid owner, string provider)
    {
        var row = new FileProviderEntity
        {
            UserId = owner,
            Provider = provider,
            Status = "connected",
            AccountId = $"{owner:N}-{provider}",
            AccountName = provider
        };
        _temp!.Db.Set<FileProviderEntity>().Add(row);
        return row;
    }

    private void SeedFile(FileProviderEntity provider, string name, bool deleted)
    {
        _temp!.Db.Set<FileItemEntity>().Add(new FileItemEntity
        {
            Provider = provider,
            ProviderId = provider.Id,
            ExternalFileId = Guid.NewGuid().ToString("N"),
            Path = "/" + name,
            Name = name,
            ItemType = "file",
            MimeType = "application/octet-stream",
            IsDeleted = deleted,
            DeletedAt = deleted ? Now : null
        });
    }

    private CalendarEntity SeedCalendar(Guid owner, string name, string kind)
    {
        var row = new CalendarEntity
        {
            UserId = owner,
            Name = name,
            Kind = kind,
            Color = "#2563EB"
        };
        _temp!.Db.Set<CalendarEntity>().Add(row);
        return row;
    }

    private EventEntity SeedEvent(CalendarEntity book, string title, bool deleted)
    {
        var row = new EventEntity
        {
            Calendar = book,
            CalendarId = book.Id,
            Uid = Guid.NewGuid().ToString("N"),
            Title = title,
            DtStart = Now.AddHours(1),
            DtEnd = Now.AddHours(2),
            DeletedAt = deleted ? Now : null,
            DeletedByOperationId = deleted ? Guid.NewGuid() : null,
            DeletedByOperationKind = deleted ? "single" : null
        };
        _temp!.Db.Set<EventEntity>().Add(row);
        return row;
    }

    private TaskEntity SeedTask(CalendarEntity book, string title, bool deleted)
    {
        var row = new TaskEntity
        {
            UserId = book.UserId,
            Calendar = book,
            CalendarId = book.Id,
            Uid = Guid.NewGuid().ToString("N"),
            Title = title,
            DtStart = Now.AddHours(1),
            DeletedAt = deleted ? Now : null,
            DeletedByOperationId = deleted ? Guid.NewGuid() : null,
            DeletedByOperationKind = deleted ? "single" : null
        };
        _temp!.Db.Set<TaskEntity>().Add(row);
        return row;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        _harness?.Dispose();
        if (_temp is not null) await _temp.DisposeAsync();
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
}
