using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Middleware;
using Pim.Core.Common;
using Pim.Core.Exceptions;
using Xunit;

namespace Pim.UnitTests.Api;

public class ExceptionMiddlewareTests
{
    [Theory]
    [InlineData(4004)]
    [InlineData(4006)]
    public async Task InvokeAsync_MapsQuickNoteNotFoundDomainErrorsTo404(int errorCode)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionMiddleware(
            _ => throw new DomainException(errorCode, "Not found"),
            NullLogger<ExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status404NotFound, context.Response.StatusCode);
        var response = await ReadResponseAsync(context);
        Assert.Equal(errorCode, response.Code);
    }

    [Fact]
    public async Task InvokeAsync_MapsValidationDomainErrorsTo400()
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionMiddleware(
            _ => throw new DomainException(4003, "Invalid quick note status"),
            NullLogger<ExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        var response = await ReadResponseAsync(context);
        Assert.Equal(4003, response.Code);
    }

    private static async Task<ApiResponse<string>> ReadResponseAsync(HttpContext context)
    {
        context.Response.Body.Position = 0;
        var response = await JsonSerializer.DeserializeAsync<ApiResponse<string>>(context.Response.Body);
        return Assert.IsType<ApiResponse<string>>(response);
    }
}
