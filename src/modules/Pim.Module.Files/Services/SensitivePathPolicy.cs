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
    private readonly string[] _protectedDirectories;

    public SensitivePathPolicy(IConfiguration? configuration)
    {
        var configured = configuration?.GetSection(ConfigSection).Get<string[]>();
        _patterns = configured is { Length: > 0 } ? configured : DefaultPatterns;
        _protectedDirectories = _patterns
            .Where(pattern => !string.IsNullOrWhiteSpace(pattern))
            .Select(pattern => pattern.TrimEnd('*').TrimEnd('/'))
            .Where(directory => directory.Length > 0)
            // 与 IsProtected 的段边界语义一致：只比较目录，忽略大小写
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// 受保护的**基目录**（已去掉通配符与尾部斜杠），供数据库侧排除使用。
    ///
    /// 结果搜索要「敏感项既不进结果、也不进总数」，就必须把排除下推到 SQL；把解析出来的
    /// 基目录暴露给查询层，避免查询层自己再解析一遍配置而与 <see cref="IsProtected"/> 漂移。
    /// </summary>
    public IReadOnlyList<string> ProtectedDirectories => _protectedDirectories;

    public bool IsProtected(string? path)
    {
        if (string.IsNullOrEmpty(path) || path == "/")
        {
            return false;
        }

        var normalized = path.TrimEnd('/');
        foreach (var baseDirectory in _protectedDirectories)
        {
            if (normalized.Equals(baseDirectory, StringComparison.OrdinalIgnoreCase)
                || normalized.StartsWith(baseDirectory + "/", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
