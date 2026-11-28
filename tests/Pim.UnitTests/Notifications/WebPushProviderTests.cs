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

    [Fact]
    public void Aes128GcmPayload_DecryptsWithTheUserAgentPrivateKey()
    {
        using var userAgent = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var userAgentParameters = userAgent.ExportParameters(true);
        var userAgentPublicKey = EncodePoint(userAgentParameters);
        var authSecret = RandomNumberGenerator.GetBytes(16);
        var payload = Encoding.UTF8.GetBytes(
            """{"title":"任务已完成","body":"RFC8291 回环","data":{"url":"/tasks"}}""");

        var body = WebPushCrypto.Encrypt(
            payload, userAgentPublicKey, authSecret, out var applicationServerPublicKey);
        Assert.Equal(65, body[20]);
        var keyId = body[21..86];
        Assert.Equal(applicationServerPublicKey, keyId);

        using var applicationServer = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = keyId[1..33].ToArray(),
                Y = keyId[33..65].ToArray()
            }
        });
        var sharedSecret = userAgent.DeriveKeyMaterial(applicationServer.PublicKey);
        var authInfo = Concat("WebPush: info\0"u8.ToArray(), userAgentPublicKey, keyId);
        var ikm = HkdfExpand(HkdfExtract(authSecret, sharedSecret), authInfo, 32);
        var salt = body[..16];
        var prk = HkdfExtract(salt, ikm);
        var cek = HkdfExpand(prk, "Content-Encoding: aes128gcm\0"u8.ToArray(), 16);
        var nonce = HkdfExpand(prk, "Content-Encoding: nonce\0"u8.ToArray(), 12);
        var ciphertext = body[86..^16];
        var tag = body[^16..];
        var plaintext = new byte[ciphertext.Length];
        using (var aes = new AesGcm(cek, 16))
            aes.Decrypt(nonce, ciphertext, tag, plaintext);

        Assert.Equal(0x02, plaintext[^1]);
        Assert.Equal(payload, plaintext[..^1]);
    }

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("192.0.0.1")]
    [InlineData("192.0.2.10")]
    [InlineData("192.88.99.1")]
    [InlineData("198.51.100.10")]
    [InlineData("203.0.113.10")]
    [InlineData("2001:db8::1")]
    [InlineData("2001:2::1")]
    [InlineData("2001:10::1")]
    [InlineData("fec0::1")]
    [InlineData("64:ff9b::7f00:1")]
    public void SpecialPurposeAddressesAreNotPublic(string address)
    {
        Assert.False(WebPushNotificationProvider.IsPublicAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("2606:4700:4700::1111")]
    public void InternetAddressesRemainPublic(string address)
    {
        Assert.True(WebPushNotificationProvider.IsPublicAddress(IPAddress.Parse(address)));
    }

    private static byte[] EncodePoint(ECParameters parameters)
    {
        var key = new byte[65];
        key[0] = 0x04;
        parameters.Q.X!.CopyTo(key, 1);
        parameters.Q.Y!.CopyTo(key, 33);
        return key;
    }

    private static byte[] Concat(params byte[][] values)
    {
        var result = new byte[values.Sum(value => value.Length)];
        var offset = 0;
        foreach (var value in values)
        {
            value.CopyTo(result, offset);
            offset += value.Length;
        }
        return result;
    }

    private static byte[] HkdfExtract(byte[] salt, byte[] ikm)
    {
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(ikm);
    }

    private static byte[] HkdfExpand(byte[] prk, byte[] info, int length)
    {
        var output = new byte[length];
        var previous = Array.Empty<byte>();
        var offset = 0;
        byte counter = 1;
        while (offset < length)
        {
            var input = new byte[previous.Length + info.Length + 1];
            previous.CopyTo(input, 0);
            info.CopyTo(input, previous.Length);
            input[^1] = counter++;
            using var hmac = new HMACSHA256(prk);
            previous = hmac.ComputeHash(input);
            var take = Math.Min(previous.Length, length - offset);
            previous.AsSpan(0, take).CopyTo(output.AsSpan(offset));
            offset += take;
        }
        return output;
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
