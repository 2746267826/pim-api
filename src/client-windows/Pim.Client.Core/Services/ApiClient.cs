using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Pim.Client.Core;
using Pim.Client.Core.Models;

namespace Pim.Client.Core.Services;

public class ApiClient
{
    private HttpClient _httpClient;
    private readonly Func<HttpMessageHandler> _messageHandlerFactory;

    /// <summary>
    /// 令牌续期回调（主动到期前续期与 401 被动续期**共用**）。返回 true 表示续期成功。
    /// </summary>
    public Func<Task<bool>>? RenewTokenAsync { get; set; }

    /// <summary>
    /// 令牌是否已进入"到期前必须续期"的窗口（含已过期）。
    /// 每次发请求前先问一次：为真就先续期再发，不再依赖"先 401、再刷新、再重试"
    /// （WO-ISSUES-396-400-20261007 REQ-5 / issue #400）。
    /// </summary>
    public Func<bool>? IsTokenRenewalDue { get; set; }

    public event Action<string, long>? RequestTiming;

    /// <summary>续期串行闸门：同一时刻只允许一次真实续期，其余调用等待并复用结果。</summary>
    private readonly SemaphoreSlim _tokenRenewalGate = new(1, 1);

    /// <summary>续期请求自身发起的嵌套调用不得再触发续期（否则会无限递归）。</summary>
    private static readonly AsyncLocal<bool> InsideTokenRenewal = new();

    /// <summary>每次成功续期自增；用于判断"我这边的 401 是不是已经被别人续好了"。</summary>
    private long _tokenRenewalVersion;

    public ApiClient()
        : this(null)
    {
    }

    /// <param name="messageHandlerFactory">
    /// 传输层工厂。生产传 null（用 <see cref="HttpClientHandler"/>）；
    /// 测试注入桩 handler 以覆盖"刷新失败/401/重试"这类链路行为。
    /// 每次重建 HttpClient（<see cref="SetBaseUrl"/>）都会重新调用它。
    /// </param>
    public ApiClient(Func<HttpMessageHandler>? messageHandlerFactory)
    {
        _messageHandlerFactory = messageHandlerFactory ?? (() => new HttpClientHandler { UseProxy = false });
        _httpClient = new HttpClient(_messageHandlerFactory())
        {
            BaseAddress = new Uri($"{ClientDefaults.DefaultServerUrl}/api/v1/")
        };
    }

    public void SetBaseUrl(string baseUrl)
    {
        var normalized = NormalizeServerUrl(baseUrl);
        var uri = new Uri(normalized.TrimEnd('/') + "/api/v1/");

        // Build a fresh HttpClient so we can safely change the base address
        // even after the previous client has started sending requests.
        var newClient = new HttpClient(_messageHandlerFactory()) { BaseAddress = uri };

        // Preserve the current auth header
        if (_httpClient.DefaultRequestHeaders.Authorization is { } auth)
            newClient.DefaultRequestHeaders.Authorization = auth;

        // Atomically swap (safe because HttpClient disposal is immediate
        // on .NET — all in-flight operations are cancelled by the OS).
        Interlocked.Exchange(ref _httpClient, newClient).Dispose();
    }

    public string CurrentBaseUrl => _httpClient.BaseAddress?.ToString().TrimEnd('/') ?? "";

    private string Resolve(string endpoint)
    {
        return endpoint.TrimStart('/');
    }

