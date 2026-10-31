using System.Security.Cryptography;
using System.Text;

namespace Pim.Infrastructure.Notes;

/// <summary>
/// Pure orchestration seam for a single fixed PCM interval. Persistence, provider routing,
/// consent, logging and money decisions stay in the caller-supplied callbacks.
/// </summary>
internal sealed class NoteAsrAdaptiveTranscription
{
    private readonly NoteAsrAdaptiveOptions _options;
    private readonly Func<NoteAsrAdaptiveRequest, CancellationToken, ValueTask> _prepare;
    private readonly Func<NoteAsrAdaptiveRequest, CancellationToken, ValueTask<NoteAsrAdaptiveResult?>> _lookup;
    private readonly Func<NoteAsrAdaptiveRequest, CancellationToken, ValueTask<NoteAsrAdaptiveResult>> _call;

    internal NoteAsrAdaptiveTranscription(
        NoteAsrAdaptiveOptions? options,
        Func<NoteAsrAdaptiveRequest, CancellationToken, ValueTask> prepareAsync,
        Func<NoteAsrAdaptiveRequest, CancellationToken, ValueTask<NoteAsrAdaptiveResult?>> lookupAsync,
        Func<NoteAsrAdaptiveRequest, CancellationToken, ValueTask<NoteAsrAdaptiveResult>> callAsync)
    {
        _options = options ?? NoteAsrAdaptiveOptions.Default;
        _options.Validate();
        _prepare = prepareAsync ?? throw new ArgumentNullException(nameof(prepareAsync));
        _lookup = lookupAsync ?? throw new ArgumentNullException(nameof(lookupAsync));
        _call = callAsync ?? throw new ArgumentNullException(nameof(callAsync));
    }

    internal async ValueTask<NoteAsrAdaptiveRun> RunAsync(
        Guid rootRequestId,
        NoteAsrAdaptiveRange interval,
        CancellationToken cancellationToken = default)
    {
        if (rootRequestId == Guid.Empty) throw new ArgumentException("Root request id is required", nameof(rootRequestId));
        interval.Validate();

        var state = new RunState(rootRequestId);
        var root = new NoteAsrAdaptiveRequest(rootRequestId, null, interval, 0, "root");
        await VisitAsync(root, state, cancellationToken).ConfigureAwait(false);

        var successes = state.Successes
            .OrderBy(x => x.Request.Range.OffsetSamples)
            .ThenBy(x => x.Request.Range.Samples)
            .ToArray();
        var complete = IsCompleteCoverage(interval, successes);
        var status = complete
            ? NoteAsrAdaptiveRunStatus.Succeeded
            : successes.Length > 0 || state.Failures.Count > 0
                ? NoteAsrAdaptiveRunStatus.Partial
                : NoteAsrAdaptiveRunStatus.Stopped;
        var transcript = string.Concat(successes.Select(x => x.Text));
        var partialReason = status == NoteAsrAdaptiveRunStatus.Succeeded
            ? null
            : state.HaltReason ?? state.Failures.FirstOrDefault()?.Reason ?? state.StopResult?.ReasonCode;

        return new NoteAsrAdaptiveRun(
            status,
            transcript,
            successes,
            state.Failures.ToArray(),
            state.ParentResult,
            state.StopResult,
            partialReason,
            state.ExternalCalls,
            state.MaxDepthReached);
    }

