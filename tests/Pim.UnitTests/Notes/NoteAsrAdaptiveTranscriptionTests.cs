using Pim.Infrastructure.Notes;
using Xunit;

namespace Pim.UnitTests.Notes;

public sealed class NoteAsrAdaptiveTranscriptionTests
{
    private static readonly Guid RootRequestId = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private const int SampleRate = 16_000;

    [Fact]
    public async Task Truncated_parent_splits_and_concatenates_real_child_results_in_range_order()
    {
        var capture = await RunAsync(request => request.Range.Samples > 4_000
            ? new(NoteAsrAdaptiveOutcome.Truncated, "discarded-parent-tail", "length")
            : new(NoteAsrAdaptiveOutcome.Succeeded, request.Branch.EndsWith(".L", StringComparison.Ordinal) ? "L" : "R"));

        Assert.Equal(NoteAsrAdaptiveRunStatus.Succeeded, capture.Run.Status);
        Assert.Equal("LR", capture.Run.Transcript);
        Assert.Equal(3, capture.Run.ExternalCalls);
        Assert.Equal(1, capture.Run.MaxDepthReached);
        Assert.Equal(NoteAsrAdaptiveOutcome.Truncated, capture.Run.ParentResult?.Outcome);
        Assert.Equal("discarded-parent-tail", capture.Run.ParentResult?.Text);
        Assert.Equal(new long[] { 0, 4_000 }, capture.Run.Successes.Select(x => x.Request.Range.OffsetSamples).ToArray());
        Assert.Equal(new long[] { 4_000, 4_000 }, capture.Run.Successes.Select(x => x.Request.Range.Samples).ToArray());
        Assert.Equal(3, capture.Calls.Count);
        Assert.Equal(3, capture.Prepared.Count);
        Assert.True(capture.Calls[1].ChildRequestId != capture.Calls[2].ChildRequestId);
    }

    [Theory]
    [InlineData((int)NoteAsrAdaptiveOutcome.Truncated)]
    [InlineData((int)NoteAsrAdaptiveOutcome.LengthLimit)]
    [InlineData((int)NoteAsrAdaptiveOutcome.Empty)]
    [InlineData((int)NoteAsrAdaptiveOutcome.Repetition)]
    [InlineData((int)NoteAsrAdaptiveOutcome.ProvenProviderFailure)]
    public async Task Only_proven_deterministic_failures_are_split(int outcomeValue)
    {
        var outcome = (NoteAsrAdaptiveOutcome)outcomeValue;
        var capture = await RunAsync(request => request.Depth == 0
            ? new(outcome, ReasonCode: "deterministic")
            : new(NoteAsrAdaptiveOutcome.Succeeded, request.Range.OffsetSamples == 0 ? "first" : "second"));

        Assert.Equal(NoteAsrAdaptiveRunStatus.Succeeded, capture.Run.Status);
        Assert.Equal("firstsecond", capture.Run.Transcript);
        Assert.Equal(outcome, capture.Run.ParentResult?.Outcome);
        Assert.Equal(3, capture.Run.ExternalCalls);
    }

    [Theory]
    [InlineData((int)NoteAsrAdaptiveOutcome.Unknown)]
    [InlineData((int)NoteAsrAdaptiveOutcome.UnconfirmedTimeout)]
    [InlineData((int)NoteAsrAdaptiveOutcome.Cancelled)]
    [InlineData((int)NoteAsrAdaptiveOutcome.ConfigurationRejected)]
    [InlineData((int)NoteAsrAdaptiveOutcome.ConsentRejected)]
    public async Task Unknown_timeout_cancel_config_and_consent_never_resubmit_or_guess_children(
        int outcomeValue)
    {
        var outcome = (NoteAsrAdaptiveOutcome)outcomeValue;
        var capture = await RunAsync(_ => new(outcome, ReasonCode: "terminal"));

        Assert.Equal(NoteAsrAdaptiveRunStatus.Stopped, capture.Run.Status);
        Assert.Equal(1, capture.Run.ExternalCalls);
        Assert.Single(capture.Prepared);
        Assert.Single(capture.Calls);
        Assert.Empty(capture.Run.Successes);
        Assert.Equal(outcome, capture.Run.StopResult?.Outcome);
        Assert.Equal("terminal", capture.Run.StopResult?.ReasonCode);
    }

