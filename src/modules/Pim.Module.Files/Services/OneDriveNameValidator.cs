namespace Pim.Module.Files.Services;

/// <summary>
/// OneDrive 条目命名的合法性校验（REQ-15 / AC-15.2）。
///
/// 规则来自 OneDrive 平台限制：不得为空、不得含 <c>\ / : * ? " &lt; &gt; |</c>、
/// 不得以点或空格结尾、不得是保留名。这里集中一处，供新建文件夹、改名、上传三条路径复用——
/// 分散校验必然出现「改名拦住了、上传没拦住」这类漏洞。
/// </summary>
public static class OneDriveNameValidator
{
    /// <summary>非法字符（含 Windows 保留字符与路径分隔符）。</summary>
    private static readonly char[] InvalidCharacters = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>OneDrive/SharePoint 保留名（大小写不敏感）。</summary>
    private static readonly string[] ReservedNames =
    [
        ".lock", "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
        "_vti_", "desktop.ini",
    ];

    /// <summary>
    /// 校验单个条目名。非法时抛 <see cref="Pim.Core.Exceptions.DomainException"/>（5308），
    /// 错误信息必须**可读**并指明具体原因（AC-15.2：给可读错误并阻止提交）。
    /// </summary>
    public static void EnsureValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new Pim.Core.Exceptions.DomainException(5308, "名称不能为空");
        }

        var trimmed = name.Trim();
        if (trimmed.Length > 255)
        {
            throw new Pim.Core.Exceptions.DomainException(5308, "名称过长（最多 255 个字符）");
        }

        if (trimmed.IndexOfAny(InvalidCharacters) >= 0)
        {
            throw new Pim.Core.Exceptions.DomainException(
                5308,
                "名称不能包含下列任一字符：\\ / : * ? \" < > |");
        }

        if (trimmed.EndsWith('.') || trimmed.EndsWith(' '))
        {
            throw new Pim.Core.Exceptions.DomainException(5308, "名称不能以句点或空格结尾");
        }

        var withoutExtension = Path.GetFileNameWithoutExtension(trimmed);
        if (ReservedNames.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            || ReservedNames.Contains(withoutExtension, StringComparer.OrdinalIgnoreCase))
        {
            throw new Pim.Core.Exceptions.DomainException(5308, $"「{trimmed}」是 OneDrive 保留名称，请换一个");
        }
    }

    /// <summary>校验一个「父目录 + 名称」形式的完整路径的最后一段（供新建文件夹复用）。</summary>
    public static (string FolderPath, string Name) SplitTargetPath(string? path)
    {
        var normalized = FileOperationService.NormalizePath(path);
        if (string.IsNullOrWhiteSpace(normalized) || normalized == "/")
        {
            throw new Pim.Core.Exceptions.DomainException(5301, "目标路径必须包含文件夹名称");
        }

        var lastSlash = normalized.LastIndexOf('/');
        var name = lastSlash < 0 ? normalized : normalized[(lastSlash + 1)..];
        var parent = lastSlash <= 0 ? "/" : normalized[..lastSlash];
        EnsureValidName(name);
        return (parent, name);
    }
}
