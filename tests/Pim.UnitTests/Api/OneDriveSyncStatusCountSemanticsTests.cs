using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Pim.Module.Files.Entities;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// issue #353：同步横幅「已同步 0 项」误导。
///
/// <para>
/// <c>syncedItemCount</c> 的真实语义是「**最近一次同步运行中应用（新增/更新）的项目数**」，
/// 每次运行结束时覆盖写入；日常最常见的「无增量变更」同步会把它写成 0。
/// 而界面文案「已同步 X 项」给人的是累计/总量直觉 —— 索引其实完整（生产实测 12 万+ 条）、
/// 浏览正常，却显示「已同步 0 项」，容易被误判为同步失败。
/// </para>
///
/// <para>
/// 需求方 2026-09-27 定稿的展示口径是**组合方案**：
/// 同时展示「上次同步时间 + 本次变更数 + 已索引总量」，
/// 参考格式「上次同步 12:40 · 本次变更 0 项 · 共 121,399 项」。
/// </para>
///
/// <para>
/// 本用例锁定后端契约：状态接口必须同时带出
/// 「本次变更数」（沿用 <c>syncedItemCount</c>，保持向后兼容）与
/// 「已索引总量」（新增字段），且两者语义互相独立 —— 无变更批次下
/// 本次变更数可以为 0，但已索引总量必须反映真实索引规模。
/// </para>
/// </summary>
public class OneDriveSyncStatusCountSemanticsTests
{
    private static WebApplicationFactory<Program> CreateFactory(string dbName)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(
                    d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(dbName));
            });
        });

    private static async Task<string> RegisterAndGetTokenAsync(HttpClient client, string username)
    {
        var response = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = username,
        });
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!;
    }

    /// <summary>从数据库反查刚注册用户的 Id（注册响应不保证带 userId）。</summary>
    private static async Task<Guid> ResolveUserIdAsync(IServiceProvider services, string username)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        return await db.Users.Where(user => user.Username == username).Select(user => user.Id).SingleAsync();
    }

    /// <summary>直接落库一条 provider 与其索引条目（不依赖真实 Graph）。</summary>
    private static async Task<Guid> SeedProviderAsync(
        IServiceProvider services,
        Guid userId,
        long syncedItemCount,
        string syncStatus,
        DateTimeOffset? lastSyncAt,
        int indexedItemCount)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();

        var provider = new FileProviderEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Provider = "onedrive",
            Status = "connected",
            SyncStatus = syncStatus,
            SyncedItemCount = syncedItemCount,
            LastSyncAt = lastSyncAt,
            LastError = null,
        };
        db.Set<FileProviderEntity>().Add(provider);

        for (var index = 0; index < indexedItemCount; index++)
        {
            db.Set<FileItemEntity>().Add(new FileItemEntity
            {
                Id = Guid.NewGuid(),
                ProviderId = provider.Id,
                ExternalFileId = $"ext-{index}",
                Path = $"/f{index}.txt",
                Name = $"f{index}.txt",
                ItemType = "file",
                IsDeleted = false,
                ModifiedAt = DateTimeOffset.UtcNow,
                SyncedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }

        await db.SaveChangesAsync();
        return provider.Id;
    }

    [Fact]
    public async Task SyncStatus_ExposesBothLastRunChangeCountAndTotalIndexedCount()
    {
        using var factory = CreateFactory($"sync-status-353-{Guid.NewGuid()}");
        var username = $"sync353_{Guid.NewGuid():N}"[..20];
        var token = await RegisterAndGetTokenAsync(factory.CreateClient(), username);
        var userId = await ResolveUserIdAsync(factory.Services, username);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // 无变更批次：本次变更 0 项，但索引里有 7 条。
        var lastSyncAt = new DateTimeOffset(2026, 9, 27, 12, 40, 0, TimeSpan.Zero);
        var providerId = await SeedProviderAsync(
            factory.Services, userId, syncedItemCount: 0, syncStatus: "idle",
            lastSyncAt: lastSyncAt, indexedItemCount: 7);

        var response = await client.GetAsync($"/api/v1/files/providers/{providerId}/sync-status");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");

        // 向后兼容：原字段仍存在且语义不变（本次运行的应用数）。
        Assert.True(data.TryGetProperty("syncedItemCount", out var changed));
        Assert.Equal(0, changed.GetInt64());

        // 新增：已索引总量，必须反映真实索引规模（而不是最近一次运行数）。
        Assert.True(
            data.TryGetProperty("totalIndexedCount", out var total),
            "状态接口必须带出「已索引总量」，否则横幅无法按定稿口径显示。");
        Assert.Equal(7, total.GetInt64());

        // 上次同步时间仍可用于「上次同步 12:40」。
        Assert.True(data.TryGetProperty("lastSyncAt", out _));
    }

    /// <summary>
    /// 有变更批次：本次变更数与已索引总量各自独立，
    /// 变更数不得被当成总量（这正是原缺陷的误导来源）。
    /// </summary>
    [Fact]
    public async Task SyncStatus_WithChanges_KeepsChangeCountSeparateFromIndexedTotal()
    {
        using var factory = CreateFactory($"sync-status-353b-{Guid.NewGuid()}");
        var username = $"sync353b_{Guid.NewGuid():N}"[..20];
        var token = await RegisterAndGetTokenAsync(factory.CreateClient(), username);
        var userId = await ResolveUserIdAsync(factory.Services, username);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var providerId = await SeedProviderAsync(
            factory.Services, userId, syncedItemCount: 3, syncStatus: "idle",
            lastSyncAt: DateTimeOffset.UtcNow, indexedItemCount: 120);

        var response = await client.GetAsync($"/api/v1/files/providers/{providerId}/sync-status");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");

        Assert.Equal(3, data.GetProperty("syncedItemCount").GetInt64());
        Assert.Equal(120, data.GetProperty("totalIndexedCount").GetInt64());
    }

    /// <summary>
    /// 已索引总量只统计**未删除**条目（软删除的条目不该计入「共 N 项」）。
    /// </summary>
    [Fact]
    public async Task SyncStatus_TotalIndexedCount_ExcludesDeletedItems()
    {
        using var factory = CreateFactory($"sync-status-353c-{Guid.NewGuid()}");
        var username = $"sync353c_{Guid.NewGuid():N}"[..20];
        var token = await RegisterAndGetTokenAsync(factory.CreateClient(), username);
        var userId = await ResolveUserIdAsync(factory.Services, username);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var providerId = await SeedProviderAsync(
            factory.Services, userId, syncedItemCount: 0, syncStatus: "idle",
            lastSyncAt: DateTimeOffset.UtcNow, indexedItemCount: 5);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
            var victim = await db.Set<FileItemEntity>().FirstAsync(item => item.ProviderId == providerId);
            victim.IsDeleted = true;
            victim.DeletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        var response = await client.GetAsync($"/api/v1/files/providers/{providerId}/sync-status");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var data = document.RootElement.GetProperty("data");

        Assert.Equal(4, data.GetProperty("totalIndexedCount").GetInt64());
    }

    /// <summary>
    /// providers 列表也带出该字段：文件页首屏据此渲染横幅，避免多打一次请求。
    /// </summary>
    [Fact]
    public async Task Providers_List_AlsoCarriesTotalIndexedCount()
    {
        using var factory = CreateFactory($"sync-status-353d-{Guid.NewGuid()}");
        var username = $"sync353d_{Guid.NewGuid():N}"[..20];
        var token = await RegisterAndGetTokenAsync(factory.CreateClient(), username);
        var userId = await ResolveUserIdAsync(factory.Services, username);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedProviderAsync(
            factory.Services, userId, syncedItemCount: 0, syncStatus: "idle",
            lastSyncAt: DateTimeOffset.UtcNow, indexedItemCount: 11);

        var response = await client.GetAsync("/api/v1/files/providers");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var providers = document.RootElement.GetProperty("data").EnumerateArray().ToList();

        var provider = Assert.Single(providers);
        Assert.Equal(11, provider.GetProperty("totalIndexedCount").GetInt64());
    }
}
