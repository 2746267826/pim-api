using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class ActivityClassificationSnapshotService
{
    public const string ClassifierVersion = "local-v1";

    private readonly PimDbContext _db;
    private readonly ILogger<ActivityClassificationSnapshotService> _logger;

    public ActivityClassificationSnapshotService(PimDbContext db, ILogger<ActivityClassificationSnapshotService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<List<PcDetailRecord>> EnsureClassificationsAsync(
        IReadOnlyCollection<PcDetailRecord> records,
        IReadOnlyCollection<ActivityCategoryRuleEntity> rules,
        Guid? auditId,
        CancellationToken ct,
        bool saveChanges = true)
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
        var categoryNamesById = await _db.Set<PcCategoryEntity>()
            .Select(category => new { category.Id, category.Name })
            .ToDictionaryAsync(item => item.Id, item => item.Name, ct);

        foreach (var keyedRecord in keyedRecords)
        {
            var record = keyedRecord.Record;
            var classification = ActivityClassifier.Classify(ToContext(record), rules, _logger, categoryNamesById);

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

            ApplySourceMetadata(snapshot, keyedRecord);

            if (IsProtectedSnapshot(snapshot))
            {
                classifiedRecords[record] = ToClassificationResult(snapshot);
                continue;
            }

            ApplySnapshot(snapshot, keyedRecord, classification, auditId, now);
            classifiedRecords[record] = classification;
        }

        if (saveChanges && keyedRecords.Count > 0)
            await SaveWithUniqueKeyRetryAsync(keys, ct);

        return records
            .Select(record =>
            {
                return classifiedRecords.TryGetValue(record, out var classification)
                    ? ApplyClassification(record, classification)
                    : record;
            })
            .ToList();
    }

    /// <summary>
    /// 并发防护：后台定时补齐与页面触发的 ensure 可能同时插入同一 record_key，
    /// PG 唯一索引会让后提交方抛 DbUpdateException。此处重查该批 keys、剔除他方已写入的
    /// 重复实体后重试一次；仅当确实存在重复键时才重试（其他更新异常原样抛出），
    /// 重试再失败抛原始异常。
    /// </summary>
    private async Task SaveWithUniqueKeyRetryAsync(List<string> keys, CancellationToken ct)
    {
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException original)
        {
            var tracked = _db.ChangeTracker.Entries<ActivityClassificationEntity>()
                .Where(entry => entry.State == EntityState.Added)
                .ToList();

            var existingKeys = new HashSet<string>(
                await _db.Set<ActivityClassificationEntity>()
                    .Where(entity => keys.Contains(entity.RecordKey))
                    .Select(entity => entity.RecordKey)
                    .ToListAsync(ct),
                StringComparer.Ordinal);

            var duplicates = tracked
                .Where(entry => existingKeys.Contains(entry.Entity.RecordKey))
                .ToList();
            if (duplicates.Count == 0)
                throw;

            foreach (var entry in duplicates)
                entry.State = EntityState.Detached;

            try
            {
                await _db.SaveChangesAsync(ct);
            }
            catch
            {
                throw original;
            }
        }
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
            record.BucketType);
    }

    private static void ApplySnapshot(
        ActivityClassificationEntity snapshot,
        KeyedRecord keyedRecord,
        ActivityClassificationResult classification,
        Guid? auditId,
        DateTimeOffset classifiedAt)
    {
        ApplySourceMetadata(snapshot, keyedRecord);
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

    private static void ApplySourceMetadata(
        ActivityClassificationEntity snapshot,
        KeyedRecord keyedRecord)
    {
        var record = keyedRecord.Record;
        var key = PcActivityRecordKeyService.Build(record);
        snapshot.RecordType = record.RecordType;
        snapshot.DeviceId = record.DeviceId;
        snapshot.SourceEventIdsJson = key.SourceEventIdsJson;
        snapshot.RecordKeyVersion = key.KeyVersion;
        snapshot.RecordKeyStability = key.Stability;
        snapshot.SourceType = key.SourceType;
        snapshot.SourceBucketIdsJson = key.SourceBucketIdsJson;
        snapshot.InterpretationVersion = record.InterpretationVersion ?? "interpreted-aw-v1";
        snapshot.StartedAt = keyedRecord.StartedAt;
        snapshot.EndedAt = keyedRecord.EndedAt;
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
