using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// Regression tests for the in-process dispatcher against a REAL minimal-API pipeline:
/// JSON body binding (IHttpRequestBodyDetectionFeature), IHttpContextAccessor identity,
/// query/header passthrough, content-type routing and redirect handling.
/// </summary>
public sealed class McpInProcessClientTests
{
    private sealed record EchoRequest(string? Title, string? Status);

    private static (McpInProcessClient Client, IServiceProvider RootServices) CreateClient(
        Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        builder.Services.AddAuthentication();
        builder.Services.AddHttpContextAccessor();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        configure(app);

        var tail = new ApplicationBuilder(app.Services);
        tail.Properties["__EndpointRouteBuilder"] = app;
        tail.UseEndpoints(_ => { });
        var tailPipeline = tail.Build();
        app.Use(_ => tailPipeline);
        var inner = ((IApplicationBuilder)app).Build();
        var outer = new ApplicationBuilder(app.Services);
        outer.Properties["__GlobalEndpointRouteBuilder"] = app;
        outer.UseRouting();
        outer.Run(inner);
        var pipeline = outer.Build();

        var dispatcher = new McpInProcessDispatcher();
        dispatcher.Initialize(pipeline, app.Services);
        return (new McpInProcessClient(dispatcher), app.Services);
    }

    [Fact]
    public async Task PostJsonBody_BindsIntoMinimalApiParameter()
    {
        var (client, _) = CreateClient(app => app.MapPost("/api/v1/echo", (EchoRequest req) =>
            Results.Ok(new { title = req.Title, status = req.Status })));

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/echo")
        {
            Content = new StringContent("""{"title":"t1","status":"pending"}""", Encoding.UTF8, "application/json"),
        };
        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("t1", body.RootElement.GetProperty("title").GetString());
        Assert.Equal("pending", body.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task CurrentUserService_SeesAmbientHttpContext()
    {
        var (client, rootServices) = CreateClient(app =>
        {
            app.MapGet("/api/v1/identity", ([Microsoft.AspNetCore.Mvc.FromServices] IHttpContextAccessor accessor) =>
            {
                // Same lookup as CurrentUserService — must see the in-flight request.
                var userId = accessor.HttpContext?.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                return Results.Ok(new { userId = userId ?? "anonymous" });
            });
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/identity");
        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("anonymous", body.RootElement.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task QueryAndHeaders_PassThrough()
    {
        var (client, _) = CreateClient(app => app.MapGet("/api/v1/query", (HttpContext ctx) =>
            Results.Ok(new { q = ctx.Request.Query["q"].ToString(), custom = ctx.Request.Headers["X-Custom"].ToString() })));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/query?q=hello");
        request.Headers.TryAddWithoutValidation("X-Custom", "value-42");
        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("hello", body.RootElement.GetProperty("q").GetString());
        Assert.Equal("value-42", body.RootElement.GetProperty("custom").GetString());
    }

    [Fact]
    public async Task ContentType_IsRoutedToContentHeaders()
    {
        var (client, _) = CreateClient(app => app.MapGet("/api/v1/text", () => Results.Text("plain", "text/plain")));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/text");
        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/plain", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Redirect_FollowsInProcess()
    {
        var (client, _) = CreateClient(app =>
        {
            app.MapGet("/api/v1/old", () => Results.Redirect("/api/v1/new", permanent: false));
            app.MapGet("/api/v1/new", () => Results.Ok(new { moved = true }));
        });

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/old");
        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(body.RootElement.GetProperty("moved").GetBoolean());
    }

    [Fact]
    public async Task PostRedirect_ReplaysBodyWithNewContent()
    {
        var (client, _) = CreateClient(app =>
        {
            app.MapPost("/api/v1/move", async (EchoRequest req) => Results.Redirect("/api/v1/land", permanent: true, preserveMethod: true));
            app.MapPost("/api/v1/land", (EchoRequest req) => Results.Ok(new { title = req.Title }));
        });

        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/move")
        {
            Content = new StringContent("""{"title":"replayed","status":"x"}""", Encoding.UTF8, "application/json"),
        };
        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("replayed", body.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task Fallback_Returns404LikeRealHost()
    {
        var (client, _) = CreateClient(app => app.MapGet("/api/v1/known", () => Results.Ok()));

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/unknown");
        var response = await client.SendAsync(request, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}