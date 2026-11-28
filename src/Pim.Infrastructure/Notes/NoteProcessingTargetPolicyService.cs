using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Pim.Core.Ai;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Ai;
using Pim.Infrastructure.Ai.Asr;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;

namespace Pim.Infrastructure.Notes;

internal sealed record NoteProcessingTargetPolicy(Guid Revision,
    IReadOnlyList<string> Descriptions);

public sealed class NoteProcessingTargetPolicyService(
    PimDbContext db,
    AiAsrPresetRegistry presets)
{
    internal async Task<NoteProcessingTargetPolicy> ResolveAsync(Guid owner,
        CancellationToken ct)
    {
        var runtime = await db.AiModelRuntime.AsNoTracking().SingleOrDefaultAsync(ct);
        var route = await db.NoteAsrRouteConfigs.AsNoTracking()
            .SingleOrDefaultAsync(x => x.OwnerId == owner, ct);
        // No route config still means the legacy cloud ASR shape, but the displayed route must be
        // the one the audio would actually take. Both cases therefore resolve through the same
        // helper the ASR service and the route settings use.
        var configuredRoute = route?.DefaultRoute.Trim();
        var localRoute = configuredRoute == NoteAsrRoute.Local ? route : null;
        var isLocal = localRoute is not null;
        var asrModelId = isLocal
            ? null
            : route?.CloudModelId ?? runtime?.TranscriptionModelId;
        var textModelId = AiPurposeModelResolver.ResolveId(runtime,
            AiPurpose.NoteOrganization, hasImages: false);
        var visionModelId = AiPurposeModelResolver.ResolveId(runtime,
            AiPurpose.NoteOrganization, hasImages: true);
        var modelIds = new[]
            {
                asrModelId,
                textModelId,
                visionModelId
            }
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToArray();
        var profiles = await db.AiModelProfiles.AsNoTracking()
            .Where(model => modelIds.Contains(model.Id))
            .ToDictionaryAsync(model => model.Id, ct);

        var targets = new List<object>();
        var descriptions = new List<string>();
        var routeName = NoteAsrRoute.Local;
        if (localRoute is not null)
        {
            targets.Add(new
            {
                key = "Asr",
                route = NoteAsrRoute.Local,
                routeRevision = localRoute.Revision,
                routeConsentVersion = localRoute.ConsentVersion,
                localModelId = localRoute.LocalModelId,
                localModelRevision = localRoute.LocalModelRevision,
                executionMode = NoteAsrExecutionMode.Realtime
            });
            descriptions.Add(
                $"转写：本机模型 {localRoute.LocalModelId ?? "未配置"}；模型 revision {localRoute.LocalModelRevision ?? "未配置"}；执行模式 realtime；路由 revision {localRoute.Revision}");
        }
        else
        {
            var routeRevision = route?.Revision;
            var profile = asrModelId is { } selected && profiles.TryGetValue(selected, out var found)
                ? found
                : null;
            routeName = NoteAsrRouteResolution.EffectiveCloudRoute(
                configuredRoute ?? NoteAsrRoute.CloudAsr,
                route?.CloudModelId, EffectivePresetId(profile));
            var endpoint = AsrEndpoint(profile);
            targets.Add(new
            {
                key = "Asr",
                route = routeName,
                routeRevision,
                routeConsentVersion = route?.ConsentVersion,
                profile = ProfileIdentity(profile, endpoint),
                executionMode = NoteAsrExecutionMode.Realtime,
                presetVersion = PresetVersion(profile)
            });
            descriptions.Add(DescribeCloudTarget("转写", routeName, profile,
                PresetId(profile), PresetVersion(profile), routeRevision));
        }

        var text = FindProfile(textModelId, profiles);
        var vision = FindProfile(visionModelId, profiles) ?? text;
        targets.Add(new
        {
            key = "DerivedText",
            profile = ProfileIdentity(text, text?.BaseUrl),
            executionMode = NoteAsrExecutionMode.Realtime
        });
        descriptions.Add(DescribeModelTarget("纯文本整理", text));
        if (vision?.Id != text?.Id)
        {
            targets.Add(new
            {
                key = "DerivedVision",
                profile = ProfileIdentity(vision, vision?.BaseUrl),
                executionMode = NoteAsrExecutionMode.Realtime
            });
            descriptions.Add(DescribeModelTarget("含图片材料的整理", vision));
        }

        var revisionPayload = JsonSerializer.Serialize(new
        {
            runtime = runtime is null ? null : new
            {
                runtime.Revision,
                runtime.Enabled,
                runtime.TextModelId,
                runtime.VisionModelId,
                runtime.TranscriptionModelId,
                runtime.NoteOrganizationModelId
            },
            route = route is null ? null : new
            {
                route.Revision,
                configuredRoute = route.DefaultRoute,
                effectiveRoute = isLocal ? NoteAsrRoute.Local : routeName,
                route.LocalModelId,
                route.LocalModelRevision,
                route.CloudModelId,
                route.ConsentVersion
            },
            targets,
            costNotice = NoteProcessingAuthorizationRules.CostNotice
        });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(revisionPayload));
        var identity = bytes.AsSpan(0, 16).ToArray();
        identity[6] = (byte)((identity[6] & 0x0f) | 0x50);
        identity[8] = (byte)((identity[8] & 0x3f) | 0x80);
        return new(new Guid(identity), descriptions);
    }

    private string? AsrEndpoint(AiModelProfileEntity? profile)
    {
        if (profile is null) return null;
        try
        {
            var preset = presets.Resolve(profile.AsrPresetId);
            return AiAsrPresetRegistry.OperationEndpoint(
                preset.NormalizeEndpoint(profile.BaseUrl), preset.Descriptor);
        }
        catch (Exception error) when (error is DomainException or UriFormatException or ArgumentException)
        {
            return profile.BaseUrl;
        }
    }

    private string? PresetId(AiModelProfileEntity? profile)
    {
        try { return profile is null ? null : presets.Resolve(profile.AsrPresetId).Descriptor.Id; }
        catch (DomainException) { return profile?.AsrPresetId; }
    }

    private string? PresetVersion(AiModelProfileEntity? profile)
    {
        try { return profile is null ? null : presets.Resolve(profile.AsrPresetId).Descriptor.Version; }
        catch (DomainException) { return null; }
    }

    /// <summary>
    /// Only an enabled transcription-capable profile with a resolvable preset can decide the
    /// effective cloud route; anything else keeps the configured route instead of guessing.
    /// Mirrors <c>NoteAsrRoutingService.GlobalCloudProfileAsync</c>.
    /// </summary>
    private static string? EffectivePresetId(AiModelProfileEntity? profile) =>
        profile is { Enabled: true, AsrPresetId: not null } &&
        AiModelCapabilityFlags.Has(profile.Capabilities, AiModelCapability.Transcription)
            ? profile.AsrPresetId
            : null;

    private static AiModelProfileEntity? FindProfile(Guid? id,
        IReadOnlyDictionary<Guid, AiModelProfileEntity> profiles) =>
        id is { } value && profiles.TryGetValue(value, out var profile) ? profile : null;

    private static object? ProfileIdentity(AiModelProfileEntity? profile, string? endpoint) =>
        profile is null ? null : new
        {
            profile.Id,
            profile.Revision,
            profile.Name,
            profile.Model,
            endpoint,
            profile.Enabled,
            profile.Capabilities,
            profile.AsrPresetId
        };

    private static string DescribeCloudTarget(string label, string route,
        AiModelProfileEntity? profile, string? presetId, string? presetVersion,
        Guid? routeRevision)
    {
        var model = profile is null
            ? "账号默认模型未配置"
            : $"{profile.Name} ({profile.Model}) revision {profile.Revision}";
        var preset = presetId is null ? "预设未配置" : $"preset {presetId}/{presetVersion ?? "unknown"}";
        var routing = routeRevision is null ? "默认路由" : $"路由 revision {routeRevision}";
        return $"{label}：{route}；{model}；{preset}；执行模式 realtime；{routing}；endpoint 已纳入目标策略";
    }

    private static string DescribeModelTarget(string label, AiModelProfileEntity? profile) =>
        profile is null
            ? $"{label}：账号默认模型未配置"
            : $"{label}：{profile.Name} ({profile.Model}) revision {profile.Revision}；endpoint 已纳入目标策略";
}
