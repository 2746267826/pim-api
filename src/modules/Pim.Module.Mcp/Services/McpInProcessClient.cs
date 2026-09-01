using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Pim.Module.Mcp.Services;

/// <summary>
/// In-process HTTP dispatcher: forwards <see cref="HttpRequestMessage"/> through the
/// Pim.Api request pipeline (same process, same DI scopes, same middleware chain —
/// including JWT auth and <c>McpScopedTokenMiddleware</c>). No sockets, no second
/// token round-trip. Registered once per app; the pipeline is captured by the host
/// bootstrap after all endpoints are mapped (see <c>McpServerBootstrap</c>).
/// </summary>
public sealed class McpInProcessDispatcher : HttpMessageHandler
{
    private RequestDelegate? _pipeline;
    private IServiceProvider? _rootServices;

    private sealed class RequestBodyDetectionFeature(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody { get; } = canHaveBody;
    }

    /// <summary>Captured once by the host bootstrap after the full pipeline is built.</summary>
    public void Initialize(RequestDelegate pipeline, IServiceProvider rootServices)
    {
        _pipeline = pipeline;
        _rootServices = rootServices;
    }

    public bool IsInitialized => _pipeline is not null;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (_pipeline is null || _rootServices is null)
            throw new InvalidOperationException("McpInProcessDispatcher is not initialized (MCP bootstrap did not run).");

        using var scope = _rootServices.CreateScope();
        var context = new DefaultHttpContext();
        context.RequestServices = scope.ServiceProvider;
        context.Request.Method = request.Method.Method;
        // The server normally registers this feature; without it the JSON body binder in
        // RequestDelegateFactory skips reading the request body entirely.
        context.Features.Set<IHttpRequestBodyDetectionFeature>(
            new RequestBodyDetectionFeature(canHaveBody: request.Content is not null));

        var uri = request.RequestUri
            ?? throw new InvalidOperationException("In-process MCP requests must carry an absolute request URI.");
        context.Request.Path = uri.AbsolutePath;
        context.Request.QueryString = QueryString.FromUriComponent(uri.Query);

        foreach (var header in request.Headers)
        {
            if (header.Value is null)
                continue;
            context.Request.Headers[header.Key] = header.Value.ToArray();
        }

        if (request.Content is not null)
        {
            foreach (var contentHeader in request.Content.Headers)
            {
                if (contentHeader.Value is null)
                    continue;
                context.Request.Headers[contentHeader.Key] = contentHeader.Value.ToArray();
            }

            // Buffer the body once: ReadAsStreamAsync on buffered content types hands back the
            // content's own stream, and disposing it would break HttpClient's later content
            // buffering ("Cannot access a closed Stream").
            var bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken);
            context.Request.Body = new MemoryStream(bodyBytes, writable: false);
        }
        else
        {
            context.Request.Body = Stream.Null;
        }

        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        context.Response.StatusCode = StatusCodes.Status200OK;

        // The server's HttpContextFactory normally populates IHttpContextAccessor for the
        // ambient request; in-process dispatch must do it explicitly or ICurrentUserService
        // (and any auth-derived logic) sees no user ("未登录").
        var accessor = scope.ServiceProvider.GetService<IHttpContextAccessor>();
        var previousContext = accessor?.HttpContext;
        if (accessor is not null)
            accessor.HttpContext = context;
        try
        {
            await _pipeline(context);
        }
        finally
        {
            if (accessor is not null)
                accessor.HttpContext = previousContext;
        }

        var response = new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode);
        // Copy the buffered body out before the MemoryStream is disposed by the using block.
        response.Content = new ByteArrayContent(responseBody.ToArray());
        foreach (var header in context.Response.Headers)
        {
            var values = header.Value.ToArray();
            // Content headers must live on response.Content so content-type sniffing works.
            if (IsContentHeader(header.Key))
                response.Content.Headers.TryAddWithoutValidation(header.Key, values);
            else
                response.Headers.TryAddWithoutValidation(header.Key, values);
        }
        if (context.Response.ContentLength is { } length)
            response.Content.Headers.ContentLength = length;

        return response;
    }

    private static bool IsContentHeader(string name)
        => name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Type", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Disposition", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Encoding", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Language", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Location", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Content-Range", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Allow", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Expires", StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, "Last-Modified", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Typed wrapper over the shared in-process <see cref="HttpClient"/> so tool execution
/// stays testable and the dispatcher is created exactly once.
/// </summary>
public sealed class McpInProcessClient
{
    private const int MaxRedirects = 10;
    private readonly HttpClient _client;

    public McpInProcessClient(McpInProcessDispatcher dispatcher, TimeSpan? timeout = null)
    {
        _client = new HttpClient(dispatcher, disposeHandler: false)
        {
            BaseAddress = new Uri("http://pim-in-process", UriKind.Absolute),
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
        };
    }

    /// <summary>
    /// Sends a request following redirects like httpx (the Python reference followed them by
    /// default). Redirect handling lives in HttpClientHandler, which custom handlers replace,
    /// so it is re-implemented here: up to <see cref="MaxRedirects"/> hops, POST→GET on
    /// 301/302/303, headers re-sent on same-origin hops (Authorization dropped cross-origin).
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var current = request;
        var baseAddress = _client.BaseAddress ?? throw new InvalidOperationException("BaseAddress is required for in-process dispatch.");

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            var response = await _client.SendAsync(current, ct);
            var status = (int)response.StatusCode;
            if (status < 300 || status >= 400 || response.Headers.Location is not { } location)
                return response;

            if (hop == MaxRedirects)
                return response;

            var nextUri = location.IsAbsoluteUri
                ? location
                : new Uri(baseAddress, location);

            var nextMethod = current.Method;
            if (status == 303 || (current.Method == HttpMethod.Post && status is 301 or 302))
                nextMethod = HttpMethod.Get;

            var next = new HttpRequestMessage(nextMethod, nextUri);
            if (current.Content is not null && nextMethod == current.Method)
                next.Content = current.Content;
            else
                current.Content?.Dispose();

            var sameOrigin = string.Equals(baseAddress.GetLeftPart(UriPartial.Authority), nextUri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
            foreach (var header in current.Headers)
            {
                if (header.Key == "Authorization" && !sameOrigin)
                    continue;
                next.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            current = next;
        }

        throw new InvalidOperationException("Unreachable: redirect loop bounded by MaxRedirects.");
    }
}