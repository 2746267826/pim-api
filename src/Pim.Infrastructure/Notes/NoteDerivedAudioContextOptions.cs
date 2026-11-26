namespace Pim.Infrastructure.Notes;

/// <summary>
/// Explicit opt-in for models that may receive raw note audio. Empty by default so the
/// existing ASR transcript path remains the only path for every configured provider.
/// </summary>
public sealed class NoteDerivedAudioContextOptions
{
    internal const long DefaultMaxTotalBytes = 256L * 1024 * 1024;
    internal const long HardMaxTotalBytes = 1024L * 1024 * 1024;
    internal const int DefaultMaxBatches = 32;
    internal const int HardMaxBatches = 64;
    internal const int DefaultMaxParallelCalls = 2;
    internal const int HardMaxParallelCalls = 4;

    public string[] ModelIds { get; set; } = [];
    public string[] ModelNames { get; set; } = [];
    /// <summary>Total raw-audio budget admitted for one derived task.</summary>
    public long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;
    /// <summary>Maximum number of provider calls used to cover the raw audio.</summary>
    public int MaxBatches { get; set; } = DefaultMaxBatches;
    /// <summary>Maximum concurrent provider calls inside one derived task.</summary>
    public int MaxParallelCalls { get; set; } = DefaultMaxParallelCalls;
    /// <summary>
    /// Per-call raw-audio budget. Production may lower it, never raise it above the
    /// hard 8 MiB request boundary enforced by AiTextCallService.
    /// </summary>
    public int MaxBatchBytes { get; set; } = NoteDerivedAudioContext.MaxBytes;

    public bool IsAllowed(Guid modelId, string model, string name) =>
        ModelIds.Any(value => Guid.TryParse(value, out var id) && id == modelId) ||
        ModelNames.Any(value => string.Equals(value, model, StringComparison.Ordinal) ||
            string.Equals(value, name, StringComparison.Ordinal));

    public bool IsValid() =>
        ModelIds.Length <= 32 && ModelNames.Length <= 32 &&
        ModelIds.All(value => Guid.TryParse(value, out var id) && id != Guid.Empty) &&
        ModelNames.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
            !value.Any(char.IsControl)) &&
        MaxTotalBytes is >= NoteDerivedAudioContext.MaxBytes and <= HardMaxTotalBytes &&
        MaxBatches is >= 1 and <= HardMaxBatches &&
        MaxParallelCalls is >= 1 and <= HardMaxParallelCalls &&
        MaxBatchBytes is >= 1024 and <= NoteDerivedAudioContext.MaxBytes;
}
