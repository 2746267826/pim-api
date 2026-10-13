namespace Pim.Api.Services;

/// <summary>
/// 镜像内 <c>wwwroot/web-version.json</c> 的内容（由 pim-web 的产物镜像带进来）。
///
/// 缺失/不可读时统一返回 <see cref="UnknownValue"/> 哨兵值，而不是抛异常或假装有值：
/// 调用方（<c>/api/version</c>）必须能区分「没内嵌前端」与「内嵌了某个版本」，且不得 500。
/// </summary>
public sealed record WebVersionInfo(string Version, string GitSha, string BuiltAt);

/// <summary>
/// 读取镜像内前端版本文件的只读入口。文件在镜像构建期写入，运行期不会变化，
/// 因此只解析一次并缓存。
/// </summary>
public sealed class WebVersionProvider
{
    /// <summary>产物版本文件名（与 pim-web 的 <c>web-version.json</c> 契约一致）。</summary>
    public const string FileName = "web-version.json";

    /// <summary>「未知 / 未内嵌」哨兵值。</summary>
    public const string UnknownValue = "unknown";

    private readonly Lazy<WebVersionInfo> _current;

    public WebVersionProvider(string webRootPath, ILogger<WebVersionProvider>? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(webRootPath);
        Path = System.IO.Path.Combine(webRootPath, FileName);
        _current = new Lazy<WebVersionInfo>(() => Read(Path, logger));
    }

    /// <summary>版本文件在镜像内的绝对路径。</summary>
    public string Path { get; }

    public WebVersionInfo Current => _current.Value;

    private static WebVersionInfo Read(string path, ILogger? logger)
    {
        logger ??= Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        if (!File.Exists(path))
        {
            logger.LogWarning("前端版本文件缺失，按「未知 / 未内嵌」处理：{Path}", path);
            return new WebVersionInfo(UnknownValue, UnknownValue, UnknownValue);
        }

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return new WebVersionInfo(
                Field(root, "version", logger, path),
                Field(root, "gitSha", logger, path),
                Field(root, "builtAt", logger, path));
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "前端版本文件不可读，按「未知 / 未内嵌」处理：{Path}", path);
            return new WebVersionInfo(UnknownValue, UnknownValue, UnknownValue);
        }
    }

    private static string Field(System.Text.Json.JsonElement root, string name, ILogger logger, string path)
    {
        if (root.ValueKind == System.Text.Json.JsonValueKind.Object
            && root.TryGetProperty(name, out var value)
            && value.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        logger.LogWarning("前端版本文件缺少字段 {Field}，按「未知」处理：{Path}", name, path);
        return UnknownValue;
    }
}
