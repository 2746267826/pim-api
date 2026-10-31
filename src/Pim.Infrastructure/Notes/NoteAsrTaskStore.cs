using System.Text.Json;
using Microsoft.Extensions.Options;
using Pim.Core.Exceptions;
using Pim.Core.Notes;

namespace Pim.Infrastructure.Notes;

/// <summary>Task intents/checkpoints share the material store's persistent local-filesystem deployment boundary.</summary>
public sealed class NoteAsrTaskStore(IOptions<NoteMaterialUploadOptions> options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private string Root => Path.Combine(Path.GetFullPath(options.Value.StorageRoot), "asr-tasks");
    private string Folder(Guid owner)
    {
        if (owner == Guid.Empty) throw new DomainException(40101, "请先登录");
        return Path.Combine(Root, owner.ToString("D"));
    }
    private string FilePath(Guid owner, Guid id)
    {
        if (id == Guid.Empty) throw new DomainException(40001, "任务标识无效");
        return Path.Combine(Folder(owner), id.ToString("D") + ".json");
    }
    internal async Task<NoteAsrState> CreateAsync(Guid owner, StartNoteAsrRequest request,
        NoteMaterialVersionDto version, List<NoteAsrStep> steps, NoteAsrRouteConsent? route,
        string? executionMode, int samplesPerCall, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var hash = NoteMaterialManifestValidator.Hash(JsonSerializer.Serialize(request, Json));
        var path = FilePath(owner, request.TaskId);
        if (File.Exists(path))
        {
            var existing = await ReadAsync(owner, request.TaskId, ct);
            if (existing.Fingerprint != hash) throw new DomainException(40901, "任务标识已绑定另一份输入；原任务保留");
            return existing;
        }
        var files = Directory.GetFiles(Folder(owner), "*.json");
        if (files.Length >= 200) throw new DomainException(41301, "转写任务数量达到200，请联系管理员归档后继续");
        var active = 0;
        foreach (var file in files)
        {
            var task = await ReadAsync(owner, Guid.Parse(Path.GetFileNameWithoutExtension(file)), ct);
            if (Active(task)) active++;
        }
        if (active >= 10) throw new DomainException(41301, "本账号待处理转写任务已达10个，请等待或停止部分任务");
        var state = new NoteAsrState { Owner = owner, Request = request, Fingerprint = hash,
            NoteId = version.Upload.NoteId, InputSha256 = NoteMaterialManifestValidator.Hash(version.InputJson),
            ManifestSha256 = NoteMaterialManifestValidator.Hash(version.ManifestJson), Steps = steps,
            Route = route?.Route, RouteConfigRevision = route?.ConfigRevision ?? Guid.Empty,
            RouteConsentVersion = route?.ConsentVersion ?? "", ExecutionMode = executionMode,
            ParentTaskId = request.ParentTaskId, ReprocessReason = request.ReprocessReason,
            SamplesPerCall = samplesPerCall };
        await SaveAsync(state, ct); return state;
    }
    internal Task<NoteAsrState> CreateAsync(Guid owner, StartNoteAsrRequest request,
        NoteMaterialVersionDto version, List<NoteAsrStep> steps, CancellationToken ct) =>
        CreateAsync(owner, request, version, steps, null, null, NoteAsrPlan.SamplesPerCall, ct);
    internal async Task<NoteAsrState> ReadAsync(Guid owner, Guid id, CancellationToken ct)
    {
        var path = FilePath(owner, id);
        if (!File.Exists(path)) throw new DomainException(40401, "转写任务不存在");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var state = await JsonSerializer.DeserializeAsync<NoteAsrState>(stream, Json, ct) ?? throw new InvalidDataException("Invalid ASR task");
        if (state.SchemaVersion != 1 || state.Owner != owner || state.Request.TaskId != id) throw new InvalidDataException("Invalid ASR task identity");
        return state;
    }
    internal async Task<NoteAsrState> UpdateAsync(Guid owner, Guid id, Action<NoteAsrState> update, CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        var state = await ReadAsync(owner, id, ct); update(state);
        state.Revision = Guid.NewGuid(); state.UpdatedAt = DateTimeOffset.UtcNow;
        await SaveAsync(state, ct); return state;
    }
    internal async Task<NoteAsrState> ReadLockedAsync(Guid owner, Guid id, CancellationToken ct)
    {
        return await WithLockAsync(owner, token => ReadAsync(owner, id, token), ct);
    }
    /// <summary>
    /// Holds the same owner-scoped checkpoint lock across a file read and the caller's durable
    /// compare-and-swap. This prevents a stop committed in the file from racing a retry/claim CAS.
    /// </summary>
    internal async Task<T> WithLockAsync<T>(Guid owner, Func<CancellationToken, Task<T>> action,
        CancellationToken ct)
    {
        await using var lease = await LockAsync(owner, ct);
        return await action(ct);
    }
    internal IEnumerable<(Guid Owner, Guid Id)> Enumerate(Guid? owner = null)
    {
        if (!Directory.Exists(Root)) yield break;
        var folders = owner.HasValue ? new[] { Folder(owner.Value) } : Directory.EnumerateDirectories(Root);
        foreach (var folder in folders)
            if (Guid.TryParseExact(Path.GetFileName(folder), "D", out var user) && Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
                    if (Guid.TryParseExact(Path.GetFileNameWithoutExtension(file), "D", out var id)) yield return (user, id);
    }
    internal FileStream? TryRunLease(Guid owner, Guid id)
    {
        Directory.CreateDirectory(Folder(owner));
        try { return new(FilePath(owner, id) + ".running", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return null; }
    }
    internal static bool CloudPipeline(NoteAsrState state) =>
        state.ExecutionMode != NoteAsrExecutionMode.Batch &&
        state.Route is null or NoteAsrRoute.CloudAsr or NoteAsrRoute.CloudMultimodal;
    internal static bool Active(NoteAsrState state) => CloudPipeline(state) &&
        (state.Route is null || state.AttemptId is not null) &&
        state.Steps.Any(x => x.Current.Status == "Running" || !state.StopRequested && x.Current.Status == "Queued");
    internal static NoteAsrTaskDto Describe(NoteAsrState state)
    {
        var done = state.Steps.Where(x => x.Current.Status == "Succeeded").ToArray();
        var failed = state.Steps.Count(x => x.Current.Status is not ("Queued" or "Running" or "Succeeded"));
        var complete = done.Length == state.Steps.Count &&
            (state.Route is null || state.CompletionAcknowledged);
        var phase = complete ? "Succeeded" : state.StopRequested ? "Stopped"
            : state.Steps.Any(x => x.Current.Status == "Running") ? "Running"
            : state.Steps.Any(x => x.Current.Status == "Queued") ? "Queued" : "NeedsAttention";
        return new(state.Request.TaskId, state.Revision, state.NoteId, state.Request.VersionId, state.InputSha256,
            state.ManifestSha256, phase, state.Request.Language, state.Request.Consent, state.CreatedAt, state.UpdatedAt,
            state.Steps.Count, done.Length, failed, state.Steps.Sum(x => x.Samples), done.Sum(x => x.Samples),
            NoteAsrPlan.SampleRate, state.StopRequested, Route: state.Route, AttemptId: state.AttemptId,
            RouteConfigRevision: state.RouteConfigRevision, RouteConsentVersion: state.RouteConsentVersion,
            ExecutionMode: state.ExecutionMode);
    }
    private async Task SaveAsync(NoteAsrState state, CancellationToken ct)
    {
        var path = FilePath(state.Owner, state.Request.TaskId); var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { await JsonSerializer.SerializeAsync(stream, state, Json, ct); await stream.FlushAsync(ct); stream.Flush(true); }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private async Task<FileStream> LockAsync(Guid owner, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder(owner));
        for (var attempt = 0; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try { return new(Path.Combine(Folder(owner), "tasks.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (attempt < 100) { await Task.Delay(50, ct); }
        }
    }
}
