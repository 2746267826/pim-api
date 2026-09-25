using Pim.Core.Common;

namespace Pim.Module.Files.DTOs;

public sealed record FileProviderDto(
    Guid Id,
    string Provider,
    string BaseUrl,
    string? InternalBaseUrl,
    string Username,
    string Status,
    DateTimeOffset? LastSyncAt,
    string? LastError,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ClientId = null,
    string? DriveId = null,
    string? AccountId = null,
    string? AccountName = null,
    string SyncStatus = "idle",
    long SyncedItemCount = 0,
    DateTimeOffset? DeltaResetAt = null,
    DateTimeOffset? TokenExpiresAt = null);

public sealed record BindNextcloudProviderRequest(
    string BaseUrl,
    string? InternalBaseUrl,
    string Username,
    string AppPassword);

public sealed record FileProviderTestDto(bool Success, string Status, string? ErrorMessage);

public sealed record FileItemDto(
    Guid Id,
    Guid ProviderId,
    string ExternalFileId,
    string? ParentExternalFileId,
    string Path,
    string Name,
    string ItemType,
    string? MimeType,
    long? Size,
    string? Etag,
    string? ContentHash,
    Guid? CurrentVersionId,
    string? Permissions,
    bool IsDeleted,
    DateTimeOffset? DeletedAt,
    DateTimeOffset? LastSeenAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset ModifiedAt,
    DateTimeOffset SyncedAt,
    string IndexStatus,
    FileAiResultDto? Ai);

public sealed record FileVersionDto(
    Guid Id,
    Guid FileItemId,
    string ExternalVersionId,
    string? Etag,
    long? Size,
    DateTimeOffset ModifiedAt,
    string Source,
    bool IsCurrent,
    DateTimeOffset SyncedAt);

public sealed record FileAiResultDto(
    Guid Id,
    Guid FileItemId,
    Guid VersionId,
    string Summary,
    IReadOnlyList<string> Tags,
    string? Language,
    string? Sensitivity,
    DateTimeOffset GeneratedAt,
    string? Model,
    Guid? AiRequestLogId,
    IReadOnlyList<Guid> EvidenceChunkIds);

public sealed record FileSuggestionDto(
    Guid Id,
    Guid FileItemId,
    string SuggestionType,
    string Title,
    string Reason,
    decimal Confidence,
    string PayloadJson,
    string Status,
    Guid? AiRequestLogId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// 目录列表查询（REQ-2/3/8/9）。
/// <paramref name="Q"/> 为当前文件夹内的名称过滤；<paramref name="Sort"/> 取
/// <c>name</c>/<c>modified</c>/<c>size</c>，<paramref name="Order"/> 取 <c>asc</c>/<c>desc</c>，
/// <paramref name="Type"/> 取 <c>folder</c>/<c>file</c>（省略表示全部）。
/// 取值非法时回落到默认（名称升序、全部类型），不抛错——前端本地缓存可能带着旧值。
/// </summary>
public sealed record FileListQuery(
    string? Path,
    string? Q = null,
    string? Sort = null,
    string? Order = null,
    string? Type = null);
public sealed record FileSearchQuery(string? Q, string? Mode);
/// <summary>
/// 元数据搜索结果（REQ-8 / P7）。
/// <paramref name="TotalCount"/>/<paramref name="TotalPages"/> 是**新增的可选字段**：
/// 老调用方（含 MCP <c>search_files</c>）只会多拿到信息，不会少拿到字段。
/// </summary>
public sealed record FileSearchResultDto(
    IReadOnlyList<FileItemDto> Items,
    IReadOnlyList<FileChunkSearchHitDto> Chunks,
    int TotalCount = 0,
    int TotalPages = 0);
public sealed record FileChunkSearchHitDto(Guid ChunkId, Guid FileItemId, Guid VersionId, string Text, decimal Score);
public sealed record MoveFileRequest(string DestinationPath);
public sealed record CreateFolderRequest(string Path);
public sealed record CreateShareRequest(string PermissionType, int? ExpiresInDays);

/// <summary>REQ-14：创建上传会话（只创建会话，不搬字节）。</summary>
public sealed record CreateUploadSessionRequest(string Path, string FileName);

/// <summary>REQ-14：上传会话信息。uploadUrl 已预授权，分片 PUT 不得带 Authorization。</summary>
public sealed record UploadSessionDto(
    string UploadUrl,
    DateTimeOffset? ExpirationDateTime,
    string Path,
    string FileName);

/// <summary>REQ-14：上传完成登记（内容已在微软侧）。</summary>
public sealed record CompleteUploadRequest(string Path, string FileName, string? UploadedItemId = null);

/// <summary>REQ-25：手动同步已入队的即时反馈（不再等待同步跑完）。</summary>
public sealed record OneDriveSyncStartedDto(bool Started, string Message);

/// <summary>REQ-25：同步状态（顶部横幅的数据源）。</summary>
public sealed record OneDriveSyncStatusDto(
    string SyncStatus,
    string? LastError,
    DateTimeOffset? LastSyncAt,
    long SyncedItemCount);
public sealed record RenameFileRequest(string Name);
public sealed record FileOpenLinkDto(string Url, string Mode);
public sealed record VersionRestorePreviewDto(Guid FileItemId, Guid VersionId, string CurrentVersionLabel, string RestoreVersionLabel, bool RequiresConfirmation, string Summary);
public sealed record FileIndexJobDto(Guid Id, Guid FileItemId, Guid? VersionId, string Status, string Stage, int AttemptCount, string? LastError);
public sealed record FileSuggestionStatusRequest(string Status);
public sealed record FileListResponse(PagedResult<FileItemDto> Result);
