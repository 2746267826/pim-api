using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;
using Serilog.Sinks.Grafana.Loki;

namespace Pim.Api.Infrastructure;

public static class LoggingConfig
{
    public const int DefaultRetainedFileCount = 30;
    public const long DefaultFileSizeLimitBytes = 1073741824L; // 1 GiB
    public const bool DefaultRollOnFileSizeLimit = true;

    /// <summary>
    /// 生产日志文件路径（容器内固定路径，由 compose 挂载宿主机目录）。
    /// </summary>
    public const string DefaultLogFilePath = "/data/pim/logs/pim-api-.jsonl";

    /// <summary>
    /// 默认最低日志级别。选 Information 而不是 Debug：Debug 档下框架内务（EF 的实体跟踪 /
    /// 命令流水、Kestrel、路由匹配等）会淹没真错误 —— 维护者在生产抽样 50MB 日志里看到
    /// Debug 69,219 条 / 真错误 0 条，其中 EF 内务约 37,000 条。Information 保留请求、
    /// 启动、迁移与所有 Warning 及以上，同时把内务压到 Warning 档以下。
    /// </summary>
    public const LogEventLevel DefaultMinimumLevel = LogEventLevel.Information;

    /// <summary>最低级别的显式环境变量（与 PIM_LOG_RETAINED_FILES 等同族，运维一眼可改）。</summary>
    public const string MinimumLevelEnvironmentVariable = "PIM_LOG_LEVEL";

    /// <summary>EF 内务类别的默认覆盖（可在配置里改）：命令流水与实体跟踪抬到 Warning。</summary>
    public static readonly IReadOnlyDictionary<string, LogEventLevel> DefaultCategoryLevels =
        new Dictionary<string, LogEventLevel>(StringComparer.Ordinal)
        {
            ["Microsoft.EntityFrameworkCore.ChangeTracking"] = LogEventLevel.Warning,
            ["Microsoft.EntityFrameworkCore.Database.Command"] = LogEventLevel.Warning
        };

    /// <summary>
    /// 解析结果：最低级别 + 类别覆盖 + 无法解析的配置项（必须能被调用方看见，不能静默吞掉）。
    /// </summary>
    /// <param name="MinimumLevel">生效的最低级别。</param>
    /// <param name="MinimumLevelSource">这个级别的来源（环境变量 / 配置键 / 默认值），启动时会记一条日志。</param>
    /// <param name="CategoryLevels">类别级覆盖（含 EF 内务默认档）。</param>
    /// <param name="InvalidEntries">取值无法解析的配置项（键=值），由调用方记 Warning。</param>
    public sealed record LoggingLevels(
        LogEventLevel MinimumLevel,
        string MinimumLevelSource,
        IReadOnlyDictionary<string, LogEventLevel> CategoryLevels,
        IReadOnlyList<string> InvalidEntries);

    /// <summary>
    /// 解析日志级别：<c>PIM_LOG_LEVEL</c> 环境变量 &gt; <c>Logging:LogLevel:Default</c> 配置 &gt; Information。
    /// 类别覆盖来自 <c>Logging:LogLevel:*</c> 的其余键；EF 内务的两个类别在未显式配置时取
    /// <see cref="DefaultCategoryLevels"/>。取不到或写错的值不会被静默忽略 —— 计入 <c>InvalidEntries</c>。
    /// </summary>
    public static LoggingLevels ResolveLevels(IConfiguration configuration, string? environmentLevel = null)
    {
        var invalid = new List<string>();
        var source = "默认值 Information";
        LogEventLevel minimumLevel = DefaultMinimumLevel;

        environmentLevel ??= Environment.GetEnvironmentVariable(MinimumLevelEnvironmentVariable);
        var fromEnvironment = !string.IsNullOrWhiteSpace(environmentLevel);
        var configured = fromEnvironment
            ? environmentLevel
            : configuration["Logging:LogLevel:Default"];
        if (fromEnvironment)
        {
            source = MinimumLevelEnvironmentVariable;
        }
        else if (!string.IsNullOrWhiteSpace(configured))
        {
            source = "配置 Logging:LogLevel:Default";
        }

        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (TryParseLevel(configured, out var parsed))
            {
                minimumLevel = parsed;
            }
            else
            {
                invalid.Add($"Logging:LogLevel:Default={configured}");
            }
        }

