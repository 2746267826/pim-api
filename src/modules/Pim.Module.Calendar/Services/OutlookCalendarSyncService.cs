using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Core.Exceptions;
using Pim.Infrastructure.Data;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;

namespace Pim.Module.Calendar.Services;

public sealed class OutlookCalendarSyncService
{
    private static readonly Dictionary<string, string> GraphColorToHex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lightBlue"] = "#69AFE5",
        ["lightYellow"] = "#F9D859",
        ["lightOrange"] = "#E8912D",
        ["lightGreen"] = "#51A351",
        ["lightGray"] = "#A0A0A0",
        ["lightRed"] = "#D14C4C",
        ["lightBrown"] = "#C08A53",
        ["lightTeal"] = "#45B39D",
        ["lightPink"] = "#F5A9C7",
        ["lightCoral"] = "#E68A8A",
        ["auto"] = "#3B82F6"
    };

    private readonly PimDbContext _db;
    private readonly GraphCalendarClient _graph;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OutlookCalendarSyncService> _logger;

    public OutlookCalendarSyncService(
        PimDbContext db,
        GraphCalendarClient graph,
        TimeProvider timeProvider,
        ILogger<OutlookCalendarSyncService> logger)
    {
        _db = db;
        _graph = graph;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OutlookCalendarBindingResponse>> DiscoverAsync(Guid userId, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct)
            ?? throw new DomainException(02005, "Outlook is not connected.");

        var allItems = await CollectAllRemoteCalendarsAsync(connection.Id, ct);
        var deduped = DeduplicateItems(allItems);

        var now = _timeProvider.GetUtcNow();
        var existingBindings = await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == connection.Id)
            .ToListAsync(ct);

        var existingCalendarIds = existingBindings.Select(b => b.PimCalendarId).ToHashSet();
        var existingCalendars = await _db.Set<CalendarEntity>()
            .IgnoreQueryFilters()
            .Where(c => existingCalendarIds.Contains(c.Id))
            .ToListAsync(ct);
        var calendarById = existingCalendars.ToDictionary(c => c.Id);

        var seenBindingIds = new HashSet<Guid>();

        foreach (var (graphId, groupId, groupName, item) in deduped)
        {
            var binding = existingBindings.FirstOrDefault(b => b.GraphCalendarId == graphId);

            if (binding is null)
            {
                var calendar = new CalendarEntity
                {
                    UserId = userId,
                    Name = ReadString(item, "name") ?? graphId,
                    Color = MapGraphColor(ReadString(item, "color")),
                    Kind = "calendar",
                    Source = "outlook",
                    IsVisible = true
                };
                _db.Set<CalendarEntity>().Add(calendar);

                binding = new OutlookCalendarBindingEntity
                {
                    ConnectionId = connection.Id,
                    PimCalendarId = calendar.Id,
                    GraphCalendarId = graphId,
                    GraphGroupId = groupId,
                    GraphGroupName = groupName,
                    Name = ReadString(item, "name") ?? graphId,
                    Color = ReadString(item, "color"),
                    OwnerName = ReadOwnerName(item),
                    OwnerAddress = ReadOwnerAddress(item),
                    IsDefaultCalendar = ReadBool(item, "isDefaultCalendar"),
                    CanEdit = ReadBool(item, "canEdit"),
                    CanViewPrivateItems = ReadBool(item, "canViewPrivateItems"),
                    IsSelected = true,
                    RemoteState = "active",
                    LastDiscoveryAt = now,
                    UpdatedAt = now
                };
                _db.Set<OutlookCalendarBindingEntity>().Add(binding);
            }
            else
            {
                binding.GraphGroupId = groupId;
                binding.GraphGroupName = groupName;
                binding.Name = ReadString(item, "name") ?? binding.Name;
                binding.Color = ReadString(item, "color");
                binding.OwnerName = ReadOwnerName(item);
                binding.OwnerAddress = ReadOwnerAddress(item);
                binding.IsDefaultCalendar = ReadBool(item, "isDefaultCalendar");
                binding.CanEdit = ReadBool(item, "canEdit");
                binding.CanViewPrivateItems = ReadBool(item, "canViewPrivateItems");
                binding.RemoteState = "active";
                binding.LastDiscoveryAt = now;
                binding.UpdatedAt = now;

                if (calendarById.TryGetValue(binding.PimCalendarId, out var existingCal))
                {
                    if (existingCal.DeletedAt is not null)
                    {
                        var replacement = new CalendarEntity
                        {
                            UserId = userId,
                            Name = ReadString(item, "name") ?? graphId,
                            Color = MapGraphColor(ReadString(item, "color")),
                            Kind = "calendar",
                            Source = "outlook",
                            IsVisible = binding.IsSelected
                        };
                        _db.Set<CalendarEntity>().Add(replacement);
                        binding.PimCalendarId = replacement.Id;
                        calendarById[replacement.Id] = replacement;
                    }
                    else
                    {
                        existingCal.Name = ReadString(item, "name") ?? existingCal.Name;
                        existingCal.Color = MapGraphColor(ReadString(item, "color"));
                        existingCal.IsVisible = binding.IsSelected;
                        existingCal.UpdatedAt = now;
                    }
                }
            }
            seenBindingIds.Add(binding.Id);
        }

        foreach (var existing in existingBindings)
        {
            if (!seenBindingIds.Contains(existing.Id))
            {
                existing.RemoteState = "remote-missing";
                existing.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct);

        return await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == connection.Id)
            .OrderBy(b => b.GraphCalendarId)
            .Select(b => new OutlookCalendarBindingResponse(
                b.Id, b.PimCalendarId, b.GraphCalendarId,
                b.GraphGroupId, b.GraphGroupName, b.Name, b.Color,
                b.OwnerName, b.OwnerAddress,
                b.IsDefaultCalendar, b.CanEdit, b.IsSelected, b.RemoteState,
                b.LastSyncedAt, b.LastErrorMessage))
            .ToListAsync(ct);
    }

    public async Task SetSelectionAsync(Guid userId, IReadOnlyCollection<Guid> selectedBindingIds, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct)
            ?? throw new DomainException(02005, "Outlook is not connected.");

        var userBindingIds = await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == connection.Id)
            .Select(b => b.Id)
            .ToListAsync(ct);
        var userBindingIdSet = userBindingIds.ToHashSet();

        foreach (var id in selectedBindingIds)
        {
            if (!userBindingIdSet.Contains(id))
                throw new DomainException(02005, "One or more calendar bindings do not belong to the current user.");
        }

        var allBindings = await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == connection.Id)
            .ToListAsync(ct);

        foreach (var binding in allBindings)
        {
            binding.IsSelected = selectedBindingIds.Contains(binding.Id);
            binding.UpdatedAt = _timeProvider.GetUtcNow();
        }

        var calendarIds = allBindings.Select(b => b.PimCalendarId).ToHashSet();
        var calendars = await _db.Set<CalendarEntity>()
            .Where(c => calendarIds.Contains(c.Id))
            .ToListAsync(ct);
        var calById = calendars.ToDictionary(c => c.Id);

        foreach (var binding in allBindings)
        {
            if (calById.TryGetValue(binding.PimCalendarId, out var cal))
            {
                cal.IsVisible = binding.IsSelected;
            }
        }

        await _db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<OutlookCalendarBindingResponse>> ListCalendarsAsync(Guid userId, CancellationToken ct)
    {
        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct);

        if (connection is null)
            return Array.Empty<OutlookCalendarBindingResponse>();

        return await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == connection.Id)
            .OrderBy(b => b.GraphCalendarId)
            .Select(b => new OutlookCalendarBindingResponse(
                b.Id, b.PimCalendarId, b.GraphCalendarId,
                b.GraphGroupId, b.GraphGroupName, b.Name, b.Color,
                b.OwnerName, b.OwnerAddress,
                b.IsDefaultCalendar, b.CanEdit, b.IsSelected, b.RemoteState,
                b.LastSyncedAt, b.LastErrorMessage))
            .ToListAsync(ct);
    }

    private async Task<List<(string GraphId, string? GroupId, string? GroupName, JsonElement Item)>>
        CollectAllRemoteCalendarsAsync(Guid connectionId, CancellationToken ct)
    {
        var groups = new List<(string Id, string Name)>();

        var groupPages = _graph.GetCalendarGroupsAsync(connectionId, ct);
        await foreach (var page in groupPages)
        {
            foreach (var item in page.Items)
            {
                var id = item.GetProperty("id").GetString()!;
                var name = item.TryGetProperty("name", out var n) ? n.GetString() : null;
                groups.Add((id, name ?? ""));
            }
        }

        var allItems = new List<(string GraphId, string? GroupId, string? GroupName, JsonElement Item)>();

        foreach (var (groupId, groupName) in groups)
        {
            var calPages = _graph.GetGroupCalendarsAsync(connectionId, groupId, ct);
            await foreach (var page in calPages)
            {
                foreach (var cal in page.Items)
                {
                    var graphId = cal.GetProperty("id").GetString()!;
                    allItems.Add((graphId, groupId, groupName, cal));
                }
            }
        }

        var rootPages = _graph.GetCalendarsAsync(connectionId, ct);
        await foreach (var page in rootPages)
        {
            foreach (var cal in page.Items)
            {
                var graphId = cal.GetProperty("id").GetString()!;
                allItems.Add((graphId, null, null, cal));
            }
        }

        return allItems;
    }

    private static List<(string GraphId, string? GroupId, string? GroupName, JsonElement Item)> DeduplicateItems(
        List<(string GraphId, string? GroupId, string? GroupName, JsonElement Item)> items)
    {
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<(string GraphId, string? GroupId, string? GroupName, JsonElement Item)>();

        foreach (var item in items)
        {
            if (seenIds.Add(item.GraphId))
                result.Add(item);
        }

        return result;
    }

    internal static string MapGraphColor(string? graphColor)
    {
        if (graphColor is null)
            return "#3B82F6";

        if (GraphColorToHex.TryGetValue(graphColor, out var hex))
            return hex;

        return "#3B82F6";
    }

    private static string? ReadString(JsonElement item, string property)
        => item.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.String
            ? prop.GetString()
            : null;

    private static bool ReadBool(JsonElement item, string property)
        => item.TryGetProperty(property, out var prop) && prop.ValueKind == JsonValueKind.True;

    private static string? ReadOwnerName(JsonElement item)
    {
        if (!item.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object)
            return null;
        return owner.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString()
            : null;
    }

    private static string? ReadOwnerAddress(JsonElement item)
    {
        if (!item.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.Object)
            return null;
        return owner.TryGetProperty("address", out var addr) && addr.ValueKind == JsonValueKind.String
            ? addr.GetString()
            : null;
    }
}
