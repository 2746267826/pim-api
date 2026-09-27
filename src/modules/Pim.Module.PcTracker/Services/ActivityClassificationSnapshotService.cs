using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.DTOs;
using Pim.Module.PcTracker.Entities;

namespace Pim.Module.PcTracker.Services;

public class ActivityClassificationSnapshotService
{
    public const string ClassifierVersion = "local-v1";
    private const int MaxUniqueViolationRetries = 5;

    /// <summary>
    /// 死锁重试前的退避：让与本批次互相阻塞的那个事务先提交，避免双方同步重试再次撞车。
    /// 取值需大于 PostgreSQL 默认 <c>deadlock_timeout</c>（1s）的观测抖动，又要短到不拖慢请求。
    /// </summary>
    private static readonly TimeSpan DeadlockRetryBackoff = TimeSpan.FromMilliseconds(50);

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
        var appSignatures = await _db.Set<AppSignatureEntity>().ToListAsync(ct);

        foreach (var keyedRecord in keyedRecords)
        {
            var record = keyedRecord.Record;
            var classification = ActivityClassifier.Classify(ToContext(record), rules, _logger, categoryNamesById, appSignatures);

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

            if (auditId is null && snapshots.ContainsKey(keyedRecord.RecordKey))
            {
                // #331：非应用记录（gap/idle/afk）的历史快照可能是按旧规则判出来的
                // 「游戏」等应用类别。若照旧直接沿用，修复只会作用于新记录，
                // 存量污染会一直留在 detail / 分析等读快照的消费者里。
                // 因此这类记录即使没有审计上下文也强制按新口径重写（人工纠正仍受保护）。
                if (!PcActivityOverlapResolver.IsInactive(snapshot.RecordType)
                    || string.Equals(snapshot.CategoryName, classification.CategoryName, StringComparison.Ordinal))
                {
                    classifiedRecords[record] = ToClassificationResult(snapshot);
                    continue;
                }
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
    /// 并发防护：后台定时补齐与页面触发的 ensure 可能同时插入同一 record_key。
    /// 并发写同一批 key 会以两种方式失败，两种都必须重试，否则整批物化失败：
    /// <list type="bullet">
    ///   <item><description><b>唯一键冲突（23505）</b>：他方已写入同 key。重查该批 keys，
    ///   剔除本上下文里已成重复的 Added 实体后重试。</description></item>
    ///   <item><description><b>死锁（40P01）</b>：两批事务按不同顺序插入同一唯一索引，
    ///   PostgreSQL 选一方回滚。仅靠 23505 重试无法收敛——死锁在第 3 次尝试后以
    ///   <see cref="RetryLimitExceededException"/> 逃逸，整个物化请求 500
    ///   （实测 8 并发下可复现）。这里退避后重试整批保存。</description></item>
    /// </list>
    /// <para>
    /// 处于**外层显式事务**中时不在此处重试：死锁会让该事务进入 aborted 状态，
    /// 原地重试只会拿到 25P02。那种场景由外层 <c>ExecuteInTransactionAsync</c> 的
    /// 执行策略重跑整个工作单元（含新建事务），这里原样抛出即可。
    /// </para>
    /// </summary>
    private async Task SaveWithUniqueKeyRetryAsync(List<string> keys, CancellationToken ct)
    {
        var retryInPlace = _db.Database.CurrentTransaction is null;

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                await _db.SaveChangesAsync(ct);
                if (attempt > 0)
                {
                    _logger.LogInformation(
                        "Classification snapshot batch saved after {Attempt}/{Max} attempts (record_key contention). keys={KeyCount}",
                        attempt + 1,
                        MaxUniqueViolationRetries,
                        keys.Count);
                }

                return;
            }
            catch (Exception ex) when (retryInPlace && IsRetryableSnapshotWriteFailure(ex))
            {
                if (attempt >= MaxUniqueViolationRetries - 1)
                {
                    _logger.LogError(
                        ex,
                        "Classification snapshot batch kept failing on write contention after {Max} attempts; giving up. keys={KeyCount}",
                        MaxUniqueViolationRetries,
                        keys.Count);
                    throw;
                }

                if (IsUniqueViolation(ex))
                {
                    if (!await TryDetachConcurrentDuplicatesAsync(ex, keys, ct))
                        throw;
                }
                else
                {
                    // 死锁：给对方事务留出提交窗口，错开两个批次的重试时刻。
                    _logger.LogWarning(
                        "Classification snapshot batch hit a deadlock on attempt {Attempt}/{Max}; backing off and retrying. keys={KeyCount}",
                        attempt + 1,
                        MaxUniqueViolationRetries,
                        keys.Count);
                    await Task.Delay(DeadlockRetryBackoff, ct);
                }
            }
        }
    }

    /// <summary>
    /// 剔除本上下文里已被他方并发写入的重复 Added 实体。
    /// 返回 false 表示这次失败不是"并发重复写入"（没有可剔除的实体），调用方应原样抛出——
    /// 否则会把真实缺陷伪装成可重试的竞争。
    /// </summary>
    private async Task<bool> TryDetachConcurrentDuplicatesAsync(
        Exception failure,
        List<string> keys,
        CancellationToken ct)
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
        {
            _logger.LogWarning(
                failure,
                "Classification snapshot batch hit the record_key unique constraint but no concurrent duplicate was found among tracked entities; rethrowing. keys={KeyCount}",
                keys.Count);
            return false;
        }

        foreach (var entry in duplicates)
            entry.State = EntityState.Detached;

        _logger.LogWarning(
            "Classification snapshot batch detached {DuplicateCount} concurrently written duplicates and will retry. keys={KeyCount}",
            duplicates.Count,
            keys.Count);
        return true;
    }

    /// <summary>
    /// 可原地重试的写失败：唯一键冲突（23505）或死锁（40P01）。
    /// 沿 InnerException 链（有界深度）查找 PostgreSQL 错误码——
    /// 死锁既可能以 <see cref="DbUpdateException"/> 直接抛出，也可能被 EF 的执行策略
    /// 包装成 <see cref="RetryLimitExceededException"/>（两者都不是彼此的基类）。
    /// </summary>
    internal static bool IsRetryableSnapshotWriteFailure(Exception exception)
        => TryFindPostgresException(exception, out var postgresException)
           && postgresException.SqlState is PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.DeadlockDetected;

    private static bool IsUniqueViolation(Exception exception)
        => TryFindPostgresException(exception, out var postgresException)
           && postgresException.SqlState == PostgresErrorCodes.UniqueViolation;

    private static bool TryFindPostgresException(Exception exception, out PostgresException postgresException)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException found)
            {
                postgresException = found;
                return true;
            }
        }

        postgresException = null!;
        return false;
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

        // #235：把应用身份一并落库，使时间线 v2 不必再从 record_key 反推应用名。
        // 受保护快照（人工/纠正）同样刷新这些字段——它们描述的是「记录来源」，不随分类结论变化。
        snapshot.AppName = Truncate(record.AppName ?? record.BrowserAppName, 256);
        snapshot.AppDisplayName = Truncate(record.DisplayName, 256);
        snapshot.WindowTitle = Truncate(record.BrowserWindowTitle ?? record.Title, null);
    }

    /// <summary>按列宽截断（列宽为 null 表示不限长，如 window_title）。</summary>
    private static string? Truncate(string? value, int? maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var trimmed = value.Trim();
        if (maxLength is int limit && trimmed.Length > limit)
            return trimmed[..limit];
        return trimmed;
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
