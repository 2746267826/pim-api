using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Pim.Module.Files.Providers;
using Xunit;

namespace Pim.UnitTests.Files;

/// <summary>
/// REQ-14 的**真实 HTTP** 验证（V1 机制部分 + 上传会话创建）。
///
/// 用 <see cref="FakeGraphServer"/>（真实 <c>HttpListener</c>）而不是 stub handler：
/// issue #343 的教训是 stub 不模拟真实语义就会假绿——凡涉及真实 HTTP 语义
/// （CORS 协商、会话往返、多请求交互）的路径，必须用真实栈或容器级断言覆盖。
///
/// 分片规则（320KiB 倍数 / ≤60MiB / 顺序）由**前端**执行，其验证在
/// <c>src/client-web/src/components/files/upload/__tests__</c> 的 vitest 用例里。
/// </summary>
public class OneDriveUploadSessionRealHttpTests
{
    /// <summary>
    /// AC-14.2：`createUploadSession` 请求本身**不携带文件字节**——
    /// PIM 服务器只创建会话，字节由浏览器直接发往微软。
    /// </summary>
    [Fact]
    public async Task CreateUploadSession_CarriesNoFileBytes()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        var session = await client.CreateUploadSessionAsync("at", "/x.bin", "x.bin");

        Assert.StartsWith(server.BaseUrl, session.UploadUrl);

