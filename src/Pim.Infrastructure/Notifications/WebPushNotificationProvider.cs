using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pim.Infrastructure.Notifications;

public sealed class WebPushNotificationProvider : IWebPushTransport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly WebPushOptions _options;
    private readonly ILogger<WebPushNotificationProvider> _logger;

    public WebPushNotificationProvider(
        HttpClient http,
        IOptions<WebPushOptions> options,
        ILogger<WebPushNotificationProvider> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
        IsConfigured = WebPushCrypto.TryDecodeKeyPair(_options, out _, out _);
    }

    public bool IsConfigured { get; }

    public string? PublicKey => IsConfigured ? _options.PublicKey : null;

    public async Task<PushSendResult> SendAsync(
        PushSubscriptionSecret subscription,
        PushNotificationMessage message,
        CancellationToken ct)
    {
        if (!IsConfigured)
            return PushSendResult.Permanent("provider_not_configured");
        if (!WebPushEndpointValidator.TryValidate(
                subscription.Endpoint, _options.AllowedEndpointHosts, out var endpoint, out _))
            return PushSendResult.Permanent("endpoint_invalid");

        byte[] body;
        string authorization;
        try
        {
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                title = message.Title,
                body = message.Body,
                tag = message.NotificationId.ToString("N"),
                data = new
                {
                    url = string.IsNullOrWhiteSpace(message.DetailUrl)
                        ? "/settings/notifications"
                        : message.DetailUrl,
                    eventId = message.NotificationId.ToString("D"),
                    eventType = message.EventType
                }
            }, Json);
            body = WebPushCrypto.Encrypt(
                payload,
                subscription.P256Dh,
                subscription.Auth,
                out _);
            authorization = WebPushCrypto.CreateVapidAuthorization(
                _options, endpoint, DateTimeOffset.UtcNow);
        }
        catch (Exception error) when (error is CryptographicException or JsonException)
        {
            _logger.LogWarning("Web push payload preparation failed ({ErrorType})", error.GetType().Name);
            return PushSendResult.Permanent("payload_invalid");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Content = new ByteArrayContent(body);
        request.Content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        request.Headers.TryAddWithoutValidation("TTL", _options.TtlSeconds.ToString());
        request.Headers.TryAddWithoutValidation("Urgency", "normal");
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        try
        {
            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (response.IsSuccessStatusCode)
                return PushSendResult.Delivered();
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
                return PushSendResult.Gone();
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return PushSendResult.Retryable("push_service_429");
            if ((int)response.StatusCode >= 500)
                return PushSendResult.Retryable("push_service_5xx");
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                or HttpStatusCode.Forbidden or HttpStatusCode.RequestEntityTooLarge)
                return PushSendResult.Permanent("push_rejected");
            return PushSendResult.Retryable("push_service_unexpected");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return PushSendResult.Retryable("push_timeout");
        }
        catch (HttpRequestException error)
        {
            _logger.LogWarning("Web push transport failed ({ErrorType})", error.GetType().Name);
            return PushSendResult.Retryable("push_transport_error");
        }
    }

    public static SocketsHttpHandler CreateHttpHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectCallback = ConnectPublicEndpointAsync
    };

    private static async ValueTask<Stream> ConnectPublicEndpointAsync(
        SocketsHttpConnectionContext context,
        CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
        var target = addresses.FirstOrDefault(IsPublicAddress)
            ?? throw new HttpRequestException("push_endpoint_not_public");
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(
                new IPEndPoint(target, context.DnsEndPoint.Port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            if ((bytes[0] & 0xfe) == 0xfc) return false;
            if (bytes[0] == 0xfe && (bytes[1] & 0xc0) == 0x80) return false;
            if (bytes[0] == 0xff) return false;
            return true;
        }

        var v4 = address.GetAddressBytes();
        return v4[0] switch
        {
            0 or 10 or 127 => false,
            100 when v4[1] >= 64 && v4[1] <= 127 => false,
            169 when v4[1] == 254 => false,
            172 when v4[1] >= 16 && v4[1] <= 31 => false,
            192 when v4[1] == 168 => false,
            198 when v4[1] is 18 or 19 => false,
            >= 224 => false,
            _ => true
        };
    }
}