    [Fact]
    public async Task Resume_lookup_reuses_existing_successes_and_makes_zero_external_calls()
    {
        var interval = Interval();
        var leftRange = new NoteAsrAdaptiveRange(0, 4_000, SampleRate);
        var rightRange = new NoteAsrAdaptiveRange(4_000, 4_000, SampleRate);
        var existing = new Dictionary<Guid, NoteAsrAdaptiveResult>
        {
            [RootRequestId] = new(NoteAsrAdaptiveOutcome.Truncated, "parent", "length"),
            [NoteAsrAdaptiveTranscription.CreateRequestId(RootRequestId, leftRange, "root.L")] =
                new(NoteAsrAdaptiveOutcome.Succeeded, "L"),
            [NoteAsrAdaptiveTranscription.CreateRequestId(RootRequestId, rightRange, "root.R")] =
                new(NoteAsrAdaptiveOutcome.Succeeded, "R"),
        };

        var capture = await RunAsync(
            _ => throw new InvalidOperationException("Success chunks must not be called again"),
            existing: existing,
            interval: interval);

        Assert.Equal(NoteAsrAdaptiveRunStatus.Succeeded, capture.Run.Status);
        Assert.Equal("LR", capture.Run.Transcript);
        Assert.Equal(0, capture.Run.ExternalCalls);
        Assert.Empty(capture.Calls);
        Assert.Empty(capture.Prepared);
        Assert.All(capture.Run.Successes, success => Assert.True(success.FromLookup));
    }

    [Fact]
    public async Task Resume_unknown_on_one_side_keeps_partial_without_calling_the_other_side()
    {
        var leftRange = new NoteAsrAdaptiveRange(0, 4_000, SampleRate);
        var rightRange = new NoteAsrAdaptiveRange(4_000, 4_000, SampleRate);
        var existing = new Dictionary<Guid, NoteAsrAdaptiveResult>
        {
            [RootRequestId] = new(NoteAsrAdaptiveOutcome.Truncated, "parent", "length"),
            [NoteAsrAdaptiveTranscription.CreateRequestId(RootRequestId, leftRange, "root.L")] =
                new(NoteAsrAdaptiveOutcome.Succeeded, "L"),
            [NoteAsrAdaptiveTranscription.CreateRequestId(RootRequestId, rightRange, "root.R")] =
                new(NoteAsrAdaptiveOutcome.Unknown, ReasonCode: "unconfirmed"),
        };

        var capture = await RunAsync(
            _ => throw new InvalidOperationException("Unknown must stop without a paid retry"),
            existing: existing);

        Assert.Equal(NoteAsrAdaptiveRunStatus.Partial, capture.Run.Status);
        Assert.Equal("L", capture.Run.Transcript);
        Assert.Equal(0, capture.Run.ExternalCalls);
        Assert.Equal("unknown", capture.Run.PartialReason);
        Assert.Equal(NoteAsrAdaptiveOutcome.Unknown, capture.Run.StopResult?.Outcome);
    }

    [Fact]
    public void Child_request_ids_are_stable_for_the_exact_root_range_and_branch()
    {
        var interval = Interval();
        var first = NoteAsrAdaptiveTranscription.CreateRequestId(RootRequestId, interval, "root.L");
        var second = NoteAsrAdaptiveTranscription.CreateRequestId(RootRequestId, interval, "root.L");
        var sibling = NoteAsrAdaptiveTranscription.CreateRequestId(RootRequestId, interval, "root.R");
        var otherRoot = NoteAsrAdaptiveTranscription.CreateRequestId(
            Guid.Parse("22222222-2222-4222-8222-222222222222"), interval, "root.L");

        Assert.Equal(first, second);
        Assert.NotEqual(first, sibling);
        Assert.NotEqual(first, otherRoot);
    }

