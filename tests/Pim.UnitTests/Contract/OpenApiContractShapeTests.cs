using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Pim.Infrastructure.Data;
using Swashbuckle.AspNetCore.Swagger;
using Xunit;

namespace Pim.UnitTests.Contract;

/// <summary>
/// 契约形状回归（工单《pim-api 契约一致性与校验修正》→ issue #417 / #418 / #419）。
///
/// 契约（<c>contract/openapi.json</c>）由 <c>src/Pim.Api/Program.cs</c> 的 SwaggerGen 配置生成，
/// 这里用**同一套配置**在进程内生成文档并断言形状：CI 的契约硬校验保证「快照 == 生成物」，
/// 本文件保证「生成物的口径」不再退化（readOnly 不进 required、信封有 required、
/// 整数枚举带成员名、import-ics 有 requestBody……）。
/// </summary>
public sealed class OpenApiContractShapeTests
{
    private static readonly Lazy<OpenApiDocument> LazyDocument =
        new(BuildDocument, LazyThreadSafetyMode.ExecutionAndPublication);

    private static OpenApiDocument Document => LazyDocument.Value;

    private static OpenApiDocument BuildDocument()
    {
        // 与 CI 导出契约同源：同一个 Program 装配（含 Program.cs 注册的 SchemaFilter 链）。
        // 只把数据库换成隔离内存库 —— 文档生成不应依赖真库，避免用例在无库环境里假失败。
        var databaseName = $"openapi-shape-{Guid.NewGuid():N}";
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("DisableHangfire", "true").UseSetting("Database:Migrations:FailFast", "false");
            b.UseSetting("GitHub:Repo", "invalid/invalid-test-repo-xyz");
            b.ConfigureServices(services =>
            {
                var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<PimDbContext>));
                if (descriptor is not null) services.Remove(descriptor);
                services.AddDbContext<PimDbContext>(o => o.UseInMemoryDatabase(databaseName));
            });
        });

        return factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
    }

    private static OpenApiSchema Schema(string name)
    {
        Assert.True(Document.Components.Schemas.TryGetValue(name, out var schema), $"契约里没有 schema：{name}");
        return schema!;
    }

    private static ICollection<string> RequiredOf(OpenApiSchema schema)
        => schema.Required ?? new HashSet<string>();

    private static OpenApiSchema Resolve(OpenApiSchema schema)
        => schema.Reference is null ? schema : Schema(schema.Reference.Id);

    // ===== C1：readOnly 的字段不得进 required =====

    [Fact]
    public void NoSchemaDeclaresAPropertyBothReadOnlyAndRequired()
    {
        var offenders = new List<string>();
        foreach (var (schemaName, schema) in Document.Components.Schemas)
        {
            foreach (var (propertyName, property) in schema.Properties ?? new Dictionary<string, OpenApiSchema>())
            {
                if (property.ReadOnly && RequiredOf(schema).Contains(propertyName))
                    offenders.Add($"{schemaName}.{propertyName}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoteAsrBatchMimeContractExportsEveryTypedMultipartField()
    {
        var operation = Document.Paths["/api/v1/native-notes/asr-routing/batches"].Operations[OperationType.Post];
        Assert.NotNull(operation.RequestBody);
        Assert.True(operation.RequestBody!.Content.TryGetValue("multipart/form-data", out var content));
        var schema = Resolve(content!.Schema);
        foreach (var property in new[]
                 {
                     "taskId", "attemptId", "modelId", "consentRef", "batchScopeConfirmed",
                     "completionWindow", "file"
                 })
            Assert.True(schema.Properties.ContainsKey(property), $"Batch multipart 缺字段：{property}");
        Assert.Equal("string", Resolve(schema.Properties["file"]).Type);
        Assert.Equal("binary", Resolve(schema.Properties["file"]).Format);
    }

    [Fact]
    public void NoteAsrSegmentContentUsesHeadersAndDocumentsImmutableIdentityMetadata()
    {
        var operation = Document.Paths[
            "/api/v1/native-notes/asr-routing/leases/{leaseId}/segments/{segmentIndex}/content"]
            .Operations[OperationType.Get];
        var leaseToken = Assert.Single(operation.Parameters.Where(x => x.Name == "X-Pim-Lease-Token"));
        Assert.Equal(ParameterLocation.Header, leaseToken.In);
        Assert.True(leaseToken.Required);
        var fencingToken = Assert.Single(operation.Parameters.Where(x => x.Name == "X-Pim-Fencing-Token"));
        Assert.Equal(ParameterLocation.Header, fencingToken.In);
        Assert.True(fencingToken.Required);
        Assert.DoesNotContain(operation.Parameters,
            x => x.In == ParameterLocation.Query && x.Name is "leaseToken" or "fencingToken");

        var response = operation.Responses["200"];
        Assert.True(response.Content.TryGetValue("audio/wav", out var audio));
        Assert.Equal("string", audio!.Schema.Type);
        Assert.Equal("binary", audio.Schema.Format);
        foreach (var name in new[]
                 {
                     "X-Content-SHA256", "X-Pim-Source-SHA256", "X-Pim-Owner-Id", "X-Pim-Node-Id",
                     "X-Pim-Lease-Id", "X-Pim-Attempt-Id", "X-Pim-Version-Id", "X-Pim-Fencing-Token",
                     "X-Pim-Segment-Index", "X-Pim-Source-File-Index", "X-Pim-Source-Offset-Samples",
                     "X-Pim-Samples"
                 })
            Assert.True(response.Headers.ContainsKey(name), $"segment response header 未导出：{name}");
    }

    [Theory]
    // issue #417 逐条点名的 6 处（服务端都是 C# record 里的只读计算属性）
    [InlineData("MobileDeviceDto", "apiLevel")]
    [InlineData("MobileDeviceRegisterRequest", "apiLevel")]
    [InlineData("MobileLocationPointRequest", "isMock")]
    [InlineData("MobileUsageEventsUploadRequest", "windowStartUtc")]
    [InlineData("MobileUsageEventsUploadRequest", "windowEndUtc")]
    [InlineData("MobileUsageSummaryDto", "totalTimeVisibleMs")]
    public void ReadOnlyComputedPropertyIsNotRequired(string schemaName, string propertyName)
    {
        var schema = Schema(schemaName);
        Assert.True(schema.Properties.ContainsKey(propertyName), $"{schemaName} 上找不到 {propertyName}");
        Assert.True(schema.Properties[propertyName].ReadOnly, $"{schemaName}.{propertyName} 应当是 readOnly");
        Assert.DoesNotContain(propertyName, RequiredOf(schema));
    }

    // ===== C2-2：ApiResponse 信封必须有 required =====

    [Fact]
    public void EveryApiResponseEnvelopeDeclaresItsAlwaysPresentMembers()
    {
        var envelopes = Document.Components.Schemas
            .Where(entry => entry.Key.EndsWith("ApiResponse", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(envelopes);
        foreach (var (name, schema) in envelopes)
        {
            var required = RequiredOf(schema);
            Assert.True(required.Contains("code"), $"{name} 的 required 缺 code");
            Assert.True(required.Contains("message"), $"{name} 的 required 缺 message");
            Assert.True(required.Contains("timestamp"), $"{name} 的 required 缺 timestamp");

            // data 的 required 必须与它自己的可空标记自洽：可空载荷（nullable:true，或
            // $ref 到某个 schema —— OpenAPI 3.0 的 $ref 无法携带兄弟关键字）不得进 required；
            // 值类型载荷（ApiResponse<bool> / ApiResponse<int>，JSON 里永远有值）必须进 required。
            var dataSchema = schema.Properties["data"];
            var nonNullablePayload = dataSchema.Nullable != true && dataSchema.Reference is null;
            Assert.True(
                nonNullablePayload == required.Contains("data"),
                $"{name} 的 data：nullable={dataSchema.Nullable} ref={dataSchema.Reference?.Id} 与 required={required.Contains("data")} 不自洽");
        }
    }

    // ===== C2-3：整数枚举必须带成员名（且线上格式仍是整数）=====

    [Fact]
    public void EveryIntegerEnumCarriesMemberNames()
    {
        var integerEnums = Document.Components.Schemas
            .Where(entry => entry.Value.Type == "integer" && entry.Value.Enum?.Count > 0)
            .ToList();

        Assert.NotEmpty(integerEnums);
        foreach (var (name, schema) in integerEnums)
        {
            var names = EnumVarNames(schema);
            Assert.True(names is not null, $"{name} 没有成员名（x-enum-varnames）");
            Assert.Equal(schema.Enum.Count, names!.Count);
            Assert.All(names, value => Assert.False(string.IsNullOrWhiteSpace(value)));
        }
    }

    [Fact]
    public void PimHealthStatusKeepsIntegerWireFormatAndGainsNames()
    {
        var schema = Schema("PimHealthStatus");
        Assert.Equal("integer", schema.Type);
        Assert.Equal(
            new[] { "Unknown", "Healthy", "Warning", "Critical" },
            EnumVarNames(schema));
    }

    // ===== C2-1：import-ics 有 requestBody =====

    [Fact]
    public void ImportIcsDeclaresItsMultipartRequestBody()
    {
        var operation = Document.Paths["/api/v1/calendar/import-ics"].Operations[OperationType.Post];

        Assert.NotNull(operation.RequestBody);
        Assert.True(
            operation.RequestBody!.Content.ContainsKey("multipart/form-data"),
            "import-ics 的 requestBody 必须是 multipart/form-data");
        Assert.True(operation.RequestBody.Required, "import-ics 的 requestBody 应当是必填的");

        var schema = Resolve(operation.RequestBody.Content["multipart/form-data"].Schema);
        Assert.True(schema.Properties.ContainsKey("file"), "import-ics 的 requestBody 必须声明 file 字段");
        Assert.Equal("binary", schema.Properties["file"].Format);
        Assert.Contains("file", RequiredOf(schema));
    }

    // ===== A2：两个 keystats 端点字段名一致（小写 peakKps / peakCps）=====

    [Fact]
    public void KeystatsPeakFieldNamesAreUnifiedAndCamelCase()
    {
        foreach (var schemaName in new[] { "KeystatsUploadRequest", "KeystatsSampleUploadRequest" })
        {
            var schema = Schema(schemaName);
            Assert.True(schema.Properties.ContainsKey("peakKps"), $"{schemaName} 应当用 peakKps");
            Assert.True(schema.Properties.ContainsKey("peakCps"), $"{schemaName} 应当用 peakCps");
            Assert.False(schema.Properties.ContainsKey("peakKPS"), $"{schemaName} 不应再有 peakKPS");
            Assert.False(schema.Properties.ContainsKey("peakCPS"), $"{schemaName} 不应再有 peakCPS");
        }
    }

    // ===== A1：校验清单必须与契约的 required 逐字一致（防止契约说必填、服务端不查）=====

    [Fact]
    public void KeystatsUploadRequiredListMatchesTheValidator()
    {
        Assert.Equal(
            RequiredOf(Schema("KeystatsUploadRequest")).OrderBy(x => x, StringComparer.Ordinal),
            Pim.Module.PcTracker.DTOs.KeystatsRequiredFields.Upload.OrderBy(x => x, StringComparer.Ordinal));
    }

    [Fact]
    public void KeystatsSampleRequiredListMatchesTheValidator()
    {
        Assert.Equal(
            RequiredOf(Schema("KeystatsSampleUploadRequest")).OrderBy(x => x, StringComparer.Ordinal),
            Pim.Module.PcTracker.DTOs.KeystatsRequiredFields.Sample.OrderBy(x => x, StringComparer.Ordinal));
    }

    // ===== A1：请求体文档与请求 DTO 仍然对得上（改用 Accepts 后不得丢 requestBody）=====

    [Theory]
    [InlineData("/api/v1/pc/keystats/upload", "KeystatsUploadRequest")]
    [InlineData("/api/v1/pc/keystats/samples", "KeystatsSampleUploadRequest")]
    public void KeystatsOperationsStillDeclareTheirJsonRequestBody(string path, string schemaName)
    {
        var operation = Document.Paths[path].Operations[OperationType.Post];

        Assert.NotNull(operation.RequestBody);
        Assert.True(operation.RequestBody!.Required);
        Assert.True(operation.RequestBody.Content.ContainsKey("application/json"));
        Assert.Equal(
            $"#/components/schemas/{schemaName}",
            operation.RequestBody.Content["application/json"].Schema.Reference?.ReferenceV3);
    }

    // ===== C4：任务状态 enum（工单 03 D1.2 / AC-2）=====

    private static readonly string[] ExpectedTaskStatuses =
        ["NEEDS-ACTION", "IN-PROCESS", "COMPLETED", "CANCELLED"];

    [Fact]
    public void TaskUpdateSchemaDistinguishesOmissionFromNull()
    {
        var schema = Schema("UpdateTaskRequest");
        Assert.Empty(RequiredOf(schema));
        Assert.DoesNotContain("providedFields", schema.Properties.Keys);
        Assert.Contains("omitted", schema.Description);
        foreach (var name in new[] { "title", "priority", "percentComplete" })
            Assert.False(schema.Properties[name].Nullable);
        foreach (var name in new[] { "description", "calendarId", "taskBookId", "due", "dtStart", "plannedEnd", "estimatedDuration", "minimumSegment" })
            Assert.True(schema.Properties[name].Nullable);
    }

    /// <summary>
    /// 请求侧：三个任务状态写入口（新建 / 更新 / 批量更新）都必须标出 enum，
    /// 且与服务端真实白名单（<c>TaskStatuses.All</c>）逐字一致。
    /// </summary>
    [Theory]
    [InlineData("CreateTaskRequest")]
    [InlineData("UpdateTaskRequest")]
    [InlineData("BatchTaskUpdateRequest")]
    public void TaskStatusRequestSchemasDeclareTheEnum(string schemaName)
    {
        var status = Schema(schemaName).Properties["status"];

        Assert.Null(status.Reference);
        Assert.Equal("string", status.Type);
        Assert.Equal(
            ExpectedTaskStatuses,
            status.Enum.OfType<OpenApiString>().Select(v => v.Value));
    }

    /// <summary>
    /// 响应侧：<c>TaskResponse.status</c> **故意不声明闭合 enum**（与维护者确认的口径）。
    /// 请求侧的四值是真校验；响应侧的值来自库里既有数据，而工单 D1.3 不迁移收紧前的历史值，
    /// 标闭合 enum 就等于替历史行做契约上不成立的承诺（严格生成的客户端会反序列化失败）。
    /// 取而代之：把「当前写入侧只产生哪四个值」与「历史行可能是别的字符串」写进 description。
    /// </summary>
    [Fact]
    public void TaskResponseStatusIsDocumentedInsteadOfClosedEnum()
    {
        var status = Schema("TaskResponse").Properties["status"];

        Assert.Equal("string", status.Type);
        // Swashbuckle 把未标注的 enum 初始化成空列表，而不是 null
        Assert.True(status.Enum is null || status.Enum.Count == 0, $"响应侧不应声明闭合 enum，实际：{string.Join(",", status.Enum ?? [])}");
        Assert.False(string.IsNullOrWhiteSpace(status.Description));
        foreach (var value in ExpectedTaskStatuses)
            Assert.Contains(value, status.Description);
        Assert.Contains("历史", status.Description);
    }

    /// <summary>累计时长口径（含「不代表任务成功完成」）必须出现在契约里，供客户端与报告侧阅读。</summary>
    [Fact]
    public void TaskTimerStateDocumentsTheAccumulatedDurationRule()
    {
        var schema = Schema("TaskTimerStateResponse");
        Assert.Contains("accumulatedSeconds", RequiredOf(schema));
        var description = schema.Properties["accumulatedSeconds"].Description;
        Assert.False(string.IsNullOrWhiteSpace(description));
        Assert.Contains("planned", description);
        Assert.Contains("不代表任务成功完成", description);
    }

    /// <summary>请求侧 enum 必须与运行时白名单同源，防止「契约写了、校验没写」或反过来。</summary>
    [Fact]
    public void TaskStatusEnumMatchesTheServerSideWhitelist()
        => Assert.Equal(Pim.Module.Calendar.Entities.TaskStatuses.All, ExpectedTaskStatuses);

    // ===== C3：displayName 契约改成可空 =====

    [Fact]
    public void UserInfoDisplayNameIsOptionalAndNullable()
    {
        var schema = Schema("UserInfo");
        Assert.DoesNotContain("displayName", RequiredOf(schema));
        Assert.True(schema.Properties["displayName"].Nullable, "displayName 必须允许 null");
        // 其余字段仍然必填，不能顺手改松。
        Assert.Contains("id", RequiredOf(schema));
        Assert.Contains("username", RequiredOf(schema));
        Assert.Contains("role", RequiredOf(schema));
    }

    // ===== B：REST 资源完整 =====

    [Fact]
    public void FlatAppCategoryRuleResourceSupportsDelete()
    {
        Assert.True(Document.Paths.ContainsKey("/api/v1/pc/app-category-rules"));
        Assert.True(
            Document.Paths["/api/v1/pc/app-category-rules"].Operations.ContainsKey(OperationType.Post),
            "扁平规则资源应当有 POST");

        var deletePath = Document.Paths
            .FirstOrDefault(entry => entry.Key.StartsWith("/api/v1/pc/app-category-rules/", StringComparison.Ordinal));
        Assert.False(string.IsNullOrEmpty(deletePath.Key), "扁平规则资源缺少 DELETE 子路径");
        Assert.True(deletePath.Value.Operations.ContainsKey(OperationType.Delete));
    }

    [Fact]
    public void TreeCategoryResourceStillSupportsDelete()
    {
        var operation = Document.Paths["/api/v1/pc/categories/{id}"].Operations;
        Assert.True(operation.ContainsKey(OperationType.Delete), "树形分类删除入口不能被删掉");
    }

    private static IList<string>? EnumVarNames(OpenApiSchema schema)
    {
        if (!schema.Extensions.TryGetValue("x-enum-varnames", out var raw) || raw is not OpenApiArray array)
            return null;

        return array.OfType<OpenApiString>().Select(value => value.Value).ToList();
    }

    // ===== issue #539：mobile usage/events 处理响应协议 =====

    private const string MobileUsageEventsPath = "/api/v1/mobile/usage/events";
    private static readonly string[] ProcessingProtocolFields =
        ["processingState", "retryAfterSeconds", "leaseUntilUtc", "retryAtUtc"];

    [Fact]
    public void MobileUsageIngestResultDeclaresExactlyTheFourOptionalProcessingFields()
    {
        var schema = Schema("MobileUsageIngestResult");
        foreach (var field in ProcessingProtocolFields)
        {
            Assert.True(schema.Properties.ContainsKey(field), $"{field} 必须在契约里");
            Assert.DoesNotContain(field, RequiredOf(schema));
            Assert.True(
                schema.Properties[field].Nullable == true,
                $"{field} 必须 optional/nullable（旧客户端持久化 JSON 不能因 required 退化）");
        }
        // 终态 200 里绝不出现这些字段；契约侧保持它们非 required 即可表达。
        Assert.Equal(4, schema.Properties.Keys.Count(ProcessingProtocolFields.Contains));
    }

    [Fact]
    public void MobileProcessingStateIsConstrainedToTheTwoWireValues()
    {
        var state = Schema("MobileUsageIngestResult").Properties["processingState"];
        Assert.Equal("string", state.Type);
        var values = state.Enum?.OfType<OpenApiString>().Select(value => value.Value).ToList();
        Assert.Equal(["in-flight", "retry-scheduled"], values);
    }

    [Fact]
    public void MobileUsageEventsUploadDeclares202WithMatchingRetryAfterHeader()
    {
        var operation = Document.Paths[MobileUsageEventsPath].Operations[OperationType.Post];
        Assert.True(operation.Responses.ContainsKey("200"), "终态回放仍是 200");
        Assert.True(operation.Responses.ContainsKey("202"), "处理响应必须是 202");

        var accepted = operation.Responses["202"];
        Assert.True(
            accepted.Headers.TryGetValue("Retry-After", out var retryAfter),
            "202 必须声明 Retry-After 头");
        Assert.Equal("integer", retryAfter!.Schema.Type);
        Assert.Equal(1m, retryAfter.Schema.Minimum!.Value);
    }
}
