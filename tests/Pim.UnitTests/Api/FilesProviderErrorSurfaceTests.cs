using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers;
using Pim.Module.Files.Providers.Abstractions;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 文件模块**错误出口**的 HTTP 层用例（独立验收返修单 I-1 / I-2）。
///
/// 为什么必须有这一层：抽象层把 provider 失败归一化成自己的异常族（不是 <c>DomainException</c>），
/// 如果出口没有映射，上游的 404/429/502 会退化成 `500 内部服务器错误`
/// —— 这既是「OneDrive 既有功能回退」，也让「缺配置要明确报错」落空。
/// 只断言 service 抛异常覆盖不到出口，所以这里一律走真实宿主 + 真 HTTP。
///
/// 与其它 files E2E 放在同一 xunit collection：这几个类每次都要起一个真实宿主，
/// 并发起宿主的峰值是共享主机上 E2E 不稳定的来源之一，串行它们可以压低峰值。
/// </summary>
[Collection("files-e2e")]
public class FilesProviderErrorSurfaceTests
{
    /// <summary>可注入上游故障的假 Graph 客户端。</summary>
    private sealed class ThrowingGraphClient : OneDriveFilesEndpointsE2ETests.E2EGraphClient
    {
        public Exception? PatchException { get; set; }

        public override Task<string> PatchItemAsync(
            string accessToken,
            string itemId,
            string? newName,
            string? newParentId,
            CancellationToken ct = default)
            => PatchException is null
                ? base.PatchItemAsync(accessToken, itemId, newName, newParentId, ct)
                : Task.FromException<string>(PatchException);
    }

    private static async Task<(WebApplicationFactory<Program> Factory, HttpClient User, Guid ItemId)>
        CreateUserWithFileAsync(string dbName, ThrowingGraphClient graph)
    {
        var factory = OneDriveFilesEndpointsE2ETests.CreateFactory(dbName, graph);
        var anon = factory.CreateClient();
        var username = ("files-err-" + Guid.NewGuid().ToString("N"))[..18];
        var token = await OneDriveFilesEndpointsE2ETests.RegisterAndGetTokenAsync(anon, username);
        var user = OneDriveFilesEndpointsE2ETests.Authed(factory, token);

        await user.PostAsJsonAsync("/api/v1/files/providers/onedrive", new { clientId = "cid-err" });
        var providerId = await OneDriveFilesEndpointsE2ETests.GetSingleProviderIdAsync(factory);
        await user.GetAsync($"/api/v1/files/providers/{providerId}/binding-status");
        await user.PostAsync($"/api/v1/files/providers/{providerId}/sync", null);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var item = await db.Set<FileItemEntity>()
            .SingleAsync(row => row.ProviderId == providerId && row.ItemType == "file");
        return (factory, user, item.Id);
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseHeaders Headers)> RenameAsync(
        Exception upstream)
    {
        var graph = new ThrowingGraphClient { PatchException = upstream };
        var (factory, user, itemId) = await CreateUserWithFileAsync($"files-err-{Guid.NewGuid():N}", graph);
        using (factory)
        {
            var response = await user.PostAsJsonAsync(
                $"/api/v1/files/items/{itemId}/rename", new { name = "renamed.txt" });
            return (response.StatusCode, await response.Content.ReadAsStringAsync(), response.Headers);
        }
    }

    // ---- I-2：上游失败不得退化成 500 ----

    /// <summary>
    /// 上游限流（OneDrive 429）：必须把 429 与 <c>Retry-After</c> 透传给客户端，
    /// 而不是 500。基线由 `OneDriveGraphException` 支路提供这个语义，重构后不能丢。
    /// </summary>
    [Fact]
    public async Task UpstreamThrottling_SurfacesAs429WithRetryAfter()
    {
        var (status, body, headers) = await RenameAsync(new OneDriveGraphException(429, 7, "activityLimitReached"));

        Assert.Equal(HttpStatusCode.TooManyRequests, status);
        Assert.Equal("7", headers.RetryAfter?.ToString());
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(5390, doc.RootElement.GetProperty("code").GetInt32());
        // 不把上游错误体/令牌回显给客户端
        Assert.DoesNotContain("activityLimitReached", body, StringComparison.Ordinal);
    }

