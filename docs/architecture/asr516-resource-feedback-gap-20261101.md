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
   The initial worker slice left generated contract artifacts untouched.
   Integration exported actual Test Swagger from source `a6b4cca3`, normalized
   the 1,252,896-byte snapshot with `jq -S`, and ran the official Web
   generation and consistency check successfully. The snapshot SHA-256 is
   `a6ac67bfcebad14e3c5d5e67e09f26883dc54e869786524a31b7bd1422287247`.
   Separate native consumers must still refresh from an actually merged API
   revision before claiming contract freshness.

The endpoint remains `POST /api/v1/native-notes/asr-routing/leases/claim`; no
separate reporting endpoint is added.

## Focused verification

- `dotnet build tests/Pim.UnitTests/Pim.UnitTests.csproj --no-restore -m:1`
  succeeds with zero errors.
- Earlier limited routing-related run: 62 tests total, 31 passed, 31 skipped,
  0 failed. The four resource-gate PostgreSQL cases passed, including the
  legacy same-key replay regression; the remaining PostgreSQL cases skipped
  because `PIM_ASR516_PG` was not set. Log and TRX:
  `scratch/full-roadmap-20261101/asr-resource-integrated-validation-20261102/`
  (`routing-related-realdb.log` and `trx/asr-routing-related-20261102.trx`).
- A separate run against a pre-existing database failed 26/35 with PostgreSQL
  `42703` because that database lacked the current
  `consecutive_automatic_resource_failures` column (9 passed). This is stale
  schema evidence, not a passing current-schema routing verification.
- Final current-schema verification passed 55/55 tests (0 failed, 0 skipped)
  against the previously nonexistent synthetic database
  `test_asr516_resource_20261102_0858` on the owned PostgreSQL 18 instance at
  `127.0.0.1:35439`, with `Maximum Pool Size=5`. That database contains 97
  tables from the current model, including `note_asr_resource_decisions` and
  `consecutive_automatic_resource_failures`. The test binary ran the same
  routing/resource/reprocess filter without weakening any skip or catch. TRX:
  `scratch/full-roadmap-20261101/asr-resource-final-20261102-0858/trx/
  asr-resource-vstest-20261102-0945.trx`
  (SHA-256 `C4DFEA8AD1CDA5B60E9E43EFE05065E218FA15BCAAAEC1B1AA2985D66BD44311`).
  Binary provenance is **unproven / version-source-unverified**: the captured
  `tests/Pim.UnitTests.dll` SHA-256 is
  `308FE0A0FADDAB7A221CCAB3FBC59B6683DC09EFF2AC838E89EA153037A97003` and its
  filesystem timestamp is 2026-11-02 08:21 local, but those facts do not prove
  that the binary was compiled from the later source at HEAD
  `6e5a2710a126acdd8ab284a8cfe0ecd5cf3052df`. The current test source blob
  `98e634afcea80f89e342aa0c73de50d4c6c26881` is recorded separately only as
  source-state evidence. A fresh source-linked build and broader integration
  filter are owned by the delivery tree; do not infer their result from this
  run. An earlier retry run at the same path
  (`asr-resource-final-20261102-0858-retry.trx`, 55 total, 34 passed, 21 failed
  with `3D000` because the synthetic database did not yet exist) is retained as
  failure evidence, not superseded silently.
- The delivery-tree verification supersedes the old binary-provenance gap: its
  receipt `scratch/full-roadmap-20261101/resource-feedback-integrated-20261102/verified-receipt.json`
  records source `d618acc6f752f1f6a2f5205c8299c97ff421cb9c` based on `d4a95882`,
  a fresh source build, and the combined routing/resource/reprocess, material
  endpoint, and task-suggestion filter against synthetic owner-isolated
  PostgreSQL: 87 passed, 0 failed, 0 skipped in 2m7s. The rebuilt test DLL
  SHA-256 is
  `3bd35999660ea72480c693447e22480f8cba012f96031871fbccc9961a299935`; its
  TRX SHA-256 is
  `356b24ba1bcae0fabf54c8bc938da23747da0faffb7bc2766dadb23bf88dafca`. This
  receipt is the source-linked verification for the integrated resource
  feedback slice. The earlier local 55/55 run remains recorded as observed
  execution only; its DLL provenance remains unverified and it is not used as
  source-linked acceptance evidence. The receipt also records the actual
  Swagger source `a6b4cca326321ebbacf63ab7acf5d942291b262a` and contract hash
  `a6ac67bfcebad14e3c5d5e67e09f26883dc54e869786524a31b7bd1422287247`.
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