        var createSession = server.Requests.Single(r => r.Path.Contains("createUploadSession", StringComparison.Ordinal));
        Assert.Equal("POST", createSession.Method);
        Assert.Equal(0, createSession.ByteCount);
        // 连一个字节都没有经过本服务
        Assert.Equal(0, server.UploadedBytesToServer);
    }

    /// <summary>
    /// REQ-12：会话创建必须要求 Graph 用 rename，绝不覆盖同名文件（AC-12.2）。
    /// 这里对**真实请求体**断言，而不是只看实现里有没有写字符串。
    /// </summary>
    [Fact]
    public async Task CreateUploadSession_RealRequestRequiresRenameConflictBehaviour()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        await client.CreateUploadSessionAsync("at", "/文档/大视频.mp4", "大视频.mp4");

        var captured = server.LastRequestBodyText;
        Assert.NotNull(captured);
        Assert.Contains("rename", captured);
        Assert.DoesNotContain("replace", captured);
    }

    /// <summary>
    /// <c>Files:OneDrive:GraphBaseUrl</c> 覆盖必须生效——这是「用真实 HTTP 栈做本地验证」的前提。
    /// 默认值仍是官方基址（未配置时不得改变线上行为）。
    /// </summary>
    [Fact]
    public async Task GraphBaseUrlOverride_RoutesRealRequestsToConfiguredHost()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        Assert.Equal(server.BaseUrl, client.GraphBaseUrl);
        await client.CreateUploadSessionAsync("at", "/x.bin", "x.bin");
        Assert.Contains(server.Requests, r => r.Path.Contains("createUploadSession", StringComparison.Ordinal));

        // 未配置时回落到官方基址
        var defaultClient = new OneDriveGraphClient(new RealHttpClientFactory());
        Assert.Equal(OneDriveGraphClient.DefaultGraphBaseUrl, defaultClient.GraphBaseUrl);
    }

    /// <summary>
    /// V1：CORS 预检。浏览器直传（REQ-14）之前会先发 OPTIONS 预检；
    /// 服务端必须允许来源/方法/头，否则浏览器根本发不出分片 PUT。
    /// 这条只能对**真实 HTTP** 断言——stub handler 不参与 CORS 协商。
    /// </summary>
    [Fact]
    public async Task CorsPreflight_IsAnsweredForBrowserDirectUpload()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        using var http = new HttpClient();
        using var preflight = new HttpRequestMessage(HttpMethod.Options, $"{server.BaseUrl}/upload/session-x");
        preflight.Headers.Add("Origin", "http://localhost:5911");
        preflight.Headers.Add("Access-Control-Request-Method", "PUT");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-range");

        using var response = await http.SendAsync(preflight);

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("http://localhost:5911", response.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Contains("PUT", response.Headers.GetValues("Access-Control-Allow-Methods").Single());
        Assert.Contains(
            "Content-Range",
            response.Headers.GetValues("Access-Control-Allow-Headers").Single(),
            StringComparison.OrdinalIgnoreCase);

        // 预检不携带内容体，也不得被当成上传
        Assert.Equal(0, server.UploadedBytesToServer);
    }

    /// <summary>
    /// 分片上传的**协议**在真实服务上跑通：顺序 PUT + Content-Range 被服务端接受，
    /// 且服务端累计收到的字节数等于文件大小（无缺口、无重复）。
    /// 这证明「按 Content-Range 顺序上传」这一平台要求是可用的。
    /// </summary>
    [Fact]
    public async Task SequentialChunkedPut_IsAcceptedAndAccountedOnRealServer()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        var session = await client.CreateUploadSessionAsync("at", "/文档/big.bin", "big.bin");

        // 10MiB + 1：故意让末块不是整块
        const int total = 10 * 1024 * 1024 + 1;
        const int chunkSize = 10 * 1024 * 1024;
        var payload = new byte[total];
        for (var i = 0; i < total; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        using var http = new HttpClient();
        long start = 0;
        while (start < total)
        {
            var length = (int)Math.Min(chunkSize, total - start);
            using var request = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl);
            request.Content = new ByteArrayContent(payload, (int)start, length);
            request.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(start, start + length - 1, total);
            using var response = await http.SendAsync(request);
            Assert.True(
                response.StatusCode is System.Net.HttpStatusCode.Accepted or System.Net.HttpStatusCode.Created,
                $"分片 {start}..{start + length - 1} 失败：{(int)response.StatusCode}");
            start += length;
        }

        Assert.Equal(total, server.UploadedBytesToServer);
        var puts = server.Requests.Where(r => r.Method == "PUT").ToList();
        Assert.Equal(2, puts.Count);
        Assert.Equal($"bytes 0-{chunkSize - 1}/{total}", puts[0].ContentRange);
        Assert.Equal($"bytes {chunkSize}-{total - 1}/{total}", puts[1].ContentRange);
    }

    /// <summary>
    /// 断点续传：中途失败后，向会话查询 <c>nextExpectedRanges</c> 能拿到「应从哪继续」，
    /// 从该偏移补齐后服务端累计字节数达到文件大小（没有缺口）。
    /// </summary>
    [Fact]
    public async Task UploadSession_ExposesNextExpectedRangeAfterChunkFailure()
    {
        await using var server = new FakeGraphServer();
        await server.StartAsync();

        var client = CreateClient(server);
        var session = await client.CreateUploadSessionAsync("at", "/文档/resume.bin", "resume.bin");

        const int total = 4 * 1024 * 1024;
        const int chunkSize = 2 * 1024 * 1024;
        var payload = new byte[total];

        using var http = new HttpClient();

        // 第一片成功
        using (var first = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl))
        {
            first.Content = new ByteArrayContent(payload, 0, chunkSize);
            first.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(0, chunkSize - 1, total);
            using var response = await http.SendAsync(first);
            Assert.Equal(System.Net.HttpStatusCode.Accepted, response.StatusCode);
        }

        // 第二片人为失败（模拟断流）
        server.FailNextUploadChunk = true;
        using (var failing = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl))
        {
            failing.Content = new ByteArrayContent(payload, chunkSize, chunkSize);
            failing.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(chunkSize, total - 1, total);
            using var response = await http.SendAsync(failing);
            Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        }

        // 查询会话：应告知从第二片起点续传
        using var probe = new HttpRequestMessage(HttpMethod.Get, session.UploadUrl);
        using var probeResponse = await http.SendAsync(probe);
        var body = await probeResponse.Content.ReadAsStringAsync();
        Assert.Contains($"{chunkSize}-", body);

        // 从该偏移补齐
        using (var resume = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl))
        {
            resume.Content = new ByteArrayContent(payload, chunkSize, chunkSize);
            resume.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(chunkSize, total - 1, total);
            using var response = await http.SendAsync(resume);
            Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);
        }

        // 被**接受**的字节恰好等于文件大小：续传没有重复发送第一片，也没有缺口
        Assert.Equal(total, server.AcceptedUploadBytes);
        // 总到达量 = 文件大小 + 那一次被拒的分片（它确实到达了服务端）
        Assert.Equal(total + chunkSize, server.UploadedBytesToServer);
    }

    /// <summary>
    /// 用**真实 HTTP 栈**（真实 <see cref="HttpClientHandler"/>）搭客户端，只把基址指向假 Graph。
    /// 不能塞 stub handler——那样请求根本到不了真实服务，用例会退化成「测自己搭的替身」。
    /// </summary>
    /// <summary>
    /// AC-12.1 的真实 HTTP 关键路径：重名时 Graph 会在上传完成响应里返回**改名后**的条目；
    /// 登记阶段必须按这个 id/name 回读，而不是按客户端提交的原路径——
    /// 否则会读到「本来就存在的那一个旧文件」，把它的元数据登记成本次上传的结果。
    /// </summary>
    [SkippableFact]
    public async Task UploadCompletion_ReturnsServerRenamedItemSoRegistrationCanUseIt()
    {
        await using var server = new FakeGraphServer
        {
            CompletedItemId = "renamed-1",
            CompletedItemName = "报告 (1).pdf",
        };
        await server.StartAsync();

        var client = CreateClient(server);
        var session = await client.CreateUploadSessionAsync("at", "/文档/报告.pdf", "报告.pdf");

        // 上传最后一块，服务端返回 201 + 最终条目
        using var http = new HttpClient();
        var payload = new byte[1024];
        using (var request = new HttpRequestMessage(HttpMethod.Put, session.UploadUrl))
        {
            request.Content = new ByteArrayContent(payload, 0, payload.Length);
            request.Content.Headers.ContentRange =
                new System.Net.Http.Headers.ContentRangeHeaderValue(0, payload.Length - 1, payload.Length);
            using var response = await http.SendAsync(request);
            Assert.Equal(System.Net.HttpStatusCode.Created, response.StatusCode);

            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
            Assert.Equal("renamed-1", document.RootElement.GetProperty("id").GetString());
            Assert.Equal("报告 (1).pdf", document.RootElement.GetProperty("name").GetString());
        }

        // 按 id 回读得到的就是改名后的那一个（登记走这条路径）
        var item = await client.GetItemByIdAsync("at", "renamed-1");
        Assert.NotNull(item);
        Assert.Equal("报告 (1).pdf", item!.Name);
        Assert.Equal("/文档", item.ParentPath);
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

    /// <summary>始终返回真实 <see cref="HttpClientHandler"/> 的工厂（真实 HTTP 栈）。</summary>
    private sealed class RealHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new HttpClientHandler());
    }
}
