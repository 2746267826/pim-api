using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Auth;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Pim.Infrastructure.Operations;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileOperationServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task SyncProviderAsync_UpsertsByProviderAndExternalFileIdWithoutChangingId()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var existing = await SeedItemAsync(
            db,
            provider,
            externalFileId: "file-1",
            path: "/Old/report.txt",
            name: "report.txt",
            etag: "old-etag");
        var service = CreateService(db, adapter);
        adapter.FolderItems =
        [
            ProviderItem("file-1", "/New/report-renamed.txt", "report-renamed.txt", etag: "new-etag")
        ];

        var synced = await service.SyncProviderAsync(provider.Id);

        Assert.Single(synced);
        Assert.Equal(existing.Id, synced[0].Id);

        var saved = await db.Set<FileItemEntity>()
            .Include(item => item.Versions)
            .SingleAsync(item => item.Id == existing.Id);
        Assert.Equal("/New/report-renamed.txt", saved.Path);
        Assert.Equal("report-renamed.txt", saved.Name);
        Assert.Equal("new-etag", saved.Etag);
        Assert.False(saved.IsDeleted);
        Assert.NotNull(saved.CurrentVersionId);
        Assert.Single(saved.Versions.Where(version => version.IsCurrent));
        Assert.Equal("current", saved.Versions.Single(version => version.IsCurrent).Source);
    }

    [Fact]
    public async Task SyncProviderAsync_MarksPreviouslySeenMissingProviderItemsDeleted()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var missing = await SeedItemAsync(db, provider, "missing-file", "/missing.txt", "missing.txt");
        await SeedItemAsync(db, provider, "seen-file", "/seen.txt", "seen.txt");
        var service = CreateService(db, adapter);
        adapter.FolderItems = [ProviderItem("seen-file", "/seen.txt", "seen.txt")];

        await service.SyncProviderAsync(provider.Id);

        var savedMissing = await db.Set<FileItemEntity>().SingleAsync(item => item.Id == missing.Id);
        Assert.True(savedMissing.IsDeleted);
        Assert.NotNull(savedMissing.DeletedAt);
    }

    [Fact]
    public async Task SyncProviderAsync_DoesNotDeleteNestedItemsDuringShallowRootSync()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        await SeedItemAsync(db, provider, "reports-folder", "/Reports", "Reports", itemType: "folder");
        var nested = await SeedItemAsync(db, provider, "nested-file", "/Reports/nested.txt", "nested.txt");
        var service = CreateService(db, adapter);
        adapter.FolderItems = [ProviderItem("reports-folder", "/Reports", "Reports", itemType: "folder")];

        await service.SyncProviderAsync(provider.Id);

        var savedNested = await db.Set<FileItemEntity>().SingleAsync(item => item.Id == nested.Id);
        Assert.False(savedNested.IsDeleted);
        Assert.Null(savedNested.DeletedAt);
    }

    [Fact]
    public async Task SyncProviderAsync_EtagChangeCreatesNewCurrentVersionWithoutMutatingOldVersionIdentity()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/report.txt", "report.txt", etag: "etag-old");
        var oldVersion = new FileVersionEntity
        {
            FileItemId = item.Id,
            ExternalVersionId = "current:etag-old",
            Etag = "etag-old",
            Size = 100,
            Source = "current",
            IsCurrent = true,
            ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
            SyncedAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.Set<FileVersionEntity>().Add(oldVersion);
        item.CurrentVersionId = oldVersion.Id;
        await db.SaveChangesAsync();
        var service = CreateService(db, adapter);
        adapter.FolderItems = [ProviderItem("file-1", "/report.txt", "report.txt", etag: "etag-new")];

        await service.SyncProviderAsync(provider.Id);

        var versions = await db.Set<FileVersionEntity>()
            .Where(version => version.FileItemId == item.Id)
            .OrderBy(version => version.ExternalVersionId)
            .ToListAsync();
        Assert.Equal(2, versions.Count);

        var savedOldVersion = versions.Single(version => version.ExternalVersionId == "current:etag-old");
        var newVersion = versions.Single(version => version.ExternalVersionId == "current:etag-new");
        Assert.Equal(oldVersion.Id, savedOldVersion.Id);
        Assert.False(savedOldVersion.IsCurrent);
        Assert.True(newVersion.IsCurrent);
        Assert.NotEqual(oldVersion.Id, newVersion.Id);

        var savedItem = await db.Set<FileItemEntity>().SingleAsync(saved => saved.Id == item.Id);
        Assert.Equal(newVersion.Id, savedItem.CurrentVersionId);
    }

    [Fact]
    public async Task ListItemsAsync_RootReturnsOnlyNonDeletedDirectChildren()
    {
        await using var db = CreateDb();
        var provider = await SeedProviderAsync(db);
        await SeedItemAsync(db, provider, "root-file", "/root.txt", "root.txt");
        await SeedItemAsync(db, provider, "root-folder", "/Reports", "Reports", itemType: "folder");
        await SeedItemAsync(db, provider, "nested-file", "/Reports/nested.txt", "nested.txt");
        await SeedItemAsync(db, provider, "deleted-file", "/deleted.txt", "deleted.txt", isDeleted: true);
        var service = CreateService(db, new FakeFileProviderAdapter());

        var result = await service.ListItemsAsync(new FileListQuery("/"));

        Assert.Equal(2, result.TotalCount);
        Assert.Equal(new[] { "Reports", "root.txt" }, result.Items.Select(item => item.Name).ToArray());
        Assert.All(result.Items, item => Assert.False(item.IsDeleted));
    }

    [Fact]
    public async Task ListItemsAsync_MapsLatestIndexJobStatus()
    {
        await using var db = CreateDb();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "root-file", "/root.txt", "root.txt");
        db.Set<FileIndexJobEntity>().Add(new FileIndexJobEntity
        {
            FileItemId = item.Id,
            Status = "completed",
            Stage = "content",
            FinishedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeFileProviderAdapter());

        var result = await service.ListItemsAsync(new FileListQuery("/"));

        Assert.Equal("completed", Assert.Single(result.Items).IndexStatus);
    }

    [Fact]
    public async Task MoveAsync_CallsAdapterPreservesExternalFileIdUpdatesPathAndRecordsAudit()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt", etag: "etag-1");
        adapter.MoveResult = ProviderItem("file-1", "/Archive/report.txt", "report.txt", etag: "etag-2");
        var service = CreateService(db, adapter);

        var moved = await service.MoveAsync(item.Id, new MoveFileRequest("/Archive/report.txt"));

        Assert.Equal("/Archive/report.txt", moved.Path);
        Assert.Equal("file-1", moved.ExternalFileId);
        Assert.Equal(1, adapter.MoveCallCount);
        Assert.Equal("/Reports/report.txt", adapter.LastMoveSourcePath);
        Assert.Equal("/Archive/report.txt", adapter.LastMoveDestinationPath);

        var saved = await db.Set<FileItemEntity>().SingleAsync(savedItem => savedItem.Id == item.Id);
        Assert.Equal("file-1", saved.ExternalFileId);
        Assert.Equal("/Archive/report.txt", saved.Path);
        Assert.Equal("etag-2", saved.Etag);
        await AssertAuditAsync(db, "files.move", item.Id);
    }

    [Fact]
    public async Task MoveAsync_WhenFolderMovesUpdatesDescendantPaths()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var folder = await SeedItemAsync(db, provider, "folder-1", "/Reports", "Reports", itemType: "folder");
        var child = await SeedItemAsync(db, provider, "child-1", "/Reports/nested.txt", "nested.txt");
        var grandchild = await SeedItemAsync(db, provider, "grandchild-1", "/Reports/Q1/deep.txt", "deep.txt");
        adapter.MoveResult = ProviderItem("folder-1", "/Archive/Reports", "Reports", itemType: "folder");
        var service = CreateService(db, adapter);

        await service.MoveAsync(folder.Id, new MoveFileRequest("/Archive/Reports"));

        var savedChild = await db.Set<FileItemEntity>().SingleAsync(item => item.Id == child.Id);
        var savedGrandchild = await db.Set<FileItemEntity>().SingleAsync(item => item.Id == grandchild.Id);
        Assert.Equal("/Archive/Reports/nested.txt", savedChild.Path);
        Assert.Equal("/Archive/Reports/Q1/deep.txt", savedGrandchild.Path);
    }

    [Fact]
    public async Task RenameAsync_CallsAdapterPreservesExternalFileIdUpdatesPathAndRecordsAudit()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt", etag: "etag-1");
        adapter.RenameResult = ProviderItem("file-1", "/Reports/summary.txt", "summary.txt", etag: "etag-2");
        var service = CreateService(db, adapter);

        var renamed = await service.RenameAsync(item.Id, new RenameFileRequest("summary.txt"));

        Assert.Equal("/Reports/summary.txt", renamed.Path);
        Assert.Equal("file-1", renamed.ExternalFileId);
        Assert.Equal(1, adapter.RenameCallCount);
        Assert.Equal("/Reports/report.txt", adapter.LastRenameSourcePath);
        Assert.Equal("summary.txt", adapter.LastRenameName);

        var saved = await db.Set<FileItemEntity>().SingleAsync(savedItem => savedItem.Id == item.Id);
        Assert.Equal("file-1", saved.ExternalFileId);
        Assert.Equal("/Reports/summary.txt", saved.Path);
        Assert.Equal("etag-2", saved.Etag);
        await AssertAuditAsync(db, "files.rename", item.Id);
    }

    [Fact]
    public async Task DeleteAsync_CallsDeleteToTrashMarksLocalItemDeletedAndRecordsAudit()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt");
        var service = CreateService(db, adapter);

        await service.DeleteAsync(item.Id);

        Assert.Equal(1, adapter.DeleteCallCount);
        Assert.Equal("/Reports/report.txt", adapter.LastDeletePath);

        var saved = await db.Set<FileItemEntity>().SingleAsync(savedItem => savedItem.Id == item.Id);
        Assert.True(saved.IsDeleted);
        Assert.NotNull(saved.DeletedAt);
        await AssertAuditAsync(db, "files.delete_to_trash", item.Id);
    }

    [Fact]
    public async Task DeleteAsync_WhenFolderDeletedMarksDescendantsDeleted()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var folder = await SeedItemAsync(db, provider, "folder-1", "/Reports", "Reports", itemType: "folder");
        var child = await SeedItemAsync(db, provider, "child-1", "/Reports/nested.txt", "nested.txt");
        var grandchild = await SeedItemAsync(db, provider, "grandchild-1", "/Reports/Q1/deep.txt", "deep.txt");
        var service = CreateService(db, adapter);

        await service.DeleteAsync(folder.Id);

        var savedChildren = await db.Set<FileItemEntity>()
            .Where(item => item.Id == child.Id || item.Id == grandchild.Id)
            .ToListAsync();
        Assert.All(savedChildren, item =>
        {
            Assert.True(item.IsDeleted);
            Assert.NotNull(item.DeletedAt);
        });
    }

    [Fact]
    public async Task UploadAsync_CallsAdapterUpsertsItemCurrentVersionAndRecordsAudit()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        adapter.UploadResult = ProviderItem("uploaded-file", "/Uploads/report.txt", "report.txt", etag: "etag-upload");
        var service = CreateService(db, adapter);
        await using var content = new MemoryStream("hello"u8.ToArray());

        var uploaded = await service.UploadAsync(provider.Id, "/Uploads/report.txt", content, "text/plain");

        Assert.Equal("uploaded-file", uploaded.ExternalFileId);
        Assert.Equal("/Uploads/report.txt", uploaded.Path);
        Assert.NotNull(uploaded.CurrentVersionId);
        Assert.Equal(1, adapter.UploadCallCount);
        Assert.Equal("/Uploads/report.txt", adapter.LastUploadDestinationPath);
        Assert.Equal("text/plain", adapter.LastUploadContentType);
        Assert.Equal("hello", adapter.LastUploadContent);

        var saved = await db.Set<FileItemEntity>()
            .Include(item => item.Versions)
            .SingleAsync(item => item.ProviderId == provider.Id && item.ExternalFileId == "uploaded-file");
        Assert.Equal(uploaded.Id, saved.Id);
        Assert.Equal("etag-upload", saved.Etag);
        var current = Assert.Single(saved.Versions.Where(version => version.IsCurrent));
        Assert.Equal("current:etag-upload", current.ExternalVersionId);
        Assert.Equal(current.Id, saved.CurrentVersionId);
        await AssertAuditAsync(db, "files.upload", saved.Id);
    }

    [Fact]
    public async Task UploadAsync_WhenProviderReturnsExistingExternalIdUpdatesItemAndCurrentVersion()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var existing = await SeedItemAsync(
            db,
            provider,
            externalFileId: "uploaded-file",
            path: "/Uploads/old-report.txt",
            name: "old-report.txt",
            etag: "etag-old");
        var oldVersion = await SeedVersionAsync(db, existing, "current:etag-old", "etag-old", isCurrent: true);
        existing.CurrentVersionId = oldVersion.Id;
        await db.SaveChangesAsync();
        adapter.UploadResult = ProviderItem("uploaded-file", "/Uploads/report.txt", "report.txt", etag: "etag-new");
        var service = CreateService(db, adapter);
        await using var content = new MemoryStream("new"u8.ToArray());

        var uploaded = await service.UploadAsync(provider.Id, "/Uploads/report.txt", content, "text/plain");

        Assert.Equal(existing.Id, uploaded.Id);
        Assert.Equal("etag-new", uploaded.Etag);

        var saved = await db.Set<FileItemEntity>()
            .Include(item => item.Versions)
            .SingleAsync(item => item.Id == existing.Id);
        Assert.Equal("/Uploads/report.txt", saved.Path);
        Assert.Equal("report.txt", saved.Name);
        Assert.Equal("etag-new", saved.Etag);
        Assert.False(saved.IsDeleted);
        var current = Assert.Single(saved.Versions.Where(version => version.IsCurrent));
        Assert.Equal("current:etag-new", current.ExternalVersionId);
        Assert.Equal(current.Id, saved.CurrentVersionId);
        Assert.False(saved.Versions.Single(version => version.Id == oldVersion.Id).IsCurrent);
    }

    [Fact]
    public async Task UploadAsync_WhenDestinationHasNoFileNameThrows()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var service = CreateService(db, adapter);
        await using var content = new MemoryStream("hello"u8.ToArray());

        var ex = await Assert.ThrowsAsync<DomainException>(() =>
            service.UploadAsync(provider.Id, "/", content, "text/plain"));

        Assert.Equal(5301, ex.ErrorCode);
        Assert.Equal(0, adapter.UploadCallCount);
    }

    [Fact]
    public async Task DownloadAsync_WhenFolderThrowsDomainException()
    {
        await using var db = CreateDb();
        var provider = await SeedProviderAsync(db);
        var folder = await SeedItemAsync(db, provider, "folder-1", "/Reports", "Reports", itemType: "folder");
        var service = CreateService(db, new FakeFileProviderAdapter());

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.DownloadAsync(folder.Id));

        Assert.Equal(5303, ex.ErrorCode);
        Assert.Equal("Folders cannot be downloaded through this endpoint", ex.Message);
    }

    [Fact]
    public async Task DownloadAsync_WhenFileCallsAdapterWithItemPath()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt");
        adapter.DownloadResult = new ProviderDownload(new MemoryStream("download"u8.ToArray()), "text/plain", "report.txt");
        var service = CreateService(db, adapter);

        var download = await service.DownloadAsync(item.Id);

        Assert.Equal(1, adapter.DownloadCallCount);
        Assert.Equal("/Reports/report.txt", adapter.LastDownloadPath);
        Assert.Equal("text/plain", download.ContentType);
        Assert.Equal("report.txt", download.FileName);
    }

    [Fact]
    public async Task GetItemAsync_WhenItemIsDeletedThrowsNotFound()
    {
        await using var db = CreateDb();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(
            db,
            provider,
            "deleted-file",
            "/Reports/deleted.txt",
            "deleted.txt",
            isDeleted: true);
        var service = CreateService(db, new FakeFileProviderAdapter());

        var ex = await Assert.ThrowsAsync<DomainException>(() => service.GetItemAsync(item.Id));

        Assert.Equal(5300, ex.ErrorCode);
    }

    [Fact]
    public async Task ListTrashAsync_AggregatesCurrentUserProviderTrashInProviderOrder()
    {
        await using var db = CreateDb();
        var firstProvider = await SeedProviderAsync(db);
        var otherUserProvider = await SeedProviderAsync(db, Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var secondProvider = await SeedProviderAsync(db);
        var adapter = new FakeFileProviderAdapter();
        adapter.TrashByProvider[firstProvider.Id] =
        [
            new ProviderTrashItem("trash-1", "/Reports/old.txt", "old.txt", "file", 10, DateTimeOffset.UtcNow)
        ];
        adapter.TrashByProvider[otherUserProvider.Id] =
        [
            new ProviderTrashItem("trash-other", "/private.txt", "private.txt", "file", 10, DateTimeOffset.UtcNow)
        ];
        adapter.TrashByProvider[secondProvider.Id] =
        [
            new ProviderTrashItem("trash-2", "/Reports/new.txt", "new.txt", "file", 20, DateTimeOffset.UtcNow)
        ];
        var service = CreateService(db, adapter);

        var trash = await service.ListTrashAsync();

        Assert.Equal(new[] { "trash-1", "trash-2" }, trash.Select(item => item.TrashId).ToArray());
        Assert.Equal(new[] { firstProvider.Id, secondProvider.Id }, adapter.ListTrashProviderIds);
    }

    [Fact]
    public async Task RestoreTrashAsync_CallsAdapterForCurrentUserProviderAndAuditsProviderId()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var service = CreateService(db, adapter);

        await service.RestoreTrashAsync(provider.Id, "trash-1");

        Assert.Equal(1, adapter.RestoreTrashCallCount);
        Assert.Equal(provider.Id, adapter.LastRestoreTrashProviderId);
        Assert.Equal("trash-1", adapter.LastRestoreTrashId);
        await AssertAuditAsync(db, "files.trash_restore", provider.Id, "file_provider");
    }

    [Fact]
    public async Task ListVersionsAsync_StoresHistoricalVersionsWithoutCreatingIndexJobs()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt");
        adapter.Versions =
        [
            new ProviderFileVersion("1700000000", "etag-old", 90, DateTimeOffset.UtcNow.AddDays(-1), "history", false),
            new ProviderFileVersion("1700001000", "etag-older", 80, DateTimeOffset.UtcNow.AddDays(-2), "history", false)
        ];
        var service = CreateService(db, adapter);

        var versions = await service.ListVersionsAsync(item.Id);

        Assert.Equal(new[] { "1700001000", "1700000000" }, versions.Select(version => version.ExternalVersionId).ToArray());
        Assert.Equal(2, await db.Set<FileVersionEntity>().CountAsync(version => version.FileItemId == item.Id));
        Assert.Empty(await db.Set<FileIndexJobEntity>().ToListAsync());
    }

    [Fact]
    public async Task DownloadVersionAsync_CallsAdapterWithItemAndVersionExternalIds()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt");
        var version = await SeedVersionAsync(db, item, "1700000000", "etag-old", isCurrent: false);
        adapter.DownloadVersionResult = new ProviderDownload(new MemoryStream("old"u8.ToArray()), "text/plain", "report.txt");
        var service = CreateService(db, adapter);

        var download = await service.DownloadVersionAsync(item.Id, version.Id);

        Assert.Equal(1, adapter.DownloadVersionCallCount);
        Assert.Equal("file-1", adapter.LastDownloadVersionExternalFileId);
        Assert.Equal("1700000000", adapter.LastDownloadVersionExternalVersionId);
        Assert.Equal("report.txt", adapter.LastDownloadVersionFileName);
        Assert.Equal("text/plain", download.ContentType);
    }

    [Fact]
    public async Task RestoreVersionAsync_CallsAdapterMarksRestoredVersionCurrentAndAudits()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt", etag: "etag-current");
        var current = await SeedVersionAsync(db, item, "current:etag-current", "etag-current", isCurrent: true);
        var history = await SeedVersionAsync(db, item, "1700000000", "etag-old", isCurrent: false);
        item.CurrentVersionId = current.Id;
        await db.SaveChangesAsync();
        var service = CreateService(db, adapter);

        await service.RestoreVersionAsync(item.Id, history.Id);

        Assert.Equal(1, adapter.RestoreVersionCallCount);
        Assert.Equal("file-1", adapter.LastRestoreVersionExternalFileId);
        Assert.Equal("1700000000", adapter.LastRestoreVersionExternalVersionId);

        var versions = await db.Set<FileVersionEntity>()
            .Where(version => version.FileItemId == item.Id)
            .ToListAsync();
        Assert.False(versions.Single(version => version.Id == current.Id).IsCurrent);
        Assert.True(versions.Single(version => version.Id == history.Id).IsCurrent);

        var savedItem = await db.Set<FileItemEntity>().SingleAsync(saved => saved.Id == item.Id);
        Assert.Equal(history.Id, savedItem.CurrentVersionId);
        Assert.Equal(history.ModifiedAt, savedItem.ModifiedAt);
        Assert.True(savedItem.SyncedAt >= savedItem.ModifiedAt);
        await AssertAuditAsync(db, "files.version_restore", item.Id);
    }

    [Fact]
    public async Task RestoreVersionPreviewAsync_ReturnsConfirmationRequired()
    {
        await using var db = CreateDb();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt", etag: "etag-current");
        var current = new FileVersionEntity
        {
            FileItemId = item.Id,
            ExternalVersionId = "current:etag-current",
            Etag = "etag-current",
            Size = 100,
            Source = "current",
            IsCurrent = true,
            ModifiedAt = DateTimeOffset.UtcNow,
            SyncedAt = DateTimeOffset.UtcNow
        };
        var history = new FileVersionEntity
        {
            FileItemId = item.Id,
            ExternalVersionId = "1700000000",
            Etag = "etag-old",
            Size = 90,
            Source = "history",
            IsCurrent = false,
            ModifiedAt = DateTimeOffset.UtcNow.AddDays(-1),
            SyncedAt = DateTimeOffset.UtcNow
        };
        db.Set<FileVersionEntity>().AddRange(current, history);
        item.CurrentVersionId = current.Id;
        await db.SaveChangesAsync();
        var service = CreateService(db, new FakeFileProviderAdapter());

        var preview = await service.RestoreVersionPreviewAsync(item.Id, history.Id);

        Assert.Equal(item.Id, preview.FileItemId);
        Assert.Equal(history.Id, preview.VersionId);
        Assert.True(preview.RequiresConfirmation);
        Assert.Contains("etag-current", preview.CurrentVersionLabel, StringComparison.Ordinal);
        Assert.Contains("etag-old", preview.RestoreVersionLabel, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildOpenLinkAsync_PassesExternalFileIdToAdapter()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt");
        var service = CreateService(db, adapter);

        var link = await service.BuildOpenLinkAsync(item.Id, null);

        Assert.Equal("view", link.Mode);
        Assert.Contains("openfile=file-1", link.Url, StringComparison.Ordinal);
        Assert.Equal("/Reports/report.txt", adapter.LastOpenLinkPath);
        Assert.Equal("view", adapter.LastOpenLinkMode);
        Assert.Equal("file-1", adapter.LastOpenLinkExternalFileId);
    }

    [Fact]
    public async Task AcceptSuggestionAsync_OnlyChangesSuggestionStatusAndDoesNotCallProviderMutation()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var provider = await SeedProviderAsync(db);
        var item = await SeedItemAsync(db, provider, "file-1", "/Reports/report.txt", "report.txt");
        var suggestion = new FileSuggestionEntity
        {
            FileItemId = item.Id,
            SuggestionType = "rename",
            Title = "Rename report",
            Reason = "The title is clearer.",
            Confidence = 0.9m,
            PayloadJson = """{"name":"summary.txt"}""",
            Status = "pending",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        db.Set<FileSuggestionEntity>().Add(suggestion);
        await db.SaveChangesAsync();
        var originalUpdatedAt = suggestion.UpdatedAt;
        var service = CreateService(db, adapter);

        var accepted = await service.AcceptSuggestionAsync(suggestion.Id);

        Assert.Equal("accepted", accepted.Status);
        Assert.True(accepted.UpdatedAt > originalUpdatedAt);
        Assert.Equal(0, adapter.MoveCallCount);
        Assert.Equal(0, adapter.RenameCallCount);
        Assert.Equal(0, adapter.DeleteCallCount);
        Assert.Equal(0, adapter.RestoreVersionCallCount);

        var savedItem = await db.Set<FileItemEntity>().SingleAsync(saved => saved.Id == item.Id);
        Assert.Equal("/Reports/report.txt", savedItem.Path);
        await AssertAuditAsync(db, "files.suggestion_accept", item.Id);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"file-operation-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileOperationService CreateService(PimDbContext db, FakeFileProviderAdapter adapter)
    {
        var currentUser = new FixedCurrentUserService(UserId);
        var bindings = new FileProviderBindingService(db, currentUser, new FakeSecretProtector(), adapter);
        return new FileOperationService(db, currentUser, new AuditLogService(db), bindings, adapter);
    }

    private static async Task<FileProviderEntity> SeedProviderAsync(PimDbContext db, Guid? userId = null)
    {
        var provider = new FileProviderEntity
        {
            UserId = userId ?? UserId,
            BaseUrl = "https://cloud.example.test",
            InternalBaseUrl = "http://nextcloud",
            Username = "alice",
            AppPasswordSecret = "protected:app-password",
            Status = "connected",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Set<FileProviderEntity>().Add(provider);
        await db.SaveChangesAsync();
        return provider;
    }

    private static async Task<FileItemEntity> SeedItemAsync(
        PimDbContext db,
        FileProviderEntity provider,
        string externalFileId,
        string path,
        string name,
        string itemType = "file",
        string? etag = "etag-1",
        bool isDeleted = false)
    {
        var now = DateTimeOffset.UtcNow;
        var item = new FileItemEntity
        {
            ProviderId = provider.Id,
            ExternalFileId = externalFileId,
            ParentExternalFileId = null,
            Path = path,
            Name = name,
            ItemType = itemType,
            MimeType = itemType == "folder" ? null : "text/plain",
            Size = itemType == "folder" ? null : 100,
            Etag = etag,
            Permissions = "RGDNVW",
            IsDeleted = isDeleted,
            DeletedAt = isDeleted ? now : null,
            LastSeenAt = now,
            CreatedAt = now,
            ModifiedAt = now,
            SyncedAt = now
        };
        db.Set<FileItemEntity>().Add(item);
        await db.SaveChangesAsync();
        return item;
    }

    private static ProviderFileItem ProviderItem(
        string externalFileId,
        string path,
        string name,
        string itemType = "file",
        string? etag = "etag-1")
        => new(
            externalFileId,
            null,
            path,
            name,
            itemType,
            itemType == "folder" ? null : "text/plain",
            itemType == "folder" ? null : 100,
            etag,
            "RGDNVW",
            DateTimeOffset.UtcNow);

    private static async Task<FileVersionEntity> SeedVersionAsync(
        PimDbContext db,
        FileItemEntity item,
        string externalVersionId,
        string? etag,
        bool isCurrent)
    {
        var now = DateTimeOffset.UtcNow;
        var version = new FileVersionEntity
        {
            FileItemId = item.Id,
            ExternalVersionId = externalVersionId,
            Etag = etag,
            Size = 90,
            Source = externalVersionId.StartsWith("current:", StringComparison.Ordinal) ? "current" : "history",
            IsCurrent = isCurrent,
            ModifiedAt = now,
            SyncedAt = now
        };
        db.Set<FileVersionEntity>().Add(version);
        await db.SaveChangesAsync();
        return version;
    }

    private static async Task AssertAuditAsync(
        PimDbContext db,
        string action,
        Guid resourceId,
        string resourceType = "file")
    {
        var audit = await db.Set<AuditLogEntity>()
            .SingleOrDefaultAsync(log => log.Action == action && log.ResourceId == resourceId.ToString());
        Assert.NotNull(audit);
        Assert.Equal(UserId, audit.UserId);
        Assert.Equal(resourceType, audit.ResourceType);
        Assert.Equal("files", audit.Source);
        Assert.Equal("Success", audit.Result);
    }

    private sealed class FixedCurrentUserService(Guid userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private sealed class FakeSecretProtector : ISecretProtector
    {
        public string Protect(string value) => $"protected:{value}";

        public string Unprotect(string protectedValue)
            => protectedValue.StartsWith("protected:", StringComparison.Ordinal)
                ? protectedValue["protected:".Length..]
                : protectedValue;
    }

    private sealed class FakeFileProviderAdapter : IFileProviderAdapter
    {
        public IReadOnlyList<ProviderFileItem> FolderItems { get; set; } = [];
        public IReadOnlyList<ProviderFileVersion> Versions { get; set; } = [];
        public Dictionary<Guid, IReadOnlyList<ProviderTrashItem>> TrashByProvider { get; } = new();
        public List<Guid> ListTrashProviderIds { get; } = [];
        public ProviderFileItem? UploadResult { get; set; }
        public ProviderDownload? DownloadResult { get; set; }
        public ProviderDownload? DownloadVersionResult { get; set; }
        public ProviderFileItem? MoveResult { get; set; }
        public ProviderFileItem? RenameResult { get; set; }
        public int UploadCallCount { get; private set; }
        public int DownloadCallCount { get; private set; }
        public int MoveCallCount { get; private set; }
        public int RenameCallCount { get; private set; }
        public int DeleteCallCount { get; private set; }
        public int RestoreTrashCallCount { get; private set; }
        public int DownloadVersionCallCount { get; private set; }
        public int RestoreVersionCallCount { get; private set; }
        public string? LastUploadDestinationPath { get; private set; }
        public string? LastUploadContentType { get; private set; }
        public string? LastUploadContent { get; private set; }
        public string? LastDownloadPath { get; private set; }
        public string? LastMoveSourcePath { get; private set; }
        public string? LastMoveDestinationPath { get; private set; }
        public string? LastRenameSourcePath { get; private set; }
        public string? LastRenameName { get; private set; }
        public string? LastDeletePath { get; private set; }
        public Guid? LastRestoreTrashProviderId { get; private set; }
        public string? LastRestoreTrashId { get; private set; }
        public string? LastDownloadVersionExternalFileId { get; private set; }
        public string? LastDownloadVersionExternalVersionId { get; private set; }
        public string? LastDownloadVersionFileName { get; private set; }
        public string? LastRestoreVersionExternalFileId { get; private set; }
        public string? LastRestoreVersionExternalVersionId { get; private set; }
        public string? LastOpenLinkPath { get; private set; }
        public string? LastOpenLinkMode { get; private set; }
        public string? LastOpenLinkExternalFileId { get; private set; }

        public Task<FileProviderTestResult> TestConnectionAsync(
            FileProviderConnection connection,
            CancellationToken ct = default)
            => Task.FromResult(new FileProviderTestResult(true, "connected", null));

        public Task<IReadOnlyList<ProviderFileItem>> ListFolderAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => Task.FromResult(FolderItems);

        public Task<ProviderFileItem> GetMetadataAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderFileItem> UploadAsync(
            FileProviderConnection connection,
            string destinationPath,
            Stream content,
            string contentType,
            CancellationToken ct = default)
        {
            UploadCallCount++;
            LastUploadDestinationPath = destinationPath;
            LastUploadContentType = contentType;
            using var reader = new StreamReader(
                content,
                System.Text.Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 1024,
                leaveOpen: true);
            LastUploadContent = reader.ReadToEnd();
            return Task.FromResult(UploadResult ?? ProviderItem("uploaded-file", destinationPath, Path.GetFileName(destinationPath)));
        }

        public Task<ProviderDownload> DownloadAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
        {
            DownloadCallCount++;
            LastDownloadPath = path;
            return Task.FromResult(DownloadResult ?? new ProviderDownload(new MemoryStream(), "application/octet-stream", Path.GetFileName(path)));
        }

        public Task<ProviderFileItem> MoveAsync(
            FileProviderConnection connection,
            string sourcePath,
            string destinationPath,
            CancellationToken ct = default)
        {
            MoveCallCount++;
            LastMoveSourcePath = sourcePath;
            LastMoveDestinationPath = destinationPath;
            return Task.FromResult(MoveResult ?? ProviderItem("moved-file", destinationPath, Path.GetFileName(destinationPath)));
        }

        public Task<ProviderFileItem> RenameAsync(
            FileProviderConnection connection,
            string sourcePath,
            string name,
            CancellationToken ct = default)
        {
            RenameCallCount++;
            LastRenameSourcePath = sourcePath;
            LastRenameName = name;
            var slashIndex = sourcePath.LastIndexOf('/');
            var parentPath = slashIndex <= 0 ? "/" : sourcePath[..slashIndex];
            var destinationPath = parentPath == "/" ? $"/{name}" : $"{parentPath}/{name}";
            return Task.FromResult(RenameResult ?? ProviderItem("renamed-file", destinationPath, name));
        }

        public Task DeleteToTrashAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
        {
            DeleteCallCount++;
            LastDeletePath = path;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProviderTrashItem>> ListTrashAsync(
            FileProviderConnection connection,
            CancellationToken ct = default)
        {
            ListTrashProviderIds.Add(connection.ProviderId);
            return Task.FromResult(TrashByProvider.GetValueOrDefault(connection.ProviderId) ?? []);
        }

        public Task RestoreTrashAsync(
            FileProviderConnection connection,
            string trashId,
            CancellationToken ct = default)
        {
            RestoreTrashCallCount++;
            LastRestoreTrashProviderId = connection.ProviderId;
            LastRestoreTrashId = trashId;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ProviderFileVersion>> ListVersionsAsync(
            FileProviderConnection connection,
            string externalFileId,
            CancellationToken ct = default)
            => Task.FromResult(Versions);

        public Task<ProviderDownload> DownloadVersionAsync(
            FileProviderConnection connection,
            string externalFileId,
            string externalVersionId,
            string fileName,
            CancellationToken ct = default)
        {
            DownloadVersionCallCount++;
            LastDownloadVersionExternalFileId = externalFileId;
            LastDownloadVersionExternalVersionId = externalVersionId;
            LastDownloadVersionFileName = fileName;
            return Task.FromResult(DownloadVersionResult ?? new ProviderDownload(new MemoryStream(), "application/octet-stream", fileName));
        }

        public Task RestoreVersionAsync(
            FileProviderConnection connection,
            string externalFileId,
            string externalVersionId,
            CancellationToken ct = default)
        {
            RestoreVersionCallCount++;
            LastRestoreVersionExternalFileId = externalFileId;
            LastRestoreVersionExternalVersionId = externalVersionId;
            return Task.CompletedTask;
        }

        public ProviderOpenLink BuildOpenLink(
            FileProviderConnection connection,
            string path,
            string mode,
            string? externalFileId = null)
        {
            LastOpenLinkPath = path;
            LastOpenLinkMode = mode;
            LastOpenLinkExternalFileId = externalFileId;
            var url = $"https://cloud.example.test/apps/files/files?dir=/&mode={Uri.EscapeDataString(mode)}";
            if (!string.IsNullOrWhiteSpace(externalFileId))
                url = $"{url}&openfile={Uri.EscapeDataString(externalFileId)}";

            return new ProviderOpenLink(url, mode);
        }
    }
}
