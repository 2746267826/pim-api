using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// FileOperationService（文件模块 v2 的元数据读取面）：列表的「直接子项」语义、分页、
/// 单项/建议的归属校验，以及软删项不可见。
///
/// 这些行为原先由 FileOperationServiceTests 覆盖，但那个文件同时测了大量已退役的
/// WebDAV 写路径（move/rename/upload/trash/versions）而随之删除，本文件把仍生效的部分补回来。
/// </summary>
public class FileOperationServiceTests
{
    private static readonly Guid UserId = Guid.Parse("abababab-1111-2222-3333-444444444471");
    private static readonly Guid OtherUserId = Guid.Parse("abababab-9999-8888-7777-666666666671");

    private sealed class StubCurrentUser(Guid? userId) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role => "user";
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"file-ops-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderEntity SeedProvider(PimDbContext db, Guid userId)
    {
        var provider = new FileProviderEntity { UserId = userId, Provider = "onedrive", Status = "connected" };
        db.Set<FileProviderEntity>().Add(provider);
        db.SaveChanges();
        return provider;
    }

    private static FileItemEntity SeedItem(
        PimDbContext db, FileProviderEntity provider, string path, string name,
        string type = "file", bool deleted = false)
    {
        var item = new FileItemEntity
        {
            ProviderId = provider.Id,
            Provider = provider,
            ExternalFileId = Guid.NewGuid().ToString("N"),
            Path = path,
            Name = name,
            ItemType = type,
            IsDeleted = deleted,
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        };
        db.Set<FileItemEntity>().Add(item);
        return item;
    }

    private static FileOperationService CreateService(PimDbContext db, Guid? userId, StubAuditLog? audit = null)
        => new(db, new StubCurrentUser(userId), audit ?? new StubAuditLog());

    [Fact]
    public async Task ListItemsAsync_RootReturnsOnlyNonDeletedDirectChildren()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, "/合同", "合同", "folder");
        SeedItem(db, provider, "/a.txt", "a.txt");
        // 间接子项（更深层）与软删项都不应出现在根列表
        SeedItem(db, provider, "/合同/内部.txt", "内部.txt");
        SeedItem(db, provider, "/已删.txt", "已删.txt", deleted: true);
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/"));

        Assert.Equal(2, page.Items.Count);
        Assert.Contains(page.Items, i => i.Name == "合同");
        Assert.Contains(page.Items, i => i.Name == "a.txt");
        Assert.DoesNotContain(page.Items, i => i.Name == "内部.txt");
        Assert.DoesNotContain(page.Items, i => i.Name == "已删.txt");
    }

    [Fact]
    public async Task ListItemsAsync_FoldersSortBeforeFiles()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        SeedItem(db, provider, "/z.txt", "z.txt");
        SeedItem(db, provider, "/文件夹", "文件夹", "folder");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page = await service.ListItemsAsync(new FileListQuery("/"));

        Assert.Equal("文件夹", page.Items[0].Name);
    }

    [Fact]
    public async Task ListItemsAsync_PaginatesWithTotals()
    {
        await using var db = CreateDb();
        var provider = SeedProvider(db, UserId);
        for (var i = 0; i < 7; i++)
        {
            SeedItem(db, provider, $"/f{i:D2}.txt", $"f{i:D2}.txt");
        }

        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var page1 = await service.ListItemsAsync(new FileListQuery("/"), page: 1, pageSize: 3);
        var page3 = await service.ListItemsAsync(new FileListQuery("/"), page: 3, pageSize: 3);

        Assert.Equal(3, page1.Items.Count);
        Assert.Equal(7, page1.TotalCount);
        Assert.Equal(3, page1.TotalPages);
        Assert.Single(page3.Items);
        Assert.Empty(page1.Items.Select(i => i.Id).Intersect(page3.Items.Select(i => i.Id)));
    }

    [Fact]
    public async Task ListItemsAsync_WhenNotLoggedIn_Throws1002()
    {
        await using var db = CreateDb();
        var service = CreateService(db, null);

        var error = await Assert.ThrowsAsync<DomainException>(
            () => service.ListItemsAsync(new FileListQuery("/")));

        Assert.Equal(1002, error.ErrorCode);
    }

    [Fact]
    public async Task GetItemAsync_WhenDeletedOrForeign_ThrowsNotFound()
    {
        await using var db = CreateDb();
        var mine = SeedProvider(db, UserId);
        var theirs = SeedProvider(db, OtherUserId);
        var deleted = SeedItem(db, mine, "/gone.txt", "gone.txt", deleted: true);
        var foreign = SeedItem(db, theirs, "/other.txt", "other.txt");
        await db.SaveChangesAsync();
        var service = CreateService(db, UserId);

        var deletedError = await Assert.ThrowsAsync<DomainException>(() => service.GetItemAsync(deleted.Id));
        var foreignError = await Assert.ThrowsAsync<DomainException>(() => service.GetItemAsync(foreign.Id));

        Assert.Equal(5300, deletedError.ErrorCode);
        Assert.Equal(5300, foreignError.ErrorCode);
    }

    /// <summary>整理建议的归属校验：只能看到/操作自己的建议。</summary>
    [Fact]
    public async Task Suggestions_AreScopedToCurrentUser()
    {
        await using var db = CreateDb();
        var mine = SeedProvider(db, UserId);
        var theirs = SeedProvider(db, OtherUserId);
        var myItem = SeedItem(db, mine, "/a.txt", "a.txt");
        var theirItem = SeedItem(db, theirs, "/b.txt", "b.txt");
        await db.SaveChangesAsync();

        var mySuggestion = new FileSuggestionEntity
        {
            FileItemId = myItem.Id, SuggestionType = "organize", Title = "归档",
            Reason = "-", Confidence = 0.5m, Status = "pending", PayloadJson = "{}",
        };
        var theirSuggestion = new FileSuggestionEntity
        {
            FileItemId = theirItem.Id, SuggestionType = "organize", Title = "归档他人",
            Reason = "-", Confidence = 0.5m, Status = "pending", PayloadJson = "{}",
        };
        db.Set<FileSuggestionEntity>().AddRange(mySuggestion, theirSuggestion);
        await db.SaveChangesAsync();
        var audit = new StubAuditLog();
        var service = CreateService(db, UserId, audit);

        var listed = await service.ListSuggestionsAsync();
        Assert.Single(listed);
        Assert.Equal(mySuggestion.Id, listed[0].Id);

        // 操作他人的建议应 404，且不写审计
        var error = await Assert.ThrowsAsync<DomainException>(() => service.DismissSuggestionAsync(theirSuggestion.Id));
        Assert.Equal(5305, error.ErrorCode);
        Assert.Empty(audit.Actions);

        // 操作自己的建议成功并写审计
        var dismissed = await service.DismissSuggestionAsync(mySuggestion.Id);
        Assert.Equal("dismissed", dismissed.Status);
        Assert.Contains("files.suggestion_dismiss", audit.Actions);
    }
}
