using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Pim.Core.Exceptions;

namespace Pim.Infrastructure.Ai.Asr;

/// <summary>
/// Declares the expected audio format and, optionally, the exact digest of the frozen bytes.
/// The URL is never trusted to establish either value.
/// </summary>
public sealed record RemoteAudioExpectation(string Format, string? Sha256 = null);

/// <summary>Safe-to-log source metadata. Path, query and fragment are deliberately absent.</summary>
public sealed record RemoteAudioProvenance(
    string Origin,
    string Format,
    string Sha256,
    long ByteCount,
    string? ContentType,
    DateTimeOffset FetchedAt)
{
    public override string ToString() =>
        $"RemoteAudioProvenance {{ Origin = {Origin}, Format = {Format}, Bytes = {ByteCount}, Sha256 = {Sha256} }}";
}

/// <summary>Frozen response bytes plus provenance. ToString never includes audio bytes or a signed URL.</summary>
public sealed record RemoteAudioDownload(ReadOnlyMemory<byte> Bytes, RemoteAudioProvenance Provenance)
{
    public override string ToString() => Provenance.ToString();
}

/// <summary>Hard security budgets for public URL audio. MaxBytes cannot exceed 100 MiB.</summary>
public sealed record RemoteAudioFetchLimits
{
    public const long HardMaxBytes = 100L * 1024 * 1024;
    public static readonly TimeSpan HardMaxDnsTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan HardMaxHeadTimeout = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan HardMaxGetTimeout = TimeSpan.FromMinutes(10);
    public long MaxBytes { get; init; } = HardMaxBytes;
    public TimeSpan DnsTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeadTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan GetTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public static RemoteAudioFetchLimits Default { get; } = new();

    internal void Validate()
    {
        if (MaxBytes <= 0 || MaxBytes > HardMaxBytes)
            throw new DomainException(40001, "URL音频大小上限必须在1字节到100MiB之间");
        if (DnsTimeout <= TimeSpan.Zero || DnsTimeout > HardMaxDnsTimeout ||
            HeadTimeout <= TimeSpan.Zero || HeadTimeout > HardMaxHeadTimeout ||
            GetTimeout <= TimeSpan.Zero || GetTimeout > HardMaxGetTimeout)
            throw new DomainException(40001, "URL音频DNS、HEAD与GET超时必须为有限正数且不超过安全上限");
    }
}

/// <summary>
/// Fetches a caller-supplied public HTTPS audio URL without credentials, cookies, proxies or
/// redirects. DNS answers are validated once and the HTTPS connection is pinned to those
/// approved addresses, preventing a later DNS rebinding from changing the destination.
/// </summary>
public interface IRemoteAudioFetcher
{
    Task<RemoteAudioDownload> FetchAsync(Uri url, RemoteAudioExpectation expectation, CancellationToken ct);
}

public sealed class SecureRemoteAudioFetcher : IRemoteAudioFetcher
{
    public const int MaxUrlChars = 8_192;
    public static readonly TimeSpan DefaultOperationBudget =
        RemoteAudioFetchLimits.Default.DnsTimeout +
        RemoteAudioFetchLimits.Default.HeadTimeout +
        RemoteAudioFetchLimits.Default.GetTimeout;

    private readonly IRemoteAudioDnsResolver _dns;
    private readonly IRemoteAudioHttpClientFactory _clients;
    private readonly RemoteAudioFetchLimits _limits;
    private readonly TimeProvider _time;

    public SecureRemoteAudioFetcher()
        : this(new SystemRemoteAudioDnsResolver(), new PinnedRemoteAudioHttpClientFactory(),
            RemoteAudioFetchLimits.Default, TimeProvider.System)
    {
    }

    internal SecureRemoteAudioFetcher(IRemoteAudioDnsResolver dns, IRemoteAudioHttpClientFactory clients,
        RemoteAudioFetchLimits limits, TimeProvider? time = null)
    {
        _dns = dns;
        _clients = clients;
        _limits = limits;
        _limits.Validate();
        _time = time ?? TimeProvider.System;
    }

