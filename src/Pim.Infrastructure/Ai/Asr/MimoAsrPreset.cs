using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using ChatMessage = Microsoft.Extensions.AI.ChatMessage;

namespace Pim.Infrastructure.Ai.Asr;

public sealed class MimoAsrPreset(IAiProfileChatClientFactory clients) : IAiAsrPreset
{
    public AiAsrPresetDto Descriptor { get; } = new("mimo-asr", "1", "MiMo 语音识别", "https://api.xiaomimimo.com/v1/",
        "chat/completions", "mimo-v2.5-asr", Array.AsReadOnly(new[] { "mimo-v2.5-asr" }),
        Array.AsReadOnly(new[] { "wav", "mp3" }), Array.AsReadOnly(new[] { "auto", "zh", "en" }), 4 * 1024 * 1024, false);

    public string NormalizeEndpoint(string value)
    {
        var endpoint = AiModelSettingsService.Endpoint(value);
        var uri = new Uri(endpoint); var suffix = "/" + Descriptor.OperationPath + "/";
        if (!uri.AbsolutePath.EndsWith(suffix, StringComparison.Ordinal)) return endpoint;
        return new UriBuilder(uri) { Path = uri.AbsolutePath[..^suffix.Length] + "/" }.Uri.AbsoluteUri;
    }
    public void ValidateModel(string model)
    {
        if (!Descriptor.Models.Contains(model, StringComparer.Ordinal))
            throw new DomainException(40001, "MiMo ASR预设只允许其已声明的语音识别模型");
    }
    public string AuditRequest(byte[] sdkBody, AiModelConnection connection, AiAsrInput audio, Guid audioId,
        string language, AiAsrPromptSnapshot? prompt = null)
    {
        var root = JsonNode.Parse(sdkBody)!; var messages = root["messages"]!.AsArray();
        if (root["model"]?.GetValue<string>() != connection.Model || messages.Count != 1 || messages[0]?["role"]?.GetValue<string>() != "user") throw Mismatch();
        var content = messages[0]?["content"] as JsonArray;
        if (content?.Count != 1 || content[0]?["type"]?.GetValue<string>() != "input_audio") throw Mismatch();
        var input = content[0]?["input_audio"]!;
        var data = input["data"]?.GetValue<string>(); var expected = Convert.ToBase64String(audio.Bytes);
        var isDataUrl = data == $"data:{audio.ContentType};base64,{expected}" || audio.Format == "mp3" && data == $"data:audio/mp3;base64,{expected}";
        if (data != expected && !isDataUrl || input["format"] is not null && input["format"]!.GetValue<string>() != audio.Format ||
            !isDataUrl && input["format"] is null || root["asr_options"]?["language"]?.GetValue<string>() != language) throw Mismatch();
        input["data"] = $"pim-ai-audio:{audioId:D}";
        return root.ToJsonString();
    }
    private static InvalidDataException Mismatch() => new("SDK音频、模型或语言与固定请求不一致，本次未发送");
    public async Task<AiAsrProviderResult> TranscribeAsync(AiModelConnection connection, AiAsrInput audio,
        string language, CancellationToken ct, AiAsrPromptSnapshot? prompt = null)
    {
        ValidateModel(connection.Model);
        if (!Descriptor.Formats.Contains(audio.Format) || !Descriptor.Languages.Contains(language) || audio.Bytes.Length > Descriptor.MaxAudioBytes)
            throw new DomainException(40001, "音频格式、语言或大小不符合ASR预设");
        using var client = clients.Create(connection);
        var options = new ChatOptions { RawRepresentationFactory = _ => {
            var native = new ChatCompletionOptions();
            // The SDK's vendor-extension surface is experimental; isolate the opt-in and wire-test it here.
#pragma warning disable SCME0001
            native.Patch.Set("$.asr_options.language"u8, language);
#pragma warning restore SCME0001
            return native;
        } };
        // Genuine audio-only SDK content: no text/system prompt and no generic text output-token override.
        var result = await client.Client.GetResponseAsync([new ChatMessage(ChatRole.User,
            [new DataContent(audio.Bytes, audio.ContentType)])], options, ct);
        var raw = AiTextWireCapture.Current?.ResponseBody ?? (result.RawRepresentation is ChatCompletion completion
            ? ModelReaderWriter.Write(completion).ToString() : JsonSerializer.Serialize(new { result.Text, result.FinishReason, result.Usage }));
        var present = false; double? seconds = null;
        using (var document = JsonDocument.Parse(raw))
        {
            var root = document.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("message", out var message) && message.TryGetProperty("content", out var text))
                present = text.ValueKind == JsonValueKind.String;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object &&
                usage.TryGetProperty("seconds", out var duration) && duration.ValueKind == JsonValueKind.Number && duration.TryGetDouble(out var value) && double.IsFinite(value) && value >= 0)
                seconds = value;
        }
        return new(result.Text, result.FinishReason?.Value, result.Usage?.InputTokenCount, result.Usage?.OutputTokenCount,
            result.Usage?.TotalTokenCount, raw, present, seconds);
    }
}
