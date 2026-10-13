using Pim.Api.Services;
using Pim.Core.Common;

namespace Pim.Api.Endpoints;

public sealed record ApiVersionResponse(
    string Version,
    IReadOnlyList<string> Capabilities,
    string? LatestVersion,
    DateTimeOffset? CheckedAt,
    string? Error,
    string? WindowsVersion = null,
    string? AndroidVersion = null,
    string? ShellWindowsVersion = null,
    string? ShellAndroidVersion = null,
    // 尾部追加（可空、向后兼容）：本镜像内嵌的前端是哪一版。
    // 数据来源是镜像内 wwwroot/web-version.json，原样透传；文件缺失或不可读时
    // 返回哨兵值 "unknown"（见 WebVersionProvider.UnknownValue），既不假装有值也不 500。
    string? WebVersion = null,
    string? WebSha = null,
    string? WebBuiltAt = null);

public static class VersionEndpoints
{
    public const string MobileItemResultsV1 = "mobileItemResultsV1";
    public const string AndroidEmbedV1 = "androidEmbedV1";
    public static IReadOnlyList<string> Capabilities { get; } = [MobileItemResultsV1, AndroidEmbedV1];

    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/api/version", (GitHubReleaseService gh, WebVersionProvider web) =>
        {
            var version = typeof(Program).Assembly
                .GetCustomAttributes(false)
                .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
                .FirstOrDefault()?.InformationalVersion ?? "0.0.0(unknown)";
            var snap = gh.Snapshot;
            return Results.Ok(new ApiVersionResponse(
                version,
                Capabilities,
                // 最新版 = 本服务自身的版本（程序集版本）；客户端版本走 snap 的各组件字段
                version,
                snap.CheckedAt,
                snap.Error,
                snap.WindowsUrl != null ? (snap.WindowsVersion ?? snap.LatestVersion) : null,
                snap.AndroidUrl != null ? (snap.AndroidVersion ?? snap.LatestVersion) : null,
                snap.ShellWindowsUrl != null ? (snap.ShellWindowsVersion ?? snap.LatestVersion) : null,
                snap.ShellAndroidUrl != null ? (snap.ShellAndroidVersion ?? snap.LatestVersion) : null,
                web.Current.Version,
                web.Current.GitSha,
                web.Current.BuiltAt));
        }).AllowAnonymous()
        .Produces<ApiVersionResponse>(200)
        .Produces<ApiResponse<string>>(500);
        return endpoints;
    }
}