    public async Task<RemoteAudioDownload> FetchAsync(Uri url, RemoteAudioExpectation expectation,
        CancellationToken ct)
    {
        try { return await FetchCoreAsync(url, expectation, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is not DomainException)
        {
            // Never include the signed URL: Exception.ToString here only shows this safe message.
            _ = error;
            throw TransportFailure();
        }
    }

    private async Task<RemoteAudioDownload> FetchCoreAsync(Uri url, RemoteAudioExpectation expectation,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var format = NormalizeFormat(expectation.Format);
        var expectedSha256 = NormalizeDigest(expectation.Sha256);
        ValidateUrl(url);

        var addresses = await RunPhaseAsync("DNS解析", _limits.DnsTimeout,
            token => _dns.ResolveAsync(url.IdnHost, token), ct);

        if (addresses.Count == 0)
            throw new DomainException(50206, "URL音频域名没有可用公网地址，本次未发起下载");
        foreach (var address in addresses)
        {
            if (!IsPublicAddress(address))
                throw new DomainException(40302, "URL音频域名解析到非公网地址，已拒绝下载");
        }

        var endpoint = new DnsEndPoint(url.IdnHost, url.Port);
        using var http = _clients.Create(addresses, endpoint);
        if (http.Timeout != Timeout.InfiniteTimeSpan)
            throw new DomainException(50001, "URL音频HttpClient必须关闭隐式超时，由分阶段受限超时管理");

        string? contentType;
        using (var head = CreateRequest(HttpMethod.Head, url))
        using (var response = await SendAsync(http, head, _limits.HeadTimeout, "HEAD", ct))
        {
            if (response.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
            {
                EnsureSuccess(response, "HEAD");
                contentType = ValidateContentType(response.Content.Headers.ContentType?.ToString());
                RejectDeclaredOversize(response.Content.Headers.ContentLength);
            }
            else
            {
                contentType = null;
            }
        }

        using var get = CreateRequest(HttpMethod.Get, url);
        using var getResponse = await SendAsync(http, get, _limits.GetTimeout, "GET", ct);
        EnsureSuccess(getResponse, "GET");
        contentType = ValidateContentType(getResponse.Content.Headers.ContentType?.ToString()) ??
                      contentType;
        RejectDeclaredOversize(getResponse.Content.Headers.ContentLength);

        var bytes = await ReadBoundedAsync(getResponse, ct);
        if (bytes.Length == 0)
            throw new DomainException(40001, "URL音频内容为空，本次未保存");
        if (!RemoteAudioSignature.Matches(format, bytes.Span))
            throw new DomainException(40001, "URL音频文件签名与声明格式不一致");

        var digest = Convert.ToHexString(SHA256.HashData(bytes.Span)).ToLowerInvariant();
        if (expectedSha256 is not null && !string.Equals(digest, expectedSha256, StringComparison.Ordinal))
            throw new DomainException(40901, "URL音频SHA-256与调用方声明不一致");

        var provenance = new RemoteAudioProvenance(
            url.GetLeftPart(UriPartial.Authority), format, digest, bytes.Length,
            contentType, _time.GetUtcNow());
        return new RemoteAudioDownload(bytes, provenance);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient http, HttpRequestMessage request,
        TimeSpan timeout, string phase, CancellationToken ct)
    {
        var response = await RunPhaseAsync(phase, timeout,
            token => http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token), ct);
        if ((int)response.StatusCode is >= 300 and <= 399)
        {
            response.Dispose();
            throw new DomainException(50204, $"URL音频{phase}返回重定向，已拒绝跟随");
        }
        return response;
    }

    private async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(HttpResponseMessage response,
        CancellationToken ct)
    {
        return await RunPhaseAsync("GET响应读取", _limits.GetTimeout, async token =>
        {
            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var content = new MemoryStream();
            var buffer = new byte[64 * 1024];
            long total = 0;
            while (true)
            {
                var read = await stream.ReadAsync(buffer.AsMemory(), token);
                if (read == 0) break;
                total += read;
                if (total > _limits.MaxBytes)
                    throw new DomainException(41301, "URL音频超过100MiB或调用方更小上限，已停止下载");
                await content.WriteAsync(buffer.AsMemory(0, read), token);
            }
            return content.GetBuffer().AsMemory(0, (int)content.Length);
        }, ct);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/*"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        return request;
    }

    private static void ValidateUrl(Uri? url)
    {
        if (url is null || !url.IsAbsoluteUri || !url.Scheme.Equals(Uri.UriSchemeHttps,
                StringComparison.OrdinalIgnoreCase))
            throw new DomainException(40001, "URL音频只允许绝对HTTPS地址");
        var original = url.OriginalString;
        if (original.Length > MaxUrlChars)
            throw new DomainException(40001, "URL音频地址超过8192字符上限");
        if (original.Any(char.IsControl) ||
            original.Contains("%0d", StringComparison.OrdinalIgnoreCase) ||
            original.Contains("%0a", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(40001, "URL音频禁止CR/LF或其他控制字符");
        if (url.UserInfo.Length != 0)
            throw new DomainException(40302, "URL音频禁止包含用户名或密码");
        if (url.Fragment.Length != 0)
            throw new DomainException(40001, "URL音频禁止包含片段");
        var host = url.IdnHost.TrimEnd('.');
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase))
            throw new DomainException(40302, "URL音频禁止本机或本地DNS名称");
    }

    private static string NormalizeFormat(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new DomainException(40001, "URL音频声明格式不能为空");
        var format = value.Trim().ToLowerInvariant();
        if (!RemoteAudioSignature.Supported.Contains(format, StringComparer.Ordinal))
            throw new DomainException(40001, "URL音频声明格式必须是wav、mp3、flac、m4a或ogg");
        return format;
    }

    private static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digest = value.Trim().ToLowerInvariant();
        if (digest.Length != 64 || digest.Any(character => !Uri.IsHexDigit(character)))
            throw new DomainException(40001, "URL音频期望SHA-256必须是64位十六进制");
        return digest;
    }

    private static string? ValidateContentType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Length > 200 || value.Any(char.IsControl))
            throw new DomainException(40001, "URL音频Content-Type无效");
        var mediaType = value.Split(';', 2)[0].Trim().ToLowerInvariant();
        if (!mediaType.StartsWith("audio/", StringComparison.Ordinal) &&
            mediaType is not ("application/octet-stream" or "binary/octet-stream"))
            throw new DomainException(40001, "URL音频Content-Type不是音频或二进制流");
        return mediaType;
    }

    private void RejectDeclaredOversize(long? contentLength)
    {
        if (contentLength > _limits.MaxBytes)
            throw new DomainException(41301, "URL音频声明的Content-Length超过大小上限");
    }

    private static void EnsureSuccess(HttpResponseMessage response, string phase)
    {
        if (!response.IsSuccessStatusCode)
            throw new DomainException(50201, $"URL音频{phase}返回HTTP {(int)response.StatusCode}，本次未自动重试");
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] is not (0 or 10 or 127) &&
                   !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                   !(bytes[0] == 169 && bytes[1] == 254) &&
                   !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                   !(bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2) &&
                   !(bytes[0] == 192 && bytes[1] == 168) &&
                   !(bytes[0] == 198 && bytes[1] is 18 or 19) &&
                   !(bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100) &&
                   !(bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113) &&
                   bytes[0] < 224;
        }
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;
        var v6 = address.GetAddressBytes();
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6Loopback)) return false;
        if (v6[0] == 0x20 && v6[1] == 0x02) return false;
        if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x00 && v6[3] == 0x00) return false;
        if ((v6[0] & 0xfe) == 0xfc || (v6[0] == 0xfe && (v6[1] & 0xc0) == 0x80) ||
            (v6[0] == 0xfe && (v6[1] & 0xc0) == 0xc0) || v6[0] == 0xff)
            return false;
        if (v6[0] == 0x20 && v6[1] == 0x01 && v6[2] == 0x0d && v6[3] == 0xb8) return false;
        if (v6[0] == 0x01 && v6[1] == 0x00 &&
            v6.Skip(2).Take(6).All(value => value == 0)) return false;
        return (v6[0] & 0xe0) == 0x20;
    }

    private static async Task<T> RunPhaseAsync<T>(string phase, TimeSpan timeout,
        Func<CancellationToken, Task<T>> action, CancellationToken ct)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutSource.CancelAfter(timeout);
        try
        {
            return await action(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DomainException(50401, $"URL音频{phase}超时，本次未自动重试");
        }
    }

    private static DomainException TransportFailure() =>
        new(50206, "URL音频连接异常，本次未自动重试");

    internal static async ValueTask<Stream> ConnectPinnedAsync(DnsEndPoint endpoint,
        IReadOnlyList<IPAddress> approved, Func<IPEndPoint, CancellationToken, ValueTask<Stream>> connect,
        CancellationToken ct)
    {
        if (approved.Count == 0)
            throw new HttpRequestException("No approved addresses");
        Exception? last = null;
        foreach (var address in approved)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await connect(new IPEndPoint(address, endpoint.Port), ct);
            }
            catch (Exception error) when (error is SocketException or IOException)
            {
                last = error;
            }
        }
        throw new HttpRequestException("All approved addresses failed", last);
    }
}

internal interface IRemoteAudioDnsResolver
{
    Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct);
}

internal sealed class SystemRemoteAudioDnsResolver : IRemoteAudioDnsResolver
{
    public async Task<IReadOnlyList<IPAddress>> ResolveAsync(string host, CancellationToken ct)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, ct);
        return Array.AsReadOnly(addresses.Distinct().ToArray());
    }
}

internal interface IRemoteAudioHttpClientFactory
{
    HttpClient Create(IReadOnlyList<IPAddress> approved, DnsEndPoint endpoint);
}

internal sealed class PinnedRemoteAudioHttpClientFactory : IRemoteAudioHttpClientFactory
{
    public HttpClient Create(IReadOnlyList<IPAddress> approved, DnsEndPoint endpoint)
    {
        var handler = CreateHandler(approved);
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    internal static SocketsHttpHandler CreateHandler(IReadOnlyList<IPAddress> approved) =>
        new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            Proxy = null,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (context, ct) =>
                await SecureRemoteAudioFetcher.ConnectPinnedAsync(context.DnsEndPoint, approved,
                    ConnectSocketAsync, ct)
        };

    private static async ValueTask<Stream> ConnectSocketAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(endpoint, ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

internal static class RemoteAudioSignature
{
    public static readonly IReadOnlyList<string> Supported =
        Array.AsReadOnly(new[] { "wav", "mp3", "flac", "m4a", "ogg" });

    public static bool Matches(string format, ReadOnlySpan<byte> bytes) => format switch
    {
        "wav" => bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) &&
                 bytes[8..12].SequenceEqual("WAVE"u8),
        "mp3" => bytes.Length >= 3 &&
                 (bytes[..3].SequenceEqual("ID3"u8) || bytes[0] == 0xff && (bytes[1] & 0xe0) == 0xe0),
        "flac" => bytes.Length >= 4 && bytes[..4].SequenceEqual("fLaC"u8),
        "m4a" => bytes.Length >= 12 && bytes[4..8].SequenceEqual("ftyp"u8),
        "ogg" => bytes.Length >= 4 && bytes[..4].SequenceEqual("OggS"u8),
        _ => false
    };
}
