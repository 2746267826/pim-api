using System.Net;
using System.Net.Http.Headers;
using System.Text.Encodings.Web;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Pim.Core.Ai;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Ai.Asr.Mimo;

/// <summary>
/// Synchronous MiMo multimodal transcription. It is a separate preset from the existing
/// <c>mimo-asr</c> preset; registering it is the integration owner's responsibility.
/// </summary>
public sealed class MimoMultimodalPreset(Func<HttpMessageHandler?, HttpClient> invoker) : IAiAsrPreset
{
    public const string PresetId = "mimo-multimodal";
    public const string FlashModel = "mimo-v2.6-flash";
    public const string ProModel = "mimo-v2.6-pro";
    public const int MaxCompletionTokens = 8192;
    private const int MaxResponseBytes = 4 * 1024 * 1024;
    // Base64 uses '+'. The default STJ encoder escapes it as \u002B, inflating large audio
    // requests by five bytes per plus even though the JSON value is unchanged. Keep the wire
    // representation at the finite Base64 budget while still emitting valid JSON.
    private static readonly JsonSerializerOptions WireJson = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public AiAsrPresetDto Descriptor { get; } = new(PresetId, "1", "MiMo 多模态",
        "https://api.xiaomimimo.com/v1/", "chat/completions", FlashModel,
        Array.AsReadOnly(new[] { FlashModel, ProModel }),
        MimoAudioFormats.Supported,
        Array.AsReadOnly(new[] { "auto", "zh", "en" }), MimoAudioFormats.MaxDecodedBytes, false);

    /// <summary>Flash is the product default; Pro is available but not recommended for long audio.</summary>
    public static bool IsRecommendedForLongAudio(string model) =>
        string.Equals(model, FlashModel, StringComparison.Ordinal);

    public string NormalizeEndpoint(string value)
    {
        var endpoint = AiModelSettingsService.Endpoint(value);
        var uri = new Uri(endpoint);
        var suffix = "/" + Descriptor.OperationPath + "/";
        return uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal)
            ? new UriBuilder(uri) { Path = uri.AbsolutePath[..^suffix.Length] + "/" }.Uri.AbsoluteUri
            : endpoint;
    }

    public void ValidateModel(string model)
    {
        if (!Descriptor.Models.Contains(model, StringComparer.Ordinal))
            throw new DomainException(40001, "MiMo多模态预设只允许其已声明的小写模型");
    }

    public string AuditRequest(byte[] sdkBody, AiModelConnection connection, AiAsrInput audio, Guid audioId,
        string language, AiAsrPromptSnapshot? prompt = null)
    {
        var root = JsonNode.Parse(sdkBody)!;
        var messages = root["messages"]?.AsArray();
        if (root["model"]?.GetValue<string>() != connection.Model || messages?.Count != 1 ||
            messages[0]?["role"]?.GetValue<string>() != "user")
            throw Mismatch();
        var content = messages[0]?["content"]?.AsArray();
        var audioPart = content?.FirstOrDefault(part => part?["type"]?.GetValue<string>() == "input_audio");
        var textPart = content?.FirstOrDefault(part => part?["type"]?.GetValue<string>() == "text");
        if (audioPart is null || textPart is null) throw Mismatch();
        var input = audioPart["input_audio"]!;
        var data = input["data"]?.GetValue<string>();
        if (!MimoAudioFormats.MatchesDataUrl(audio.Format, data, audio.Bytes)) throw Mismatch();
        if (input["format"] is not null && input["format"]!.GetValue<string>() != audio.Format) throw Mismatch();
        if (textPart["text"] is not JsonValue textValue ||
            !textValue.TryGetValue<string>(out var text) ||
            prompt is not null && text != prompt.Text ||
            prompt is null && !text.Contains($"language={language}", StringComparison.Ordinal))
            throw Mismatch();
        input["data"] = $"pim-ai-audio:{audioId:D}";
        return root.ToJsonString();
    }

    public async Task<AiAsrProviderResult> TranscribeAsync(AiModelConnection connection, AiAsrInput audio,
        string language, CancellationToken ct, AiAsrPromptSnapshot? prompt = null)
    {
        ValidateModel(connection.Model);
        if (!Descriptor.Formats.Contains(audio.Format) || !Descriptor.Languages.Contains(language) ||
            audio.Bytes.Length > Descriptor.MaxAudioBytes)
            throw new DomainException(40001, "音频格式、语言或大小不符合MiMo多模态预设");

        var endpoint = new Uri(new Uri(connection.Endpoint), Descriptor.OperationPath).AbsoluteUri;
        var payload = BuildRequest(connection.Model, audio, prompt ?? AiAsrPromptTemplate.Normalize().Snapshot(language));
        using var ownedHandler = new SocketsHttpHandler { AllowAutoRedirect = false };
        using var http = invoker(ownedHandler);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", connection.ApiKey);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(connection.TimeoutSeconds));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if ((int)response.StatusCode is >= 300 and <= 399)
                throw new MimoBatchException(50204, "MiMo多模态服务返回重定向，已拒绝跟随，避免凭据泄漏");
            if (!response.IsSuccessStatusCode)
                throw new MimoBatchException(50201,
                    $"MiMo多模态返回HTTP {(int)response.StatusCode}，本次未自动重试");
            var raw = await ReadBoundedAsync(response, timeout.Token);
            return MimoMultimodalResponseParser.Parse(raw, audio, connection.Model);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DomainException(50401, "MiMo多模态请求超时，结果未确认，本次未自动重试");
        }
        catch (HttpRequestException)
        {
            throw new DomainException(50206, "MiMo多模态连接异常，结果未确认，本次未自动重试");
        }
    }

    private static string BuildRequest(string model, AiAsrInput audio, AiAsrPromptSnapshot prompt)
    {
        var data = MimoAudioFormats.DataUrl(audio.Format, audio.Bytes);
        return JsonSerializer.Serialize(new
        {
            model,
            max_completion_tokens = MaxCompletionTokens,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_audio", input_audio = new { data, format = audio.Format } },
                        new { type = "text", text = prompt.Text }
                    }
                }
            },
            stream = false
        }, WireJson);
    }

    private static async Task<string> ReadBoundedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        long total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, ct);
            if (read <= 0) break;
            total += read;
            if (total > MaxResponseBytes)
                throw new MimoBatchException(50203, "MiMo多模态响应超过4MiB，未保存为完整结果");
            buffer.Write(chunk, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static InvalidDataException Mismatch() =>
        new("SDK音频、模型或语言与固定请求不一致，本次未发送");
}

public static class MimoAudioFormats
{
    public const int MaxDecodedBytes = 50 * 1024 * 1024;
    public static readonly IReadOnlyList<string> Supported =
        Array.AsReadOnly(new[] { "wav", "mp3", "flac", "m4a", "ogg" });

    public static string MimeType(string format) => format switch
    {
        "wav" => "audio/wav",
        "mp3" => "audio/mpeg",
        "flac" => "audio/flac",
        "m4a" => "audio/mp4",
        "ogg" => "audio/ogg",
        _ => throw new DomainException(40001, "不支持的MiMo音频格式")
    };

    public static string DataUrl(string format, byte[] bytes) =>
        $"data:{MimeType(format)};base64,{Convert.ToBase64String(bytes)}";

    public static bool MatchesDataUrl(string format, string? data, byte[] bytes)
    {
        var base64 = Convert.ToBase64String(bytes);
        return data == base64 || data == DataUrl(format, bytes) ||
               format == "mp3" && data == $"data:audio/mp3;base64,{base64}";
    }
}