    /// <summary>上游 5xx：映射成 502（网关侧故障），而不是 500。</summary>
    [Fact]
    public async Task UpstreamServerError_SurfacesAs502()
    {
        var (status, body, _) = await RenameAsync(new OneDriveGraphException(503, null, "serviceUnavailable"));

        Assert.Equal(HttpStatusCode.BadGateway, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(5390, doc.RootElement.GetProperty("code").GetInt32());
    }

    /// <summary>上游 404：保持 404 语义（不要再变成 500）。</summary>
    [Fact]
    public async Task UpstreamNotFound_SurfacesAs404()
    {
        var (status, body, _) = await RenameAsync(new OneDriveGraphException(404, null, "itemNotFound"));

        Assert.Equal(HttpStatusCode.NotFound, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(5104, doc.RootElement.GetProperty("code").GetInt32());
    }

    /// <summary>归一化异常族本身也要在出口被认出来（它们不是 DomainException）。</summary>
    [Fact]
    public async Task NormalizedProviderExceptions_AreMappedAtTheHttpBoundary()
    {
        var (throttled, _, _) = await RenameAsync(new ProviderTransientException(429, 3, "throttled"));
        Assert.Equal(HttpStatusCode.TooManyRequests, throttled);

        var (expired, expiredBody, _) = await RenameAsync(new ProviderAuthorizationExpiredException("invalid_grant"));
        Assert.Equal(HttpStatusCode.BadRequest, expired);
        using var expiredDoc = JsonDocument.Parse(expiredBody);
        Assert.Equal(5322, expiredDoc.RootElement.GetProperty("code").GetInt32());

        var (missing, _, _) = await RenameAsync(new ProviderItemNotFoundException("gone"));
        Assert.Equal(HttpStatusCode.NotFound, missing);

        var (unsupported, unsupportedBody, _) = await RenameAsync(
            new ProviderCapabilityNotSupportedException("sharing", "google"));
        Assert.Equal(HttpStatusCode.BadRequest, unsupported);
        using var unsupportedDoc = JsonDocument.Parse(unsupportedBody);
        Assert.Equal(5334, unsupportedDoc.RootElement.GetProperty("code").GetInt32());
        // 能力名与 provider 名要如实回给用户，便于定位
        Assert.Contains("google", unsupportedBody, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// I-1：Google 未配置凭据时，授权端点必须 **400 + 可执行文案**，不能是 500。
    /// 文案要指名缺哪个环境变量，用户才知道怎么修。
    /// </summary>
    [SkippableFact]
    public async Task GoogleAuthorize_WithoutCredentials_Returns400WithActionableMessage()
    {
        Skip.If(
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PIM_FILES_GOOGLE_CLIENT_ID")),
            "本机配置了 Google 凭据，跳过「未配置」用例（CI 上未配置，会真实执行）");

        using var factory = OneDriveFilesEndpointsE2ETests.CreateFactory(
            $"files-err-google-{Guid.NewGuid():N}",
            new OneDriveFilesEndpointsE2ETests.E2EGraphClient());
        var anon = factory.CreateClient();
        var token = await OneDriveFilesEndpointsE2ETests.RegisterAndGetTokenAsync(
            anon, ("files-g-" + Guid.NewGuid().ToString("N"))[..18]);
        var user = OneDriveFilesEndpointsE2ETests.Authed(factory, token);

        var response = await user.PostAsync("/api/v1/files/providers/google/authorize", null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("PIM_FILES_GOOGLE_CLIENT_ID", body, StringComparison.Ordinal);
        Assert.Contains("PIM_FILES_GOOGLE_CLIENT_SECRET", body, StringComparison.Ordinal);
        // 不允许静默失败，也不允许建一条永远 pending 的脏行
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        Assert.False(await db.Set<FileProviderEntity>().AnyAsync(p => p.Provider == "google"));
    }

    /// <summary>
    /// 运维/配置类失败必须是**服务端故障**（502 + Error 日志），不能伪装成用户可自愈的 4xx ——
    /// 否则用户按「重新连接」的提示陷入死循环，而 4xx 也绕过 5xx 监控。
    /// </summary>
    [Fact]
    public async Task ProviderConfigurationFailure_SurfacesAs502WithAdminFacingMessage()
    {
        var (status, body, _) = await RenameAsync(
            new ProviderConfigurationFailureException("SERVICE_DISABLED"));

        Assert.Equal(HttpStatusCode.BadGateway, status);
        using var doc = JsonDocument.Parse(body);
        Assert.Equal(5390, doc.RootElement.GetProperty("code").GetInt32());
        // 注意：序列化把非 ASCII 转义成 \uXXXX，断言中文必须解析后看 message 字段
        Assert.Contains("配置异常", doc.RootElement.GetProperty("message").GetString()!, StringComparison.Ordinal);
        // 上游细节（reason / 项目号等）只进日志，不回显给客户端
        Assert.DoesNotContain("SERVICE_DISABLED", body, StringComparison.Ordinal);
    }

    /// <summary>OneDrive 设备码路径缺 client id 的行为（对照组，基线即 400）。</summary>
    [Fact]
    public async Task OneDriveBind_WithoutClientId_Returns400()
    {
        using var factory = OneDriveFilesEndpointsE2ETests.CreateFactory(
            $"files-err-onedrive-{Guid.NewGuid():N}",
            new OneDriveFilesEndpointsE2ETests.E2EGraphClient());
        var anon = factory.CreateClient();
        var token = await OneDriveFilesEndpointsE2ETests.RegisterAndGetTokenAsync(
            anon, ("files-o-" + Guid.NewGuid().ToString("N"))[..18]);
        var user = OneDriveFilesEndpointsE2ETests.Authed(factory, token);

        var response = await user.PostAsJsonAsync("/api/v1/files/providers/onedrive", new { clientId = "  " });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
