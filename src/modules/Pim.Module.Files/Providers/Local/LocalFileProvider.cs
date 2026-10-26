using System.Security.Cryptography;
using System.Text;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Storage;
using Pim.Module.Files.Providers.Abstractions;

namespace Pim.Module.Files.Providers.Local;

public sealed class LocalFileProvider(LocalFileStore store, LocalFileGrants grants) : IFileProvider
{
    public const string Key = "local";
    public static Guid IdFor(Guid userId) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"pim:builtin-local:v1:{userId:D}"))[..16]);
    public string ProviderKey => Key;
    public FileProviderAuthorizationMode AuthorizationMode => FileProviderAuthorizationMode.None;
    public bool SupportsIncrementalSync => false;
    public bool SupportsSharing => false;
    public Task<IFileProviderSession> OpenSessionAsync(FileProviderContext context, CancellationToken ct = default)
    {
        if (context.ProviderKey != Key || context.UserId == Guid.Empty || context.ProviderRowId != IdFor(context.UserId))
            throw new DomainException(40301, "内置存储身份无效");
        return Task.FromResult<IFileProviderSession>(new Session(context, store, grants));
    }

    private sealed class Session(FileProviderContext owner, LocalFileStore store, LocalFileGrants grants) :
        IFileProviderSession, IFileContentReader, IFileItemWriter, IIncrementalChangeSource, IRestorableFileItemWriter
    {
        public FileProviderAccount Account => new(Key, "builtin", "内置存储（服务器）", null, LocalFileStore.RootId);
        public IFileContentReader Content => this;
        public IFileItemWriter Writer => this;
        public IFileShareManager? Shares => null;
        public IIncrementalChangeSource Changes => this;
        private Guid User => owner.UserId;
        private Guid Provider => owner.ProviderRowId;

        public async Task<RemoteItem?> GetItemAsync(string id, CancellationToken ct = default) =>
            await store.TransactionAsync(User, Provider, entries =>
                Task.FromResult(entries.TryGetValue(id, out var entry) && !entry.Trashed ? Remote(entries, entry) : null), false, ct);
        public async Task<RemoteItem?> GetItemByPathAsync(string path, CancellationToken ct = default)
        {
            if (!path.StartsWith('/') || path.Contains('\\') || path.Split('/').Any(part => part is "." or ".." || part.Any(char.IsControl)))
                throw new DomainException(5308, "路径无效");
            return await store.TransactionAsync(User, Provider, entries => Task.FromResult(entries.Values
                .Where(entry => !entry.Trashed && string.Equals(LocalFileStore.PathOf(entries, entry), path, StringComparison.OrdinalIgnoreCase))
                .Select(entry => Remote(entries, entry)).SingleOrDefault()), false, ct);
        }
        public async Task<string?> GetDownloadUrlAsync(string id, CancellationToken ct = default)
        {
            var item = await GetItemAsync(id, ct);
            return item is null || item.IsFolder ? null : grants.Read(User, Provider, item, preview: false);
        }
        public async Task<string?> GetPreviewUrlAsync(string id, CancellationToken ct = default)
        {
            var item = await GetItemAsync(id, ct);
            return item is null || item.IsFolder || !LocalFileGrants.CanPreview(item.MimeType)
                ? null : grants.Read(User, Provider, item, preview: true);
        }
        public Task<string?> GetThumbnailUrlAsync(string id, string size, CancellationToken ct = default) => GetPreviewUrlAsync(id, ct);
        public Task<string?> GetWebUrlAsync(string id, CancellationToken ct = default) => GetPreviewUrlAsync(id, ct);
        public async Task<RemoteContent?> DownloadSmallAsync(string id, long maxBytes, CancellationToken ct = default)
        {
            var item = await store.GetAsync(User, Provider, id, ct);
            if (item.IsFolder) throw new ProviderItemNotFoundException("文件夹没有文件内容");
            if (item.Size > maxBytes) throw new ProviderContentTooLargeException(item.Size);
            var path = store.Binaries.ObjectPath(User, ManagedBinaryStore.Files, Provider, item.BlobKey!);
            var bytes = await File.ReadAllBytesAsync(path, ct);
            if (bytes.LongLength > maxBytes) throw new ProviderContentTooLargeException(bytes.LongLength);
            return new(bytes, item.ContentType);
        }
        public async Task<string> CreateFileAsync(RemoteFolderRef parent, string name, byte[] bytes, string mime, CancellationToken ct = default) =>
            (await store.CreateAsync(User, Provider, parent.ExternalId, parent.Path, name, false, new MemoryStream(bytes), mime, bytes.LongLength, ct)).Id;
        public async Task PutContentAsync(string id, byte[] bytes, string mime, CancellationToken ct = default) =>
            _ = await store.ReplaceAsync(User, Provider, id, new MemoryStream(bytes), mime, bytes.LongLength, ct);
        public Task<string> PatchItemAsync(string id, string? name, string? parent, CancellationToken ct = default) => store.MoveAsync(User, Provider, id, name, parent, ct);
        public Task DeleteItemAsync(string id, CancellationToken ct = default) => store.SetTrashedAsync(User, Provider, id, true, ct);
        public Task RestoreItemAsync(string id, CancellationToken ct) => store.SetTrashedAsync(User, Provider, id, false, ct);
        public async Task<string> CreateFolderAsync(RemoteFolderRef parent, string name, CancellationToken ct = default) =>
            (await store.CreateAsync(User, Provider, parent.ExternalId, parent.Path, name, true, null, null, 0, ct)).Id;
        public Task<RemoteUploadSession> CreateUploadSessionAsync(RemoteFolderRef parent, string name, CancellationToken ct = default) =>
            grants.BeginUploadAsync(User, Provider, parent, name, ct);
        public Task<FileChangePage> GetChangesPageAsync(string? cursor, CancellationToken ct = default) =>
            store.TransactionAsync(User, Provider, entries => Task.FromResult(new FileChangePage(entries.Values.Where(item => !item.Trashed)
                .Select(item => new FileChange(item.Id, item.ParentId, item.ParentId is null ? null : LocalFileStore.PathOf(entries, entries[item.ParentId]),
                    item.Name, item.IsFolder, false, item.IsFolder ? null : item.Size, item.ContentType, item.Etag, item.ModifiedAt)).ToArray(), null, false)), false, ct);

        private static RemoteItem Remote(IReadOnlyDictionary<string, LocalFileEntry> entries, LocalFileEntry item) =>
            new(item.Id, item.Name, item.IsFolder ? null : item.Size, item.ContentType, item.IsFolder,
                LocalFileStore.PathOf(entries, item), item.Etag, item.ParentId);
    }
}
