using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Services;
using Xunit;

namespace Pim.UnitTests.Files;

public class FileProviderBindingServiceTests
{
    private static readonly Guid UserId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public async Task BindNextcloudAsync_ProtectsAppPasswordAndDoesNotReturnIt()
    {
        await using var db = CreateDb();
        var protector = new FakeSecretProtector();
        var adapter = new FakeFileProviderAdapter();
        var service = CreateService(db, protector, adapter);

        var dto = await service.BindNextcloudAsync(new BindNextcloudProviderRequest(
            "https://cloud.example.test/",
            "http://nextcloud/",
            "alice",
            "app-password"));

        Assert.Equal("nextcloud", dto.Provider);
        Assert.Equal("https://cloud.example.test", dto.BaseUrl);
        Assert.Equal("http://nextcloud", dto.InternalBaseUrl);
        Assert.Equal("alice", dto.Username);
        Assert.Equal("pending", dto.Status);

        var provider = await db.Set<FileProviderEntity>().SingleAsync();
        Assert.Equal("protected:app-password", provider.AppPasswordSecret);
        Assert.DoesNotContain("app-password", dto.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BindNextcloudAsync_RejectsNonHttpBaseUrl()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeSecretProtector(), new FakeFileProviderAdapter());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.BindNextcloudAsync(
            new BindNextcloudProviderRequest("ftp://cloud.example.test", null, "alice", "app-password")));

        Assert.Equal(5101, error.ErrorCode);
    }

    [Theory]
    [InlineData("https://user:token@cloud.example.test/")]
    [InlineData("https://cloud.example.test/?x=secret")]
    public async Task BindNextcloudAsync_RejectsUnsafeBaseUrlParts(string baseUrl)
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeSecretProtector(), new FakeFileProviderAdapter());

        var error = await Assert.ThrowsAsync<DomainException>(() => service.BindNextcloudAsync(
            new BindNextcloudProviderRequest(baseUrl, null, "alice", "app-password")));

        Assert.Equal(5101, error.ErrorCode);
    }

    [Fact]
    public async Task BindNextcloudAsync_CanonicalizesEquivalentBaseUrlsForUpsert()
    {
        await using var db = CreateDb();
        var service = CreateService(db, new FakeSecretProtector(), new FakeFileProviderAdapter());

        var first = await service.BindNextcloudAsync(new BindNextcloudProviderRequest(
            "HTTPS://Cloud.Example.Test:443/",
            null,
            "alice",
            "first-password"));
        var second = await service.BindNextcloudAsync(new BindNextcloudProviderRequest(
            "https://cloud.example.test",
            null,
            "alice",
            "second-password"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal("https://cloud.example.test", second.BaseUrl);
        var provider = await db.Set<FileProviderEntity>().SingleAsync();
        Assert.Equal("protected:second-password", provider.AppPasswordSecret);
    }

    [Fact]
    public async Task TestProviderAsync_UsesUnprotectedSecret()
    {
        await using var db = CreateDb();
        var adapter = new FakeFileProviderAdapter();
        var service = CreateService(db, new FakeSecretProtector(), adapter);
        var provider = await service.BindNextcloudAsync(new BindNextcloudProviderRequest(
            "https://cloud.example.test/",
            null,
            "alice",
            "app-password"));

        var result = await service.TestProviderAsync(provider.Id);

        Assert.True(result.Success);
        Assert.Equal("ok", result.Status);
        Assert.Null(result.ErrorMessage);
        Assert.NotNull(adapter.LastConnection);
        Assert.Equal("app-password", adapter.LastConnection.AppPassword);

        var providerEntity = await db.Set<FileProviderEntity>().SingleAsync();
        Assert.Equal("connected", providerEntity.Status);
    }

    private static PimDbContext CreateDb()
    {
        PimDbContext.RegisterModuleAssembly(typeof(FileProviderEntity).Assembly);
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"file-provider-binding-{Guid.NewGuid()}")
            .Options;
        return new PimDbContext(options);
    }

    private static FileProviderBindingService CreateService(
        PimDbContext db,
        ISecretProtector protector,
        IFileProviderAdapter adapter)
        => new(db, new FixedCurrentUserService(UserId), protector, adapter);

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
        public FileProviderConnection? LastConnection { get; private set; }

        public Task<FileProviderTestResult> TestConnectionAsync(
            FileProviderConnection connection,
            CancellationToken ct = default)
        {
            LastConnection = connection;
            return Task.FromResult(new FileProviderTestResult(true, "ok", null));
        }

        public Task<IReadOnlyList<ProviderFileItem>> ListFolderAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => throw new NotSupportedException();

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
            => throw new NotSupportedException();

        public Task<ProviderDownload> DownloadAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderFileItem> MoveAsync(
            FileProviderConnection connection,
            string sourcePath,
            string destinationPath,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderFileItem> RenameAsync(
            FileProviderConnection connection,
            string sourcePath,
            string name,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task DeleteToTrashAsync(
            FileProviderConnection connection,
            string path,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderTrashItem>> ListTrashAsync(
            FileProviderConnection connection,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RestoreTrashAsync(
            FileProviderConnection connection,
            string trashId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<ProviderFileVersion>> ListVersionsAsync(
            FileProviderConnection connection,
            string externalFileId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task<ProviderDownload> DownloadVersionAsync(
            FileProviderConnection connection,
            string externalFileId,
            string externalVersionId,
            string fileName,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public Task RestoreVersionAsync(
            FileProviderConnection connection,
            string externalFileId,
            string externalVersionId,
            CancellationToken ct = default)
            => throw new NotSupportedException();

        public ProviderOpenLink BuildOpenLink(
            FileProviderConnection connection,
            string path,
            string mode)
            => throw new NotSupportedException();
    }
}