    [Fact]
    public async Task Max_depth_exhaustion_preserves_partial_failure_ranges_without_gaps_or_overlap()
    {
        var capture = await RunAsync(
            request => new(NoteAsrAdaptiveOutcome.Truncated, ReasonCode: "too_long"),
            new NoteAsrAdaptiveOptions(MaxDepth: 1, MinSamplesPerCall: 1, MaxCalls: 31, MaxTextChars: 10_000));

        Assert.Equal(NoteAsrAdaptiveRunStatus.Partial, capture.Run.Status);
        Assert.Empty(capture.Run.Successes);
        Assert.Equal(new[] { "max_depth", "max_depth" }, capture.Run.Failures.Select(x => x.Reason).ToArray());
        Assert.Equal(3, capture.Run.ExternalCalls);
        AssertContiguous(capture.Run.Failures.Select(x => x.Request.Range).ToArray(), 0, 8_000);
        Assert.Equal("max_depth", capture.Run.PartialReason);
    }

    [Fact]
    public async Task Minimum_leaf_length_exhaustion_preserves_partial_reason()
    {
        var capture = await RunAsync(
            _ => new(NoteAsrAdaptiveOutcome.Truncated),
            new NoteAsrAdaptiveOptions(MinSamplesPerCall: 1_600),
            interval: new NoteAsrAdaptiveRange(0, 3_000, SampleRate));

        Assert.Equal(NoteAsrAdaptiveRunStatus.Partial, capture.Run.Status);
        Assert.Equal("min_samples", capture.Run.PartialReason);
        var failure = Assert.Single(capture.Run.Failures);
        Assert.Equal("min_samples", failure.Reason);
        Assert.Equal(new NoteAsrAdaptiveRange(0, 3_000, SampleRate), failure.Request.Range);
    }

    [Fact]
    public async Task Max_call_budget_stops_before_another_paid_call_and_keeps_partial()
    {
        var capture = await RunAsync(
            _ => new(NoteAsrAdaptiveOutcome.Truncated),
            new NoteAsrAdaptiveOptions(MaxCalls: 1, MinSamplesPerCall: 1));

        Assert.Equal(NoteAsrAdaptiveRunStatus.Partial, capture.Run.Status);
        Assert.Equal(1, capture.Run.ExternalCalls);
        Assert.Single(capture.Calls);
        Assert.Equal("max_calls", capture.Run.PartialReason);
        Assert.Contains(capture.Run.Failures, failure => failure.Reason == "max_calls");
    }

    [Fact]
    public async Task Text_budget_never_publishes_more_text_than_the_bound()
    {
        var capture = await RunAsync(
            request => request.Depth == 0
                ? new(NoteAsrAdaptiveOutcome.Truncated)
                : new(NoteAsrAdaptiveOutcome.Succeeded, request.Range.OffsetSamples == 0 ? "aaaa" : "bbbb"),
            new NoteAsrAdaptiveOptions(MaxTextChars: 4, MinSamplesPerCall: 1));

        Assert.Equal(NoteAsrAdaptiveRunStatus.Partial, capture.Run.Status);
        Assert.Equal("aaaa", capture.Run.Transcript);
        Assert.Equal("text_budget_exceeded", capture.Run.PartialReason);
        Assert.Equal(3, capture.Run.ExternalCalls);
        Assert.Contains(capture.Run.Failures, failure => failure.Reason == "text_budget_exceeded");
    }

