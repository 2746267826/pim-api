using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Immutable derived versions plus per-artifact head pointers on the persistent NativeNotes volume.
/// Generated versions never advance a head; only explicit manual revisions/selections do.
/// </summary>
public sealed class NoteDerivedStore(IOptions<NoteMaterialUploadOptions> options, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxArtifactScan = 8192;
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string Root => Path.Combine(Path.GetFullPath(options.Value.StorageRoot), "derived");

    internal async Task<NoteDerivedTaskState> CreateTaskAsync(Guid owner, StartNoteDerivedTaskRequest request,
        NoteDerivedPlan plan, string fingerprint, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var existing = await TryReadTaskAsync(owner, request.TaskId, ct);
        if (existing is not null)
        {
            if (existing.Fingerprint != fingerprint)
                throw Conflict("任务标识已经绑定另一份派生输入，原任务保留");
            var existingRecord = await EnsureArtifactUnlockedAsync(owner, existing.NoteId,
                NoteDerivedArtifactKind.CompleteRecord, ct);
            var existingAnalysis = await EnsureArtifactUnlockedAsync(owner, existing.NoteId,
                NoteDerivedArtifactKind.IndependentAnalysis, ct);
            if (existingRecord.ArtifactId != existing.CompleteRecordArtifactId ||
                existingAnalysis.ArtifactId != existing.IndependentAnalysisArtifactId)
                throw new InvalidDataException("Derived task artifact identity no longer matches storage");
            return existing;
        }

        var tasks = Directory.Exists(TasksFolder(owner))
            ? Directory.GetFiles(TasksFolder(owner), "*.json")
            : [];
        if (tasks.Length >= 200) throw new DomainException(41301, "派生任务数量达到200，请归档后继续");
        var active = 0;
        foreach (var path in tasks)
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id)) continue;
            var state = await ReadTaskFileAsync(path, owner, id, ct);
            if (IsActive(state)) active++;
        }
        if (active >= 10) throw new DomainException(41301, "本账号待处理派生任务已达10个，请等待或停止部分任务");

        var record = await EnsureArtifactUnlockedAsync(owner, plan.Input.NoteId,
            NoteDerivedArtifactKind.CompleteRecord, ct);
        var analysis = await EnsureArtifactUnlockedAsync(owner, plan.Input.NoteId,
            NoteDerivedArtifactKind.IndependentAnalysis, ct);
        var now = _clock.GetUtcNow();
        var task = new NoteDerivedTaskState
        {
            Owner = owner,
            Request = request,
            Fingerprint = fingerprint,
            NoteId = plan.Input.NoteId,
            InputSha256 = plan.Input.InputSha256,
            ManifestSha256 = plan.Input.ManifestSha256,
            CreatedAt = now,
            UpdatedAt = now,
            Blocked = plan.Steps.Count == 0 || plan.InitialCoverage.Issues.Count > 0,
            Plan = plan,
            Coverage = plan.InitialCoverage,
            Issues = plan.InitialCoverage.Issues.ToList(),
            Steps = plan.Steps.Select(step => new NoteDerivedStepState
            {
                Key = step.Key,
                Kind = step.Kind,
                DependsOn = step.DependsOn,
                Images = step.Images,
                InputCharacters = step.InputCharacters,
                Attempts = [new()]
            }).ToList(),
            CompleteRecordArtifactId = record.ArtifactId,
            IndependentAnalysisArtifactId = analysis.ArtifactId
        };
        await SaveTaskAsync(task, ct);
        return task;
    }

    internal async Task<NoteDerivedTaskState> ReadTaskAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        if (taskId == Guid.Empty) throw Invalid("派生任务标识无效");
        var state = await TryReadTaskAsync(owner, taskId, ct);
        return state ?? throw new DomainException(40401, "派生任务不存在");
    }

    internal async Task<NoteDerivedTaskState> UpdateTaskAsync(Guid owner, Guid taskId,
        Action<NoteDerivedTaskState> update, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var state = await ReadTaskAsync(owner, taskId, ct);
        var before = JsonSerializer.Serialize(state, Json);
        update(state);
        var after = JsonSerializer.Serialize(state, Json);
        if (before == after) return state;
        state.Revision = Guid.NewGuid();
        state.UpdatedAt = _clock.GetUtcNow();
        await SaveTaskAsync(state, ct);
        return state;
    }

    internal async Task<IReadOnlyList<NoteDerivedTaskState>> ListTasksAsync(Guid owner, Guid? noteId,
        CancellationToken ct)
    {
        if (!Directory.Exists(TasksFolder(owner))) return [];
        var result = new List<NoteDerivedTaskState>();
        foreach (var path in Directory.EnumerateFiles(TasksFolder(owner), "*.json"))
        {
            if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id)) continue;
            var state = await ReadTaskFileAsync(path, owner, id, ct);
            if (noteId is null || state.NoteId == noteId) result.Add(state);
        }
        return result.OrderByDescending(x => x.CreatedAt).ToArray();
    }

    internal IEnumerable<(Guid Owner, Guid Id)> EnumerateTasks(Guid? owner = null)
    {
        if (!Directory.Exists(Root)) yield break;
        var owners = owner.HasValue ? [owner.Value] : Directory.EnumerateDirectories(Root)
            .Select(Path.GetFileName)
            .Where(x => Guid.TryParseExact(x, "D", out _))
            .Select(x => Guid.Parse(x!))
            .ToArray();
        foreach (var account in owners)
        {
            if (!Directory.Exists(TasksFolder(account))) continue;
            foreach (var path in Directory.EnumerateFiles(TasksFolder(account), "*.json"))
                if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id))
                    yield return (account, id);
        }
    }

    internal FileStream? TryRunLease(Guid owner, Guid taskId)
    {
        if (taskId == Guid.Empty) return null;
        Directory.CreateDirectory(TasksFolder(owner));
        try
        {
            return new(TaskPath(owner, taskId) + ".running", FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    internal async Task<NoteDerivedArtifactState> EnsureArtifactAsync(Guid owner, Guid noteId,
        NoteDerivedArtifactKind kind, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        return await EnsureArtifactUnlockedAsync(owner, noteId, kind, ct);
    }

    internal async Task<NoteDerivedArtifactState> GetArtifactAsync(Guid owner, Guid artifactId,
        CancellationToken ct)
    {
        if (artifactId == Guid.Empty) throw Invalid("派生产物标识无效");
        return await ReadArtifactByIdAsync(owner, artifactId, ct);
    }

    internal async Task<IReadOnlyList<NoteDerivedArtifactState>> ListArtifactsAsync(Guid owner, Guid noteId,
        CancellationToken ct)
    {
        var result = new List<NoteDerivedArtifactState>();
        foreach (var kind in Enum.GetValues<NoteDerivedArtifactKind>())
        {
            var path = ArtifactPath(owner, noteId, kind);
            if (!File.Exists(path)) continue;
            result.Add(await ReadArtifactFileAsync(path, owner, noteId, kind, ct));
        }
        return result;
    }

    internal async Task<NoteDerivedVersionState> AppendGeneratedAsync(Guid owner, Guid artifactId,
        NoteDerivedVersionDraft draft, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var artifact = await ReadArtifactByIdAsync(owner, artifactId, ct);
        if (draft.VersionId == Guid.Empty) throw Invalid("派生版本标识无效");
        if (draft.Kind != artifact.Kind) throw Conflict("派生版本类型与产物不一致");
        var existing = await TryReadVersionAsync(owner, artifact, draft.VersionId, ct);
        var idempotencyHash = GeneratedFingerprint(artifact.ArtifactId, draft);
        if (existing is not null)
        {
            if (existing.Author != NoteDerivedVersionAuthor.Model ||
                existing.IdempotencyHash != idempotencyHash)
                throw Conflict("派生版本标识已绑定另一份内容，原版本保留");
            return existing;
        }

        var contentHash = Hash(draft.ContentMarkdown);
        var version = new NoteDerivedVersionState
        {
            Owner = owner,
            ArtifactId = artifact.ArtifactId,
            NoteId = artifact.NoteId,
            Kind = artifact.Kind,
            VersionId = draft.VersionId,
            Author = NoteDerivedVersionAuthor.Model,
            IdempotencyHash = idempotencyHash,
            ParentVersionId = draft.ParentVersionId,
            ObservedHeadVersionId = artifact.HeadVersionId,
            SourceCompleteRecordVersionId = draft.SourceCompleteRecordVersionId,
            SourceVersionId = draft.SourceVersionId,
            GenerationTaskId = draft.GenerationTaskId,
            ContentMarkdown = draft.ContentMarkdown,
            ContentSha256 = contentHash,
            InputSha256 = draft.InputSha256,
            ManifestSha256 = draft.ManifestSha256,
            PlanVersion = draft.PlanVersion,
            PromptVersion = draft.PromptVersion,
            AiCallLogId = draft.AiCallLogId,
            ModelProfileId = draft.ModelProfileId,
            ModelRevision = draft.ModelRevision,
            ModelEndpoint = draft.ModelEndpoint,
            Coverage = draft.Coverage,
            CreatedAt = _clock.GetUtcNow()
        };
        await SaveVersionAsync(version, ct);
        artifact.UpdatedAt = version.CreatedAt;
        await SaveArtifactAsync(artifact, ct);
        return version;
    }

    internal async Task<NoteDerivedMutationDto> AppendManualAsync(Guid owner, Guid artifactId,
        SaveNoteDerivedManualRevisionRequest request, CancellationToken ct)
    {
        if (request.RevisionId == Guid.Empty) throw Invalid("人工修订标识无效");
        if (request.ContentMarkdown is null ||
            request.ContentMarkdown.Length > NoteDerivedLimits.MaxDerivedContentCharacters)
            throw new DomainException(41301, "人工修订正文超过允许长度，原稿保留");

        await using var lease = await LockAsync(owner, ct);
        var artifact = await ReadArtifactByIdAsync(owner, artifactId, ct);
        var existing = await TryReadVersionAsync(owner, artifact, request.RevisionId, ct);
        var idempotencyHash = ManualFingerprint(artifact.ArtifactId, request);
        if (existing is not null)
        {
            if (existing.Author != NoteDerivedVersionAuthor.User ||
                existing.IdempotencyHash != idempotencyHash)
                throw Conflict("人工修订标识已绑定另一份内容，原稿保留");
            return new(await DescribeArtifactAsync(owner, artifact, ct), existing.VersionId,
                existing.HasConflict, artifact.HeadVersionId == existing.VersionId);
        }

        if (artifact.HeadVersionId is null)
            throw Conflict("请先明确选择一个派生版本，再创建人工修订");
        if (request.ExpectedHeadVersionId is null)
            throw Conflict("人工修订必须绑定当前人工/选中版本");
        var parent = await TryReadVersionAsync(owner, artifact, request.ExpectedHeadVersionId.Value, ct)
            ?? throw Conflict("提交所基于的派生版本不存在，请刷新后重试");

        var matches = artifact.HeadRevision == request.ExpectedHeadRevision &&
            artifact.HeadVersionId == request.ExpectedHeadVersionId;
        var contentHash = Hash(request.ContentMarkdown);
        var version = new NoteDerivedVersionState
        {
            Owner = owner,
            ArtifactId = artifact.ArtifactId,
            NoteId = artifact.NoteId,
            Kind = artifact.Kind,
            VersionId = request.RevisionId,
            Author = NoteDerivedVersionAuthor.User,
            IdempotencyHash = idempotencyHash,
            ParentVersionId = parent.VersionId,
            ObservedHeadVersionId = artifact.HeadVersionId,
            SourceCompleteRecordVersionId = parent.SourceCompleteRecordVersionId,
            SourceVersionId = parent.SourceVersionId,
            GenerationTaskId = null,
            ContentMarkdown = request.ContentMarkdown,
            ContentSha256 = contentHash,
            InputSha256 = parent.InputSha256,
            ManifestSha256 = parent.ManifestSha256,
            PlanVersion = parent.PlanVersion,
            PromptVersion = parent.PromptVersion,
            Coverage = parent.Coverage,
            HasConflict = !matches,
            CreatedAt = _clock.GetUtcNow()
        };
        await SaveVersionAsync(version, ct);
        if (matches)
        {
            artifact.HeadVersionId = version.VersionId;
            artifact.HeadRevision = Guid.NewGuid();
        }
        artifact.UpdatedAt = version.CreatedAt;
        await SaveArtifactAsync(artifact, ct);
        return new(await DescribeArtifactAsync(owner, artifact, ct), version.VersionId,
            version.HasConflict, artifact.HeadVersionId == version.VersionId);
    }

    internal async Task<NoteDerivedArtifactDto> SelectHeadAsync(Guid owner, Guid artifactId,
        SelectNoteDerivedVersionRequest request, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var artifact = await ReadArtifactByIdAsync(owner, artifactId, ct);
        var version = await TryReadVersionAsync(owner, artifact, request.VersionId, ct)
            ?? throw new DomainException(40401, "派生版本不存在");
        if (artifact.HeadRevision != request.ExpectedHeadRevision)
            throw Conflict("派生产物 head 已变化，请重新比较后选择；当前版本保留");
        if (artifact.HeadVersionId != version.VersionId)
        {
            artifact.HeadVersionId = version.VersionId;
            artifact.HeadRevision = Guid.NewGuid();
            artifact.UpdatedAt = _clock.GetUtcNow();
            await SaveArtifactAsync(artifact, ct);
        }
        return await DescribeArtifactAsync(owner, artifact, ct);
    }

    internal async Task<NoteDerivedVersionDto> GetVersionAsync(Guid owner, Guid artifactId, Guid versionId,
        CancellationToken ct)
    {
        var artifact = await ReadArtifactByIdAsync(owner, artifactId, ct);
        var version = await TryReadVersionAsync(owner, artifact, versionId, ct)
            ?? throw new DomainException(40401, "派生版本不存在");
        return DescribeVersion(artifact, version);
    }

    internal async Task<NoteDerivedArtifactDto> DescribeArtifactAsync(Guid owner, Guid artifactId,
        CancellationToken ct)
    {
        var artifact = await ReadArtifactByIdAsync(owner, artifactId, ct);
        return await DescribeArtifactAsync(owner, artifact, ct);
    }

    internal static string DescribePhase(NoteDerivedTaskState state)
    {
        if (state.Blocked) return "Blocked";
        if (state.Steps.Count > 0 && state.Steps.All(x => x.Current.Status == "Succeeded")) return "Succeeded";
        if (state.StopRequested) return "Stopped";
        if (state.Steps.Any(x => x.Current.Status == "Running")) return "Running";
        if (state.Steps.Any(x => x.Current.Status == "Queued")) return "Queued";
        return "NeedsAttention";
    }

    internal static bool IsActive(NoteDerivedTaskState state) =>
        !state.Blocked && state.Steps.Any(x => x.Current.Status == "Running" ||
            !state.StopRequested && x.Current.Status == "Queued");

    internal static NoteDerivedTaskDto DescribeTask(NoteDerivedTaskState state) => new(
        state.Request.TaskId,
        state.Revision,
        state.NoteId,
        state.Request.VersionId,
        state.Request.AsrTaskId,
        state.Plan.Input.AsrRevision,
        state.InputSha256,
        state.ManifestSha256,
        DescribePhase(state),
        state.Request.Consent,
        state.Coverage,
        [
            new(state.CompleteRecordArtifactId, NoteDerivedArtifactKind.CompleteRecord, null),
            new(state.IndependentAnalysisArtifactId, NoteDerivedArtifactKind.IndependentAnalysis, null)
        ],
        state.Issues,
        state.Steps.Take(10).Select(DescribeStep).ToArray(),
        state.Plan.PlanVersion,
        state.CreatedAt,
        state.UpdatedAt,
        state.StopRequested);

    internal static NoteDerivedStepDto DescribeStep(NoteDerivedStepState step) => new(
        step.Key,
        step.Kind,
        step.Current.Status,
        step.Attempts.Select(x => new NoteDerivedAttemptDto(
            x.RequestId, x.LogId, x.Status, x.ErrorCode, x.ErrorMessage)).ToArray());

    private async Task<NoteDerivedArtifactState> EnsureArtifactUnlockedAsync(Guid owner, Guid noteId,
        NoteDerivedArtifactKind kind, CancellationToken ct)
    {
        if (noteId == Guid.Empty) throw Invalid("笔记标识无效");
        var path = ArtifactPath(owner, noteId, kind);
        if (File.Exists(path))
        {
            var existing = await ReadArtifactFileAsync(path, owner, noteId, kind, ct);
            await EnsureLocatorAsync(existing, ct);
            return existing;
        }
        var now = _clock.GetUtcNow();
        var artifact = new NoteDerivedArtifactState
        {
            Owner = owner,
            ArtifactId = Guid.NewGuid(),
            NoteId = noteId,
            Kind = kind,
            CreatedAt = now,
            UpdatedAt = now
        };
        await SaveArtifactAsync(artifact, ct);
        return artifact;
    }

    private async Task<NoteDerivedArtifactState> ReadArtifactByIdAsync(Guid owner, Guid artifactId,
        CancellationToken ct)
    {
        var locatorPath = LocatorPath(owner, artifactId);
        if (File.Exists(locatorPath))
        {
            try
            {
                var locator = await ReadAsync<ArtifactLocator>(locatorPath, ct);
                if (locator.ArtifactId == artifactId)
                {
                    var artifact = await ReadArtifactFileAsync(ArtifactPath(owner, locator.NoteId, locator.Kind),
                        owner, locator.NoteId, locator.Kind, ct);
                    if (artifact.ArtifactId == artifactId) return artifact;
                }
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or DomainException)
            {
                // A stale or interrupted locator is repaired below from the owner's bounded artifact set.
            }
        }
        return await RecoverArtifactLocatorAsync(owner, artifactId, ct)
            ?? throw new DomainException(40401, "派生产物不存在");
    }

    private async Task<NoteDerivedArtifactState> ReadArtifactFileAsync(string path, Guid owner, Guid noteId,
        NoteDerivedArtifactKind kind, CancellationToken ct)
    {
        if (!File.Exists(path)) throw new DomainException(40401, "派生产物不存在");
        var artifact = await ReadAsync<NoteDerivedArtifactState>(path, ct);
        if (artifact.SchemaVersion != 1 || artifact.Owner != owner || artifact.NoteId != noteId ||
            artifact.Kind != kind || artifact.ArtifactId == Guid.Empty)
            throw new InvalidDataException("Invalid derived artifact state");
        return artifact;
    }

    private async Task SaveArtifactAsync(NoteDerivedArtifactState artifact, CancellationToken ct)
    {
        await WriteAsync(ArtifactPath(artifact.Owner, artifact.NoteId, artifact.Kind), artifact, ct);
        await WriteLocatorAsync(artifact, ct);
    }

    private async Task EnsureLocatorAsync(NoteDerivedArtifactState artifact, CancellationToken ct)
    {
        var path = LocatorPath(artifact.Owner, artifact.ArtifactId);
        if (File.Exists(path))
        {
            try
            {
                var locator = await ReadAsync<ArtifactLocator>(path, ct);
                if (locator.ArtifactId == artifact.ArtifactId && locator.NoteId == artifact.NoteId &&
                    locator.Kind == artifact.Kind) return;
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
            {
                // Rewrite only the derived index; the immutable artifact remains authoritative.
            }
        }
        await WriteLocatorAsync(artifact, ct);
    }

    private Task WriteLocatorAsync(NoteDerivedArtifactState artifact, CancellationToken ct) =>
        WriteAsync(LocatorPath(artifact.Owner, artifact.ArtifactId),
            new ArtifactLocator(artifact.ArtifactId, artifact.NoteId, artifact.Kind), ct);

    private async Task<NoteDerivedArtifactState?> RecoverArtifactLocatorAsync(Guid owner, Guid artifactId,
        CancellationToken ct)
    {
        var root = ArtifactsFolder(owner);
        if (!Directory.Exists(root)) return null;
        var paths = Directory.EnumerateFiles(root, "artifact.json", SearchOption.AllDirectories)
            .Take(MaxArtifactScan + 1).ToArray();
        if (paths.Length > MaxArtifactScan)
            throw new DomainException(50301, "派生产物索引需要修复，当前扫描数量超过安全上限");
        foreach (var path in paths)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var artifact = await ReadAsync<NoteDerivedArtifactState>(path, ct);
                if (artifact.SchemaVersion != 1 || artifact.Owner != owner || artifact.ArtifactId != artifactId ||
                    artifact.NoteId == Guid.Empty || !Enum.IsDefined(artifact.Kind)) continue;
                var expectedPath = ArtifactPath(owner, artifact.NoteId, artifact.Kind);
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath), StringComparison.OrdinalIgnoreCase))
                    continue;
                await WriteLocatorAsync(artifact, ct);
                return artifact;
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
            {
                // Ignore only a corrupt unrelated artifact file and continue the bounded owner scan.
            }
        }
        return null;
    }

    private async Task<NoteDerivedVersionState?> TryReadVersionAsync(Guid owner,
        NoteDerivedArtifactState artifact, Guid versionId, CancellationToken ct)
    {
        var path = VersionPath(owner, artifact.NoteId, artifact.Kind, versionId);
        if (!File.Exists(path)) return null;
        var version = await ReadAsync<NoteDerivedVersionState>(path, ct);
        if (version.SchemaVersion != 1 || version.Owner != owner || version.ArtifactId != artifact.ArtifactId ||
            version.NoteId != artifact.NoteId || version.Kind != artifact.Kind || version.VersionId != versionId)
            throw new InvalidDataException("Invalid derived version state");
        return version;
    }

    private Task SaveVersionAsync(NoteDerivedVersionState version, CancellationToken ct) =>
        WriteAsync(VersionPath(version.Owner, version.NoteId, version.Kind, version.VersionId), version, ct);

    private async Task<NoteDerivedArtifactDto> DescribeArtifactAsync(Guid owner,
        NoteDerivedArtifactState artifact, CancellationToken ct)
    {
        var versions = new List<NoteDerivedVersionState>();
        var folder = VersionsFolder(owner, artifact.NoteId, artifact.Kind);
        if (Directory.Exists(folder))
        {
            foreach (var path in Directory.EnumerateFiles(folder, "*.json"))
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "D", out var id)) continue;
                var version = await TryReadVersionAsync(owner, artifact, id, ct);
                if (version is not null) versions.Add(version);
            }
        }
        return new(
            artifact.ArtifactId,
            artifact.NoteId,
            artifact.Kind,
            artifact.HeadRevision,
            artifact.HeadVersionId,
            versions.OrderByDescending(x => x.CreatedAt).Select(x => new NoteDerivedVersionSummaryDto(
                x.VersionId, x.Author, x.SourceVersionId, x.ParentVersionId, x.SourceCompleteRecordVersionId,
                x.GenerationTaskId, x.ContentSha256, x.HasConflict, x.CreatedAt,
                artifact.HeadVersionId == x.VersionId)).ToArray());
    }

    private static NoteDerivedVersionDto DescribeVersion(NoteDerivedArtifactState artifact,
        NoteDerivedVersionState version) => new(
        version.VersionId,
        version.ArtifactId,
        version.Kind,
        version.Author,
        version.ParentVersionId,
        version.ObservedHeadVersionId,
        version.SourceCompleteRecordVersionId,
        version.SourceVersionId,
        version.GenerationTaskId,
        version.ContentMarkdown,
        version.ContentSha256,
        version.InputSha256,
        version.ManifestSha256,
        version.PlanVersion,
        version.PromptVersion,
        version.AiCallLogId,
        version.ModelProfileId,
        version.ModelRevision,
        version.ModelEndpoint,
        version.Coverage,
        version.HasConflict,
        version.CreatedAt,
        artifact.HeadVersionId == version.VersionId);

    private async Task<NoteDerivedTaskState?> TryReadTaskAsync(Guid owner, Guid taskId, CancellationToken ct)
    {
        var path = TaskPath(owner, taskId);
        return File.Exists(path) ? await ReadTaskFileAsync(path, owner, taskId, ct) : null;
    }

    private async Task<NoteDerivedTaskState> ReadTaskFileAsync(string path, Guid owner, Guid taskId,
        CancellationToken ct)
    {
        var state = await ReadAsync<NoteDerivedTaskState>(path, ct);
        if (state.SchemaVersion != 1 || state.Owner != owner || state.Request.TaskId != taskId)
            throw new InvalidDataException("Invalid derived task state");
        return state;
    }

    private Task SaveTaskAsync(NoteDerivedTaskState task, CancellationToken ct) =>
        WriteAsync(TaskPath(task.Owner, task.Request.TaskId), task, ct);

    private string OwnerFolder(Guid owner)
    {
        if (owner == Guid.Empty) throw new DomainException(40101, "请先登录");
        return Path.Combine(Root, owner.ToString("D"));
    }

    private string ArtifactsFolder(Guid owner) => Path.Combine(OwnerFolder(owner), "artifacts");
    private string ArtifactFolder(Guid owner, Guid noteId, NoteDerivedArtifactKind kind) =>
        Path.Combine(ArtifactsFolder(owner), Id(noteId), kind.ToString());
    private string ArtifactPath(Guid owner, Guid noteId, NoteDerivedArtifactKind kind) =>
        Path.Combine(ArtifactFolder(owner, noteId, kind), "artifact.json");
    private string VersionsFolder(Guid owner, Guid noteId, NoteDerivedArtifactKind kind) =>
        Path.Combine(ArtifactFolder(owner, noteId, kind), "versions");
    private string VersionPath(Guid owner, Guid noteId, NoteDerivedArtifactKind kind, Guid versionId) =>
        Path.Combine(VersionsFolder(owner, noteId, kind), Id(versionId) + ".json");
    private string LocatorPath(Guid owner, Guid artifactId) =>
        Path.Combine(OwnerFolder(owner), "artifact-index", Id(artifactId) + ".json");
    private string TasksFolder(Guid owner) => Path.Combine(OwnerFolder(owner), "tasks");
    private string TaskPath(Guid owner, Guid taskId) => Path.Combine(TasksFolder(owner), Id(taskId) + ".json");

    private async Task<FileStream> LockAsync(Guid owner, CancellationToken ct)
    {
        var folder = OwnerFolder(owner);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "derived.lock");
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (attempt < 100)
            {
                await Task.Delay(50, ct);
            }
            catch (IOException)
            {
                throw new DomainException(50301, "本账号的派生产物操作尚在进行，请稍后重试");
            }
        }
    }

    private static async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, ct)
            ?? throw new InvalidDataException("Invalid derived storage metadata");
    }

    private static Task WriteAsync<T>(string path, T value, CancellationToken ct) =>
        WriteFileAsync(path, stream => JsonSerializer.SerializeAsync(stream, value, Json, ct), ct);

    private static async Task WriteFileAsync(string path, Func<FileStream, Task> write, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            {
                await write(stream);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string Id(Guid id)
    {
        if (id == Guid.Empty) throw Invalid("标识无效");
        return id.ToString("D");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string GeneratedFingerprint(Guid artifactId, NoteDerivedVersionDraft draft) =>
        Hash(JsonSerializer.Serialize(new { artifactId, draft }, Json));

    private static string ManualFingerprint(Guid artifactId, SaveNoteDerivedManualRevisionRequest request) =>
        Hash(JsonSerializer.Serialize(new { artifactId, request }, Json));

    private static DomainException Invalid(string message) => new(40001, message);
    private static DomainException Conflict(string message) => new(40901, message);

    private sealed record ArtifactLocator(Guid ArtifactId, Guid NoteId, NoteDerivedArtifactKind Kind);
}
