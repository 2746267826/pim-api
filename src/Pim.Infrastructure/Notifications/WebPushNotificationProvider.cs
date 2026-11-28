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
                    eventType = message.EventType,
                    categoryKey = message.CategoryKey
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
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
            return false;
        return !NonPublicNetworks.Any(network =>
            network.BaseAddress.AddressFamily == address.AddressFamily &&
            network.Contains(address));
    }

    // RFC 6890 / RFC 9637 特殊用途前缀：任何命中项都不作为推送目标地址，
    // 避免 loopback、私有网段、NAT64、benchmarking、documentation 等被当作公网出口。
    private static readonly IPNetwork[] NonPublicNetworks =
    [
        IPNetwork.Parse("0.0.0.0/8"),
        IPNetwork.Parse("10.0.0.0/8"),
        IPNetwork.Parse("100.64.0.0/10"),
        IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"),
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.0.0.0/24"),
        IPNetwork.Parse("192.0.2.0/24"),
        IPNetwork.Parse("192.88.99.0/24"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"),
        IPNetwork.Parse("198.51.100.0/24"),
        IPNetwork.Parse("203.0.113.0/24"),
        IPNetwork.Parse("224.0.0.0/4"),
        IPNetwork.Parse("240.0.0.0/4"),
        IPNetwork.Parse("::/128"),
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("64:ff9b::/96"),
        IPNetwork.Parse("64:ff9b:1::/48"),
        IPNetwork.Parse("100::/64"),
        IPNetwork.Parse("2001::/23"),
        IPNetwork.Parse("2001:2::/48"),
        IPNetwork.Parse("2001:db8::/32"),
        IPNetwork.Parse("2001:10::/28"),
        IPNetwork.Parse("2001:20::/28"),
        IPNetwork.Parse("2002::/16"),
        IPNetwork.Parse("3fff::/20"),
        IPNetwork.Parse("5f00::/16"),
        IPNetwork.Parse("fc00::/7"),
        IPNetwork.Parse("fe80::/10"),
        IPNetwork.Parse("fec0::/10"),
        IPNetwork.Parse("ff00::/8")
    ];
}
