using System.Text.Json.Nodes;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Ai.Asr;

/// <summary>
/// Stores bounded browser transport evidence without changing job/provider outcome. This is the
/// only path that can truthfully record a gateway response: socket failures never acquire a
/// gateway status merely because the request was aborted.
/// </summary>
public sealed class AiAsrTransportObservationService(
    PimDbContext db,
    ICurrentUserService currentUser)
{
    private static readonly HashSet<int> GatewayStatuses = [502, 503, 504, 524];

    public async Task<AiAsrTransportObservationDto> ReportAsync(
        AiAsrTransportObservationRequest request, CancellationToken ct)
    {
        var owner = currentUser.UserId ?? throw new DomainException(1002, "请先登录");
        if (request.ExpectedUserId != owner)
            throw new DomainException(40301, "会话已改变，未记录本次传输观察");
        if (request.ClientRequestId == Guid.Empty)
            throw new DomainException(40001, "传输观察缺少请求标识");

        var gatewayName = NormalizeGatewayName(request.GatewayName);
        if (request.Source == AiAsrTransportObservationSource.ClientHttpResponse)
        {
            if (request.GatewayStatus is not { } status || !GatewayStatuses.Contains(status))
                throw new DomainException(40001,
                    "HTTP传输观察必须来自真实网关响应，状态仅接受 502、503、504 或 524");
        }
        else if (request.GatewayStatus is not null || gatewayName is not null)
        {
            throw new DomainException(40001,
                "浏览器网络失败没有HTTP状态或网关名，不能伪造网关响应证据");
        }

        var kind = Kind(request.TransportKind);
        var source = Source(request.Source);
        var (job, log) = await FindTargetAsync(owner, request, ct);

        var now = DateTimeOffset.UtcNow;
        AiAsrTransportObservationEntity? observation = null;
        if (db.Database.IsRelational())
        {
            await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO ai_asr_transport_observations
                        (id, owner_id, log_id, client_request_id, transport_kind, source,
                         gateway_status, gateway_name, observed_at)
                    VALUES
                        ({Guid.NewGuid()}, {owner}, {log.Id}, {request.ClientRequestId}, {kind},
                         {source}, {request.GatewayStatus}, {gatewayName}, {now})
                    ON CONFLICT (owner_id, client_request_id, transport_kind, source)
                    DO UPDATE SET
                        log_id = EXCLUDED.log_id,
                        gateway_status = EXCLUDED.gateway_status,
                        gateway_name = EXCLUDED.gateway_name,
                        observed_at = EXCLUDED.observed_at
                    """, ct);

                observation = await db.AiAsrTransportObservations.AsNoTracking()
                    .SingleAsync(x => x.OwnerId == owner &&
                        x.ClientRequestId == request.ClientRequestId &&
                        x.TransportKind == kind && x.Source == source, ct);

                if (job is not null &&
                    request.Source == AiAsrTransportObservationSource.ClientHttpResponse)
                {
                    await db.AiAsrJobs.Where(x => x.Id == job.Id && x.OwnerId == owner)
                        .ExecuteUpdateAsync(update => update
                            .SetProperty(x => x.GatewayStatus, request.GatewayStatus)
                            .SetProperty(x => x.GatewayName, gatewayName)
                            .SetProperty(x => x.GatewayEvidenceSource, source)
                            .SetProperty(x => x.GatewayObservedAt, now), ct);
                }

                var trackedLog = await db.AiRequestLogs.SingleAsync(x =>
                    x.Id == log.Id && x.UserId == owner, ct);
                trackedLog.MetadataJson = MergeMetadata(trackedLog.MetadataJson, request, source,
                    gatewayName, now);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);
            });
        }
        else
        {
            observation = await db.AiAsrTransportObservations.SingleOrDefaultAsync(x =>
                x.OwnerId == owner && x.ClientRequestId == request.ClientRequestId &&
                x.TransportKind == kind && x.Source == source, ct) ?? new()
            {
                OwnerId = owner,
                LogId = log.Id,
                ClientRequestId = request.ClientRequestId,
                TransportKind = kind,
                Source = source
            };
            if (db.Entry(observation).State == EntityState.Detached)
                db.AiAsrTransportObservations.Add(observation);
            observation.GatewayStatus = request.GatewayStatus;
            observation.GatewayName = gatewayName;
            observation.ObservedAt = now;
            if (job is not null &&
                request.Source == AiAsrTransportObservationSource.ClientHttpResponse)
            {
                // FindTargetAsync is no-tracking. InMemory databases may still hold another
                // tracked instance from the caller, so attach only when no tracked row exists.
                var trackedJob = db.AiAsrJobs.Local.SingleOrDefault(x => x.Id == job.Id);
                if (trackedJob is null)
                {
                    db.AiAsrJobs.Attach(job);
                    trackedJob = job;
                }
                trackedJob.GatewayStatus = request.GatewayStatus;
                trackedJob.GatewayName = gatewayName;
                trackedJob.GatewayEvidenceSource = source;
                trackedJob.GatewayObservedAt = now;
            }
            var trackedLog = await db.AiRequestLogs.SingleAsync(x =>
                x.Id == log.Id && x.UserId == owner, ct);
            trackedLog.MetadataJson = MergeMetadata(trackedLog.MetadataJson, request, source,
                gatewayName, now);
            await db.SaveChangesAsync(ct);
        }

        if (observation is null)
            throw new InvalidOperationException("ASR transport observation was not stored");
        return new(request.ClientRequestId, log.Id, request.TransportKind, request.Source,
            observation.GatewayStatus, observation.GatewayName, observation.ObservedAt);
    }

    private async Task<(AiAsrJobEntity? Job, AiRequestLogEntity Log)> FindTargetAsync(Guid owner,
        AiAsrTransportObservationRequest request, CancellationToken ct)
    {
        AiAsrJobEntity? job = null;
        AiRequestLogEntity? log;
        if (request.TransportKind is AiAsrTransportKind.AsyncPoll or AiAsrTransportKind.AsyncSubmit)
        {
            job = await db.AiAsrJobs.AsNoTracking().SingleOrDefaultAsync(x =>
                x.OwnerId == owner && x.ClientRequestId == request.ClientRequestId, ct);
            log = job is null ? null : await db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x =>
                x.Id == job.LogId && x.UserId == owner, ct);
        }
        else
        {
            var purpose = request.TransportKind == AiAsrTransportKind.SyncFileCall
                ? "ai.asr-call" : "ai.asr-url-call";
            log = await db.AiRequestLogs.AsNoTracking().SingleOrDefaultAsync(x =>
                x.UserId == owner && x.ClientRequestId == request.ClientRequestId &&
                x.Purpose == purpose, ct);
        }
        if (log is null)
            throw new DomainException(40401, "尚未找到本次ASR任务或调用记录");
        return (job, log);
    }

    internal static AiAsrJobDiagnosticsDto? Diagnostics(AiRequestLogEntity log,
        AiAsrTransportObservationEntity? observation)
    {
        if (log.Purpose is not ("ai.asr-call" or "ai.asr-url-call" or "ai.asr-job"))
            return null;
        var abortSource = "none";
        var outcomeKnown = log.Status is "succeeded" or "blocked" or "cancelled";
        int? upstreamStatus = null;
        var providerAttempted = false;
        var responseComplete = false;
        try
        {
            var root = JsonNode.Parse(log.MetadataJson) as JsonObject;
            if (root?["abortSource"]?.GetValue<string>() is { } value) abortSource = value;
            if (root?["outcomeKnown"]?.GetValue<bool>() is { } known) outcomeKnown = known;
            if (root?["upstreamStatus"]?.GetValue<int>() is { } upstream) upstreamStatus = upstream;
            if (root?["providerAttempted"]?.GetValue<bool>() is { } attempted)
                providerAttempted = attempted;
            if (root?["responseBodyComplete"]?.GetValue<bool>() is { } complete)
                responseComplete = complete;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException)
        {
        }
        return new(abortSource, outcomeKnown, observation?.GatewayStatus,
            observation?.GatewayName, observation?.Source, observation?.TransportKind,
            observation?.ObservedAt, upstreamStatus, providerAttempted, responseComplete,
            MaxOutputTokens(log.MetadataJson), EstimatedAudioSeconds(log.MetadataJson),
            EstimatedCompletionTokens(log.MetadataJson), OutputLimitExceeded(log.MetadataJson),
            NextAction(abortSource));
    }

    private static string MergeMetadata(string metadata, AiAsrTransportObservationRequest request,
        string source, string? gatewayName, DateTimeOffset now)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(metadata) as JsonObject ?? [];
        }
        catch (JsonException)
        {
            root = [];
        }
        var existingHttp = root["gatewayEvidenceSource"]?.GetValue<string>() ==
            "client_http_response";
        if (request.Source == AiAsrTransportObservationSource.ClientHttpResponse || !existingHttp)
        {
            root["gatewayStatus"] = request.GatewayStatus;
            root["gatewayName"] = gatewayName;
            root["gatewayEvidenceSource"] = source;
            root["transportObservationKind"] = Kind(request.TransportKind);
            root["gatewayObservedAt"] = now;
        }
        return root.ToJsonString();
    }

    private static string? NormalizeGatewayName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var name = value.Trim();
        if (name.Length > 200 || name.Any(char.IsControl))
            throw new DomainException(40001, "网关名称无效");
        return name;
    }

    private static int MaxOutputTokens(string metadata) => Int(metadata, "maxOutputTokens") ?? 0;

    private static double? EstimatedAudioSeconds(string metadata) => Double(metadata,
        "estimatedAudioSeconds");

    private static int? EstimatedCompletionTokens(string metadata) => Int(metadata,
        "estimatedCompletionTokens");

    private static bool OutputLimitExceeded(string metadata) => Bool(metadata,
        "outputLimitExceeded");

    private static int? Int(string metadata, string property)
    {
        try { return (JsonNode.Parse(metadata) as JsonObject)?[property]?.GetValue<int>(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException) { return null; }
    }

    private static double? Double(string metadata, string property)
    {
        try { return (JsonNode.Parse(metadata) as JsonObject)?[property]?.GetValue<double>(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException) { return null; }
    }

    private static bool Bool(string metadata, string property)
    {
        try { return (JsonNode.Parse(metadata) as JsonObject)?[property]?.GetValue<bool>() == true; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or
            FormatException) { return false; }
    }

    private static string? NextAction(string abortSource) => abortSource switch
    {
        "gateway_http" => "放宽反向代理读取超时或改走持久后台任务；不要自动重发",
        "user_cancelled" or "user_cancel" or "user_cancel_late" =>
            "按原请求标识核对服务商记录，不要自动重发",
        "provider_timeout" or "worker_timeout" or "worker_lost" or "caller_disconnect" =>
            "核对服务商记录后再决定是否明确新建请求",
        _ => null
    };

    private static string Kind(AiAsrTransportKind kind) => kind switch
    {
        AiAsrTransportKind.AsyncSubmit => "async_submit",
        AiAsrTransportKind.AsyncPoll => "async_poll",
        AiAsrTransportKind.SyncFileCall => "sync_file_call",
        AiAsrTransportKind.SyncUrlCall => "sync_url_call",
        _ => throw new DomainException(40001, "未知的ASR传输方式")
    };

    private static string Source(AiAsrTransportObservationSource source) => source switch
    {
        AiAsrTransportObservationSource.ClientHttpResponse => "client_http_response",
        AiAsrTransportObservationSource.ClientNetworkFailure => "client_network_failure",
        _ => throw new DomainException(40001, "未知的ASR传输观察来源")
    };
}
