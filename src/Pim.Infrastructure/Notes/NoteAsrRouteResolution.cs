using Pim.Core.Notes;
using Pim.Infrastructure.Ai.Asr.Mimo;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Single source for "which cloud route actually carries the audio". A per-account override keeps
/// its configured route; without one the preset of the global transcription profile decides. The
/// task builder, the route settings and the processing target policy all resolve through here so
/// the displayed target can never disagree with the route that will be charged.
/// </summary>
internal static class NoteAsrRouteResolution
{
    /// <param name="configuredRoute">The stored route, or the legacy cloud fallback when unset.</param>
    /// <param name="cloudModelId">The per-account cloud override, if any.</param>
    /// <param name="effectivePresetId">
    /// Preset of the profile that would receive the audio; null when it cannot be resolved, in
    /// which case the configured route is reported unchanged instead of being guessed.
    /// </param>
    internal static string EffectiveCloudRoute(string configuredRoute, Guid? cloudModelId,
        string? effectivePresetId) =>
        cloudModelId is null && effectivePresetId is not null
            ? effectivePresetId == MimoMultimodalPreset.PresetId
                ? NoteAsrRoute.CloudMultimodal
                : NoteAsrRoute.CloudAsr
            : configuredRoute;
}
