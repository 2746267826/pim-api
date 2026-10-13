using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using Pim.Module.PcTracker.DTOs;

namespace Pim.Module.PcTracker.Endpoints;

/// <summary>
/// issue #419：keystats 上传端点的「契约必填 → 真校验」读取器。
///
/// 为什么自己读请求体，而不是继续用 <c>[FromBody]</c> 绑定：
/// <list type="bullet">
/// <item>契约里必填字段大多是**值类型**（int/double）。绑定进 record 之后，
/// 「请求体没有这个键」与「显式传了 0」完全不可区分 —— 缺字段会被静默存 0；</item>
/// <item>.NET 8 的隐式非空校验只覆盖非可空引用类型，实测缺 <c>deviceId</c>/<c>date</c>
/// 时是一条 500（<c>DateTimeOffset.Parse(null)</c> / 空设备号），而不是契约要求的 400。</item>
/// </list>
///
/// 因此这里按契约的必填清单（<see cref="KeystatsRequiredFields"/>）在 JSON 对象上做
/// **键存在性**检查（大小写不敏感，与 <c>JsonSerializerDefaults.Web</c> 的
/// <c>PropertyNameCaseInsensitive = true</c> 一致），再用应用自己配置的
/// <see cref="JsonOptions.SerializerOptions"/> 反序列化 —— 与框架绑定同一套选项，
/// 换解析器不会改变行为。
///
/// 注册在 <c>PcTrackerModule.RegisterServices</c>：路由用 <c>[FromServices]</c> 拿它，
/// <c>PcTrackerModuleDiResolutionTests</c> 会守住这条注册。
/// </summary>
public sealed class KeystatsUploadReader(IOptions<JsonOptions> jsonOptions)
{
    /// <summary>读取 <c>POST /api/v1/pc/keystats/upload</c>；失败时返回可读中文原因。</summary>
    public Task<(KeystatsUploadRequest? Request, string? Error)> ReadUploadAsync(
        HttpRequest request, CancellationToken ct)
        => ReadAsync<KeystatsUploadRequest>(request, KeystatsRequiredFields.Upload, ct);

    /// <summary>读取 <c>POST /api/v1/pc/keystats/samples</c>；失败时返回可读中文原因。</summary>
    public Task<(KeystatsSampleUploadRequest? Request, string? Error)> ReadSampleAsync(
        HttpRequest request, CancellationToken ct)
        => ReadAsync<KeystatsSampleUploadRequest>(request, KeystatsRequiredFields.Sample, ct);

    private async Task<(T? Request, string? Error)> ReadAsync<T>(
        HttpRequest request, string[] requiredFields, CancellationToken ct)
        where T : class
    {
        string body;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8))
        {
            body = await reader.ReadToEndAsync(ct);
        }

        if (string.IsNullOrWhiteSpace(body))
            return (null, "请求体不能为空");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException ex)
        {
            return (null, $"请求体不是合法 JSON：{ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return (null, "请求体必须是 JSON 对象");

            var missing = requiredFields.Where(field => !HasValue(root, field)).ToArray();
            if (missing.Length > 0)
                return (null, $"缺少必填字段：{string.Join("、", missing)}");

            try
            {
                var bound = root.Deserialize<T>(jsonOptions.Value.SerializerOptions);
                return bound is null
                    ? (null, "请求体不能为空")
                    : (bound, null);
            }
            catch (JsonException ex)
            {
                return (null, $"请求体字段类型不正确：{ex.Message}");
            }
        }
    }

    private static bool HasValue(JsonElement root, string field)
    {
        foreach (var property in root.EnumerateObject())
        {
            // 与 PropertyNameCaseInsensitive 一致：peakKps / peakKPS / PeakKps 都算同一个字段。
            if (!string.Equals(property.Name, field, StringComparison.OrdinalIgnoreCase))
                continue;

            // 显式 null 视为缺值：值类型字段本来也反序列化不了 null，
            // 与其抛 JsonException，不如报「缺了哪个字段」。
            return property.Value.ValueKind != JsonValueKind.Null;
        }

        return false;
    }
}
