using System.Net;
using Microsoft.AspNetCore.Http;
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

            context.Request.Body = await request.Content.ReadAsStreamAsync(cancellationToken);
        }
        else
        {
            context.Request.Body = Stream.Null;
        }

        using var responseBody = new MemoryStream();
        context.Response.Body = responseBody;
        context.Response.StatusCode = StatusCodes.Status200OK;

        await _pipeline(context);

        var response = new HttpResponseMessage((HttpStatusCode)context.Response.StatusCode);
        foreach (var header in context.Response.Headers)
        {
            if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()))
                response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

        responseBody.Position = 0;
        response.Content = new StreamContent(responseBody);
        if (context.Response.ContentLength is { } length)
            response.Content.Headers.ContentLength = length;

        return response;
    }
}

/// <summary>
/// Typed wrapper over the shared in-process <see cref="HttpClient"/> so tool execution
/// stays testable and the dispatcher is created exactly once.
/// </summary>
public sealed class McpInProcessClient
{
    private readonly HttpClient _client;

    public McpInProcessClient(McpInProcessDispatcher dispatcher, TimeSpan? timeout = null)
    {
        _client = new HttpClient(dispatcher, disposeHandler: false)
        {
            BaseAddress = new Uri("http://pim-in-process", UriKind.Absolute),
            Timeout = timeout ?? TimeSpan.FromSeconds(60),
        };
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        => _client.SendAsync(request, ct);
}