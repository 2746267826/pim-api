using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Storage;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Services;
using Pim.Module.Files.Providers.Abstractions;
using Pim.Module.Files.Providers.Local;
using Xunit;

namespace Pim.UnitTests.Files;

public sealed class LocalFileProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-local-files", Guid.NewGuid().ToString("N"));
    private readonly Guid _user = Guid.NewGuid();
    private readonly ManagedBinaryOptions _options;
    private readonly ManagedBinaryStore _binary;
    private readonly LocalFileStore _store;
    private readonly LocalFileGrants _grants;
    private readonly LocalFileProvider _provider;
    public LocalFileProviderTests()
    {
        _options = new() { Root = _root };
        _binary = new(Options.Create(_options)); _store = new(_binary);
        _grants = new(new EphemeralDataProtectionProvider(), _store, new HttpContextAccessor());
        _provider = new(_store, _grants);
    }
    private Task<IFileProviderSession> Session(Guid? user = null) => _provider.OpenSessionAsync(new(LocalFileProvider.IdFor(user ?? _user),
        user ?? _user, "local", null, null, "builtin", _ => throw new Exception("Local storage must never request an OAuth token")));

    [Fact]
    public async Task NoOAuthFileFolderRenameMoveReadAndTrashRestoreUseTheCommonProvider()
    {
        var session = await Session(); var root = new RemoteFolderRef("root", "/");
        var folder = await session.Writer.CreateFolderAsync(root, "课堂");
        var file = await session.Writer.CreateFileAsync(new(folder, "/课堂"), "笔记.txt", "原稿"u8.ToArray(), "text/plain");
        Assert.Equal("原稿"u8.ToArray(), (await session.Content.DownloadSmallAsync(file, 1024))!.Bytes);
        await session.Writer.PatchItemAsync(file, "改名.txt", "root");
        Assert.Equal("/改名.txt", (await session.Content.GetItemAsync(file))!.Path);
        await session.Writer.DeleteItemAsync(file);
        Assert.Null(await session.Content.GetItemAsync(file));
        await ((IRestorableFileItemWriter)session.Writer).RestoreItemAsync(file, default);
        Assert.NotNull(await session.Content.GetItemAsync(file));
        Assert.Null(session.Shares); Assert.Equal(FileProviderAuthorizationMode.None, _provider.AuthorizationMode);
        Assert.NotNull(await session.Content.GetDownloadUrlAsync(file));
    }

    [Fact]
    public async Task AccountIsolationTraversalDuplicatesAndRootMutationsAreRejected()
    {
        var a = await Session(); var b = await Session(Guid.NewGuid());
        var file = await a.Writer.CreateFileAsync(new("root", "/"), "a.txt", [1], "text/plain");
        Assert.Null(await b.Content.GetItemAsync(file));
        await Assert.ThrowsAsync<DomainException>(() => a.Writer.CreateFileAsync(new(null, "/../outside"), "a.txt", [1], "text/plain"));
        await Assert.ThrowsAsync<DomainException>(() => a.Writer.CreateFileAsync(new("root", "/"), "a.txt", [2], "text/plain"));
        await Assert.ThrowsAsync<DomainException>(() => a.Writer.DeleteItemAsync("root"));
        await Assert.ThrowsAsync<DomainException>(() => a.Writer.PatchItemAsync("root", "x", null));
        Assert.Equal(new byte[] { 1 }, (await a.Content.DownloadSmallAsync(file, 1024))!.Bytes);
    }

    [Fact]
    public async Task LargeUploadResumesRetriesAndReadsBackBeyondTheFourMbSmallFileLimit()
    {
        var session = await Session(); var data = new byte[6 * 1024 * 1024]; RandomNumberGenerator.Fill(data);
        var upload = await session.Writer.CreateUploadSessionAsync(new("root", "/"), "录音.wav");
        var ticket = Uri.UnescapeDataString(upload.UploadUrl.Split("ticket=")[1]);
        var uploadId = Guid.Parse(upload.UploadUrl.Split('?')[0].Split('/')[^1]);
        var grant = _grants.UploadTicket(uploadId, ticket);
        var first = data[..(3 * 1024 * 1024)]; var second = data[(3 * 1024 * 1024)..];
        var progress = await _grants.UploadAsync(grant, $"bytes 0-{first.Length - 1}/{data.Length}", new MemoryStream(first), default);
        Assert.Null(progress.Id);
        var retried = await _grants.UploadAsync(grant, $"bytes 0-{first.Length - 1}/{data.Length}", new MemoryStream(first), default);
        Assert.Equal(progress.Id, retried.Id); Assert.Equal(progress.NextExpectedRanges, retried.NextExpectedRanges);
        var done = await _grants.UploadAsync(grant, $"bytes {first.Length}-{data.Length - 1}/{data.Length}", new MemoryStream(second), default);
        Assert.NotNull(done.Id);
        var reopened = new LocalFileStore(new ManagedBinaryStore(Options.Create(_options)));
        var entry = await reopened.GetAsync(_user, LocalFileProvider.IdFor(_user), done.Id!);
        Assert.Equal(data, await File.ReadAllBytesAsync(_binary.ObjectPath(_user, ManagedBinaryStore.Files, LocalFileProvider.IdFor(_user), entry.BlobKey!)));
        Assert.Equal(data.LongLength, entry.Size);
        var again = await _grants.UploadAsync(grant, $"bytes {first.Length}-{data.Length - 1}/{data.Length}", new MemoryStream(second), default);
        Assert.Equal(done.Id, again.Id);
        second[0] ^= 1;
        var changed = await Assert.ThrowsAsync<DomainException>(() => _grants.UploadAsync(grant,
            $"bytes {first.Length}-{data.Length - 1}/{data.Length}", new MemoryStream(second), default));
        Assert.Equal(40901, changed.ErrorCode);
    }

    [Fact]
    public async Task PartialAppendRecoveryDoesNotDuplicateOrChangeAcknowledgedBytes()
    {
        var upload = Guid.NewGuid(); var data = new byte[] { 1, 2, 3, 4, 5 };
        Assert.Equal(3, await _binary.AppendUploadAsync(_user, upload, 0, data[..3], default));
        Assert.Equal(5, await _binary.AppendUploadAsync(_user, upload, 0, data, default));
        await Assert.ThrowsAsync<DomainException>(() => _binary.AppendUploadAsync(_user, upload, 0, new byte[] { 9, 2, 3 }, default));
    }

    [Fact]
    public async Task TrashingAndRestoringAFolderDoesNotResurrectPreviouslyDeletedChildren()
    {
        var session = await Session(); var folder = await session.Writer.CreateFolderAsync(new("root", "/"), "目录");
        var a = await session.Writer.CreateFileAsync(new(folder, "/目录"), "a.txt", [1], "text/plain");
        var b = await session.Writer.CreateFileAsync(new(folder, "/目录"), "b.txt", [2], "text/plain");
        await session.Writer.DeleteItemAsync(a); await session.Writer.DeleteItemAsync(folder);
        await ((IRestorableFileItemWriter)session.Writer).RestoreItemAsync(folder, default);
        Assert.Null(await session.Content.GetItemAsync(a)); Assert.NotNull(await session.Content.GetItemAsync(b));
    }

    [Fact]
    public async Task ManagedQuotasAndScopesKeepNoteVersionsSeparateFromOrdinaryFiles()
    {
        _options.MaxUserBytes = 4; var collection = LocalFileProvider.IdFor(_user);
        var file = await _binary.PutAsync(_user, ManagedBinaryStore.Files, collection, new MemoryStream([1, 2]), 2);
        var noteCollection = Guid.NewGuid();
        var note = await _binary.PutAsync(_user, ManagedBinaryStore.NoteParts, noteCollection, new MemoryStream([3, 4]), 2);
        Assert.NotEqual(file.Key, note.Key);
        await Assert.ThrowsAsync<DomainException>(() => _binary.PutAsync(_user, ManagedBinaryStore.Files, collection, new MemoryStream([5]), 1));
        var duplicate = await _binary.PutAsync(_user, ManagedBinaryStore.Files, collection, new MemoryStream([1, 2]), 2);
        Assert.Equal(file, duplicate);
        await _binary.DeleteCollectionAsync(_user, ManagedBinaryStore.NoteParts, noteCollection);
        Assert.Equal(new byte[] { 1, 2 }, await File.ReadAllBytesAsync(_binary.ObjectPath(_user, ManagedBinaryStore.Files, collection, file.Key)));
    }

    [Fact]
    public async Task ReadAndUploadTicketsRejectTamperingWrongPurposeAndExpiredGrants()
    {
        var clock = new Clock();
        var grants = new LocalFileGrants(new EphemeralDataProtectionProvider(), _store, new HttpContextAccessor(), clock);
        var provider = LocalFileProvider.IdFor(_user);
        var file = await _store.CreateAsync(_user, provider, "root", "/", "x.txt", false, new MemoryStream([1]), "text/plain", 1);
        var read = grants.Read(_user, provider, new(file.Id, file.Name, 1, "text/plain", false, "/x.txt", file.Etag, "root"), false).Split("ticket=")[1];
        var token = Uri.UnescapeDataString(read);
        Assert.Equal(file.Id, grants.ReadTicket(token).ItemId);
        var upload = await grants.BeginUploadAsync(_user, provider, new("root", "/"), "y.txt", default);
        var id = Guid.Parse(upload.UploadUrl.Split('?')[0].Split('/')[^1]);
        var uploadToken = Uri.UnescapeDataString(upload.UploadUrl.Split("ticket=")[1]);
        Assert.Throws<DomainException>(() => grants.ReadTicket(uploadToken));
        Assert.Throws<DomainException>(() => grants.UploadTicket(id, token));
        Assert.Throws<DomainException>(() => grants.ReadTicket("x" + token[1..]));
        clock.Now = clock.Now.AddMinutes(6);
        Assert.Throws<DomainException>(() => grants.ReadTicket(token));
        Assert.Equal(id, grants.UploadTicket(id, uploadToken).UploadId);
        clock.Now = clock.Now.AddDays(1);
        Assert.Throws<DomainException>(() => grants.UploadTicket(id, uploadToken));
    }

    [Fact]
    public async Task ExpiredPendingUploadsReleaseCapacityAndStagedBytesWithoutDeletingPublishedFiles()
    {
        var clock = new Clock(); var grants = new LocalFileGrants(new EphemeralDataProtectionProvider(), _store, new HttpContextAccessor(), clock);
        var provider = LocalFileProvider.IdFor(_user);
        var upload = await grants.BeginUploadAsync(_user, provider, new("root", "/"), "first.bin", default);
        var id = Guid.Parse(upload.UploadUrl.Split('?')[0].Split('/')[^1]);
        var token = Uri.UnescapeDataString(upload.UploadUrl.Split("ticket=")[1]);
        await grants.UploadAsync(grants.UploadTicket(id, token), "bytes 0-0/2", new MemoryStream([1]), default);
        for (var i = 1; i < 10; i++) await grants.BeginUploadAsync(_user, provider, new("root", "/"), $"{i}.bin", default);
        await Assert.ThrowsAsync<DomainException>(() => grants.BeginUploadAsync(_user, provider, new("root", "/"), "overflow.bin", default));
        var published = await _store.CreateAsync(_user, provider, "root", "/", "keep.bin", false, new MemoryStream([3]), "application/octet-stream", 1);
        clock.Now = clock.Now.AddDays(2);
        await grants.BeginUploadAsync(_user, provider, new("root", "/"), "new.bin", default);
        Assert.False(File.Exists(_binary.ObjectPath(_user, ManagedBinaryStore.Uploads, id, ManagedBinaryStore.UploadKey)));
        Assert.NotNull(await _store.GetAsync(_user, provider, published.Id));
    }

    [Fact]
    public async Task ProvisioningIsIdempotentAndCannotSaveTheCallersPendingEntities()
    {
        var options = new DbContextOptionsBuilder<PimDbContext>().UseInMemoryDatabase("local-provision-" + Guid.NewGuid()).Options;
        await using var caller = new PimDbContext(options);
        var pending = new FileProviderEntity { UserId = _user, Provider = "onedrive", AccountId = "not-yet-saved" };
        caller.Add(pending);
        var first = await BuiltinLocalAccount.EnsureAsync(caller, _user, default);
        var second = await BuiltinLocalAccount.EnsureAsync(caller, _user, default);
        Assert.Equal(first.Id, second.Id); Assert.Equal(EntityState.Added, caller.Entry(pending).State);
        await using var reader = new PimDbContext(options);
        Assert.Equal("local", Assert.Single(await reader.Set<FileProviderEntity>().ToListAsync()).Provider);
        Assert.Equal("/", Assert.Single(await reader.Set<FileItemEntity>().ToListAsync()).Path);
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 26, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
