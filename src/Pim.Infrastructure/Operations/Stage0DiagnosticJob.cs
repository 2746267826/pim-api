using Microsoft.Extensions.Logging;

namespace Pim.Infrastructure.Operations;

public sealed class Stage0DiagnosticJob
{
    private readonly ILogger<Stage0DiagnosticJob> _logger;

    public Stage0DiagnosticJob(ILogger<Stage0DiagnosticJob> logger)
    {
        _logger = logger;
    }

    public Task RunAsync()
    {
        _logger.LogInformation("Stage0 diagnostic job executed at {ExecutedAt}", DateTimeOffset.UtcNow);
        return Task.CompletedTask;
    }
}
