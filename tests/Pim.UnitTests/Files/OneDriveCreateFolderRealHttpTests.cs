using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Pim.Module.Files.Providers;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// REQ-15 新建文件夹的**真实 HTTP** 验证。
///
/// 这一组用例的存在理由是一次验收打回：`CreateFolderAsync` 一度打成
/// <c>POST /drive/root:/{parent}</c>（缺 `:/children`），在真实账号上**必然 400 invalidRequest**，
/// 而当时所有测试仍然全绿——因为假 Graph 服务根本没有建文件夹分支，POST 落到默认分支
/// 返回 200，端点形态错了也看不出来。
///
/// 所以这里的假服务**复刻真实行为**：非 `:/children` 形态一律 400。
/// 用 stub handler 或宽松假服务都不行——它们不会像真服务那样拒绝错误端点。
/// </summary>
public class OneDriveCreateFolderRealHttpTests
{
    /// <summary>
    /// F-1（Critical）：建文件夹必须打到 `:/children` 形态，否则真实账号必失败。
    /// 断言的是**真实请求路径**，不是实现里有没有那个字符串。
    /// </summary>
    [Fact]
    public async Task CreateFolder_UsesChildrenEndpointForm()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        await client.CreateFolderAsync("at", "/文档", "报告");

        var request = server.Requests.Single(r =>
            r.Method == "POST" && r.Path.Contains("/drive/root", StringComparison.Ordinal));

        // 必须是 /drive/root:/文档:/children —— 缺 :/children 在真实账号上是 400
        Assert.Equal("/v1.0/drive/root:/%E6%96%87%E6%A1%A3:/children", request.Path);
        Assert.EndsWith(":/children", request.Path, StringComparison.Ordinal);
    }

    /// <summary>F-1：根目录场景必须打成 `/drive/root/children`（不能是 `/drive/root:/`）。</summary>
    [Fact]
    public async Task CreateFolder_InRoot_UsesRootChildrenEndpoint()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        await client.CreateFolderAsync("at", "/", "新建文件夹");

        var request = server.Requests.Single(r =>
            r.Method == "POST" && r.Path.Contains("/drive/root", StringComparison.Ordinal));
        Assert.Equal("/v1.0/drive/root/children", request.Path);
    }

    /// <summary>
    /// F-1 的**故障对照**：把端点形态写回错误的样子，真实 HTTP 假服务必须回 400——
    /// 证明这组用例真的能守住端点形态，而不是「跟着实现一起写绿」。
    /// </summary>
    [Fact]
    public async Task MalformedEndpointForm_IsRejectedLikeRealGraph()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{server.BaseUrl}/drive/root:/{Uri.EscapeDataString("文档")}");
        request.Content = new StringContent("""{"name":"报告","folder":{}}""", System.Text.Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>
    /// AC-15.1 / F-2：重名时 Graph 自动改名为 `名称 1`（实测格式是空格 + 序号），
    /// 客户端必须从**服务端返回**读到真实名称，而不是沿用输入名。
    /// </summary>
    [Fact]
    public async Task CreateFolder_DuplicateName_ReportsServerSideName()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        var first = await client.CreateFolderAsync("at", "/", "报告");
        var second = await client.CreateFolderAsync("at", "/", "报告");

        Assert.NotEqual(first, second);

        // 按 id 回读拿到的是服务端真实名称
        var firstItem = await client.GetItemByIdAsync("at", first);
        var secondItem = await client.GetItemByIdAsync("at", second);
        Assert.Equal("报告", firstItem!.Name);
        Assert.Equal("报告 1", secondItem!.Name);
        Assert.Equal("/", secondItem.ParentPath);
    }

    /// <summary>REQ-12 / AC-12.2：建文件夹也必须带 rename，绝不覆盖同名目录。</summary>
    [Fact]
    public async Task CreateFolder_RequiresRenameConflictBehaviour()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        await client.CreateFolderAsync("at", "/", "报告");

        var body = server.LastRequestBodyText;
        Assert.NotNull(body);
        Assert.Contains("rename", body);
        Assert.DoesNotContain("replace", body);
    }

    private static OneDriveGraphClient CreateClient(FakeGraphServer server)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Files:OneDrive:GraphBaseUrl"] = server.BaseUrl,
            })
            .Build();
        return new OneDriveGraphClient(new RealHttpClientFactory(), configuration);
    }

    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler());
    }
}
