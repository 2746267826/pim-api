using System;
using System.Collections.Generic;

namespace Pim.Module.Mobile.DTOs;

/// <summary>
/// 一条取证事件（工单 §7.1）。字段为需求契约，命名由实现方定。
/// <para>
/// <see cref="PayloadJson"/> 原样保存设备端负载（jsonb），服务端不在写入路径上做字段级校验，
/// 因此旧版本客户端多带未知字段不会导致报错（AC-30.2）。
/// </para>
/// </summary>
public sealed record MobileForensicEventUploadItem(
    string ClientItemKey,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    string? PayloadJson);

/// <summary>设备端按「本地日 + 原因」聚合的定位丢弃统计快照（REQ-9）。</summary>
public sealed record MobileDroppedReasonSummaryItem(
    string LocalDate,
    string Reason,
    int Count);

/// <summary>
/// 取证事件批量上报请求（沿用既有批量语义：设备 + 事件数组 + 幂等键）。
/// </summary>
public sealed record MobileForensicsUploadRequest(
    string DeviceId,
    string? BatchId,
    IReadOnlyList<MobileForensicEventUploadItem>? Events,
    IReadOnlyList<MobileDroppedReasonSummaryItem>? DroppedReasonSummaries);

/// <summary>逐条接受/跳过结果（AC-5.2 幂等；AC-5.1 服务端条数与设备端一致）。</summary>
public sealed record MobileForensicsIngestResult(
    int AcceptedCount,
    int SkippedCount,
    int RejectedCount,
    int FailedCount,
    IReadOnlyList<string> AcceptedKeys,
    IReadOnlyList<string> SkippedKeys,
    IReadOnlyList<string> RejectedKeys,
    int AcceptedDroppedReasonCount);

/// <summary>「设备存活」页里一台设备的完整数据块（REQ-7）。</summary>
public sealed record MobileDeviceLivenessDto(
    string DeviceId,
    string DisplayName,
    string DeviceKind,
    string DeviceKindLabel,
    bool HasData,
    string Conclusion,
    double? CoverageByHour,
    double? CoverageByExpectedHeartbeat,
    int ObservedHours,
    int TotalHours,
    int ObservedHeartbeats,
    int ExpectedHeartbeats,
    int ExpectedHeartbeatIntervalMinutes,
    int LongestSilenceMinutes,
    DateTimeOffset? LongestSilenceStartUtc,
    DateTimeOffset? LongestSilenceEndUtc,
    string LongestSilenceSeverity,
    bool HasSilenceOverOneHour,
    IReadOnlyList<MobileSilenceWindowDto> Silences,
    IReadOnlyList<MobileLivenessCauseDto> Causes,
    DateTimeOffset? LastEventAtUtc,
    string CoverageByHourDefinition,
    string CoverageByExpectedHeartbeatDefinition);

/// <summary>一段静默（REQ-7.4 的标色对象）。</summary>
public sealed record MobileSilenceWindowDto(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    double Minutes,
    string Severity,
    string SeverityLabel);

/// <summary>死因分布中的一项；未知原因带推断依据。</summary>
public sealed record MobileLivenessCauseDto(
    string Cause,
    string Label,
    int Count,
    string? Inference);

/// <summary>Web「设备存活」子页首屏数据（REQ-7 / AC-7.1 / AC-7.3）。</summary>
public sealed record MobileLivenessOverviewResponse(
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    int ExpectedHeartbeatIntervalMinutes,
    IReadOnlyList<MobileDeviceLivenessDto> Phones,
    IReadOnlyList<MobileDeviceLivenessDto> Tablets,
    IReadOnlyList<MobileDeviceLivenessDto> Unclassified);

/// <summary>单设备存活事件分页（REQ-7.2：可展开、可查看原始 JSON）。</summary>
public sealed record MobileLivenessEventDto(
    Guid Id,
    string EventType,
    string EventTypeLabel,
    DateTimeOffset OccurredAtUtc,
    string? Reason,
    string? ReasonLabel,
    string? Inference,
    int? Importance,
    long? PssKb,
    long? RssKb,
    string? Description,
    string PayloadJson);

/// <summary>存活事件分页结果。</summary>
public sealed record MobileLivenessEventPageDto(
    IReadOnlyList<MobileLivenessEventDto> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

/// <summary>按天按原因的丢弃统计（REQ-9 / AC-9.2）。</summary>
public sealed record MobileDroppedReasonDailyDto(
    string LocalDate,
    string Reason,
    int Count);

/// <summary>丢弃原因统计查询结果。</summary>
public sealed record MobileDroppedReasonResponse(
    string DeviceId,
    DateTimeOffset RangeStartUtc,
    DateTimeOffset RangeEndUtc,
    IReadOnlyList<MobileDroppedReasonDailyDto> Items,
    int TotalCount);
