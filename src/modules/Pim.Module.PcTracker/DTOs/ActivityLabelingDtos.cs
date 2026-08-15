namespace Pim.Module.PcTracker.DTOs;

public sealed record ActivityLabelingQueueItem(
    string TargetType, string Target, string DisplayName, int Minutes, List<string> SampleTitles);

public sealed record ActivityLabelingQueueResponse(List<ActivityLabelingQueueItem> Items);

public sealed record ActivityLabelingRequest(
    string TargetType, string Target, Guid? CategoryId, string? CategoryName, string Scope, string? Keyword);

public sealed record ActivityLabelingResponse(bool Ok, Guid? CategoryId, string? CategoryName, string Created);

public sealed record CategoryDictionaryItemDto(Guid Id, string Name, string Color, string? Icon);