    [Fact]
    public async Task Prepare_failure_makes_zero_external_calls()
    {
        var prepared = new List<NoteAsrAdaptiveRequest>();
        var calls = 0;
        var engine = new NoteAsrAdaptiveTranscription(
            null,
            (request, _) =>
            {
                prepared.Add(request);
                return ValueTask.FromException(new InvalidOperationException("durable write unavailable"));
            },
            (_, _) => ValueTask.FromResult<NoteAsrAdaptiveResult?>(null),
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new NoteAsrAdaptiveResult(NoteAsrAdaptiveOutcome.Succeeded, "must-not-run"));
            });

        var run = await engine.RunAsync(RootRequestId, Interval());

        Assert.Equal(NoteAsrAdaptiveRunStatus.Stopped, run.Status);
        Assert.Equal(0, run.ExternalCalls);
        Assert.Equal(0, calls);
        Assert.Single(prepared);
        Assert.Equal(NoteAsrAdaptiveOutcome.PreparationFailed, run.ParentResult?.Outcome);
        Assert.Equal("prepare_failed", run.PartialReason);
    }

    [Fact]
    public async Task Lookup_failure_is_unknown_and_never_falls_through_to_a_call()
    {
        var calls = 0;
        var engine = new NoteAsrAdaptiveTranscription(
            null,
            (_, _) => ValueTask.CompletedTask,
            (_, _) => ValueTask.FromException<NoteAsrAdaptiveResult?>(new InvalidOperationException("lookup unavailable")),
            (_, _) =>
            {
                calls++;
                return ValueTask.FromResult(new NoteAsrAdaptiveResult(NoteAsrAdaptiveOutcome.Succeeded, "must-not-run"));
            });

        var run = await engine.RunAsync(RootRequestId, Interval());

        Assert.Equal(NoteAsrAdaptiveRunStatus.Stopped, run.Status);
        Assert.Equal(0, run.ExternalCalls);
        Assert.Equal(0, calls);
        Assert.Equal(NoteAsrAdaptiveOutcome.LookupFailed, run.StopResult?.Outcome);
        Assert.Equal("lookup_failed", run.PartialReason);
    }

    [Fact]
    public async Task Odd_sample_intervals_remain_contiguous_and_fully_covered()
    {
        var capture = await RunAsync(
            request => request.Range.Samples > 2_000
                ? new(NoteAsrAdaptiveOutcome.Truncated)
                : new(NoteAsrAdaptiveOutcome.Succeeded, request.Range.OffsetSamples.ToString()),
            new NoteAsrAdaptiveOptions(MinSamplesPerCall: 1_000),
            interval: new NoteAsrAdaptiveRange(17, 8_001, SampleRate));

        Assert.Equal(NoteAsrAdaptiveRunStatus.Succeeded, capture.Run.Status);
        AssertContiguous(capture.Run.Successes.Select(x => x.Request.Range).ToArray(), 17, 8_001);
        Assert.Equal(8_001, capture.Run.Successes.Sum(x => x.Request.Range.Samples));
    }

    private static NoteAsrAdaptiveRange Interval() => new(0, 8_000, SampleRate);

    private static async Task<RunCapture> RunAsync(
        Func<NoteAsrAdaptiveRequest, NoteAsrAdaptiveResult> respond,
        NoteAsrAdaptiveOptions? options = null,
        IReadOnlyDictionary<Guid, NoteAsrAdaptiveResult>? existing = null,
        NoteAsrAdaptiveRange? interval = null)
    {
        var prepared = new List<NoteAsrAdaptiveRequest>();
        var lookedUp = new List<NoteAsrAdaptiveRequest>();
        var calls = new List<NoteAsrAdaptiveRequest>();
        var engine = new NoteAsrAdaptiveTranscription(
            options,
            (request, _) =>
            {
                prepared.Add(request);
                return ValueTask.CompletedTask;
            },
            (request, _) =>
            {
                lookedUp.Add(request);
                return ValueTask.FromResult(existing is not null && existing.TryGetValue(request.ChildRequestId, out var result)
                    ? result
                    : null);
            },
            (request, _) =>
            {
                calls.Add(request);
                return ValueTask.FromResult(respond(request));
            });

        var run = await engine.RunAsync(RootRequestId, interval ?? Interval());
        return new(run, prepared, lookedUp, calls);
    }

    private static void AssertContiguous(IReadOnlyList<NoteAsrAdaptiveRange> ranges, long start, long samples)
    {
        var ordered = ranges.OrderBy(x => x.OffsetSamples).ToArray();
        var expected = start;
        foreach (var range in ordered)
        {
            Assert.Equal(expected, range.OffsetSamples);
            expected = range.EndExclusive;
        }
        Assert.Equal(checked(start + samples), expected);
    }

    private sealed record RunCapture(
        NoteAsrAdaptiveRun Run,
        IReadOnlyList<NoteAsrAdaptiveRequest> Prepared,
        IReadOnlyList<NoteAsrAdaptiveRequest> LookedUp,
        IReadOnlyList<NoteAsrAdaptiveRequest> Calls);
}
