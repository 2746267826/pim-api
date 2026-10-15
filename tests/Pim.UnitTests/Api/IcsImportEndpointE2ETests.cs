using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Infrastructure.Data;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// issue #417 第二节第 1 条：`POST /api/v1/calendar/import-ics` 补 requestBody 时**只动文档**。
///
/// 用 <c>.Accepts&lt;T&gt;("multipart/form-data")</c> 补文档会连带改变端点匹配：非 multipart
/// 请求匹配不上端点，落到全局兜底 404「接口不存在」，而改动前它们是能进 handler 的
/// （text/plain → 400「需要 multipart/form-data」、form-urlencoded 缺文件 → 400「缺少文件字段」）。
/// 因此实现改用只影响文档的 <c>IcsImportRequestBodyOperationFilter</c>；本文件把
/// 「匹配与错误文案不变」钉住，真实上传链路也照跑一遍。
/// </summary>
public sealed class IcsImportEndpointE2ETests
{
    private const string Path = "/api/v1/calendar/import-ics";

    [Fact]
    public async Task ImportIcs_NonMultipartContentType_StillReachesTheHandlerFriendly400()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var response = await client.PostAsync(
            Path,
            new StringContent("{}", Encoding.UTF8, "text/plain"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(400, document.RootElement.GetProperty("code").GetInt32());
        Assert.Equal("需要 multipart/form-data", document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ImportIcs_FormUrlEncodedWithoutFile_StillReportsMissingFileField()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var response = await client.PostAsync(
            Path,
            new FormUrlEncodedContent([new KeyValuePair<string, string>("calendarId", Guid.NewGuid().ToString())]));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("缺少文件字段", document.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task ImportIcs_MultipartUpload_StillImportsTheEvent()
    {
        using var factory = CreateFactory();
        var client = await CreateAuthedClientAsync(factory);

        var ics = string.Join("\r\n",
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//PIM//contract-fix//EN",
            "BEGIN:VEVENT",
            $"UID:contract-fix-{Guid.NewGuid():N}@example.com",
            "DTSTART:20261001T080000Z",
            "DTEND:20261001T090000Z",
            "SUMMARY:契约修正回归",
            "END:VEVENT",
            "END:VCALENDAR",
            string.Empty);

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(ics));
        file.Headers.ContentType = new MediaTypeHeaderValue("text/calendar");
        form.Add(file, "file", "import.ics");

        var response = await client.PostAsync(Path, form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var report = document.RootElement.GetProperty("data");
        Assert.Equal(1, report.GetProperty("imported").GetInt32());
        Assert.Equal(0, report.GetProperty("skipped").GetInt32());
    }

    // ===== helpers =====

    private static async Task<HttpClient> CreateAuthedClientAsync(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        var username = $"ics-e2e-{Guid.NewGuid():N}"[..24];
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            username,
            email = $"{username}@example.com",
            password = "password123",
            displayName = "ics-e2e"
        });
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var token = doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        var databaseName = $"ics-e2e-{Guid.NewGuid():N}";
        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            // WO-AUTH-REGISTER-20261014：公开注册默认关闭；本文件靠注册造号，
            // 按决策记录 §2 在测试工厂里显式开启开关（不是删用例/跳用例换绿灯）。
            b.UseSetting("Auth:AllowPublicRegistration", "true");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(databaseName));
            });
        });
    }
}
