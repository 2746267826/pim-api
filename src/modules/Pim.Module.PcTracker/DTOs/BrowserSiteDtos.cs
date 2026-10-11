using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Pim.Module.PcTracker.DTOs;

// —— 站点级数据上传（守护进程 → 服务端） ——
// 字段名与守护进程 SiteEventDto 的 JSON 契约保持一致。
public class SiteEventUploadDto
{
    [property:JsonPropertyName("kind")] public string Kind { get; set; } = string.Empty;
    [property:JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [property:JsonPropertyName("startMs")] public long? StartMs { get; set; }
    [property:JsonPropertyName("endMs")] public long? EndMs { get; set; }
    [property:JsonPropertyName("durationMs")] public long? DurationMs { get; set; }
    [property:JsonPropertyName("date")] public string? Date { get; set; }
}

public class SiteEventsUploadRequest
{
    [property:Required][property:JsonPropertyName("deviceId")] public string DeviceId { get; set; } = string.Empty;
    [property:JsonPropertyName("events")] public List<SiteEventUploadDto> Events { get; set; } = new();
}

// —— 查询 ——
public class SiteDailyRowDto
{
    [property:JsonPropertyName("date")] public string Date { get; set; } = string.Empty;
    [property:JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [property:JsonPropertyName("focusMs")] public long FocusMs { get; set; }
    [property:JsonPropertyName("visitCount")] public long VisitCount { get; set; }
    [property:JsonPropertyName("runMs")] public long RunMs { get; set; }
    [property:JsonPropertyName("mediaMs")] public long MediaMs { get; set; }
}

public class SiteDailyQuery
{
    [property:JsonPropertyName("from")] public string? From { get; set; }
    [property:JsonPropertyName("to")] public string? To { get; set; }
    [property:JsonPropertyName("date")] public string? Date { get; set; }
}

public class SiteTimelineRowDto
{
    [property:JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [property:JsonPropertyName("startMs")] public long StartMs { get; set; }
    [property:JsonPropertyName("durationMs")] public long DurationMs { get; set; }
}

public class SiteTopHostDto
{
    [property:JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [property:JsonPropertyName("alias")] public string? Alias { get; set; }
    [property:JsonPropertyName("focusMs")] public long FocusMs { get; set; }
    [property:JsonPropertyName("visitCount")] public long VisitCount { get; set; }
}

public class SiteSummaryDto
{
    [property:JsonPropertyName("from")] public string From { get; set; } = string.Empty;
    [property:JsonPropertyName("to")] public string To { get; set; } = string.Empty;
    [property:JsonPropertyName("totalFocusMs")] public long TotalFocusMs { get; set; }
    [property:JsonPropertyName("totalVisits")] public long TotalVisits { get; set; }
    [property:JsonPropertyName("totalRunMs")] public long TotalRunMs { get; set; }
    [property:JsonPropertyName("totalMediaMs")] public long TotalMediaMs { get; set; }
    [property:JsonPropertyName("siteCount")] public int SiteCount { get; set; }
    [property:JsonPropertyName("topHosts")] public List<SiteTopHostDto> TopHosts { get; set; } = new();
}

public class SiteMetaDto
{
    [property:JsonPropertyName("host")] public string Host { get; set; } = string.Empty;
    [property:JsonPropertyName("alias")] public string? Alias { get; set; }
    [property:JsonPropertyName("iconUrl")] public string? IconUrl { get; set; }
    [property:JsonPropertyName("cate")] public string? Cate { get; set; }
}

// —— 历史数据自助导入 ——
public class SiteImportRequest
{
    /// <summary>文件全文：支持 tt4b 备份 markdown（内嵌 JSON）或记录页导出 JSON 数组。</summary>
    [property:Required][property:JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    /// <summary>overwrite（默认，按 date+host 覆盖）或 add（累加）。</summary>
    [property:JsonPropertyName("mode")] public string Mode { get; set; } = "overwrite";
    /// <summary>导入归属设备，缺省为 "__imported__"。</summary>
    [property:JsonPropertyName("deviceId")] public string? DeviceId { get; set; }
}

public class SiteImportResultDto
{
    [property:JsonPropertyName("rows")] public int Rows { get; set; }
    [property:JsonPropertyName("dates")] public int Dates { get; set; }
    [property:JsonPropertyName("hosts")] public int Hosts { get; set; }
    [property:JsonPropertyName("skipped")] public int Skipped { get; set; }
    [property:JsonPropertyName("format")] public string Format { get; set; } = string.Empty;
}
