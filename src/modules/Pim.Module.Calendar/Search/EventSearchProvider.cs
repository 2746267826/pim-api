using Microsoft.EntityFrameworkCore;
using Pim.Core.Modules;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;

namespace Pim.Module.Calendar.Search;

public sealed class EventSearchProvider(PimDbContext db, ICurrentUserService currentUser) : ISearchProvider
{
    public string Domain => "event";

    public async Task<SearchProviderResponse> SearchAsync(SearchProviderQuery query, CancellationToken ct)
    {
        var userId = currentUser.UserId;
        if (userId is null) return new SearchProviderResponse(Domain, 0, Array.Empty<SearchResult>());

        var events = await db.Set<EventEntity>()
            .AsNoTracking()
            .Where(item => item.Calendar.UserId == userId)
            .ToListAsync(ct);

        var matches = new List<SearchResult>();
        foreach (var item in events)
        {
            var participants = EventFieldCodec.DeserializeAttendees(item.AttendeesJson)
                .Select(attendee => string.IsNullOrWhiteSpace(attendee.Name)
                    ? attendee.Email
                    : attendee.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var organizer = EventFieldCodec.DeserializePerson(item.OrganizerJson, item.Organizer);
            var organizerName = string.IsNullOrWhiteSpace(organizer?.Name)
                ? organizer?.Email
                : organizer.Name;
            var people = participants
                .Append(organizerName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var fields = new List<SearchField>
            {
                new("title", "标题", item.Title, 70),
                new("description", "描述", item.Description ?? string.Empty, 35),
                new("location", "地点", item.Location ?? string.Empty, 40),
                new("participants", "参与人", string.Join(' ', people), 40)
            };
            var meeting = string.Join(' ',
                new[] { item.OnlineMeetingProvider, item.OnlineMeetingUrl, item.ExternalLink }
                    .Where(value => !string.IsNullOrWhiteSpace(value)));
            if (!string.IsNullOrWhiteSpace(meeting))
                fields.Add(new("meetingLink", "会议链接", meeting, 35));
            var occurrenceText = SearchScoring.DateText(item.DtStart);
            if (item.IsSeriesMaster || item.IsException || !string.IsNullOrWhiteSpace(item.RRule))
                fields.Add(new("recurrence", "重复日程", occurrenceText, 30));

            var match = SearchScoring.Match(fields, query);
            if (!match.Matches) continue;

            var occurrence = new SearchEventOccurrence(
                item.Id,
                item.RecurrenceId,
                item.SeriesMasterId ?? (item.IsSeriesMaster ? item.Id : null),
                item.DtStart);
            matches.Add(new SearchResult(
                "calendar",
                "event",
                item.Id.ToString("D"),
                item.Title,
                SearchScoring.Snippet(fields, query.Terms),
                $"/calendar?event={item.Id:D}",
                Domain: Domain,
                Score: match.Score,
                MatchedFields: match.MatchedFields,
                FieldLocations: match.FieldLocations,
                EventOccurrence: occurrence,
                SourceState: "server",
                UpdatedAt: item.UpdatedAt,
                CreatedAt: item.CreatedAt,
                SortKey: $"{Domain}|{item.Id:D}|{item.RecurrenceId ?? string.Empty}"));
        }

        return new SearchProviderResponse(Domain, matches.Count, matches);
    }
}
