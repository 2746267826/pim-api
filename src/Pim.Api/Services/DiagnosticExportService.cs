using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Module.Mobile.Diagnostics;
using Pim.Module.Mobile.DTOs;

namespace Pim.Api.Services;

public sealed record DiagnosticLogSummary(
    bool Available,
    int FileCount,
    long TotalBytes,
    DateTimeOffset? LatestWriteAtUtc);

public sealed record DiagnosticJobsSummary(
    bool Available,
    string Status,
    int Processing,
    int Enqueued,
    int Scheduled,
    int Failed,
    DateTimeOffset? CheckedAt);

public sealed record DiagnosticComponentSummary(
    string Key,
    string Status,
    DateTimeOffset CheckedAt);

public sealed record DiagnosticServerSummary(
    string SchemaVersion,
    string ApiVersion,
    string WebVersion,
    string WebSha,
    string WebBuiltAt,
    string Environment,
    string Runtime,
    string OperatingSystem,
    DateTimeOffset GeneratedAtUtc,
    long ProcessUptimeSeconds,
    DiagnosticLogSummary Logs,
    DiagnosticJobsSummary Jobs,
    IReadOnlyList<DiagnosticComponentSummary> Components,
    string RedactionPolicy);

public sealed record DiagnosticExportManifest(
    string SchemaVersion,
    DateTimeOffset GeneratedAtUtc,
    int Days,
    int EventCount,
    string? DeviceId,
    string? Platform,
    IReadOnlyList<string> Files,
    string RedactionPolicy);

public sealed class DiagnosticExportService
{
    private const string SchemaVersion = "pim.diagnostics.v1";
    private const string RedactionPolicy = "automatic-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly DiagnosticEventService _events;
    private readonly OpsLogsService _logs;
    private readonly ISystemStatusService _status;
    private readonly IBackgroundJobStatusService _jobs;
    private readonly WebVersionProvider _webVersion;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;

    public DiagnosticExportService(
        DiagnosticEventService events,
        OpsLogsService logs,
        ISystemStatusService status,
        IBackgroundJobStatusService jobs,
        WebVersionProvider webVersion,
        IHostEnvironment environment,
        TimeProvider timeProvider)
    {
        _events = events;
        _logs = logs;
        _status = status;
        _jobs = jobs;
        _webVersion = webVersion;
        _environment = environment;
        _timeProvider = timeProvider;
    }

    public async Task<DiagnosticExportBundle> BuildAsync(
        string? deviceId,
        string? platform,
        int days,
        CancellationToken ct = default)
    {
        var generatedAt = _timeProvider.GetUtcNow();
        var events = await _events.GetExportEventsAsync(deviceId, platform, days, ct);
        ct.ThrowIfCancellationRequested();
        var server = await BuildServerSummaryAsync(generatedAt, ct);
        var fileNames = new[]
        {
            "manifest.json",
            "diagnostic-events.json",
            "server-summary.json",
            "README.txt"
        };
        var manifest = new DiagnosticExportManifest(
            SchemaVersion,
            generatedAt,
            days,
            events.Count,
            string.IsNullOrWhiteSpace(deviceId)
                ? null
                : DiagnosticRedaction.RedactText(deviceId.Trim(), 128),
            string.IsNullOrWhiteSpace(platform) ? null : platform.Trim().ToLowerInvariant(),
            fileNames,
            RedactionPolicy);

        await using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteJsonEntry(archive, fileNames[0], manifest, ct);
            WriteJsonEntry(archive, fileNames[1], events, ct);
            WriteJsonEntry(archive, fileNames[2], server, ct);
            WriteTextEntry(archive, fileNames[3], BuildReadme(), ct);
        }

        if (buffer.Length > DiagnosticLimits.MaxExportBytes)
        {
            throw new DomainException(41301, "诊断包超过允许的大小上限，请缩小时间范围或按设备导出。");
        }

