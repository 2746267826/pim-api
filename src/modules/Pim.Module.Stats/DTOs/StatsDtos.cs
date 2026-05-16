namespace Pim.Module.Stats.DTOs;

public record AppUsageEntry(
    string PackageName,
    long StartTime,
    long EndTime,
    long DurationMs,
    long LastTimeUsed
);

public record UploadBatch(
    string DeviceId,
    List<AppUsageEntry> Entries
);
