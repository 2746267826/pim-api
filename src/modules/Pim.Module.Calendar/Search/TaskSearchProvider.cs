using Microsoft.EntityFrameworkCore;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Search;

public sealed class TaskSearchProvider(PimDbContext db, ICurrentUserService currentUser) : ISearchProvider
{
    public string Domain => "task";

    public async Task<SearchProviderResponse> SearchAsync(SearchProviderQuery query, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId is null) return new SearchProviderResponse(Domain, 0, Array.Empty<SearchResult>());

        var tasks = await db.Set<TaskEntity>()
            .AsNoTracking()
            .Where(task => task.UserId == userId)
            .Include(task => task.ChecklistItems)
            .Include(task => task.TaskBook)
            .ToListAsync(ct);

        var matches = new List<SearchResult>();
        foreach (var task in tasks)
        {
            var fields = new List<SearchField>
            {
                new("title", "标题", task.Title, 70),
                new("description", "描述", task.Description ?? string.Empty, 35)
            };
            var checklist = task.ChecklistItems
                .OrderBy(item => item.SortOrder)
                .ThenBy(item => item.Id)
                .ToArray();
            for (var index = 0; index < checklist.Length; index++)
                fields.Add(new("checklist", "清单", checklist[index].Title, 40, index + 1));
            if (!string.IsNullOrWhiteSpace(task.TaskBook?.Name))
                fields.Add(new("taskBook", "任务本", task.TaskBook.Name, 45));
            if (task.Due is { } due)
                fields.Add(new("due", "截止时间", SearchScoring.DateText(due), 25));
            var schedule = SearchScoring.DateText(task.DtStart, task.PlannedEnd);
            if (!string.IsNullOrWhiteSpace(schedule))
                fields.Add(new("schedule", "安排", schedule, 30));

            var match = SearchScoring.Match(fields, query);
            if (!match.Matches) continue;

            matches.Add(new SearchResult(
                "calendar",
                "task",
                task.Id.ToString("D"),
                task.Title,
                SearchScoring.Snippet(fields, query.Terms),
                $"/tasks?task={task.Id:D}",
                Domain: Domain,
                Score: match.Score,
                MatchedFields: match.MatchedFields,
                FieldLocations: match.FieldLocations,
                SourceState: "server",
                UpdatedAt: task.UpdatedAt,
                CreatedAt: task.CreatedAt,
                SortKey: $"{Domain}|{task.Id:D}"));
        }

        return new SearchProviderResponse(Domain, matches.Count, matches);
    }
}
