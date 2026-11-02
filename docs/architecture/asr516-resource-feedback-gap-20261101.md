# #516 Resource Feedback Slice (2026-11-01)

This is a non-canonical implementation note based on the current integrated API
and Windows source. It does not change #516, the roadmap, acceptance status, or
the generated contract. AC4/AC5 are still `NOT-VERIFIED`.

## Confirmed behavior and current source

Issue #516 requires the client to check CPU load, available memory, available
VRAM and power before claiming work. A failed gate leaves work queued and
records a visible reason. Automatic retries back off and stop after a
configurable number of consecutive failures; manual processing bypasses that
automatic limit but still passes the resource gate. The issue does not specify
numeric thresholds, retry counts, or delay values.

Windows currently evaluates the resource manifest locally in
`ResourceRequirementEvaluator`, stores per-account/model automatic resource
backoff in `RuntimeBackoffStore`, and checks it before automatic work in
`LocalAsrWorkerEngine`. `RuntimeBackoffPolicy` currently defaults to six
automatic resource failures, a 30-second base delay, and a 30-minute delay cap.
These are implementation defaults, not additional #516 product decisions.

The API keeps `ResourceSnapshotJson` opaque and returns it from the owner-scoped
node list. The claim request now optionally carries an explicit automatic/manual
resource-gate decision with sample time, pass/fail, and bounded reason codes.
The server persists failed decisions by owner/node/idempotency key and exposes
the latest reason and consecutive automatic-failure count through owner-scoped
node and claim results. `NoteAsrAckRequest.FailedSegments` remains a per-attempt
count; it is not reused as the cross-attempt counter.

## Implemented API slice

The change is limited to service-side feedback, durable state, and an
opt-in configured cap. It does not parse the opaque snapshot, count heartbeats,
or treat client measurements as proof of real GPU capability.

1. Persist every accepted decision, including pass results whose claim found no
   work, in `note_asr_resource_decisions`, uniquely keyed by owner, node, and
   existing claim idempotency key. Replays compare the immutable normalized
   payload before freshness checks and return the original outcome without
   incrementing again. This also preserves an earlier refusal after later
   successful recovery resets the live counter.
2. Keep the maximum automatic-failure count nullable and configuration-driven.
   Configure `NativeNotes:Routing:MaxAutomaticResourceFailures`. An unset cap
   does not suppress automatic processing; a configured cap returns
   `manual_required`. New decision-aware manual requests bypass only that cap
   and must still pass a fresh resource gate; legacy requests without the
   optional decision retain their previous behavior.
3. Reset the counter only after a fenced, acknowledged successful terminal
   result. Heartbeats and per-attempt segment failures do not reset or increment
   this resource-gate counter.
4. Existing Windows `RuntimeBackoffPolicy` continues to own retry delay; this
   server slice does not invent a retry interval or add `nextEligibleAt`.
5. The request/response DTOs and node DTO gained optional/observable fields.
   Generated contract artifacts remain untouched in this branch; integration
   must export Test Swagger and regenerate consumers from the merged source.

The endpoint remains `POST /api/v1/native-notes/asr-routing/leases/claim`; no
separate reporting endpoint is added.

## Focused verification

- `dotnet build tests/Pim.UnitTests/Pim.UnitTests.csproj --no-restore -m:1`
  succeeds with zero errors.
- `NoteAsrResourceGateRealDbTests` passes 2/2 against isolated PostgreSQL 18.
- Isolated PostgreSQL migration creates durable gate records and node state.
- Identical claim replay is idempotent; a distinct automatic failure increments
  once; owner isolation prevents cross-account reads/writes.
- Stale same-key replay returns its stored outcome; a passed/no-work receipt
  rejects payload changes; concurrent identical PostgreSQL claims count once.
- A configured cap yields `manual_required`; no configured cap invents a
  default; manual processing still fails closed on a failed resource gate.
- Successful fenced completion resets the count; synthetic resource values do
  not claim real GPU/VRAM acceptance.

Real GPU/VRAM measurement, model quality, and post-task resource release remain
external hardware acceptance. HTTP fakes, synthetic resource snapshots, and
isolated PostgreSQL tests can verify protocol and persistence only.
