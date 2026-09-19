using Microsoft.Extensions.Configuration;

namespace Pim.Module.Files.Services;

/// <summary>
/// 敏感路径保护：命中规则的文件不允许产生外链 / 预览 / 文本读写（设计文档 §7/§13）。
/// 规则来自配置 Files:SensitivePathPatterns（默认 /Secrets/*、/Passwords/*），段边界匹配、大小写不敏感。
/// </summary>
public sealed class SensitivePathPolicy
{
    public const string ConfigSection = "Files:SensitivePathPatterns";

    private static readonly string[] DefaultPatterns = ["/Secrets/*", "/Passwords/*"];

    private readonly string[] _patterns;

    public SensitivePathPolicy(IConfiguration? configuration)
    {
        var configured = configuration?.GetSection(ConfigSection).Get<string[]>();
        _patterns = configured is { Length: > 0 } ? configured : DefaultPatterns;
    }

    public bool IsProtected(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return false;
        }

        var normalized = path.TrimEnd('/');
        foreach (var pattern in _patterns)
        {
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            var baseDirectory = pattern.TrimEnd('*').TrimEnd('/');
            if (baseDirectory.Length == 0)
            {
                continue;
            }

            if (normalized.Equals(baseDirectory, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(baseDirectory + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
