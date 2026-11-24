namespace Pim.Infrastructure.Notes;

/// <summary>
/// Explicit opt-in for models that may receive raw note audio. Empty by default so the
/// existing ASR transcript path remains the only path for every configured provider.
/// </summary>
public sealed class NoteDerivedAudioContextOptions
{
    public string[] ModelIds { get; set; } = [];
    public string[] ModelNames { get; set; } = [];

    public bool IsAllowed(Guid modelId, string model, string name) =>
        ModelIds.Any(value => Guid.TryParse(value, out var id) && id == modelId) ||
        ModelNames.Any(value => string.Equals(value, model, StringComparison.Ordinal) ||
            string.Equals(value, name, StringComparison.Ordinal));

    public bool IsValid() =>
        ModelIds.Length <= 32 && ModelNames.Length <= 32 &&
        ModelIds.All(value => Guid.TryParse(value, out var id) && id != Guid.Empty) &&
        ModelNames.All(value => !string.IsNullOrWhiteSpace(value) && value.Length <= 128 &&
            !value.Any(char.IsControl));
}