    public static string NormalizeServerUrl(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (!trimmed.Contains("://", StringComparison.Ordinal))
        {
            trimmed = "http://" + trimmed;
        }
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            return trimmed;
        }
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return trimmed;
        return uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? new UriBuilder(uri) { Host = "127.0.0.1" }.Uri.ToString().TrimEnd('/')
            : trimmed;
    }

    public void SetAccessToken(string token)
    {
        _httpClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    public void ClearAccessToken()
    {
        _httpClient.DefaultRequestHeaders.Authorization = null;
    }

    public Task<ApiResponse<List<EndpointStatusDto>>?> GetEndpointStatusesAsync(CancellationToken ct = default)
        => GetAsync<ApiResponse<List<EndpointStatusDto>>>("/endpoints", ct);

    public Task<ApiResponse<EndpointCollectionQualityDto>?> GetEndpointCollectionQualityAsync(
        string deviceId,
        CancellationToken ct = default)
        => GetAsync<ApiResponse<EndpointCollectionQualityDto>>(
            $"/endpoints/{Uri.EscapeDataString(deviceId)}/collection-quality",
            ct);

    public Task<ApiResponse<EndpointNotificationActionResponseDto>?> SendEndpointNotificationActionAsync(
        string deviceId,
        EndpointNotificationActionRequestDto request,
        CancellationToken ct = default)
        => PostAsync<ApiResponse<EndpointNotificationActionResponseDto>>(
            $"/endpoints/{Uri.EscapeDataString(deviceId)}/notification-actions",
            request,
            ct);

    public static string BuildConfirmationDetailPath(string confirmationId)
        => $"/confirmations/{Uri.EscapeDataString(confirmationId)}";

    public async Task<T?> GetAsync<T>(string endpoint, CancellationToken ct = default)
    {
        return await SendWithAuthRetryAsync<T>(
            () => _httpClient.GetAsync(Resolve(endpoint), ct), ct);
    }

    public async Task<T?> PostAsync<T>(string endpoint, object body, CancellationToken ct = default)
    {
        return await SendWithAuthRetryAsync<T>(
            () => _httpClient.PostAsJsonAsync(Resolve(endpoint), body, ct), ct);
    }

    public async Task<T?> PutAsync<T>(string endpoint, object body, CancellationToken ct = default)
    {
        return await SendWithAuthRetryAsync<T>(
            () => _httpClient.PutAsJsonAsync(Resolve(endpoint), body, ct), ct);
    }

    public async Task DeleteAsync(string endpoint, CancellationToken ct = default)
    {
        await SendWithAuthRetryAsync<IgnoreResult>(
            () => _httpClient.DeleteAsync(Resolve(endpoint), ct), ct);
    }

    public async Task<T?> PostStringAsync<T>(string endpoint, string content, CancellationToken ct = default)
    {
        return await SendWithAuthRetryAsync<T>(
            () => _httpClient.PostAsync(Resolve(endpoint),
                new StringContent(content, System.Text.Encoding.UTF8, "text/calendar"), ct), ct);
    }

    private async Task<T?> SendWithAuthRetryAsync<T>(
        Func<Task<HttpResponseMessage>> request, CancellationToken ct)
    {
        // 到期前主动续期（REQ-5）：令牌进入临期窗口就先换新，再发本次请求。
        await EnsureTokenRenewedBeforeRequestAsync(ct);

        var sw = Stopwatch.StartNew();
        // 记下**发送前**的续期代数：收到 401 时用它判断"这次 401 之后有没有别人已经换过令牌"。
        // 不能用"收到 401 时"的代数 —— 那样后来者会误判为"还没人换过"再续期一次，
        // 把前一个请求刚拿到的新令牌又轮换掉，导致它的重试撞上 401（并发下的连锁失败）。
        var versionAtSend = Interlocked.Read(ref _tokenRenewalVersion);
        var response = await request();
        var firstHopMs = sw.ElapsedMilliseconds;

        if (response.StatusCode == HttpStatusCode.Unauthorized && RenewTokenAsync is not null)
        {
            // 被动兜底：仍然保留"401 → 续期 → 重试一次"，但并发到达的多个 401 只会产生**一次**
            // 真实续期（旧实现用 volatile bool 直接跳过后来者，被跳过的请求拿着旧令牌继续走，
            // 最终 EnsureSuccessStatusCode 抛 401 —— 上传循环把它当"客户端错误"丢掉整批数据）。
            if (await RenewTokenAfterUnauthorizedAsync(versionAtSend, ct))
            {
                sw.Restart();
                response = await request();
                RequestTiming?.Invoke($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri} (after refresh)", sw.ElapsedMilliseconds);
            }
        }
        else
        {
            RequestTiming?.Invoke($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri}", firstHopMs);
        }

        response.EnsureSuccessStatusCode();

        if (typeof(T) == typeof(IgnoreResult))
            return default;

        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    /// <summary>
    /// 发请求前的到期前续期。
    /// 续期失败时**不发**这次请求，而是抛一个不带状态码的 <see cref="HttpRequestException"/>：
    /// 拿已经过期的令牌去请求必然得到 401，而调用方把 401 视为"数据不合格"会丢掉待上报队列
    /// （REQ-5 反面行为要求不得丢队列、不得登出）。无状态码的传输层异常走的是"稍后重试"那条路。
    /// </summary>
    private async Task EnsureTokenRenewedBeforeRequestAsync(CancellationToken ct)
    {
        if (RenewTokenAsync is null || IsTokenRenewalDue?.Invoke() != true)
        {
            return;
        }

        if (InsideTokenRenewal.Value)
        {
            // 续期请求自身：不再触发续期。
            return;
        }

        var versionBefore = Interlocked.Read(ref _tokenRenewalVersion);
        await _tokenRenewalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _tokenRenewalVersion) != versionBefore)
            {
                // 排队期间别人已经续期成功，直接发请求。
                return;
            }

            bool renewed;
            InsideTokenRenewal.Value = true;
            try
            {
                renewed = await RenewTokenAsync().ConfigureAwait(false);
            }
            finally
            {
                InsideTokenRenewal.Value = false;
            }

            if (renewed)
            {
                Interlocked.Increment(ref _tokenRenewalVersion);
                return;
            }

            throw new HttpRequestException(
                "access token renewal failed before request; request was not sent so the pending queue is kept for a later retry");
        }
        finally
        {
            _tokenRenewalGate.Release();
        }
    }

    /// <summary>
    /// 收到 401 之后的续期：串行化 + 复用结果。
    /// <paramref name="versionAtSend"/> 是**发出这个请求之前**的续期代数：
    /// 只要它变了，就说明已经有人换好了令牌，直接重试即可，不再打第二次 /auth/refresh。
    /// 返回 true 表示调用方可以重试本次请求。
    /// </summary>
    private async Task<bool> RenewTokenAfterUnauthorizedAsync(long versionAtSend, CancellationToken ct)
    {
        if (InsideTokenRenewal.Value)
        {
            // 续期请求自身收到 401（refresh token 已失效）：不再递归续期。
            return false;
        }

        await _tokenRenewalGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _tokenRenewalVersion) != versionAtSend)
            {
                // 本次请求发出之后已有请求续期成功：重试即可，不再打第二次 refresh。
                return true;
            }

            InsideTokenRenewal.Value = true;
            try
            {
                if (!await RenewTokenAsync().ConfigureAwait(false))
                {
                    return false;
                }
            }
            finally
            {
                InsideTokenRenewal.Value = false;
            }

            Interlocked.Increment(ref _tokenRenewalVersion);
            return true;
        }
        finally
        {
            _tokenRenewalGate.Release();
        }
    }

    private sealed class IgnoreResult { }
}
