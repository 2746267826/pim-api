using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Search;

public class CalendarSearchProvider : ISearchProvider
{
    public string ModuleName => "calendar";
    private readonly IServiceScopeFactory _scopeFactory;

    public CalendarSearchProvider(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(
        string query, int limit, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PimDbContext>();
        var currentUser = scope.ServiceProvider.GetRequiredService<ICurrentUserService>();
        var userId = currentUser.UserId;
        if (userId is null) return Array.Empty<SearchResult>();

        var results = new List<SearchResult>();

        var eventMatches = await db.Set<EventEntity>()
            .Where(e => e.Calendar.UserId == userId &&
                        (EF.Functions.ILike(e.Title, $"%{query}%") ||
                         e.Description != null && EF.Functions.ILike(e.Description, $"%{query}%")))
            .OrderByDescending(e => EF.Functions.ILike(e.Title, $"%{query}%"))
            .Take(limit)
            .Select(e => new { e.Id, e.Title, e.Description })
            .ToListAsync(ct);

        results.AddRange(eventMatches.Select(e => new SearchResult(
            "calendar", "event", e.Id.ToString(), e.Title,
            Truncate(e.Description ?? e.Title, 200),
            $"/calendar/event/{e.Id}")));

        var remaining = limit - results.Count;
        if (remaining > 0)
        {
            var taskMatches = await db.Set<TaskEntity>()
                .Where(t => t.UserId == userId &&
                            (EF.Functions.ILike(t.Title, $"%{query}%") ||
                             t.Description != null && EF.Functions.ILike(t.Description, $"%{query}%")))
                .OrderByDescending(t => EF.Functions.ILike(t.Title, $"%{query}%"))
                .Take(remaining)
                .Select(t => new { t.Id, t.Title, t.Description })
                .ToListAsync(ct);

            results.AddRange(taskMatches.Select(t => new SearchResult(
                "calendar", "task", t.Id.ToString(), t.Title,
                Truncate(t.Description ?? t.Title, 200),
                $"/calendar/task/{t.Id}")));
        }

        return results;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..(maxLength - 3)] + "...";
}
