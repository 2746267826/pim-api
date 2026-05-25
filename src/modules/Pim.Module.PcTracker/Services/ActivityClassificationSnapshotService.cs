using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class ActivityClassificationSnapshotService
{
    public const string ClassifierVersion = "local-v1";

    private readonly PimDbContext _db;

    public ActivityClassificationSnapshotService(PimDbContext db)
    {
        _db = db;
    }

    public async Task<List<PcDetailRecord>> EnsureClassificationsAsync(
        IReadOnlyCollection<PcDetailRecord> records,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules,
        Guid? auditId,
        CancellationToken ct)
    {
        if (records.Count == 0)
            return [];

        var keyedRecords = records
            .Select(record => TryCreateKeyedRecord(record, out var keyedRecord) ? keyedRecord : null)
            .OfType<KeyedRecord>()
            .ToList();
        var keys = keyedRecords
            .Select(item => item.RecordKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var snapshots = keys.Count == 0
            ? new Dictionary<string, ActivityClassificationEntity>(StringComparer.Ordinal)
            : await _db.Set<ActivityClassificationEntity>()
                .Where(entity => keys.Contains(entity.RecordKey))
                .ToDictionaryAsync(entity => entity.RecordKey, StringComparer.Ordinal, ct);

        var newSnapshots = new Dictionary<string, ActivityClassificationEntity>(StringComparer.Ordinal);
        var classifiedRecords = new Dictionary<PcDetailRecord, ActivityClassificationResult>();
        var now = DateTimeOffset.UtcNow;

        foreach (var keyedRecord in keyedRecords)
        {
            var record = keyedRecord.Record;
            var classification = ActivityClassifier.Classify(ToContext(record), rules);

            if (!snapshots.TryGetValue(keyedRecord.RecordKey, out var snapshot)
                && !newSnapshots.TryGetValue(keyedRecord.RecordKey, out snapshot))
            {
                snapshot = new ActivityClassificationEntity
                {
                    Id = Guid.NewGuid(),
                    RecordKey = keyedRecord.RecordKey
                };
                _db.Set<ActivityClassificationEntity>().Add(snapshot);
                newSnapshots[keyedRecord.RecordKey] = snapshot;
            }

            if (IsProtectedSnapshot(snapshot))
            {
                classifiedRecords[record] = ToClassificationResult(snapshot);
                continue;
            }

            ApplySnapshot(snapshot, keyedRecord, classification, auditId, now);
            classifiedRecords[record] = classification;
        }

        if (keyedRecords.Count > 0)
            await _db.SaveChangesAsync(ct);

        return records
            .Select(record =>
            {
                return classifiedRecords.TryGetValue(record, out var classification)
                    ? ApplyClassification(record, classification)
                    : record;
            })
            .ToList();
    }

    private static bool TryCreateKeyedRecord(PcDetailRecord record, out KeyedRecord? keyedRecord)
    {
        keyedRecord = null;

        if (record.DurationSeconds is not > 0
            || !DateTimeOffset.TryParse(record.Start, out var startedAt)
            || !DateTimeOffset.TryParse(record.End ?? record.Start, out var endedAt))
            return false;

        keyedRecord = new KeyedRecord(
            record,
            ActivityClassificationRecordKey.FromRecord(record),
            startedAt,
            endedAt);
        return true;
    }

    private static ActivityClassificationContext ToContext(PcDetailRecord record)
    {
        var normalizedApp = AppNameNormalizer.Normalize(record.AppName ?? record.BrowserAppName ?? record.DisplayName);
        return new ActivityClassificationContext(
            record.RecordType,
            record.AppName ?? record.BrowserAppName,
            normalizedApp,
            record.Domain,
            record.Path,
            record.Title,
            record.BrowserWindowTitle ?? record.Title,
            record.IsLocalFile ? record.Path : null,
            null);
    }

    private static void ApplySnapshot(
        ActivityClassificationEntity snapshot,
        KeyedRecord keyedRecord,
        ActivityClassificationResult classification,
        Guid? auditId,
        DateTimeOffset classifiedAt)
    {
        var record = keyedRecord.Record;
        snapshot.RecordType = record.RecordType;
        snapshot.DeviceId = record.DeviceId;
        snapshot.SourceEventIdsJson = ActivityClassificationRecordKey.SourceEventIdsJson(record);
        snapshot.StartedAt = keyedRecord.StartedAt;
        snapshot.EndedAt = keyedRecord.EndedAt;
        snapshot.CategoryName = classification.CategoryName;
        snapshot.CategoryColor = classification.CategoryColor;
        snapshot.ProjectTag = classification.ProjectTag;
        snapshot.Confidence = classification.Confidence;
        snapshot.Source = classification.Source;
        snapshot.SourceRuleId = classification.SourceRuleId;
        snapshot.Explanation = classification.Explanation;
        snapshot.ClassifierVersion = ClassifierVersion;
        snapshot.ClassifiedAt = classifiedAt;
        snapshot.AuditId = auditId;
    }

    private static bool IsProtectedSnapshot(ActivityClassificationEntity snapshot) =>
        snapshot.Source is "manual" or "corrected" or "user_corrected" or "llm_corrected"
        || string.Equals(snapshot.Source, "manual", StringComparison.OrdinalIgnoreCase)
        || string.Equals(snapshot.Source, "corrected", StringComparison.OrdinalIgnoreCase)
        || string.Equals(snapshot.Source, "user_corrected", StringComparison.OrdinalIgnoreCase)
        || string.Equals(snapshot.Source, "llm_corrected", StringComparison.OrdinalIgnoreCase);

    private static ActivityClassificationResult ToClassificationResult(ActivityClassificationEntity snapshot) =>
        new(
            snapshot.CategoryName,
            snapshot.CategoryColor,
            snapshot.ProjectTag,
            snapshot.Confidence,
            snapshot.Source,
            snapshot.Explanation,
            snapshot.SourceRuleId);

    private static PcDetailRecord ApplyClassification(
        PcDetailRecord record,
        ActivityClassificationResult classification) =>
        record with
        {
            CategoryName = classification.CategoryName,
            CategoryColor = classification.CategoryColor,
            ProjectTag = classification.ProjectTag,
            ClassificationConfidence = classification.Confidence,
            ClassificationSource = classification.Source,
            ClassificationExplanation = classification.Explanation
        };

    private sealed record KeyedRecord(
        PcDetailRecord Record,
        string RecordKey,
        DateTimeOffset StartedAt,
        DateTimeOffset EndedAt);
}
