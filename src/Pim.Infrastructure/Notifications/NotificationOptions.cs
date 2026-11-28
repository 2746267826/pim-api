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
    /// <summary>
    /// Minimum lease for a claimed event/delivery row. Delivery leases are additionally
    /// derived from the worst-case serial provider batch below.
    /// </summary>
    public int LockSeconds { get; set; } = 120;
    /// <summary>
    /// Upper bound for one provider call made while the delivery transaction holds row locks.
    /// The full out-of-transaction send redesign is tracked separately; this bound keeps a
    /// hung provider from blocking revocation indefinitely.
    /// </summary>
    public int ProviderTimeoutSeconds { get; set; } = 15;
    /// <summary>
    /// Extra time on the derived delivery lease for lock waits, commits and clock skew.
    /// </summary>
    public int LeaseSafetyMarginSeconds { get; set; } = 60;

    /// <summary>
    /// Lease used when a delivery batch is claimed. It must cover the worst case of
    /// <see cref="DeliveryBatchSize"/> serial provider calls at
    /// <see cref="ProviderTimeoutSeconds"/> each, plus fixed per-call overhead and the
    /// configured safety margin; otherwise a later item in the same batch can expire its
    /// claim and be delivered by a second worker.
    /// </summary>
    public TimeSpan DeliveryClaimLease
    {
        get
        {
            const int perCallOverheadSeconds = 5;
            var batchBudget = (long)Math.Max(1, DeliveryBatchSize) *
                (Math.Max(1, ProviderTimeoutSeconds) + perCallOverheadSeconds);
            var seconds = Math.Max(
                Math.Max(10, LockSeconds),
                batchBudget + Math.Max(0, LeaseSafetyMarginSeconds));
            return TimeSpan.FromSeconds(Math.Min(seconds, 24 * 60 * 60));
        }
    }
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
