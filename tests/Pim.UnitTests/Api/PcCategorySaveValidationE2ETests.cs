using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.UnitTests.Harness.RealDb;
using Xunit;
using Xunit.Abstractions;

namespace Pim.UnitTests.Api;

/// <summary>
/// <c>POST /api/v1/pc/categories</c>（树形分类保存）的 <c>Name</c> 最小校验回归。
///
/// <para><b>为什么必须是真宿主 + 隔离真库</b>：本组断言的核心是
/// 「非法 Name 不得落库、不得覆盖既有行」。InMemory provider 既不建真实 NOT NULL 约束、
/// 也不做真实 SQL 翻译，脏数据是否真的写进库里在 InMemory 上根本看不出来；
/// 之前 <c>PcTrackerEndpointsE2ETests</c> 的分类用例正是跑在 InMemory 上，
/// 且只断言 200 往返，从未断言过任何 4xx 行为。</para>
///
/// <para><b>隔离</b>：每个用例在 <c>PIM_TEST_CONN</c> 指向的服务器上新建一次性库
/// <c>{前缀}_{GUID}</c>，跑完 <c>DROP DATABASE ... WITH (FORCE)</c>。
/// 生产库与镜像库不会被触碰。</para>
///
/// <para><b>认证边界（如实说明）</b>：带 token 的用例从宿主 DI 取
/// <see cref="JwtService"/> <b>直接签发</b> access token（<c>GenerateAccessToken</c>），
/// 绕开了 <c>/auth/register</c> 与登录流程，因此它<b>不能</b>证明「注册/登录/发号」链路正确；
/// 它只证明「请求带着本宿主认可的签名能进入受保护 handler」。
/// 鉴权拒绝侧由独立的无 Authorization 用例证明。</para>
/// </summary>
[Trait("DataSource", "RealDb")]
public sealed class PcCategorySaveValidationE2ETests
{
    private readonly ITestOutputHelper _output;

    public PcCategorySaveValidationE2ETests(ITestOutputHelper output)
    {
        _output = output;
    }

    private const string CategoriesPath = "/api/v1/pc/categories";

    /// <summary>既有子分类用于「带 id 更新」用例的关键属性基线值（刻意取得很特殊，便于肉眼比对）。</summary>
    private const string OriginalChildName = "RedR2-Original-Child";

    private const string OriginalChildColor = "#0a0b0c";
    private const string OriginalChildIcon = "redr2-icon";
    private const string OriginalChildProductivity = "neutral";
    private const int OriginalChildSortOrder = 973;

    /// <summary>
    /// 响应体中一旦出现这些片段，就说明内部细节泄露给了调用方（r1 review Important 1）。
    /// 覆盖 SQL 语句、PG 错误码/约束名、驱动与异常类型、.NET 调用栈、以及连接串/口令。
    /// </summary>
    private static readonly string[] LeakMarkers =
    {
        "Npgsql",
        "PostgresException",
        "PostgresErrorException",
        "23502",
        "23503",
        "not-null constraint",
        "INSERT INTO",
        "UPDATE ",
        "DELETE FROM",
        "SELECT ",
        "Exception:",
        "System.",
        "Stack",
        "Password=",
        "Username=",
        "Host=",
        "postgresql://",
        "Npgsql."
    };

    /// <summary>建隔离库 + 起真宿主。只注入连接串，**不**移除重建 <c>DbContextOptions&lt;PimDbContext&gt;</c>：
    /// 保持 <c>AddPimInfrastructure</c> 的生产装配路径，否则拦截器等装配差异不会被覆盖到。
    /// </summary>
    private static async Task<(TempMigrationDatabase Db, WebApplicationFactory<Program> Factory)> NewIsolatedHostAsync()
    {
        // 模块程序集必须先登记，否则 context 里没有对应 DbSet
        PimDbContext.RegisterModuleAssembly(typeof(PcCategoryEntity).Assembly);

        var temp = await TempMigrationDatabase.CreateAsync("test_pc_category_validation");
        await temp.MigrateAsync();
        // 迁移链不含 PcTracker 运行时初始化的那批对象，两步都要跑（与 CI 同）
        await temp.RunPcTrackerSchemaInitializerAsync();

        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("DisableHangfire", "true");
            builder.UseSetting("Database:Migrations:FailFast", "false");
            builder.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            builder.UseSetting("ConnectionStrings:DefaultConnection", temp.ConnectionString);
            // appsettings.json 默认指向 /data/keys/data-protection，本机不可写
            builder.UseSetting(
                "DataProtection:KeysPath",
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pim-dp-{Guid.NewGuid():N}"));
        });