        var categories = new Dictionary<string, LogEventLevel>(DefaultCategoryLevels, StringComparer.Ordinal);
        foreach (var entry in configuration.GetSection("Logging:LogLevel").GetChildren())
        {
            if (string.Equals(entry.Key, "Default", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(entry.Value))
            {
                continue;
            }

            if (TryParseLevel(entry.Value, out var categoryLevel))
            {
                categories[entry.Key] = categoryLevel;
            }
            else
            {
                invalid.Add($"Logging:LogLevel:{entry.Key}={entry.Value}");
            }
        }

        return new LoggingLevels(minimumLevel, source, categories, invalid);
    }

    /// <summary>解析级别名（大小写不敏感）。空值与非法值都不算成功。</summary>
    public static bool TryParseLevel(string? rawValue, out LogEventLevel level)
        => Enum.TryParse(rawValue?.Trim(), ignoreCase: true, out level) && Enum.IsDefined(level);

    /// <summary>日志落盘 / 转发参数（生产值来自环境变量，测试里可指向临时路径）。</summary>
    public sealed record LoggingSinkOptions(
        string FilePath,
        int RetainedFileCount,
        long? FileSizeLimitBytes,
        bool RollOnFileSizeLimit,
        string? LokiUrl,
        bool ConsoleToStandardError);

    /// <summary>
    /// 按解析出的级别构造 Serilog 配置：控制台 + 文件（+ 可选 Loki）。
    /// <c>--mcp-stdio</c> 只是把控制台输出换到 stderr（stdout 必须只走 MCP 协议），
    /// 级别/覆盖/接收器完全同一套 —— 因此 stdio 分支同样受配置控制。
    /// </summary>
    public static LoggerConfiguration CreateLoggerConfiguration(LoggingLevels levels, LoggingSinkOptions sinks)
    {
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(levels.MinimumLevel)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Service", "pim-api");

        foreach (var (category, level) in levels.CategoryLevels)
        {
            configuration = configuration.MinimumLevel.Override(category, level);
        }

        configuration = sinks.ConsoleToStandardError
            ? configuration.WriteTo.TextWriter(new CompactJsonFormatter(), Console.Error)
            : configuration.WriteTo.Console(new CompactJsonFormatter());

        configuration = configuration.WriteTo.File(
            new CompactJsonFormatter(),
            sinks.FilePath,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: sinks.FileSizeLimitBytes,
            rollOnFileSizeLimit: sinks.RollOnFileSizeLimit,
            retainedFileCountLimit: sinks.RetainedFileCount);

        if (!string.IsNullOrWhiteSpace(sinks.LokiUrl))
        {
            configuration = configuration.WriteTo.GrafanaLoki(
                sinks.LokiUrl,
                [new LokiLabel { Key = "app", Value = "pim-api" }]);
        }

        return configuration;
    }

    public static int ResolveRetainedFileCount(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return DefaultRetainedFileCount;
        if (int.TryParse(rawValue, out var parsed) && parsed >= 1)
            return parsed;
        return DefaultRetainedFileCount;
    }

    public static long? ResolveFileSizeLimitBytes(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
            return DefaultFileSizeLimitBytes;
        var trimmed = rawValue.Trim();
        if (trimmed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("null", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("none", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("unlimited", StringComparison.OrdinalIgnoreCase))
            return null;
        if (long.TryParse(trimmed, out var parsed) && parsed > 0)
            return parsed;
        return DefaultFileSizeLimitBytes;
    }

    public static bool ResolveRollOnFileSizeLimit(string? rawValue, long? fileSizeLimitBytes = DefaultFileSizeLimitBytes)
    {
        // Serilog requires fileSizeLimitBytes != null when rollOnFileSizeLimit is true.
        // If file size is unlimited (null), rolling on size limit is not applicable.
        if (fileSizeLimitBytes == null)
            return false;
        if (string.IsNullOrWhiteSpace(rawValue))
            return DefaultRollOnFileSizeLimit;
        var trimmed = rawValue.Trim();
        if (trimmed.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
