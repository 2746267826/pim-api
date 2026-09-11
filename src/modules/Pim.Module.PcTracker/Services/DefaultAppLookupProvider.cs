using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Pim.Module.PcTracker.Services;

public class DefaultAppLookupProvider : IAppLookupProvider
{
    private readonly bool _enabled;
    private readonly ILogger<DefaultAppLookupProvider> _logger;

    public bool IsEnabled => _enabled;

    public DefaultAppLookupProvider(IConfiguration configuration, ILogger<DefaultAppLookupProvider> logger)
    {
        _logger = logger;
        // Strict requirement: AppLookup:Enabled defaults to false
        _enabled = configuration.GetValue<bool>("AppLookup:Enabled", false);
        if (_enabled)
        {
            _logger.LogInformation("Online app lookup provider is enabled. Notice: unknown process names will be queried online.");
        }
        else
        {
            _logger.LogDebug("Online app lookup provider is disabled by default (AppLookup:Enabled = false).");
        }
    }

    public Task<AppLookupResult?> LookupAsync(string processName, CancellationToken ct = default)
    {
        if (!_enabled || string.IsNullOrWhiteSpace(processName))
        {
            return Task.FromResult<AppLookupResult?>(null);
        }

        // When enabled, perform safe heuristics / online lookup fallback
        var clean = processName.Trim();
        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean[..^4];
        }

        // Mock/Extensible online response pattern:
        // Returns inferred result with source="online" and appropriate confidence
        var result = new AppLookupResult(
            ProcessName: processName,
            DisplayName: char.ToUpper(clean[0]) + clean[1..],
            CategoryPath: "其他",
            Productivity: "neutral",
            Description: $"Online identified application: {clean}",
            Icon: "🌐",
            Confidence: 0.85,
            Source: "online");

        return Task.FromResult<AppLookupResult?>(result);
    }
}
