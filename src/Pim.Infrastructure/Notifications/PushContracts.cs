namespace Pim.Infrastructure.Notifications;

public sealed record PushSubscriptionSecret(
    string Endpoint,
    byte[] P256Dh,
    byte[] Auth);

public sealed record PushNotificationMessage(
    string Title,
    string Body,
    string? DetailUrl,
    Guid NotificationId,
    string EventType);

public enum PushSendOutcome
{
    Delivered,
    RetryableFailure,
    SubscriptionGone,
    PermanentFailure
}

public sealed record PushSendResult(
    PushSendOutcome Outcome,
    string ErrorCode,
    string SafeMessage)
{
    public static PushSendResult Delivered() =>
        new(PushSendOutcome.Delivered, string.Empty, string.Empty);

    public static PushSendResult Retryable(string code) =>
        new(PushSendOutcome.RetryableFailure, code, "推送服务暂时不可用，稍后自动重试");

    public static PushSendResult Gone(string code = "subscription_gone") =>
        new(PushSendOutcome.SubscriptionGone, code, "推送订阅已失效，请重新启用浏览器推送");

    public static PushSendResult Permanent(string code) =>
        new(PushSendOutcome.PermanentFailure, code, "推送服务拒绝了该条通知");
}

public interface IWebPushTransport
{
    bool IsConfigured { get; }

    string? PublicKey { get; }

    Task<PushSendResult> SendAsync(
        PushSubscriptionSecret subscription,
        PushNotificationMessage message,
        CancellationToken ct);
}
