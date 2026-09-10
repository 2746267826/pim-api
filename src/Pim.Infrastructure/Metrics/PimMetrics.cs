using Prometheus;

namespace Pim.Infrastructure.Metrics;

/// <summary>
/// PIM 自定义 Prometheus 指标。命名规范：pim_ 前缀。
/// HTTP 请求指标由 prometheus-net 的 UseHttpMetrics 自动采集（http_requests_received_total 等）。
/// </summary>
public static class PimMetrics
{
    /// <summary>AI 网关请求总数（按模块与结果状态）。</summary>
    public static readonly Counter AiRequests = Prometheus.Metrics.CreateCounter(
        "pim_ai_requests_total",
        "AI gateway requests by module and status",
        new CounterConfiguration { LabelNames = ["module", "status"] });

    /// <summary>AI 网关请求耗时（秒，按模块）。</summary>
    public static readonly Histogram AiRequestDuration = Prometheus.Metrics.CreateHistogram(
        "pim_ai_request_duration_seconds",
        "AI gateway request duration in seconds",
        new HistogramConfiguration
        {
            LabelNames = ["module"],
            Buckets = Histogram.ExponentialBuckets(0.1, 2, 10)
        });

    /// <summary>守护进程心跳新鲜度：距上次心跳的秒数（按设备与守护类型）。</summary>
    public static readonly Gauge DaemonHeartbeatFreshness = Prometheus.Metrics.CreateGauge(
        "pim_daemon_heartbeat_freshness_seconds",
        "Seconds since the last daemon heartbeat, by device and daemon kind",
        new GaugeConfiguration { LabelNames = ["device", "kind"] });

    /// <summary>Hangfire 队列状态计数（state: enqueued / processing / scheduled / failed）。</summary>
    public static readonly Gauge HangfireJobs = Prometheus.Metrics.CreateGauge(
        "pim_hangfire_jobs",
        "Hangfire job counts by state",
        new GaugeConfiguration { LabelNames = ["state"] });

    /// <summary>数据质量异常计数（按分类与严重度，如 heartbeat_stale, sync_backlog, untagged_queue, ai_error_rate）。</summary>
    public static readonly Counter DataQualityIssuesTotal = Prometheus.Metrics.CreateCounter(
        "pim_data_quality_issues_total",
        "Total count of data quality issues detected by background inspections",
        new CounterConfiguration { LabelNames = ["category", "severity"] });

    /// <summary>移动端未完成/超期同步批次积压数量。</summary>
    public static readonly Gauge SyncBatchBacklog = Prometheus.Metrics.CreateGauge(
        "pim_sync_batch_backlog_total",
        "Total count of overdue or pending mobile sync batches");

    /// <summary>待分类/待打标签建议队列积压数量。</summary>
    public static readonly Gauge UntaggedRecordsBacklog = Prometheus.Metrics.CreateGauge(
        "pim_untagged_records_backlog_total",
        "Total count of pending activity classification suggestions or untagged items");

    /// <summary>AI 网关近 24 小时请求错误率（0.0 ~ 1.0）。</summary>
    public static readonly Gauge AiGatewayErrorRate24h = Prometheus.Metrics.CreateGauge(
        "pim_ai_gateway_error_rate_24h",
        "AI gateway request error rate over the last 24 hours (0.0 to 1.0)");

    /// <summary>构建信息（值恒为 1，版本走 label）。</summary>
    public static readonly Gauge BuildInfo = Prometheus.Metrics.CreateGauge(
        "pim_build_info",
        "PIM build information",
        new GaugeConfiguration { LabelNames = ["version"] });
}
