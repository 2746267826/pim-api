using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pim.Module.Files;
using Pim.Module.Files.Entities;
using Pim.Module.Mcp.Services;
using Xunit;

namespace Pim.UnitTests.Mcp;

/// <summary>
/// MCP 工具表 → 真实 API 路由的契约护栏。
///
/// 背景（P4a 复审）：<c>McpToolTable</c> 是 MCP 执行器的 HTTP 映射事实源，但没有任何用例
/// 校验它指向的路由真的被模块 <c>MapEndpoints</c> 注册过。#328 因此把
/// <c>read_file_text</c> 指向 <c>/api/v1/files/items/{file_id}/extracted-text</c>、
/// 把 <c>restore_file</c> 指向 <c>/api/v1/files/items/{file_id}/restore</c>，
/// 而 FilesModule 里这两个路由从未注册（处理器写好了却没接线）——工具在运行时只会 404，
/// 契约测试却因为只比对 JSON 数量而全绿。
///
/// 本用例把「工具表声明的 files 路由」与「FilesModule 实际注册的路由」逐条对账，
/// 以后新增工具却忘了映射路由会直接失败。
/// </summary>
public sealed class McpToolRouteContractTests
{
    [Fact]
    public void EveryFilesToolInMcpToolTable_HasAMatchingRegisteredRoute()
    {
        var registered = RegisteredFilesRoutes();
        Assert.NotEmpty(registered);

        // 抽查：确认路由扫描确实命中了已知路由（否则本用例会退化成恒真式）。
        Assert.Contains(("GET", Normalize("/api/v1/files/items/{id:guid}/open-link")), registered);

        var filesTools = McpToolTable.All.Values
            .Where(spec => spec.Route.StartsWith("/api/v1/files", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(filesTools);

        var unmatched = new List<string>();
        foreach (var spec in filesTools)
        {
            if (!registered.Contains((spec.Method, Normalize(spec.Route))))
            {
                unmatched.Add($"{spec.Name}: {spec.Method} {spec.Route}");
            }
        }

        unmatched.Sort(StringComparer.Ordinal);

        Assert.True(
            unmatched.Count == 0,
            "以下 MCP 工具指向的 files 路由未被 FilesModule.MapEndpoints 注册，调用只会 404："
            + Environment.NewLine
            + string.Join(Environment.NewLine, unmatched));
    }

    /// <summary>
    /// 契约里声明的**参数**必须与工具表实际使用的路径参数一致：
    /// 路由里的每个路径参数都要在契约里声明（否则运行时拼不出 URL，
    /// 因为 <c>BuildPath</c> 只替换「契约参数名 == 占位符名」的项）。
    /// 复审实例：<c>restore_file</c> 用 <c>{file_id}</c>，契约却声明 <c>fileId</c>。
    ///
    /// 只对走通用 <c>BuildPath</c> 路径的工具生效：<c>CalendarById</c>/<c>ExportIcs</c> 等
    /// 有专用处理器（自己拼 URL/query），不适用本规则。
    /// </summary>
    [Fact]
    public void EveryMcpToolRouteParameter_IsDeclaredInItsContract()
    {
        var specialKinds = new[]
        {
            McpToolKind.CalendarById,
            McpToolKind.ExportIcs,
            McpToolKind.EventById,
            McpToolKind.TaskById,
            McpToolKind.HabitOccurrences,
            McpToolKind.TaskChecklist,
            McpToolKind.MobileLocationLatest,
            McpToolKind.SearchEvents,
            McpToolKind.SearchTasks,
            McpToolKind.SchedulePreview,
            McpToolKind.AttachmentMeta,
            McpToolKind.Health,
            McpToolKind.Version,
        };

        var problems = new List<string>();

        foreach (var spec in McpToolTable.All.Values)
        {
            if (specialKinds.Contains(spec.Kind))
            {
                continue;
            }

            var contract = McpToolExecutor.ToolContract.FirstOrDefault(t => t.Name == spec.Name);
            if (contract.Name is null)
            {
                continue; // 目录/契约一致性问题由其它用例负责
            }

            var declared = contract.InputSchema.TryGetProperty("properties", out var properties)
                ? properties.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>(StringComparer.Ordinal);

            foreach (var routeParam in RouteParams(spec.Route))
            {
                if (!declared.Contains(routeParam))
                {
                    problems.Add($"{spec.Name}: 路由参数 {{{routeParam}}} 未在契约中声明（契约有：{string.Join(",", declared)}）");
                }
            }
        }

        problems.Sort(StringComparer.Ordinal);
        Assert.True(
            problems.Count == 0,
            "MCP 契约参数与工具表路由不一致（BuildPath 会拼不出 URL）："
            + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }

    private static List<string> RouteParams(string route)
        => Regex.Matches(route, @"\{([^}]+)\}").Select(m => m.Groups[1].Value).ToList();

    /// <summary>
    /// MCP 工具表的路径参数名（如 {file_id}）与路由模板的参数名（如 {id:guid}）可以不同，
    /// 但**段数与静态段**必须一致，否则绑定必然失败。这里做归一化后对账。
    /// </summary>
    private static HashSet<(string Method, string Route)> RegisteredFilesRoutes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddAuthorization();
        using var app = builder.Build();

        new FilesModule().MapEndpoints(app);

        return ((IEndpointRouteBuilder)app)
            .DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(endpoint => endpoint.Metadata
                .GetMetadata<IHttpMethodMetadata>()?
                .HttpMethods
                .Select(method => (Method: method, Route: Normalize(endpoint.RoutePattern.RawText ?? string.Empty)))
                ?? [])
            .ToHashSet();
    }

    /// <summary>把 {xxx} / {xxx:guid} 这类路由参数统一成 {p}，便于跨表比较。</summary>
    private static string Normalize(string route)
    {
        var trimmed = route.Length > 1 ? route.TrimEnd('/') : route;
        return Regex.Replace(trimmed, @"\{[^}]+\}", "{p}");
    }
}
