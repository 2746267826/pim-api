using Pim.Module.PcTracker.DTOs;

namespace Pim.Module.PcTracker.Services;

public static class ActivityClassificationRecordKey
{
    public static string FromRecord(PcDetailRecord record) =>
        PcActivityRecordKeyService.Build(record).RecordKey;

    public static string SourceEventIdsJson(PcDetailRecord record) =>
        PcActivityRecordKeyService.Build(record).SourceEventIdsJson;

    public static string SourceBucketIdsJson(PcDetailRecord record) =>
        PcActivityRecordKeyService.Build(record).SourceBucketIdsJson;

    public static string KeyVersion(PcDetailRecord record) =>
        PcActivityRecordKeyService.Build(record).KeyVersion;

    public static string KeyStability(PcDetailRecord record) =>
        PcActivityRecordKeyService.Build(record).Stability;
}
