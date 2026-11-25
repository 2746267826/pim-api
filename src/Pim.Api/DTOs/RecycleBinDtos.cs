namespace Pim.Api.DTOs;

public static class RecycleBinDomains
{
    public const string All = "all";
    public const string Note = "note";
    public const string File = "file";
    public const string Task = "task";
    public const string Calendar = "calendar";
}

public static class RecycleBinItemStatuses
{
    public const string Success = "success";
    public const string Failed = "failed";
    public const string SourcePending = "source_pending";
}

public sealed record RecycleBinItemDto(
    Guid Id,
    string Domain,
    string Kind,
    string Title,
    DateTimeOffset DeletedAt,
    string Source,
    string? Location,
    long? SizeBytes,
    bool Restorable,
    bool SourcePending,
    string? SourceMessage);

public sealed record RecycleBinGroupDto(string Domain, int TotalCount);

public sealed record RecycleBinPageDto(
    IReadOnlyList<RecycleBinItemDto> Items,
    IReadOnlyList<RecycleBinGroupDto> Groups,
    int TotalCount,
    int Page,
    int PageSize,
    int TotalPages);

public sealed record RecycleBinItemRef(
    string Domain,
    Guid Id,
    DateTimeOffset? ExpectedDeletedAt = null);

public sealed record RecycleBinBatchRequest(
    IReadOnlyList<RecycleBinItemRef> Items,
    Guid? OperationId = null,
    bool RestoreAsCopy = false);

public sealed record RecycleBinItemResultDto(
    int Index,
    string Domain,
    Guid Id,
    string Title,
    string Status,
    string Action,
    string? Source,
    string? Message,
    int? ErrorCode);

public sealed record RecycleBinBatchReceiptDto(
    Guid ReceiptId,
    Guid? OperationId,
    string Action,
    int Succeeded,
    int Failed,
    int SourcePending,
    IReadOnlyList<RecycleBinItemResultDto> Items);
