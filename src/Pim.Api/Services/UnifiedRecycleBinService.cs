using Microsoft.EntityFrameworkCore;
using Pim.Api.DTOs;
using Pim.Core.Exceptions;
using Pim.Core.Operations;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Notes;
using Pim.Infrastructure.Storage;
using Pim.Module.Calendar.DTOs;
using Pim.Module.Calendar.Entities;
using Pim.Module.Calendar.Services;
using Pim.Module.Files.Entities;
using Pim.Module.Files.Providers.Local;
using Pim.Module.Files.Services;

namespace Pim.Api.Services;

/// <summary>
/// Read and batch orchestration for the user-accessible unified recycle bin.
/// Domain services remain authoritative: notes use their material ledger, files use
/// <see cref="FileWriteService"/> / <see cref="LocalFileStore"/>, and calendar items
/// reuse <see cref="CalendarRecycleBinService"/> restore rules.
/// </summary>
public sealed class UnifiedRecycleBinService(
    PimDbContext db,
    ICurrentUserService currentUser,
    CalendarRecycleBinService calendar,
    NativeNoteRecycleService notes,
    FileWriteService files,
    LocalFileStore localFiles,
    IAuditLogService audit,
    ILogger<UnifiedRecycleBinService>? logger = null)
{
    private static readonly string[] Domains =
    [
        RecycleBinDomains.Note,
        RecycleBinDomains.File,
        RecycleBinDomains.Task,
        RecycleBinDomains.Calendar
    ];

    private Guid UserId => currentUser.UserId ?? throw new DomainException(1002, "未登录");

    public async Task<RecycleBinPageDto> ListAsync(
        string? domain,
        string? search,
        DateTimeOffset? deletedFrom,
        DateTimeOffset? deletedTo,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var normalizedDomain = NormalizeDomainFilter(domain);
        var term = string.IsNullOrWhiteSpace(search) ? null : search.Trim();
        // Every domain is loaded even when one filter is active: the frontend renders
        // the other group counters from this response, and zeroing them made the
        // counters silently lie for every filtered view.
        var all = new List<RecycleBinItemDto>();
        all.AddRange((await notes.ListDeletedAsync(UserId, ct))
            .Where(item => Matches(term, item.Title))
            .Where(item => InRange(item.DeletedAt, deletedFrom, deletedTo))
            .Select(item => new RecycleBinItemDto(
                item.NoteId,
                RecycleBinDomains.Note,
                "note",
                item.Title,
                item.DeletedAt,
                "builtin",
                null,
                null,
                true,
                false,
                null)));

        var fileRows = await db.Set<FileItemEntity>()
            .AsNoTracking()
            .Include(row => row.Provider)
            .IgnoreQueryFilters()
            .Where(row => row.IsDeleted
                && row.Provider != null
                && row.Provider.UserId == UserId)
            .OrderByDescending(row => row.DeletedAt)
            .ToListAsync(ct);
        all.AddRange(fileRows
            .Select(row => FileItem(row))
            .Where(item => Matches(term, item.Title, item.Location))
            .Where(item => InRange(item.DeletedAt, deletedFrom, deletedTo)));

        all.AddRange(await LoadCalendarItemsAsync(term, deletedFrom, deletedTo, ct));

        var counts = Domains.ToDictionary(
            value => value,
            value => all.Count(item => item.Domain == value));
        var filtered = normalizedDomain == RecycleBinDomains.All
            ? all
            : all.Where(item => item.Domain == normalizedDomain).ToList();

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var ordered = filtered
            .OrderByDescending(item => item.DeletedAt)
            .ThenBy(item => item.Domain, StringComparer.Ordinal)
            .ThenBy(item => item.Title, StringComparer.Ordinal)
            .ToArray();
        var total = ordered.Length;
        return new RecycleBinPageDto(
            ordered.Skip((page - 1) * pageSize).Take(pageSize).ToArray(),
            Domains.Select(value => new RecycleBinGroupDto(value, counts[value])).ToArray(),
            total,
            page,
            pageSize,
            (int)Math.Ceiling(total / (double)pageSize));
    }

    public Task<RecycleBinBatchReceiptDto> RestoreAsync(
        RecycleBinBatchRequest request,
        CancellationToken ct = default) =>
        ExecuteBatchAsync("restore", request, restore: true, ct);

    public Task<RecycleBinBatchReceiptDto> PurgeAsync(
        RecycleBinBatchRequest request,
        CancellationToken ct = default) =>
        ExecuteBatchAsync("purge", request, restore: false, ct);

    private async Task<RecycleBinBatchReceiptDto> ExecuteBatchAsync(
        string action,
        RecycleBinBatchRequest request,
        bool restore,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var unique = (request.Items ?? [])
            .Where(item => item is not null)
            .Select(item => new RecycleBinItemRef(
                item.Domain?.Trim().ToLowerInvariant() ?? string.Empty,
                item.Id,
                item.ExpectedDeletedAt))
            .DistinctBy(item => (item.Domain, item.Id))
            .ToArray();
        if (unique.Length == 0)
            throw new DomainException(40001, "请选择至少一个回收站项目");
        if (unique.Length > 200)
            throw new DomainException(40001, "单次最多处理 200 个回收站项目，请分批提交");

        var results = new List<RecycleBinItemResultDto>(unique.Length);
        for (var index = 0; index < unique.Length; index++)
        {
            var item = unique[index];
            try
            {
                results.Add(await ExecuteItemAsync(
                    action,
                    restore,
                    request.RestoreAsCopy,
                    index,
                    item,
                    ct));
            }
            catch (DomainException error)
            {
                results.Add(Failed(index, item, action, await TitleAsync(item, ct), error.ErrorCode, error.Message));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                logger?.LogWarning(
                    error,
                    "Unified recycle-bin item did not reach a confirmed result: {Domain}/{Id}",
                    item.Domain,
                    item.Id);
                results.Add(Failed(
                    index,
                    item,
                    action,
                    await TitleAsync(item, ct),
                    null,
                    "服务器未能确认该项结果，请重试并核对回收站"));
            }
        }

        return new RecycleBinBatchReceiptDto(
            Guid.NewGuid(),
            request.OperationId,
            action,
            results.Count(result => result.Status == RecycleBinItemStatuses.Success),
            results.Count(result => result.Status == RecycleBinItemStatuses.Failed),
            results.Count(result => result.Status == RecycleBinItemStatuses.SourcePending),
            results);
    }

    private async Task<RecycleBinItemResultDto> ExecuteItemAsync(
        string action,
        bool restore,
        bool restoreAsCopy,
        int index,
        RecycleBinItemRef item,
        CancellationToken ct)
    {
        if (item.Id == Guid.Empty)
            throw new DomainException(40001, "项目标识无效");

        return item.Domain switch
        {
            RecycleBinDomains.Note => await ExecuteNoteAsync(action, restore, restoreAsCopy, index, item, ct),
            RecycleBinDomains.File => await ExecuteFileAsync(action, restore, restoreAsCopy, index, item, ct),
            RecycleBinDomains.Task or RecycleBinDomains.Calendar =>
                await ExecuteCalendarAsync(action, restore, restoreAsCopy, index, item, ct),
            _ => throw new DomainException(40001, "不支持的回收站领域")
        };
    }

    private async Task<RecycleBinItemResultDto> ExecuteNoteAsync(
        string action,
        bool restore,
        bool restoreAsCopy,
        int index,
        RecycleBinItemRef item,
        CancellationToken ct)
    {
        var id = item.Id;
        if (restore)
        {
            if (restoreAsCopy)
                throw new DomainException(40001, "笔记不支持恢复为副本；请恢复原笔记或保留副本后手动新建");
            var result = await notes.RestoreAsync(UserId, id, item.ExpectedDeletedAt, ct);
            return Success(index, RecycleBinDomains.Note, id, result.Title, action, "builtin", result.Message);
        }

        var title = await NoteTitleAsync(id, ct);
        var purge = await notes.PurgeAsync(UserId, id, item.ExpectedDeletedAt, ct);
        var alreadyGone = purge is
        {
            MaterialVersions: 0,
            AsrTasks: 0,
            DerivedTasks: 0,
            AiCalls: 0,
            AiImages: 0,
            AiAudio: 0
        };
        return Success(
            index,
            RecycleBinDomains.Note,
            id,
            title,
            action,
            "builtin",
            alreadyGone
                ? "笔记已永久删除，无需重复提交"
                : "笔记及其原稿、材料、转写与派生数据已永久删除");
    }

    private async Task<RecycleBinItemResultDto> ExecuteFileAsync(
        string action,
        bool restore,
        bool restoreAsCopy,
        int index,
        RecycleBinItemRef item,
        CancellationToken ct)
    {
        var id = item.Id;
        var row = await db.Set<FileItemEntity>()
            .Include(entity => entity.Provider)
            .SingleOrDefaultAsync(entity => entity.Id == id
                && entity.Provider != null
                && entity.Provider.UserId == UserId, ct);
        if (row is null)
        {
            if (!restore && item.ExpectedDeletedAt.HasValue)
                return Success(index, RecycleBinDomains.File, id, "文件", action, "builtin", "文件已永久删除，无需重复提交");
            throw new DomainException(40401, "文件不存在");
        }
        var provider = row.Provider ?? throw new DomainException(40401, "文件来源不存在");
        var source = SourceOf(provider.Provider);
        if (!row.IsDeleted)
        {
            if (restore)
                return Success(index, RecycleBinDomains.File, id, row.Name, action, source, "文件已恢复，无需重复提交");
            throw new DomainException(40901, "文件不在回收站，不能永久删除");
        }

        EnsureExpectedDeletedAt(row.DeletedAt ?? row.ModifiedAt, item.ExpectedDeletedAt, "文件");

        if (source != "builtin")
        {
            var label = ProviderLabel(provider.Provider);
            return new RecycleBinItemResultDto(
                index,
                RecycleBinDomains.File,
                id,
                row.Name,
                RecycleBinItemStatuses.SourcePending,
                action,
                source,
                $"{label} 的恢复与永久清理需由外部来源确认，本条未在本地误报成功",
                null);
        }

        if (restore)
        {
            if (restoreAsCopy)
                throw new DomainException(40001, "文件不支持恢复为副本；请在网盘中复制后再同步");
            var restored = await files.RestoreAsync(id, item.ExpectedDeletedAt, ct);
            return Success(index, RecycleBinDomains.File, id, row.Name, action, source, $"文件已恢复到 {restored.Path}");
        }

        if (provider.Provider != LocalFileProvider.Key)
            throw new DomainException(40901, "只能永久清理内置存储文件");

        return await AdvisoryLock.RunAsync(
            db,
            FileRecycleLock.KeyFor(UserId, provider.Id),
            () => PurgeBuiltinFileAsync(index, action, id, item.ExpectedDeletedAt, ct),
            ct);
    }

    private async Task<RecycleBinItemResultDto> PurgeBuiltinFileAsync(
        int index,
        string action,
        Guid id,
        DateTimeOffset? expectedDeletedAt,
        CancellationToken ct)
    {
        var row = await db.Set<FileItemEntity>()
            .Include(entity => entity.Provider)
            .SingleOrDefaultAsync(entity => entity.Id == id
                && entity.Provider != null
                && entity.Provider.UserId == UserId, ct);
        if (row is null)
            return Success(index, RecycleBinDomains.File, id, "文件", action, "builtin", "文件已永久删除，无需重复提交");
        var provider = row.Provider ?? throw new DomainException(40401, "文件来源不存在");
        if (provider.Provider != LocalFileProvider.Key)
            throw new DomainException(40901, "只能永久清理内置存储文件");
        if (!row.IsDeleted)
            throw new DomainException(40901, "文件不在回收站，不能永久删除");
        EnsureExpectedDeletedAt(row.DeletedAt ?? row.ModifiedAt, expectedDeletedAt, "文件");

        // Blob removal is idempotent and runs while the catalog lock is held, before the
        // catalog mutation. If it fails, the catalog and recycle-bin row remain so the
        // user can retry; if a later step fails, retrying recomputes the same keys.
        var blobKeys = await localFiles.PurgeTrashedAsync(
            UserId,
            provider.Id,
            row.ExternalFileId,
            async (keys, token) =>
            {
                foreach (var blobKey in keys)
                {
                    await localFiles.Binaries.DeleteAsync(
                        UserId,
                        ManagedBinaryStore.Files,
                        provider.Id,
                        blobKey,
                        token);
                }
            },
            ct);

        var prefix = row.Path.TrimEnd('/') + "/";
        var metadataRows = await db.Set<FileItemEntity>()
            .Where(candidate => candidate.ProviderId == provider.Id
                && candidate.IsDeleted
                && (candidate.Id == row.Id
                    || candidate.Path == row.Path
                    || candidate.Path.StartsWith(prefix)))
            .ToListAsync(ct);
        db.Set<FileItemEntity>().RemoveRange(metadataRows);
        audit.Enqueue(new CreateAuditLogRequest(
            UserId,
            AuditActorType.User,
            "files.recycle_bin.purge",
            "file_item",
            row.Id.ToString("D"),
            "recycle-bin",
            AuditResult.Success,
            null,
            null,
            null,
            new Dictionary<string, string>
            {
                ["metadataRows"] = metadataRows.Count.ToString(),
                ["blobs"] = blobKeys.Count.ToString()
            },
            null,
            null));
        await db.SaveChangesAsync(ct);
        return Success(index, RecycleBinDomains.File, id, row.Name, action, "builtin", "内置文件及服务器内容已永久删除");
    }

    private async Task<RecycleBinItemResultDto> ExecuteCalendarAsync(
        string action,
        bool restore,
        bool restoreAsCopy,
        int index,
        RecycleBinItemRef item,
        CancellationToken ct)
    {
        var target = await CalendarTargetAsync(item.Domain, item.Id, ct);
        if (target is null)
        {
            if (!restore && item.ExpectedDeletedAt.HasValue)
                return Success(
                    index,
                    item.Domain,
                    item.Id,
                    "日历项",
                    action,
                    "pim",
                    "日历项已永久删除，无需重复提交");
            throw new DomainException(40401, "日历项不存在");
        }
        if (restore && target.Active)
        {
            return Success(
                index,
                item.Domain,
                item.Id,
                target.Title,
                action,
                target.Type,
                "该日历项已恢复，无需重复提交");
        }

        if (target.DeletedAt is not { } deletedAt)
            throw new DomainException(40901, "日历项状态已变化，请刷新回收站后重试");
        EnsureExpectedDeletedAt(deletedAt, item.ExpectedDeletedAt, "日历项");

        if (restore)
        {
            var result = await calendar.RestoreWithCasAsync(
                target.Type,
                item.Id,
                new CalendarRestoreRequest(restoreAsCopy),
                item.ExpectedDeletedAt,
                ct);
            return Success(
                index,
                item.Domain,
                item.Id,
                target.Title,
                action,
                target.Type,
                result.Message);
        }

        _ = await calendar.PurgeAsync(target.Type, item.Id, item.ExpectedDeletedAt, ct);
        return Success(
            index,
            item.Domain,
            item.Id,
            target.Title,
            action,
            target.Type,
            "日历项已永久删除");
    }

    private async Task<IReadOnlyList<RecycleBinItemDto>> LoadCalendarItemsAsync(
        string? search,
        DateTimeOffset? deletedFrom,
        DateTimeOffset? deletedTo,
        CancellationToken ct)
    {
        var result = new List<RecycleBinItemDto>();
        var page = 1;
        while (true)
        {
            var batch = await calendar.ListAsync(
                "all",
                search,
                deletedFrom,
                deletedTo,
                page,
                100,
                ct);
            result.AddRange(batch.Items.Select(CalendarItem));
            if (page >= batch.TotalPages || batch.Items.Count == 0) break;
            page++;
        }
        return result;
    }

    private async Task<CalendarTarget?> CalendarTargetAsync(string domain, Guid id,
        CancellationToken ct)
    {
        var task = await db.Set<TaskEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.Id == id && row.UserId == UserId)
            .Select(row => new { row.Title, row.DeletedAt })
            .SingleOrDefaultAsync(ct);
        if (task is not null)
        {
            return domain == RecycleBinDomains.Task
                ? new CalendarTarget("task", task.Title, task.DeletedAt is null, task.DeletedAt)
                : null;
        }

        var evt = await db.Set<EventEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.Id == id && row.Calendar.UserId == UserId)
            .Select(row => new { row.Title, row.DeletedAt })
            .SingleOrDefaultAsync(ct);
        if (evt is not null)
        {
            return domain == RecycleBinDomains.Calendar
                ? new CalendarTarget("event", evt.Title, evt.DeletedAt is null, evt.DeletedAt)
                : null;
        }

        var book = await db.Set<CalendarEntity>()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(row => row.Id == id && row.UserId == UserId)
            .Select(row => new { row.Name, row.Kind, row.DeletedAt })
            .SingleOrDefaultAsync(ct);
        if (book is null) return null;
        var expectedDomain = book.Kind == "task" ? RecycleBinDomains.Task : RecycleBinDomains.Calendar;
        if (domain != expectedDomain) return null;
        return new CalendarTarget(
            book.Kind == "task" ? "task-book" : "calendar",
            book.Name,
            book.DeletedAt is null,
            book.DeletedAt);
    }

    private async Task<string> TitleAsync(RecycleBinItemRef item, CancellationToken ct)
    {
        try
        {
            return item.Domain switch
            {
                RecycleBinDomains.Note => await NoteTitleAsync(item.Id, ct),
                RecycleBinDomains.File => await db.Set<FileItemEntity>()
                    .Where(row => row.Id == item.Id && row.Provider != null && row.Provider.UserId == UserId)
                    .Select(row => row.Name)
                    .SingleOrDefaultAsync(ct) ?? "未知文件",
                RecycleBinDomains.Task or RecycleBinDomains.Calendar =>
                    (await CalendarTargetAsync(item.Domain, item.Id, ct))?.Title ?? "未知日历项",
                _ => "未知项目"
            };
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger?.LogDebug(error, "Unable to resolve recycle-bin receipt title for {Domain}/{Id}",
                item.Domain, item.Id);
            return "未知项目";
        }
    }

    private async Task<string> NoteTitleAsync(Guid id, CancellationToken ct) =>
        (await notes.ListDeletedAsync(UserId, ct))
            .FirstOrDefault(item => item.NoteId == id)?.Title
        ?? "未知笔记";

    private static RecycleBinItemDto CalendarItem(CalendarRecycleBinItem item)
    {
        var domain = item.Type is "task" or "task-book"
            ? RecycleBinDomains.Task
            : RecycleBinDomains.Calendar;
        return new RecycleBinItemDto(
            item.Id,
            domain,
            item.Type,
            item.Title,
            item.DeletedAt,
            string.IsNullOrWhiteSpace(item.Source) ? "manual" : item.Source,
            item.BookName,
            null,
            true,
            false,
            null);
    }

    private static RecycleBinItemDto FileItem(FileItemEntity row)
    {
        var source = SourceOf(row.Provider?.Provider);
        var pending = source != "builtin";
        return new RecycleBinItemDto(
            row.Id,
            RecycleBinDomains.File,
            row.ItemType,
            row.Name,
            row.DeletedAt ?? row.ModifiedAt,
            source,
            row.Path,
            row.Size,
            !pending,
            pending,
            pending
                ? $"{ProviderLabel(row.Provider?.Provider)} 的恢复与永久清理需由外部来源确认"
                : null);
    }

    private static string SourceOf(string? provider) =>
        provider?.ToLowerInvariant() switch
        {
            "local" or "builtin" or null or "" => "builtin",
            "onedrive" => "onedrive",
            "google" => "google",
            var value => value
        };

    private static string ProviderLabel(string? provider) =>
        provider?.ToLowerInvariant() switch
        {
            "onedrive" => "OneDrive",
            "google" => "Google Drive",
            "local" or "builtin" => "内置存储",
            _ => provider ?? "外部来源"
        };

    private static bool Matches(string? term, params string?[] values) =>
        term is null || values.Any(value =>
            value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);

    private static bool InRange(
        DateTimeOffset value,
        DateTimeOffset? from,
        DateTimeOffset? to) =>
        (!from.HasValue || value >= from.Value) && (!to.HasValue || value <= to.Value);

    private static string NormalizeDomainFilter(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return RecycleBinDomains.All;
        var value = domain.Trim().ToLowerInvariant();
        return value is RecycleBinDomains.All
            or RecycleBinDomains.Note
            or RecycleBinDomains.File
            or RecycleBinDomains.Task
            or RecycleBinDomains.Calendar
            ? value
            : throw new DomainException(40001, "不支持的回收站领域筛选");
    }

    private static void EnsureExpectedDeletedAt(
        DateTimeOffset actual,
        DateTimeOffset? expected,
        string label)
    {
        if (expected.HasValue && expected.Value != actual)
            throw new DomainException(40901, $"{label}状态已变化，请刷新回收站后重试");
    }

    private static RecycleBinItemResultDto Success(
        int index,
        string domain,
        Guid id,
        string title,
        string action,
        string source,
        string message) =>
        new(index, domain, id, title, RecycleBinItemStatuses.Success,
            action, source, message, null);

    private static RecycleBinItemResultDto Failed(
        int index,
        RecycleBinItemRef item,
        string action,
        string title,
        int? errorCode,
        string message) =>
        new(index, item.Domain, item.Id, title, RecycleBinItemStatuses.Failed,
            action, null, message, errorCode);

    private sealed record CalendarTarget(
        string Type,
        string Title,
        bool Active,
        DateTimeOffset? DeletedAt);
}
