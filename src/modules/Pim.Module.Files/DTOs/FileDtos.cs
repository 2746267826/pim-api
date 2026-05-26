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
    DateTimeOffset UpdatedAt);

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

public sealed record FileListQuery(string? Path);
public sealed record FileSearchQuery(string? Q, string? Mode);
public sealed record FileSearchResultDto(IReadOnlyList<FileItemDto> Items, IReadOnlyList<FileChunkSearchHitDto> Chunks);
public sealed record FileChunkSearchHitDto(Guid ChunkId, Guid FileItemId, Guid VersionId, string Text, decimal Score);
public sealed record MoveFileRequest(string DestinationPath);
public sealed record RenameFileRequest(string Name);
public sealed record FileOpenLinkDto(string Url, string Mode);
public sealed record VersionRestorePreviewDto(Guid FileItemId, Guid VersionId, string CurrentVersionLabel, string RestoreVersionLabel, bool RequiresConfirmation, string Summary);
public sealed record FileIndexJobDto(Guid Id, Guid FileItemId, Guid? VersionId, string Status, string Stage, int AttemptCount, string? LastError);
public sealed record FileSuggestionStatusRequest(string Status);
public sealed record FileListResponse(PagedResult<FileItemDto> Result);
