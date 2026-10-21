using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Pim.Api.Middleware;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 幂等作用域的纯粹逻辑（不依赖 HTTP/数据库）。
/// 重点是「兜底形态化」只替换**整段**是 GUID/数字的段 —— 旧实现用全局正则，
/// 会把 <c>/api/v1/...</c> 里的 <c>v1</c> 也替换掉，导致所有作用域塌缩到同一形态。
/// </summary>
public sealed class IdempotencyKeyTests
{
    [Theory]
    [InlineData("/api/v1/calendar/tasks/3f2504e0-4f89-11d3-9a0c-0305e82c3301", "/api/v1/calendar/tasks/{}")]
    [InlineData("/api/v1/calendar/tasks/42", "/api/v1/calendar/tasks/{}")]
    [InlineData("/api/v1/pc/v1/summary", "/api/v1/pc/v1/summary")]
    [InlineData("/api/v1/files/items/3f2504e0-4f89-11d3-9a0c-0305e82c3301/versions/7",
        "/api/v1/files/items/{}/versions/{}")]
    public void FallbackRoute_OnlyRewritesWholeVolatileSegments(string path, string expected)
    {
        Assert.Equal(expected, IdempotencyKey.FallbackRoute(path));
    }

    [Fact]
    public void RouteOf_UsesRouteTemplateWhenAvailable()
    {
        var endpoint = new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse("/api/v1/calendar/tasks/{id}/checklist"),
            order: 0,
            new EndpointMetadataCollection(),
            displayName: "test");

        Assert.Equal("/api/v1/calendar/tasks/{id}/checklist", IdempotencyKey.RouteOf(endpoint));
    }

    [Fact]
    public void RouteOf_FallsBackToShapedPathWhenNoEndpoint()
    {
        Assert.Equal("/api/v1/calendar/tasks/{}",
            IdempotencyKey.RouteOf(null, new PathString("/api/v1/calendar/tasks/3f2504e0-4f89-11d3-9a0c-0305e82c3301")));
    }
}
