using System.Collections.Concurrent;
using System.Net;
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

    // ===== Connection-level lock =====

    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> ConnectionLocks = new();

    // ===== SyncAsync =====

    public async Task<OutlookSyncBatchResponse> SyncAsync(Guid userId, OutlookSyncRequest request, CancellationToken ct)
    {
        if (request.Mode != "normal")
            throw new DomainException(02009, "不支持的 Microsoft 同步模式。");

        var connection = await _db.Set<OutlookConnectionEntity>()
            .FirstOrDefaultAsync(c => c.UserId == userId, ct)
            ?? throw new DomainException(02005, "Outlook is not connected.");

        var bindings = await _db.Set<OutlookCalendarBindingEntity>()
            .Where(b => b.ConnectionId == connection.Id && b.IsSelected && b.RemoteState == "active")
            .OrderBy(b => b.Id)
            .ToListAsync(ct);

        var semaphore = ConnectionLocks.GetOrAdd(connection.Id, _ => new SemaphoreSlim(1, 1));

        if (!await semaphore.WaitAsync(0, ct))
        {
            var running = await _db.Set<OutlookSyncBatchEntity>()
                .Where(b => b.ConnectionId == connection.Id && b.Status == "running")
                .OrderByDescending(b => b.StartedAt)
                .FirstOrDefaultAsync(ct);
            if (running is not null)
                return MapBatch(running);

            throw new DomainException(02008, "已有同步任务正在运行（running），请稍后再试。");
        }

        try
        {
            return await RunSyncInternalAsync(userId, connection, bindings, ct);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private sealed record EventChangeSummary(string EventId, string? Title, string Action);
    private sealed record SyncFailureSummary(string? EventId, string? Title, string Code, string Message);

    private sealed class BindingSyncState
    {
        public OutlookCalendarBindingEntity Binding { get; }
        public int Read { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public int Failure { get; set; }
        public int SuccessfulPages { get; set; }
        public string Status { get; set; } = "running";
        public bool ProgressMade { get; set; }
        public List<EventChangeSummary> Changes { get; } = new();
        public List<SyncFailureSummary> Failures { get; } = new();
        public List<OutlookSyncStep> Steps { get; } = new();

        public BindingSyncState(OutlookCalendarBindingEntity binding) => Binding = binding;
    }

    private async Task<OutlookSyncBatchResponse> RunSyncInternalAsync(
        Guid userId,
        OutlookConnectionEntity connection,
        IReadOnlyList<OutlookCalendarBindingEntity> bindings,
        CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var windowStart = now.AddDays(-90);
        var windowEnd = now.AddDays(365);
        var generation = Guid.NewGuid();

        // Interrupt old running non-writeback batches
        var runningBatches = await _db.Set<OutlookSyncBatchEntity>()
            .Where(b => b.ConnectionId == connection.Id && b.Status == "running" && b.Mode != "writeback")
            .ToListAsync(ct);
        foreach (var rb in runningBatches)
        {
            rb.Status = "interrupted";
            rb.ConfirmationCount = 0;
            rb.FinishedAt = now;
            rb.UpdatedAt = now;
        }
        if (runningBatches.Count > 0)
            await _db.SaveChangesAsync(ct);

        // Create batch (empty bindings still persist a window + [] + ConfirmationCount=0)
        var batch = new OutlookSyncBatchEntity
        {
            UserId = userId,
            ConnectionId = connection.Id,
            Mode = "normal",
            RequestedWindowStart = windowStart,
            RequestedWindowEnd = windowEnd,
            RequestedCalendarIdsJson = JsonSerializer.Serialize(bindings.Select(b => b.Id.ToString()).ToList()),
            ConfirmationCount = 0,
            Status = "running",
            StartedAt = now,
            UpdatedAt = now
        };
        _db.Set<OutlookSyncBatchEntity>().Add(batch);
        await _db.SaveChangesAsync(ct);

        var states = new List<BindingSyncState>();
        var reauthEncountered = false;
        var canceled = false;

        try
        {
            foreach (var binding in bindings)
            {
                if (reauthEncountered)
                    break;

                var state = new BindingSyncState(binding);
                states.Add(state);

                try
                {
                    await ProcessSingleBindingAsync(
                        connection, binding, state, batch, windowStart, windowEnd, generation, now, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    canceled = true;
                    state.Status = state.ProgressMade ? "partial" : "failed";
                    throw;
                }
                catch (OutlookReauthenticationRequiredException)
                {
                    connection.Status = "reauth-required";
                    connection.TokenHealth = "interaction-required";
                    connection.LastError = "Microsoft 需要重新验证。请使用检查连接重新授权。";
                    connection.UpdatedAt = now;
                    reauthEncountered = true;
                    state.Status = state.ProgressMade ? "partial" : "failed";
                    state.Steps.Add(new OutlookSyncStep(binding.Id.ToString(), "reauth", "需要重新验证", now));
                    state.Failure = 1;
                }
                catch (GraphRequestException ex)
                {
                    var code = ex.StatusCode is { } statusCode
                        ? ((int)statusCode).ToString()
                        : "graph-error";
                    var msg = $"Graph {code}";
                    _logger.LogWarning("同步 binding {BindingId} 失败: {Code}", binding.Id, code);
                    state.Failure = 1;
                    state.Failures.Add(new SyncFailureSummary(null, null, code, msg));
                    state.Steps.Add(new OutlookSyncStep(binding.Id.ToString(), "failed", msg, now));
                    state.Status = state.ProgressMade ? "partial" : "failed";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("同步 binding {BindingId} 时出错: {ErrorType}", binding.Id, ex.GetType().Name);
                    state.Failure = 1;
                    state.Failures.Add(new SyncFailureSummary(null, null, "unknown", "未知错误"));
                    state.Steps.Add(new OutlookSyncStep(binding.Id.ToString(), "failed", "未知错误", now));
                    state.Status = state.ProgressMade ? "partial" : "failed";
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && !canceled)
        {
            canceled = true;
        }
        catch (OperationCanceledException) when (canceled)
        {
            // Already recorded; swallow rethrow path.
        }

        // Aggregate batch counters from per-binding states
        batch.ReadCount = states.Sum(s => s.Read);
        batch.CreatedCount = states.Sum(s => s.Created);
        batch.UpdatedCount = states.Sum(s => s.Updated);
        batch.FailureCount = states.Count(s => s.Failure > 0);
        batch.ConfirmationCount = 0;

        var stepsList = new List<OutlookSyncStep>();
        var errorsList = new List<object>();
        var calEntries = new List<object>();
        var anyProgress = false;
        var allFailedNoProgress = states.Count > 0;

        foreach (var s in states)
        {
            stepsList.AddRange(s.Steps);
            if (s.ProgressMade)
                anyProgress = true;
            if (s.ProgressMade || s.Status != "failed")
                allFailedNoProgress = false;

            foreach (var f in s.Failures)
            {
                errorsList.Add(new
                {
                    bindingId = s.Binding.Id.ToString(),
                    eventId = f.EventId,
                    title = f.Title,
                    code = f.Code,
                    message = f.Message
                });
            }

            calEntries.Add(new
            {
                bindingId = s.Binding.Id.ToString(),
                calendarName = s.Binding.Name,
                status = s.Status,
                readCount = s.Read,
                createdCount = s.Created,
                updatedCount = s.Updated,
                deletedCount = s.Deleted,
                failureCount = s.Failure,
                changes = s.Changes.Select(c => new { id = c.EventId, title = c.Title, action = c.Action }),
                failures = s.Failures.Select(f => new
                {
                    eventId = f.EventId,
                    title = f.Title,
                    code = f.Code,
                    message = f.Message
                })
            });
        }

        batch.FinishedAt = now;
        batch.UpdatedAt = now;
        batch.StepsJson = JsonSerializer.Serialize(stepsList);
        batch.PerCalendarJson = JsonSerializer.Serialize(calEntries);
        batch.ErrorsJson = JsonSerializer.Serialize(errorsList);

        if (states.Count == 0)
        {
            errorsList = new List<object>();
            batch.ErrorsJson = JsonSerializer.Serialize(errorsList);
            batch.ErrorSummary = null;
        }
        else if (errorsList.Count > 0)
        {
            batch.ErrorSummary = $"部分日历同步失败，失败日历数 {batch.FailureCount}";
        }
        else
        {
            batch.ErrorSummary = null;
        }

        // Batch status (canceled/reauth preserved first)
        if (canceled)
        {
            batch.Status = "canceled";
        }
        else if (reauthEncountered)
        {
            batch.Status = anyProgress ? "partial" : "failed";
        }
        else if (allFailedNoProgress)
        {
            batch.Status = "failed";
        }
        else if (batch.FailureCount > 0)
        {
            batch.Status = "partial";
        }
        else
        {
            batch.Status = "completed";
        }

        // Connection status (reauth not overridden)
        if (!reauthEncountered && !canceled)
        {
            if (batch.Status == "completed")
            {
                connection.LastSyncedAt = now;
                connection.LastError = null;
            }
            else if (batch.Status == "partial")
            {
                connection.LastSyncedAt = now;
                connection.LastError = batch.ErrorSummary ?? "部分同步失败";
            }
            else if (batch.Status == "failed")
            {
                connection.LastError = batch.ErrorSummary ?? "同步失败";
            }
            connection.UpdatedAt = now;
        }

        var saveCt = ct.IsCancellationRequested ? CancellationToken.None : ct;
        await _db.SaveChangesAsync(saveCt);

        return MapBatch(batch);
    }

    private async Task ProcessSingleBindingAsync(
        OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding,
        BindingSyncState state,
        OutlookSyncBatchEntity batch,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        Guid generation,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var pagesCompleted = false;

        try
        {
            var pages = _graph.GetCalendarViewAsync(
                connection.Id, binding.GraphCalendarId, windowStart, windowEnd, ct);

            await foreach (var page in pages)
            {
                ct.ThrowIfCancellationRequested();

                var pageAdded = new List<EventEntity>();
                var pageModified = new List<EventEntity>();
                var pageRead = 0;
                var pageCreated = 0;
                var pageUpdated = 0;
                var pageChanges = new List<EventChangeSummary>();

                try
                {
                    foreach (var graphEvent in page.Items)
                    {
                        var eventId = graphEvent.GetProperty("id").GetString()!;

                        var existing = await _db.Set<EventEntity>()
                            .IgnoreQueryFilters()
                            .Where(e => e.OutlookConnectionId == connection.Id && e.OutlookEventId == eventId)
                            .FirstOrDefaultAsync(ct);

                        if (existing is not null)
                        {
                            existing.CalendarId = binding.PimCalendarId;
                            existing.OutlookCalendarBindingId = binding.Id;
                            existing.DeletedAt = null;
                            existing.DeletedByOperationId = null;
                            existing.DeletedByOperationKind = null;
                            OutlookEventMapper.ApplyGraphEvent(
                                existing, graphEvent, binding.Id, binding.PimCalendarId, connection.Id, generation);
                            existing.UpdatedAt = now;
                            pageModified.Add(existing);
                            pageUpdated++;
                        }
                        else
                        {
                            var newEvent = new EventEntity();
                            OutlookEventMapper.ApplyGraphEvent(
                                newEvent, graphEvent, binding.Id, binding.PimCalendarId, connection.Id, generation);
                            newEvent.CreatedAt = now;
                            newEvent.UpdatedAt = now;
                            newEvent.DtStamp = now;
                            newEvent.OutlookConnectionId = connection.Id;
                            newEvent.Source = "outlook";
                            _db.Set<EventEntity>().Add(newEvent);
                            pageAdded.Add(newEvent);
                            pageCreated++;
                        }
                        pageRead++;
                        pageChanges.Add(new EventChangeSummary(
                            eventId,
                            graphEvent.GetProperty("subject").GetString(),
                            existing is not null ? "updated" : "created"));
                    }

                    await _db.SaveChangesAsync(ct);
                }
                catch
                {
                    // Detach this page's unsaved Added/Modified events so the final batch save
                    // cannot accidentally commit half a page. Prior successful pages persist.
                    foreach (var added in pageAdded)
                        _db.Entry(added).State = EntityState.Detached;
                    foreach (var modified in pageModified)
                    {
                        var entry = _db.Entry(modified);
                        if (entry.State == EntityState.Modified)
                            entry.CurrentValues.SetValues(entry.OriginalValues);
                        entry.State = EntityState.Unchanged;
                    }
                    throw;
                }

                // Page fully processed and saved -> commit local progress for this page
                state.Read += pageRead;
                state.Created += pageCreated;
                state.Updated += pageUpdated;
                state.Changes.AddRange(pageChanges);
                state.SuccessfulPages++;
                state.ProgressMade = true;
            }

            pagesCompleted = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OutlookReauthenticationRequiredException)
        {
            throw;
        }
        catch (GraphRequestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("同步 binding {BindingId} 分页处理出错: {ErrorType}", binding.Id, ex.GetType().Name);
            throw;
        }

        if (pagesCompleted && !ct.IsCancellationRequested)
        {
            await RunMissingVerificationAsync(
                connection, binding, state, batch, windowStart, windowEnd, generation, now, ct);
        }

        if (!ct.IsCancellationRequested)
        {
            var detail = $"读取:{state.Read} 新建:{state.Created} 更新:{state.Updated} 删除:{state.Deleted}";
            if (state.Status == "running")
                state.Status = "completed";

            state.Steps.Add(new OutlookSyncStep(binding.Id.ToString(), state.Status, detail, now));
        }
    }

    private async Task RunMissingVerificationAsync(
        OutlookConnectionEntity connection,
        OutlookCalendarBindingEntity binding,
        BindingSyncState state,
        OutlookSyncBatchEntity batch,
        DateTimeOffset windowStart,
        DateTimeOffset windowEnd,
        Guid generation,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var missing = await _db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .Where(e => e.OutlookCalendarBindingId == binding.Id
                && e.DeletedAt == null
                && e.DtStart < windowEnd
                && e.DtEnd > windowStart
                && e.LastSeenSyncGeneration != generation)
            .ToListAsync(ct);

        var modified = new List<EventEntity>();
        var pendingDeleted = 0;
        var pendingUpdated = 0;
        var pendingChanges = new List<EventChangeSummary>();

        try
        {
            foreach (var local in missing)
            {
                if (local.OutlookEventId is null)
                    continue;

                ct.ThrowIfCancellationRequested();

                JsonElement? remote;
                try
                {
                    remote = await _graph.GetEventAsync(
                        connection.Id, binding.GraphCalendarId, local.OutlookEventId, ct);
                }
                catch (OutlookReauthenticationRequiredException)
                {
                    throw;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    state.Failures.Add(new SyncFailureSummary(local.OutlookEventId, local.Title, "timeout", "Graph 读取超时"));
                    state.Failure = 1;
                    state.Status = state.ProgressMade ? "partial" : "failed";
                    continue;
                }
                catch (GraphRequestException ex)
                {
                    // 403, 429, 5xx, etc.: keep the local event, record a safe failure.
                    var code = ex.StatusCode is { } statusCode
                        ? ((int)statusCode).ToString()
                        : "graph-error";
                    state.Failures.Add(new SyncFailureSummary(local.OutlookEventId, local.Title, code, "Graph 读取失败"));
                    state.Failure = 1;
                    state.Status = state.ProgressMade ? "partial" : "failed";
                    continue;
                }
                catch (HttpRequestException)
                {
                    state.Failures.Add(new SyncFailureSummary(local.OutlookEventId, local.Title, "network", "网络错误"));
                    state.Failure = 1;
                    state.Status = state.ProgressMade ? "partial" : "failed";
                    _logger.LogWarning("Missing verification 网络错误，保留事件 {EventId}", local.OutlookEventId);
                    continue;
                }

                modified.Add(local);
                if (remote is null)
                {
                    local.DeletedAt = now;
                    local.DeletedByOperationId = batch.Id;
                    local.DeletedByOperationKind = "outlook-sync";
                    local.UpdatedAt = now;
                    pendingDeleted++;
                    pendingChanges.Add(new EventChangeSummary(local.OutlookEventId, local.Title, "deleted"));
                }
                else
                {
                    OutlookEventMapper.ApplyGraphEvent(
                        local, remote.Value, binding.Id, binding.PimCalendarId, connection.Id, generation);
                    local.UpdatedAt = now;
                    pendingUpdated++;
                    pendingChanges.Add(new EventChangeSummary(local.OutlookEventId, local.Title, "restored"));
                }
            }

            if (modified.Count > 0)
                await _db.SaveChangesAsync(ct);
        }
        catch
        {
            foreach (var entity in modified)
            {
                var entry = _db.Entry(entity);
                if (entry.State == EntityState.Modified)
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                entry.State = EntityState.Unchanged;
            }
            throw;
        }

        state.Deleted += pendingDeleted;
        state.Updated += pendingUpdated;
        state.Changes.AddRange(pendingChanges);
    }

    private static OutlookSyncBatchResponse MapBatch(OutlookSyncBatchEntity batch)
    {
        var steps = string.IsNullOrEmpty(batch.StepsJson) || batch.StepsJson == "[]"
            ? Array.Empty<OutlookSyncStep>()
            : JsonSerializer.Deserialize<OutlookSyncStep[]>(batch.StepsJson) ?? Array.Empty<OutlookSyncStep>();

        return new OutlookSyncBatchResponse(
            batch.Id,
            batch.Provider,
            batch.Status,
            batch.ReadCount,
            batch.CreatedCount,
            batch.UpdatedCount,
            batch.ConflictCount,
            batch.ConfirmationCount,
            batch.FailureCount,
            steps,
            batch.ErrorSummary,
            batch.StartedAt,
            batch.FinishedAt,
            batch.Mode,
            batch.RequestedWindowStart,
            batch.RequestedWindowEnd,
            batch.PerCalendarJson,
            batch.CancelRequested);
    }
}
