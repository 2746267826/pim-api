using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pim.Infrastructure.Notifications;

public sealed class NotificationDispatchBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<NotificationDispatcherOptions> options,
    ILogger<NotificationDispatchBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Notification dispatcher is disabled by configuration");
            return;
        }

        var poll = TimeSpan.FromSeconds(Math.Max(1, options.Value.PollIntervalSeconds));
        var noteInterval = TimeSpan.FromSeconds(Math.Max(10, options.Value.NoteScanIntervalSeconds));
        var reminderInterval =
            TimeSpan.FromSeconds(Math.Max(5, options.Value.ReminderScanIntervalSeconds));
        var nextNoteScan = DateTimeOffset.UtcNow.AddSeconds(5);
        var nextReminderScan = DateTimeOffset.UtcNow;

        using var timer = new PeriodicTimer(poll);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var worker = scope.ServiceProvider.GetRequiredService<NotificationDeliveryWorker>();
                await worker.RunOnceAsync(stoppingToken);

                var now = DateTimeOffset.UtcNow;
                var scanner = scope.ServiceProvider.GetRequiredService<NotificationEventScanner>();
                if (now >= nextReminderScan)
                {
                    await scanner.ScanDueRemindersAsync(stoppingToken);
                    nextReminderScan = now.Add(reminderInterval);
                }
                if (now >= nextNoteScan)
                {
                    await scanner.ScanCompletedNotesAsync(stoppingToken);
                    nextNoteScan = now.Add(noteInterval);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception error)
            {
                logger.LogWarning(
                    "Notification dispatcher iteration failed ({ErrorType})",
                    error.GetType().Name);
            }
        }
    }
}