    internal static Guid CreateRequestId(
        Guid rootRequestId,
        NoteAsrAdaptiveRange range,
        string branch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);
        range.Validate();
        var canonical = FormattableString.Invariant(
            $"{rootRequestId:D}|{range.OffsetSamples}|{range.Samples}|{range.SampleRate}|{branch}");
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        var bytes = hash.AsSpan(0, 16).ToArray();
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes);
    }

    private async ValueTask VisitAsync(
        NoteAsrAdaptiveRequest request,
        RunState state,
        CancellationToken cancellationToken)
    {
        if (state.Halted) return;
        state.MaxDepthReached = Math.Max(state.MaxDepthReached, request.Depth);

        NoteAsrAdaptiveResult result;
        var fromLookup = false;
        try
        {
            var existing = await _lookup(request, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                if (state.ExternalCalls >= _options.MaxCalls)
                {
                    state.Failures.Add(new(request, new(NoteAsrAdaptiveOutcome.Unknown,
                        ReasonCode: "max_calls", Reason: "Adaptive ASR call budget is exhausted"), "max_calls"));
                    state.Halt("max_calls");
                    return;
                }

                try
                {
                    await _prepare(request, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    state.Halt("cancelled", new(NoteAsrAdaptiveOutcome.Cancelled,
                        ReasonCode: "prepare_cancelled", Reason: "Durable preparation was cancelled"));
                    return;
                }
                catch (Exception exception)
                {
                    var failed = new NoteAsrAdaptiveResult(
                        NoteAsrAdaptiveOutcome.PreparationFailed,
                        ReasonCode: "prepare_failed",
                        Reason: exception.Message);
                    if (request.Depth == 0) state.ParentResult = failed;
                    state.Halt("prepare_failed", failed);
                    return;
                }

                state.ExternalCalls++;
                try
                {
                    result = Normalize(await _call(request, cancellationToken).ConfigureAwait(false));
                }
                catch (OperationCanceledException)
                {
                    state.Halt("cancelled", new(NoteAsrAdaptiveOutcome.Cancelled,
                        ReasonCode: "call_cancelled", Reason: "ASR call was cancelled and will not be resubmitted"));
                    return;
                }
                catch (Exception exception)
                {
                    state.Halt("unknown", new(NoteAsrAdaptiveOutcome.Unknown,
                        ReasonCode: "call_unconfirmed",
                        Reason: $"ASR call outcome is unknown: {exception.Message}"));
                    return;
                }
            }
            else
            {
                result = Normalize(existing);
                fromLookup = true;
            }
        }
        catch (OperationCanceledException)
        {
            state.Halt("cancelled", new(NoteAsrAdaptiveOutcome.Cancelled,
                ReasonCode: "lookup_cancelled", Reason: "Durable outcome lookup was cancelled"));
            return;
        }
        catch (Exception exception)
        {
            var failed = new NoteAsrAdaptiveResult(
                NoteAsrAdaptiveOutcome.LookupFailed,
                ReasonCode: "lookup_failed",
                Reason: exception.Message);
            if (request.Depth == 0) state.ParentResult = failed;
            state.Halt("lookup_failed", failed);
            return;
        }

        if (request.Depth == 0) state.ParentResult = result;

        if (result.Outcome == NoteAsrAdaptiveOutcome.Succeeded)
        {
            var text = result.Text ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text))
            {
                result = result with
                {
                    Outcome = NoteAsrAdaptiveOutcome.Empty,
                    ReasonCode = result.ReasonCode ?? "empty_transcript",
                };
            }
            else if (state.TextChars + text.Length > _options.MaxTextChars)
            {
                state.Failures.Add(new(request, result, "text_budget_exceeded"));
                state.Halt("text_budget_exceeded");
                return;
            }
            else
            {
                state.Successes.Add(new(request, text, fromLookup));
                state.TextChars += text.Length;
                return;
            }
        }

        if (!CanSplit(result.Outcome))
        {
            state.Halt(UnsafeReason(result.Outcome), result);
            return;
        }

        if (request.Depth >= _options.MaxDepth)
        {
            state.Failures.Add(new(request, result, "max_depth"));
            return;
        }
        if (request.Range.Samples < checked(_options.MinSamplesPerCall * 2L))
        {
            state.Failures.Add(new(request, result, "min_samples"));
            return;
        }

        var leftSamples = request.Range.Samples / 2;
        var rightSamples = request.Range.Samples - leftSamples;
        var leftRange = new NoteAsrAdaptiveRange(request.Range.OffsetSamples, leftSamples, request.Range.SampleRate);
        var rightRange = new NoteAsrAdaptiveRange(checked(request.Range.OffsetSamples + leftSamples), rightSamples, request.Range.SampleRate);
        var leftBranch = request.Branch + ".L";
        var rightBranch = request.Branch + ".R";
        var left = new NoteAsrAdaptiveRequest(
            CreateRequestId(state.RootRequestId, leftRange, leftBranch),
            request.ChildRequestId,
            leftRange,
            request.Depth + 1,
            leftBranch);
        var right = new NoteAsrAdaptiveRequest(
            CreateRequestId(state.RootRequestId, rightRange, rightBranch),
            request.ChildRequestId,
            rightRange,
            request.Depth + 1,
            rightBranch);

        await VisitAsync(left, state, cancellationToken).ConfigureAwait(false);
        if (state.Halted) return;
        await VisitAsync(right, state, cancellationToken).ConfigureAwait(false);
    }

    private static NoteAsrAdaptiveResult Normalize(NoteAsrAdaptiveResult result) =>
        result.Outcome == NoteAsrAdaptiveOutcome.Succeeded && string.IsNullOrWhiteSpace(result.Text)
            ? result with { Outcome = NoteAsrAdaptiveOutcome.Empty, ReasonCode = result.ReasonCode ?? "empty_transcript" }
            : result;

    private static bool CanSplit(NoteAsrAdaptiveOutcome outcome) => outcome is
        NoteAsrAdaptiveOutcome.Truncated or
        NoteAsrAdaptiveOutcome.LengthLimit or
        NoteAsrAdaptiveOutcome.Empty or
        NoteAsrAdaptiveOutcome.Repetition or
        NoteAsrAdaptiveOutcome.ProvenProviderFailure;

    private static string UnsafeReason(NoteAsrAdaptiveOutcome outcome) => outcome switch
    {
        NoteAsrAdaptiveOutcome.Unknown => "unknown",
        NoteAsrAdaptiveOutcome.UnconfirmedTimeout => "unconfirmed_timeout",
        NoteAsrAdaptiveOutcome.Cancelled => "cancelled",
        NoteAsrAdaptiveOutcome.ConfigurationRejected => "configuration_rejected",
        NoteAsrAdaptiveOutcome.ConsentRejected => "consent_rejected",
        NoteAsrAdaptiveOutcome.PreparationFailed => "prepare_failed",
        NoteAsrAdaptiveOutcome.LookupFailed => "lookup_failed",
        NoteAsrAdaptiveOutcome.TransientFailure => "transient_failure",
        _ => "not_splittable",
    };

    private static bool IsCompleteCoverage(NoteAsrAdaptiveRange interval, IReadOnlyList<NoteAsrAdaptiveSuccess> successes)
    {
        if (successes.Count == 0) return false;
        var expected = interval.OffsetSamples;
        foreach (var success in successes)
        {
            if (success.Request.Range.OffsetSamples != expected) return false;
            expected = success.Request.Range.EndExclusive;
        }
        return expected == interval.EndExclusive;
    }

    private sealed class RunState(Guid rootRequestId)
    {
        internal Guid RootRequestId { get; } = rootRequestId;
        internal List<NoteAsrAdaptiveSuccess> Successes { get; } = [];
        internal List<NoteAsrAdaptiveLeafFailure> Failures { get; } = [];
        internal NoteAsrAdaptiveResult? ParentResult { get; set; }
        internal NoteAsrAdaptiveResult? StopResult { get; private set; }
        internal string? HaltReason { get; private set; }
        internal bool Halted { get; private set; }
        internal int ExternalCalls { get; set; }
        internal int TextChars { get; set; }
        internal int MaxDepthReached { get; set; }

        internal void Halt(string reason, NoteAsrAdaptiveResult? stopResult = null)
        {
            Halted = true;
            HaltReason = reason;
            StopResult = stopResult;
        }
    }
}

