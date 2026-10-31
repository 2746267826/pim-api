using System.Security.Cryptography;
using System.Text;
using Pim.Core.Ai;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Ai.Asr;

/// <summary>
/// Immutable prompt revision shared by synchronous and Batch ASR. Settings may replace the
/// transcription instructions, while language formatting remains deterministic.
/// </summary>
public sealed record AiAsrPromptTemplate(string Text, string Sha256, string Revision)
{
    public const int MaxInputLength = 16_000;
    public const int MaxCustomLength = 4_000;
    public const string DefaultText =
        "请逐字转写这段音频，不要分析、解释或总结。输出格式：起始秒-结束秒 | 说话人: 文本。";

    private static readonly IReadOnlyDictionary<string, string> Languages =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auto"] = "自动识别音频语言；language=auto。",
            ["zh"] = "转写语言为中文；language=zh。",
            ["en"] = "转写语言为英文；language=en。"
        };

    public static IReadOnlyList<string> SupportedLanguages { get; } =
        Array.AsReadOnly(new[] { "auto", "zh", "en" });

    public static AiAsrPromptTemplate Normalize(string? customPrompt = null)
    {
        var text = NormalizeText(customPrompt);
        var sha256 = Hash(text);
        return new AiAsrPromptTemplate(text, sha256, RevisionFor(sha256));
    }

    public AiAsrPromptSnapshot Snapshot(string language)
    {
        var code = language?.Trim().ToLowerInvariant();
        if (code is null || !Languages.TryGetValue(code, out var languageInstruction))
            throw new DomainException(40001, "ASR提示词语言仅支持auto、zh或en");
        var rendered = $"{Text}\n{languageInstruction}";
        var renderedSha256 = Hash(rendered);
        return new AiAsrPromptSnapshot(rendered, code, renderedSha256, RevisionFor(renderedSha256),
            Sha256, Revision);
    }

    public override string ToString() =>
        $"AiAsrPromptTemplate {{ Revision = {Revision}, Sha256 = {Sha256}, Chars = {Text.Length} }}";

    private static string NormalizeText(string? value)
    {
        // Bound raw input before Replace/StringBuilder can amplify a hostile payload.
        if (value is null) return DefaultText;
        if (value.Length > MaxInputLength)
            throw new DomainException(40001, "ASR提示词原始输入不能超过16000个字符");
        if (string.IsNullOrWhiteSpace(value)) return DefaultText;
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
        var cleaned = new StringBuilder(normalized.Length);
        var pendingSpace = false;
        foreach (var character in normalized)
        {
            if (character == '\n')
            {
                if (pendingSpace && cleaned.Length > 0 && cleaned[^1] != '\n') cleaned.Append(' ');
                cleaned.Append('\n');
                pendingSpace = false;
            }
            else if (char.IsControl(character) || char.IsWhiteSpace(character))
            {
                pendingSpace = cleaned.Length > 0;
            }
            else
            {
                if (pendingSpace && cleaned.Length > 0 && cleaned[^1] != '\n') cleaned.Append(' ');
                cleaned.Append(character);
                pendingSpace = false;
            }
        }

        var lines = cleaned.ToString().Split('\n');
        var output = new List<string>(lines.Length);
        var previousBlank = false;
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            var blank = line.Length == 0;
            if (blank && (output.Count == 0 || previousBlank)) continue;
            output.Add(line);
            previousBlank = blank;
        }
        while (output.Count > 0 && output[^1].Length == 0) output.RemoveAt(output.Count - 1);
        var result = string.Join('\n', output);
        if (result.Length == 0) return DefaultText;
        if (result.Length > MaxCustomLength)
            throw new DomainException(40001, "ASR自定义提示词不能超过4000个字符");
        return result;
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string RevisionFor(string sha256) => $"asr-prompt-v1:{sha256[..16]}";
}

