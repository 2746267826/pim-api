namespace Pim.Infrastructure.Notifications;

public sealed class NotificationDispatcherOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 5;
    public int NoteScanIntervalSeconds { get; set; } = 60;
    public int ReminderScanIntervalSeconds { get; set; } = 15;
    public int FanoutBatchSize { get; set; } = 50;
    public int DeliveryBatchSize { get; set; } = 20;
    public int MaxAttempts { get; set; } = 5;
    public int BaseDelaySeconds { get; set; } = 30;
    public int MaxDelaySeconds { get; set; } = 3600;
    public int LockSeconds { get; set; } = 120;
}

public sealed class WebPushOptions
{
    public string? PublicKey { get; set; }
    public string? PrivateKey { get; set; }
    public string Subject { get; set; } = "mailto:admin@example.invalid";
    public int TtlSeconds { get; set; } = 300;
    public int TimeoutSeconds { get; set; } = 10;
    public string[] AllowedEndpointHosts { get; set; } =
    [
        "fcm.googleapis.com",
        "android.googleapis.com",
        "updates.push.services.mozilla.com",
        "push.services.mozilla.com",
        "notify.windows.com",
        "push.apple.com"
    ];
}
