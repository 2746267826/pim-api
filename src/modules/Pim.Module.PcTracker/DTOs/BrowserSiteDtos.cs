using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Pim.Module.PcTracker.DTOs;

// —— 站点级数据上传（守护进程 → 服务端） ——
// 字段名与守护进程 SiteEventDto 的 JSON 契约保持一致。
public class SiteEventUploadDto
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [JsonPropertyName("startMs")] public long? StartMs { get; set; }
    [JsonPropertyName("endMs")] public long? EndMs { get; set; }
    [JsonPropertyName("durationMs")] public long? DurationMs { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
}

public class SiteEventsUploadRequest
{
    [Required][JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [JsonPropertyName("events")] public List<SiteEventUploadDto> Events { get; set; } = new();
}

// —— 查询 ——
public class SiteDailyRowDto
{
    [JsonPropertyName("date")] public string Date { get; set; } = string.Empty;
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [JsonPropertyName("focusMs")] public long FocusMs { get; set; }
    [JsonPropertyName("visitCount")] public long VisitCount { get; set; }
    [JsonPropertyName("runMs")] public long RunMs { get; set; }
    [JsonPropertyName("mediaMs")] public long MediaMs { get; set; }
}

public class SiteDailyQuery
{
    [JsonPropertyName("from")] public string? From { get; set; }
    [JsonPropertyName("to")] public string? To { get; set; }
    [JsonPropertyName("date")] public string? Date { get; set; }
}

public class SiteTimelineRowDto
{
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [JsonPropertyName("startMs")] public long StartMs { get; set; }
    [JsonPropertyName("durationMs")] public long DurationMs { get; set; }
}

public class SiteTopHostDto
{
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("focusMs")] public long FocusMs { get; set; }
    [JsonPropertyName("visitCount")] public long VisitCount { get; set; }
}

public class SiteSummaryDto
{
    [JsonPropertyName("from")] public string From { get; set; } = string.Empty;
    [JsonPropertyName("to")] public string To { get; set; } = string.Empty;
    [JsonPropertyName("totalFocusMs")] public long TotalFocusMs { get; set; }
    [JsonPropertyName("totalVisits")] public long TotalVisits { get; set; }
    [JsonPropertyName("totalRunMs")] public long TotalRunMs { get; set; }
    [JsonPropertyName("totalMediaMs")] public long TotalMediaMs { get; set; }
    [JsonPropertyName("siteCount")] public int SiteCount { get; set; }
    [JsonPropertyName("topHosts")] public List<SiteTopHostDto> TopHosts { get; set; } = new();
}

public class SiteMetaDto
{
    [JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [JsonPropertyName("alias")] public string? Alias { get; set; }
    [JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [JsonPropertyName("cate")] public string? Cate { get; set; }
}

// —— 历史数据自助导入 ——
public class SiteImportRequest
{
    /// <summary>文件全文：支持 tt4b 备份 markdown（内嵌 JSON）或记录页导出 JSON 数组。</summary>
    [Required][JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    /// <summary>overwrite（默认，按 date+host 覆盖）或 add（累加）。</summary>
    [JsonPropertyName("mode")] public string Mode { get; set; } = "overwrite";
    /// <summary>导入归属设备，缺省为 "__imported__"。</summary>
    [JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}

public class SiteImportResultDto
{
    [JsonPropertyName("rows")] public int Rows { get; set; }
    [JsonPropertyName("dates")] public int Dates { get; set; }
    [JsonPropertyName("hosts")] public int Hosts { get; set; }
    [JsonPropertyName("skipped")] public int Skipped { get; set; }
    [JsonPropertyName("format")] public string Format { get; set; } = string.Empty;
}
