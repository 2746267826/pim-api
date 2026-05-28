using Microsoft.EntityFrameworkCore;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Secrets;
using Pim.Module.Files.DTOs;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;

namespace Pim.Module.Files.Services;

public sealed class FileProviderBindingService
{
    private readonly PimDbContext _db;
    private readonly ICurrentUserService _currentUser;
    private readonly ISecretProtector _secretProtector;
    private readonly IFileProviderAdapter _adapter;

    public FileProviderBindingService(
        PimDbContext db,
        ICurrentUserService currentUser,
        ISecretProtector secretProtector,
        IFileProviderAdapter adapter)
    {
        _db = db;
        _currentUser = currentUser;
        _secretProtector = secretProtector;
        _adapter = adapter;
    }

    private Guid UserId => _currentUser.UserId ?? throw new DomainException(1002, "未登录");

    public async Task<IReadOnlyList<FileProviderDto>> ListProvidersAsync(CancellationToken ct = default)
    {
        var userId = UserId;
        var providers = await _db.Set<FileProviderEntity>()
            .AsNoTracking()
            .Where(provider => provider.UserId == userId)
            .OrderBy(provider => provider.Provider)
            .ThenBy(provider => provider.Username)
            .ToListAsync(ct);

        return providers.Select(MapProvider).ToList();
    }

    public async Task<FileProviderDto> BindNextcloudAsync(
        BindNextcloudProviderRequest request,
        CancellationToken ct = default)
    {
        var userId = UserId;
        var baseUrl = NormalizeHttpUrl(request.BaseUrl, "Nextcloud 外部访问地址");
        var internalBaseUrl = string.IsNullOrWhiteSpace(request.InternalBaseUrl)
            ? null
            : NormalizeHttpUrl(request.InternalBaseUrl, "Nextcloud 内部访问地址");
        var username = NormalizeRequired(request.Username, "Nextcloud 用户名");
        var appPassword = NormalizeRequired(request.AppPassword, "Nextcloud 应用密码");
        var now = DateTimeOffset.UtcNow;

        var provider = await _db.Set<FileProviderEntity>()
            .FirstOrDefaultAsync(existing =>
                existing.UserId == userId
                && existing.Provider == "nextcloud"
                && existing.BaseUrl == baseUrl
                && existing.Username == username,
                ct);

        if (provider is null)
        {
            provider = new FileProviderEntity
            {
                UserId = userId,
                Provider = "nextcloud",
                BaseUrl = baseUrl,
                Username = username,
                CreatedAt = now
            };
            _db.Set<FileProviderEntity>().Add(provider);
        }

        provider.InternalBaseUrl = internalBaseUrl;
        provider.AppPasswordSecret = _secretProtector.Protect(appPassword);
        provider.Status = "pending";
        provider.LastError = null;
        provider.UpdatedAt = now;

        await _db.SaveChangesAsync(ct);
        return MapProvider(provider);
    }

    public async Task<FileProviderTestDto> TestProviderAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await LoadProviderAsync(providerId, ct);
        var result = await _adapter.TestConnectionAsync(ToConnection(provider), ct);
        provider.Status = result.Success ? "connected" : "error";
        provider.LastError = result.ErrorMessage;
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(ct);

        return new FileProviderTestDto(result.Success, result.Status, result.ErrorMessage);
    }

    public async Task<FileProviderConnection> GetConnectionAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await LoadProviderAsync(providerId, ct);
        return ToConnection(provider);
    }

    private async Task<FileProviderEntity> LoadProviderAsync(Guid providerId, CancellationToken ct)
    {
        var userId = UserId;
        return await _db.Set<FileProviderEntity>()
            .FirstOrDefaultAsync(provider => provider.Id == providerId && provider.UserId == userId, ct)
            ?? throw new DomainException(5104, "文件来源不存在");
    }

    private FileProviderConnection ToConnection(FileProviderEntity provider)
        => new(
            provider.Id,
            provider.BaseUrl,
            provider.InternalBaseUrl,
            provider.Username,
            _secretProtector.Unprotect(provider.AppPasswordSecret));

    private static FileProviderDto MapProvider(FileProviderEntity provider)
        => new(
            provider.Id,
            provider.Provider,
            provider.BaseUrl,
            provider.InternalBaseUrl,
            provider.Username,
            provider.Status,
            provider.LastSyncAt,
            provider.LastError,
            provider.CreatedAt,
            provider.UpdatedAt);

    private static string NormalizeRequired(string? value, string label)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new DomainException(5100, $"需要填写{label}");

        return normalized;
    }

    private static string NormalizeHttpUrl(string? value, string label)
    {
        var normalized = NormalizeRequired(value, label);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new DomainException(5101, $"{label}必须是绝对 HTTP 或 HTTPS 地址");
        }

        if (!string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new DomainException(5101, $"{label}必须是绝对 HTTP 或 HTTPS 地址");
        }

        var path = uri.AbsolutePath == "/"
            ? string.Empty
            : uri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(uri.Scheme.ToLowerInvariant(), uri.Host.ToLowerInvariant())
        {
            Path = path
        };

        if (!uri.IsDefaultPort)
            builder.Port = uri.Port;

        return builder.Uri.ToString().TrimEnd('/');
    }
}
