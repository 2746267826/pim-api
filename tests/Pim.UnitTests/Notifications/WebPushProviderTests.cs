using System.Net;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pim.Infrastructure.Notifications;
using Xunit;

namespace Pim.UnitTests.Notifications;

public sealed class WebPushProviderTests
{
    [Fact]
    public async Task SendsEncryptedAes128GcmRequest_WithVerifiableVapidAuthorization()
    {
        var keys = TestKeys();
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var provider = NewProvider(keys, handler);
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();

        var result = await provider.SendAsync(
            new PushSubscriptionSecret(
                "https://fcm.googleapis.com/wp/test-endpoint",
                WebPushCrypto.Base64UrlDecode(p256Dh),
                WebPushCrypto.Base64UrlDecode(auth)),
            new PushNotificationMessage(
                "任务已完成",
                "任务已标记为完成。",
                "/tasks",
                Guid.Parse("22222222-2222-4222-8222-222222222222"),
                "calendar.task.completed"),
            CancellationToken.None);

        Assert.Equal(PushSendOutcome.Delivered, result.Outcome);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://fcm.googleapis.com/wp/test-endpoint", request.Uri.AbsoluteUri);
        Assert.Contains("aes128gcm", request.ContentEncoding);
        Assert.Contains("vapid t=", request.Authorization);
        Assert.True(request.Body.Length > 86);
        Assert.Equal(4096u, BinaryPrimitives.ReadUInt32BigEndian(request.Body.AsSpan(16, 4)));
        Assert.Equal(65, request.Body[20]);

        var authorization = request.Authorization["vapid t=".Length..];
        var comma = authorization.LastIndexOf(",k=", StringComparison.Ordinal);
        var token = authorization[..comma];
        var publicKey = authorization[(comma + 3)..];
        var parts = token.Split('.');
        Assert.Equal(3, parts.Length);
        var signingInput = Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}");
        using var ecdsa = ECDsa.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = WebPushCrypto.Base64UrlDecode(publicKey)[1..33],
                Y = WebPushCrypto.Base64UrlDecode(publicKey)[33..65]
            }
        });
        Assert.True(ecdsa.VerifyData(
            signingInput,
            WebPushCrypto.Base64UrlDecode(parts[2]),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        using var claims = JsonDocument.Parse(WebPushCrypto.Base64UrlDecode(parts[1]));
        Assert.Equal("https://fcm.googleapis.com", claims.RootElement.GetProperty("aud").GetString());
        Assert.Equal("mailto:test@example.invalid", claims.RootElement.GetProperty("sub").GetString());
        Assert.True(claims.RootElement.GetProperty("exp").GetInt64() > DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, PushSendOutcome.SubscriptionGone)]
    [InlineData(HttpStatusCode.Gone, PushSendOutcome.SubscriptionGone)]
    [InlineData(HttpStatusCode.TooManyRequests, PushSendOutcome.RetryableFailure)]
    [InlineData(HttpStatusCode.InternalServerError, PushSendOutcome.RetryableFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, PushSendOutcome.RetryableFailure)]
    [InlineData(HttpStatusCode.BadRequest, PushSendOutcome.PermanentFailure)]
    [InlineData(HttpStatusCode.Unauthorized, PushSendOutcome.PermanentFailure)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, PushSendOutcome.PermanentFailure)]
    public async Task MapsPushServiceStatuses(HttpStatusCode status, PushSendOutcome expected)
    {
        var keys = TestKeys();
        var provider = NewProvider(keys, new RecordingHandler(status));
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();

        var result = await provider.SendAsync(
            new PushSubscriptionSecret(
                "https://updates.push.services.mozilla.com/wpush/v2/test",
                WebPushCrypto.Base64UrlDecode(p256Dh),
                WebPushCrypto.Base64UrlDecode(auth)),
            new PushNotificationMessage("t", "b", null, Guid.NewGuid(), "notification.test"),
            CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task RejectsNonWhitelistedOrPrivateEndpoints_WithoutSending()
    {
        var keys = TestKeys();
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var provider = NewProvider(keys, handler);
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();

        foreach (var endpoint in new[]
                 {
                     "http://fcm.googleapis.com/wp/insecure",
                     "https://127.0.0.1/wp/private",
                     "https://fcm.googleapis.com.evil.invalid/wp/rebind",
                     "https://user@fcm.googleapis.com/wp/userinfo"
                 })
        {
            var result = await provider.SendAsync(
                new PushSubscriptionSecret(
                    endpoint,
                    WebPushCrypto.Base64UrlDecode(p256Dh),
                    WebPushCrypto.Base64UrlDecode(auth)),
                new PushNotificationMessage("t", "b", null, Guid.NewGuid(), "notification.test"),
                CancellationToken.None);
            Assert.Equal(PushSendOutcome.PermanentFailure, result.Outcome);
            Assert.Equal("endpoint_invalid", result.ErrorCode);
        }
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task UnconfiguredProviderDoesNotCallPushService()
    {
        var handler = new RecordingHandler(HttpStatusCode.Created);
        var provider = NewProvider(
            new TestVapidKeys(string.Empty, string.Empty, Array.Empty<byte>(), Array.Empty<byte>()),
            handler);
        var (p256Dh, auth) = NotificationTestSupport.SubscriptionKeys();

        var result = await provider.SendAsync(
            new PushSubscriptionSecret(
                "https://fcm.googleapis.com/wp/test",
                WebPushCrypto.Base64UrlDecode(p256Dh),
                WebPushCrypto.Base64UrlDecode(auth)),
            new PushNotificationMessage("t", "b", null, Guid.NewGuid(), "notification.test"),
            CancellationToken.None);

        Assert.Equal(PushSendOutcome.PermanentFailure, result.Outcome);
        Assert.Equal("provider_not_configured", result.ErrorCode);
        Assert.Empty(handler.Requests);
    }

    private static WebPushNotificationProvider NewProvider(
        TestVapidKeys keys,
        RecordingHandler handler) =>
        new(
            new HttpClient(handler),
            Options.Create(new WebPushOptions
            {
                PublicKey = keys.PublicKey,
                PrivateKey = keys.PrivateKey,
                Subject = "mailto:test@example.invalid",
                AllowedEndpointHosts =
                [
                    "fcm.googleapis.com",
                    "updates.push.services.mozilla.com"
                ]
            }),
            NullLogger<WebPushNotificationProvider>.Instance);

    private static TestVapidKeys TestKeys()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var parameters = ecdsa.ExportParameters(true);
        var publicKey = new byte[65];
        publicKey[0] = 0x04;
        parameters.Q.X!.CopyTo(publicKey, 1);
        parameters.Q.Y!.CopyTo(publicKey, 33);
        return new TestVapidKeys(
            NotificationTestSupport.Base64Url(publicKey),
            NotificationTestSupport.Base64Url(parameters.D!),
            publicKey,
            parameters.D!);
    }

    private sealed record TestVapidKeys(
        string PublicKey,
        string PrivateKey,
        byte[] PublicBytes,
        byte[] PrivateBytes);

    private sealed record CapturedRequest(
        Uri Uri,
        string Authorization,
        IReadOnlyList<string> ContentEncoding,
        byte[] Body);

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? Array.Empty<byte>()
                : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new CapturedRequest(
                request.RequestUri!,
                request.Headers.Authorization?.ToString() ?? string.Empty,
                request.Content?.Headers.ContentEncoding.ToArray() ?? [],
                body));
            return new HttpResponseMessage(status);
        }
    }
}