        return (temp, factory);
    }

    /// <summary>带合法 Bearer token 的客户端（token 由宿主 <see cref="JwtService"/> 直接签发，见类型注释的边界说明）。</summary>
    private static HttpClient Authed(WebApplicationFactory<Program> factory)
    {
        var jwt = factory.Services.GetRequiredService<JwtService>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", jwt.GenerateAccessToken(Guid.NewGuid(), "cat-validation", "user"));
        return client;
    }

    /// <summary>
    /// 无 Authorization 的客户端：与 <see cref="Authed"/> 用<b>同一个 host、同一个隔离库</b>，
    /// 唯一差别就是不带凭据，用于证明「鉴权拒绝发生在写库之前」。
    /// </summary>
    private static HttpClient Anonymous(WebApplicationFactory<Program> factory)
        => factory.CreateClient();

    /// <summary>直接查隔离库，确认 <paramref name="name"/> 这一行是否真的落库了。</summary>
    private static async Task<List<string>> ReadPersistedNamesAsync(TempMigrationDatabase temp)
    {
        await using var command = temp.Db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM pc_categories ORDER BY created_at, name";
        if (command.Connection!.State != System.Data.ConnectionState.Open)
        {
            await command.Connection.OpenAsync();
        }

        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.IsDBNull(0) ? "<NULL>" : reader.GetString(0));
        }

        return names;
    }

    /// <summary>某一行分类的完整关键属性快照（用于证明「整行未被覆盖」，而不只是行数不变）。</summary>
    private sealed record CategoryRow(
        Guid Id,
        Guid? ParentId,
        string Name,
        string Color,
        string? Icon,
        string Productivity,
        int SortOrder,
        bool IsBuiltin);

    private static async Task<CategoryRow?> ReadCategoryRowAsync(TempMigrationDatabase temp, Guid id)
    {
        var connection = (NpgsqlConnection)temp.Db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using var command = new NpgsqlCommand(
            "SELECT id, parent_id, name, color, icon, productivity, sort_order, is_builtin "
            + "FROM pc_categories WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return null;
        }

        return new CategoryRow(
            reader.GetGuid(0),
            reader.IsDBNull(1) ? null : reader.GetGuid(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetString(5),
            reader.GetInt32(6),
            reader.GetBoolean(7));
    }

    private static string Describe(CategoryRow? row)
        => row is null
            ? "<row missing>"
            : $"name={Q(row.Name)} parentId={(row.ParentId?.ToString() ?? "<null>")} "
              + $"color={Q(row.Color)} icon={Q(row.Icon)} productivity={Q(row.Productivity)} "
              + $"sortOrder={row.SortOrder} isBuiltin={row.IsBuiltin}";

    private static string Q(string? value) => value is null ? "<null>" : $"\"{value}\"";

    private async Task<HttpResponseMessage> PostRawAsync(HttpClient client, string rawJson)
    {
        var content = new StringContent(rawJson, Encoding.UTF8, "application/json");
        return await client.PostAsync(CategoriesPath, content);
    }

    /// <summary>
    /// 断言错误响应是「干净的 400 JSON envelope」。
    /// <list type="bullet">
    /// <item>HTTP 恰好 400（不是 4xx 区间，更不是 5xx）——原实现对 <c>name:null</c> 会返回 500。</item>
    /// <item>envelope <c>code == 400</c>、<c>data == null</c>、<c>message</c> 非空、<c>timestamp</c> 非空。</item>
    /// <item>响应体不含 SQL / PG 错误码 / 驱动与异常类型 / .NET 调用栈 / 连接串口令。</item>
    /// </list>
    /// 形状依据：<c>src/Pim.Core/Common/ApiResponse.cs</c>（<c>Error(int,string)</c> → <c>code/message/data/timestamp</c>）。
    /// </summary>
    private void AssertCleanBadRequestEnvelope(string body, string scenario)
    {
        JsonDocument envelope;
        try
        {
            envelope = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            Assert.Fail($"[{scenario}] 400 响应体不是合法 JSON，无法核对 ApiResponse envelope：{ex.Message}\nbody={Truncate(body)}");
            return;
        }

        using (envelope)
        {
            var root = envelope.RootElement;
            Assert.True(root.ValueKind == JsonValueKind.Object, $"[{scenario}] 400 响应体不是 JSON 对象。");
            Assert.True(root.TryGetProperty("code", out var code), $"[{scenario}] envelope 缺 code 字段。body={Truncate(body)}");
            Assert.Equal(400, code.GetInt32());

            Assert.True(root.TryGetProperty("data", out var data), $"[{scenario}] envelope 缺 data 字段。");
            Assert.Equal(
                JsonValueKind.Null,
                data.ValueKind);

            Assert.True(root.TryGetProperty("message", out var message), $"[{scenario}] envelope 缺 message 字段。");
            Assert.Equal(JsonValueKind.String, message.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(message.GetString()), $"[{scenario}] envelope message 为空。");

            Assert.True(root.TryGetProperty("timestamp", out var timestamp), $"[{scenario}] envelope 缺 timestamp 字段。");
            Assert.Equal(JsonValueKind.String, timestamp.ValueKind);
            Assert.False(string.IsNullOrWhiteSpace(timestamp.GetString()), $"[{scenario}] envelope timestamp 为空。");
            Assert.True(
                DateTimeOffset.TryParse(timestamp.GetString(), out _),
                $"[{scenario}] envelope timestamp 不是可解析时间。");
        }

        // 安全性：不得把内部细节回给调用方
        foreach (var marker in LeakMarkers)
        {
            Assert.False(
                body.Contains(marker, StringComparison.OrdinalIgnoreCase),
                $"[{scenario}] 400 响应体泄露内部细节 \"{marker}\"：{Truncate(body)}");
        }
    }

    // ---------------------------------------------------------------------
    // 负例：非法 Name 必须明确 400 + 干净 envelope，且不得落库
    // ---------------------------------------------------------------------

    /// <summary>
    /// 逐个非法形态都必须返回 <b>明确 HTTP 400</b>，envelope 干净，且不得落库。
    /// 覆盖工单点名的 5 种：missing / null / empty / whitespace / legacy 扁平 body（+ 空对象）。
    ///
    /// <para><b>断言顺序（有意为之）</b>：所有数据库读取（before / after）都在任何状态断言
    /// <b>之前</b>执行完，并把结果写进测试输出。这样即使 400 断言先失败，
    /// 「有没有写脏数据」的证据也一定已经采集到，不会因为 HTTP 断言抛异常而丢失。</para>
    /// </summary>
    [SkippableTheory]
    [InlineData(@"{""color"":""#111111"",""productivity"":""neutral"",""sortOrder"":900}", "missing name")]
    [InlineData(@"{""name"":null,""color"":""#111111"",""productivity"":""neutral"",""sortOrder"":901}", "name is null")]
    [InlineData(@"{""name"":"""",""color"":""#111111"",""productivity"":""neutral"",""sortOrder"":902}", "name is empty string")]
    [InlineData(@"{""name"":""   "",""color"":""#111111"",""productivity"":""neutral"",""sortOrder"":903}", "name is whitespace only")]
    [InlineData(@"{""appPattern"":""probe-legacy"",""categoryName"":""LegacyFlat"",""color"":""#222222"",""priority"":1}", "legacy flat SaveCategoryRequest body")]
    [InlineData(@"{}", "empty object")]
    public async Task PostCategories_WithInvalidName_ReturnsBadRequestEnvelope_AndWritesNothing(
        string rawJson, string scenario)
    {
        RealDbTestConnection.Require();

        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var client = Authed(factory);

        // ---- 阶段 1：只采集证据（DB 读取），不做任何断言 ----
        var before = await ReadPersistedNamesAsync(temp);
        var response = await PostRawAsync(client, rawJson);
        var body = await response.Content.ReadAsStringAsync();
        var status = (int)response.StatusCode;
        // after 查询在状态断言之前完成：HTTP 断言失败也不会丢掉「是否写库」的证据
        var after = await ReadPersistedNamesAsync(temp);

        _output.WriteLine($"scenario            : {scenario}");
        _output.WriteLine($"request body        : {rawJson}");
        _output.WriteLine($"HTTP status         : {status}");
        _output.WriteLine($"response body       : {Truncate(body)}");
        _output.WriteLine($"rows before         : {before.Count}");
        _output.WriteLine($"rows after          : {after.Count}");
        _output.WriteLine($"newly persisted     : {string.Join(" | ", after.Skip(before.Count))}");
        _output.WriteLine($"after-query ran before status assertion: true");

        // ---- 阶段 2：断言 ----
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertCleanBadRequestEnvelope(body, scenario);
        Assert.Equal(before, after);
    }

    // ---------------------------------------------------------------------
    // 负例（鉴权边界）：无 Authorization 必须 401，且同库无写入
    // ---------------------------------------------------------------------

    /// <summary>
    /// 无 Authorization 的请求，对<b>合法</b> Name 和<b>非法</b> Name 都必须 401，
    /// 且在<b>同一个隔离库</b>上不得产生任何写入（证明拒绝发生在落库之前，而不是先写后拒）。
    ///
    /// <para>这是 r1 review Important 2 要求的真实未授权边界：原先只证明了「带合法 token 能进 handler」，
    /// 没有证明「匿名请求被拒绝」。</para>
    /// </summary>
    [SkippableTheory]
    [InlineData(@"{""name"":""Anon-Valid-Name"",""color"":""#123123"",""productivity"":""neutral"",""sortOrder"":910}", "legal name, anonymous")]
    [InlineData(@"{""name"":null,""color"":""#123123"",""productivity"":""neutral"",""sortOrder"":911}", "null name, anonymous")]
    [InlineData(@"{""name"":"""",""color"":""#123123"",""productivity"":""neutral"",""sortOrder"":912}", "empty name, anonymous")]
    [InlineData(@"{""name"":""   "",""color"":""#123123"",""productivity"":""neutral"",""sortOrder"":913}", "whitespace name, anonymous")]
    public async Task PostCategories_WithoutAuthorization_ReturnsUnauthorized_AndWritesNothing(
        string rawJson, string scenario)
    {
        RealDbTestConnection.Require();

        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var __ = factory;

        // 与带 token 的用例同一个 host、同一个隔离库，唯一差别是不带凭据
        var client = Anonymous(factory);
        Assert.Null(client.DefaultRequestHeaders.Authorization);

        // ---- 阶段 1：只采集证据 ----
        var before = await ReadPersistedNamesAsync(temp);
        var response = await PostRawAsync(client, rawJson);
        var status = (int)response.StatusCode;
        var after = await ReadPersistedNamesAsync(temp);

        _output.WriteLine($"scenario            : {scenario}");
        _output.WriteLine($"request body        : {rawJson}");
        _output.WriteLine($"HTTP status         : {status}");
        _output.WriteLine($"WWW-Authenticate    : {response.Headers.WwwAuthenticate}");
        _output.WriteLine($"rows before         : {before.Count}");
        _output.WriteLine($"rows after          : {after.Count}");
        _output.WriteLine($"newly persisted     : {string.Join(" | ", after.Skip(before.Count))}");
        _output.WriteLine($"after-query ran before status assertion: true");

        // ---- 阶段 2：断言 ----
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(before, after);
    }

    /// <summary>
    /// 匿名边界对照：同一 host 上，<b>合法</b> Name 带 token 必须 200，而不带 token 必须 401。
    /// 把 401 归因到「凭据」而不是「这个 host 什么都不许写」，排除假阳性。
    /// </summary>
    [SkippableFact]
    public async Task PostCategories_AuthorizedLegalName_Succeeds_While_AnonymousSameBody_IsUnauthorized()
    {
        RealDbTestConnection.Require();

        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var __ = factory;

        const string body = @"{""name"":""Auth-Pair-Probe"",""color"":""#654321"",""productivity"":""neutral"",""sortOrder"":914}";

        var anonymous = Anonymous(factory);
        var anonymousStatus = (int)(await anonymous.PostAsync(
            CategoriesPath,
            new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode;
        var rowsAfterAnonymous = await ReadPersistedNamesAsync(temp);

        using var authed = Authed(factory);
        var authedResponse = await PostRawAsync(authed, body);
        var authedStatus = (int)authedResponse.StatusCode;
        var authedBody = await authedResponse.Content.ReadAsStringAsync();
        var rowsAfterAuthed = await ReadPersistedNamesAsync(temp);

        _output.WriteLine($"anonymous HTTP      : {anonymousStatus}");
        _output.WriteLine($"rows after anon     : {rowsAfterAnonymous.Count}");
        _output.WriteLine($"authorized HTTP     : {authedStatus}");
        _output.WriteLine($"authorized body     : {Truncate(authedBody)}");
        _output.WriteLine($"rows after authed   : {rowsAfterAuthed.Count}");

        Assert.Equal(401, anonymousStatus);
        Assert.DoesNotContain("Auth-Pair-Probe", rowsAfterAnonymous);
        Assert.Equal(HttpStatusCode.OK, authedResponse.StatusCode);
        Assert.Contains("Auth-Pair-Probe", rowsAfterAuthed);
    }

    // ---------------------------------------------------------------------
    // 负例（更新入口）：带既有 id 的非法 Name 必须 400，且整行原属性不变
    // ---------------------------------------------------------------------

    /// <summary>
    /// 更新入口的 no-write 对照（r1 review Important 3）。
    ///
    /// <para><c>PcCategoryService.SaveAsync</c> 在 <c>req.Id.HasValue</c> 时走<b>更新已有实体</b>分支，
    /// 会把 <c>Name/Color/Icon/Productivity/ParentId/SortOrder</c> 全部无条件覆盖。
    /// 原先六个非法 body 都不带 id，所以完全无法证明「非法 Name 不会把一个已存在的合法节点改坏」。</para>
    ///
    /// <para>本用例：先建根 + 子两个合法分类，取子节点 id 与其完整原始行，
    /// 再分别以 missing / null / empty / whitespace 的 Name 走更新入口，
    /// 断言 HTTP 400，且该 id 的 <b>整行</b>（name/parentId/color/icon/productivity/sortOrder/isBuiltin）
    /// 与请求前逐字段相同——不是只比行数。</para>
    /// </summary>
    [SkippableTheory]
    [InlineData(@"{""id"":""{ID}"",""color"":""#0a0b0c"",""icon"":""redr2-icon"",""productivity"":""neutral"",""sortOrder"":973}", "missing name")]
    [InlineData(@"{""id"":""{ID}"",""name"":null,""color"":""#0a0b0c"",""icon"":""redr2-icon"",""productivity"":""neutral"",""sortOrder"":973}", "name is null")]
    [InlineData(@"{""id"":""{ID}"",""name"":"""",""color"":""#0a0b0c"",""icon"":""redr2-icon"",""productivity"":""neutral"",""sortOrder"":973}", "name is empty string")]
    [InlineData(@"{""id"":""{ID}"",""name"":""   "",""color"":""#0a0b0c"",""icon"":""redr2-icon"",""productivity"":""neutral"",""sortOrder"":973}", "name is whitespace only")]
    public async Task PostCategories_UpdatingExistingId_WithInvalidName_ReturnsBadRequest_AndLeavesRowIntact(
        string rawJsonTemplate, string scenario)
    {
        RealDbTestConnection.Require();

        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var client = Authed(factory);

        // ---- 前置：先建两个合法分类（根 + 子），保证后面校验的不是「从不存在的一行」 ----
        var rootResponse = await PostRawAsync(
            client,
            @"{""name"":""RedR2-Original-Root"",""color"":""#111111"",""productivity"":""neutral"",""sortOrder"":972}");
        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);
        var rootId = JsonDocument
            .Parse(await rootResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var childBodyJson =
            "{\"name\":\"" + OriginalChildName + "\",\"parentId\":\"" + rootId + "\",\"color\":\""
            + OriginalChildColor + "\",\"icon\":\"" + OriginalChildIcon + "\",\"productivity\":\""
            + OriginalChildProductivity + "\",\"sortOrder\":" + OriginalChildSortOrder + "}";
        var childResponse = await PostRawAsync(client, childBodyJson);
        Assert.Equal(HttpStatusCode.OK, childResponse.StatusCode);
        var childId = JsonDocument
            .Parse(await childResponse.Content.ReadAsStringAsync())
            .RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var rawJson = rawJsonTemplate.Replace("{ID}", childId.ToString());

        // ---- 阶段 1：只采集证据（更新前整行 + 更新后整行），断言全部留到后面 ----
        var rowBefore = await ReadCategoryRowAsync(temp, childId);
        var countBefore = (await ReadPersistedNamesAsync(temp)).Count;

        var response = await PostRawAsync(client, rawJson);
        var body = await response.Content.ReadAsStringAsync();
        var status = (int)response.StatusCode;

        // 更新后的整行读取同样在状态断言之前完成
        var rowAfter = await ReadCategoryRowAsync(temp, childId);
        var countAfter = (await ReadPersistedNamesAsync(temp)).Count;

        _output.WriteLine($"scenario              : {scenario}");
        _output.WriteLine($"target id             : {childId}");
        _output.WriteLine($"request body          : {rawJson}");
        _output.WriteLine($"HTTP status           : {status}");
        _output.WriteLine($"response body         : {Truncate(body)}");
        _output.WriteLine($"rows before / after   : {countBefore} / {countAfter}");
        _output.WriteLine($"row before            : {Describe(rowBefore)}");
        _output.WriteLine($"row after             : {Describe(rowAfter)}");
        _output.WriteLine($"after-query ran before status assertion: true");

        // ---- 阶段 2：断言 ----
        Assert.NotNull(rowBefore);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        AssertCleanBadRequestEnvelope(body, scenario);

        // 整行逐字段比对：既证明「没被覆盖」，也证明「没被删掉/没换 id」
        Assert.Equal(countBefore, countAfter);
        Assert.NotNull(rowAfter);
        Assert.Equal(rowBefore, rowAfter);
    }

    // ---------------------------------------------------------------------
    // 正对照：合法树形保存必须仍然 200、落库、并能读回
    // ---------------------------------------------------------------------

    /// <summary>
    /// 正对照：合法树形保存（根 + 子）必须 200、落库、并从读接口读回同名字段。
    /// 用来证明校验没有把合法路径一起拒掉。
    /// </summary>
    [SkippableFact]
    public async Task PostCategories_WithValidTreeName_PersistsAndReadsBack()
    {
        RealDbTestConnection.Require();

        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var client = Authed(factory);

        const string rootName = "Validation-Root";
        const string childName = "Validation-Child";

        var rootResponse = await PostRawAsync(
            client,
            @"{""name"":""" + rootName + @""",""color"":""#333333"",""productivity"":""focus"",""sortOrder"":950}");
        Assert.Equal(HttpStatusCode.OK, rootResponse.StatusCode);

        var rootBody = JsonDocument.Parse(await rootResponse.Content.ReadAsStringAsync());
        var rootId = rootBody.RootElement.GetProperty("data").GetProperty("id").GetGuid();

        var childResponse = await PostRawAsync(
            client,
            @"{""name"":""" + childName + @""",""color"":""#444444"",""productivity"":""focus"",""sortOrder"":951,""parentId"":""" + rootId + @"""}");
        Assert.Equal(HttpStatusCode.OK, childResponse.StatusCode);

        // 读回：树形读是 #414 之后唯一的树读入口
        var treeResponse = await client.GetAsync("/api/v1/pc/categories/tree");
        Assert.Equal(HttpStatusCode.OK, treeResponse.StatusCode);
        var tree = await treeResponse.Content.ReadAsStringAsync();
        _output.WriteLine($"tree response (truncated): {Truncate(tree, 1200)}");

        Assert.Contains(rootName, tree, StringComparison.Ordinal);
        Assert.Contains(childName, tree, StringComparison.Ordinal);

        // 落库确认（真库，不是 InMemory）
        var names = await ReadPersistedNamesAsync(temp);
        _output.WriteLine($"persisted names: {string.Join(" | ", names)}");
        Assert.Contains(rootName, names);
        Assert.Contains(childName, names);

        // 父子关系确实建立：子节点的 parentId 指向根
        var childBody = JsonDocument.Parse(await childResponse.Content.ReadAsStringAsync());
        Assert.Equal(
            rootId,
            childBody.RootElement.GetProperty("data").GetProperty("parentId").GetGuid());
    }

    /// <summary>
    /// 正对照的兄弟形态：合法但带前后空白的 Name 应被接受（避免把
    /// 「仅前后空白」误判成「纯空白」而过度拒绝）。
    /// </summary>
    [SkippableFact]
    public async Task PostCategories_WithNameHavingSurroundingWhitespace_IsAccepted()
    {
        RealDbTestConnection.Require();

        var (temp, factory) = await NewIsolatedHostAsync();
        await using var _ = temp;
        using var __ = factory;
        var client = Authed(factory);

        var response = await PostRawAsync(
            client, @"{""name"":""  Padded Name  "",""color"":""#555555"",""sortOrder"":960}");
        var body = await response.Content.ReadAsStringAsync();
        _output.WriteLine($"HTTP {(int)response.StatusCode}: {Truncate(body)}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Padded Name", body, StringComparison.Ordinal);
    }

    private static string Truncate(string text, int max = 400)
        => text.Length <= max ? text : text[..max] + "…(truncated)";
}