        var stamp = generatedAt.UtcDateTime.ToString("yyyyMMdd-HHmmss");
        return new DiagnosticExportBundle(
            buffer.ToArray(),
            $"pim-diagnostics-{stamp}.zip");
    }

    private async Task<DiagnosticServerSummary> BuildServerSummaryAsync(
        DateTimeOffset generatedAt,
        CancellationToken ct)
    {
        var web = _webVersion.Current;
        var logs = await BuildLogSummaryAsync(ct);
        var jobs = await BuildJobsSummaryAsync(ct);
        var components = await BuildComponentSummariesAsync(ct);
        var apiVersion = typeof(Program).Assembly
            .GetCustomAttributes(false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0(unknown)";

        return new DiagnosticServerSummary(
            SchemaVersion,
            apiVersion,
            web.Version,
            web.GitSha,
            web.BuiltAt,
            _environment.EnvironmentName,
            RuntimeInformation.FrameworkDescription,
            RuntimeInformation.OSDescription,
            generatedAt,
            Math.Max(0, Environment.TickCount64 / 1000),
            logs,
            jobs,
            components,
            RedactionPolicy);
    }

    private async Task<DiagnosticLogSummary> BuildLogSummaryAsync(CancellationToken ct)
    {
        try
        {
            var files = await _logs.ListFilesAsync(ct);
            return new DiagnosticLogSummary(
                true,
                files.Count,
                files.Sum(file => file.Size),
                files.Count == 0 ? null : files.Max(file => file.Mtime));
        }
        catch
        {
            return new DiagnosticLogSummary(false, 0, 0, null);
        }
    }

    private async Task<DiagnosticJobsSummary> BuildJobsSummaryAsync(CancellationToken ct)
    {
        try
        {
            var summary = await _jobs.GetSummaryAsync(ct);
            return new DiagnosticJobsSummary(
                true,
                summary.Status.ToString(),
                summary.Processing,
                summary.Enqueued,
                summary.Scheduled,
                summary.Failed,
                summary.CheckedAt);
        }
        catch
        {
            return new DiagnosticJobsSummary(false, "Unknown", 0, 0, 0, 0, null);
        }
    }

    private async Task<IReadOnlyList<DiagnosticComponentSummary>> BuildComponentSummariesAsync(
        CancellationToken ct)
    {
        try
        {
            var detail = await _status.GetDetailAsync(ct);
            return detail.Components
                .Select(component => new DiagnosticComponentSummary(
                    component.Key,
                    component.Status.ToString(),
                    component.CheckedAt))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string BuildReadme()
        => string.Join(
            "\n",
            [
                "PIM 诊断包",
                string.Empty,
                "本压缩包只包含当前登录账号可见的诊断事件，以及按白名单生成的服务器摘要。",
                "消息、堆栈、设备标识和属性在服务端统一脱敏；Web 客户端入队前也会脱敏一次。",
                "不包含登录令牌、GraphEventId、ChangeKey、",
                "原始 provider secret、临时路径或服务器日志正文。",
                "server-summary.json 只保留版本、运行环境、日志文件数量与后台任务计数等白名单字段。",
                "如需排查细节，请结合问题发生时间、设备、客户端版本和 diagnostic-events.json 中的事件编号。",
                "单次导出最多覆盖最近 30 天、2000 条事件，ZIP 上限 8 MB；超出时请缩小时间范围或按设备导出。",
                string.Empty,
                $"脱敏策略：{RedactionPolicy}",
                $"格式版本：{SchemaVersion}"
            ]);

    private static void WriteJsonEntry<T>(
        ZipArchive archive,
        string name,
        T value,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, value, JsonOptions);
        ct.ThrowIfCancellationRequested();
    }

    private static void WriteTextEntry(
        ZipArchive archive,
        string name,
        string value,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(value);
        ct.ThrowIfCancellationRequested();
    }
}
