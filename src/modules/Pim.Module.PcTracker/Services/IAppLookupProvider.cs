namespace Pim.Module.PcTracker.Services;

public record AppLookupResult(
    string ProcessName,
    string DisplayName,
    string? CategoryPath,
    string? Productivity,
    string? Description,
    string? Icon,
    double Confidence,
    string Source = "online");

public interface IAppLookupProvider
{
    bool IsEnabled { get; }
    Task<AppLookupResult?> LookupAsync(string processName, CancellationToken ct = default);
}