internal enum NoteAsrAdaptiveOutcome
{
    Succeeded,
    Truncated,
    LengthLimit,
    Empty,
    Repetition,
    ProvenProviderFailure,
    Unknown,
    UnconfirmedTimeout,
    Cancelled,
    ConfigurationRejected,
    ConsentRejected,
    PreparationFailed,
    LookupFailed,
    TransientFailure,
}

internal enum NoteAsrAdaptiveRunStatus
{
    Succeeded,
    Partial,
    Stopped,
}

internal sealed record NoteAsrAdaptiveRange(long OffsetSamples, long Samples, int SampleRate)
{
    internal long EndExclusive => checked(OffsetSamples + Samples);

    internal void Validate()
    {
        if (OffsetSamples < 0) throw new ArgumentOutOfRangeException(nameof(OffsetSamples));
        if (Samples <= 0) throw new ArgumentOutOfRangeException(nameof(Samples));
        if (SampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(SampleRate));
        _ = EndExclusive;
    }
}

internal sealed record NoteAsrAdaptiveRequest(
    Guid ChildRequestId,
    Guid? ParentRequestId,
    NoteAsrAdaptiveRange Range,
    int Depth,
    string Branch);

internal sealed record NoteAsrAdaptiveResult(
    NoteAsrAdaptiveOutcome Outcome,
    string? Text = null,
    string? ReasonCode = null,
    string? Reason = null);

internal sealed record NoteAsrAdaptiveSuccess(
    NoteAsrAdaptiveRequest Request,
    string Text,
    bool FromLookup);

internal sealed record NoteAsrAdaptiveLeafFailure(
    NoteAsrAdaptiveRequest Request,
    NoteAsrAdaptiveResult Result,
    string Reason);

internal sealed record NoteAsrAdaptiveRun(
    NoteAsrAdaptiveRunStatus Status,
    string Transcript,
    IReadOnlyList<NoteAsrAdaptiveSuccess> Successes,
    IReadOnlyList<NoteAsrAdaptiveLeafFailure> Failures,
    NoteAsrAdaptiveResult? ParentResult,
    NoteAsrAdaptiveResult? StopResult,
    string? PartialReason,
    int ExternalCalls,
    int MaxDepthReached);

internal sealed record NoteAsrAdaptiveOptions(
    int MaxDepth = 6,
    long MinSamplesPerCall = 1_600,
    int MaxCalls = 31,
    int MaxTextChars = 200_000)
{
    internal static NoteAsrAdaptiveOptions Default { get; } = new();

    internal void Validate()
    {
        if (MaxDepth < 0) throw new ArgumentOutOfRangeException(nameof(MaxDepth));
        if (MinSamplesPerCall <= 0) throw new ArgumentOutOfRangeException(nameof(MinSamplesPerCall));
        if (MaxCalls <= 0) throw new ArgumentOutOfRangeException(nameof(MaxCalls));
        if (MaxTextChars <= 0) throw new ArgumentOutOfRangeException(nameof(MaxTextChars));
        _ = checked(MinSamplesPerCall * 2);
    }
}
