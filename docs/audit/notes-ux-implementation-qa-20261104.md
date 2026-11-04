# Notes UX implementation QA (2026-11-04)

## Scope and approach

Reviewed the Web notes flow from note discovery through reading/editing, explicit save consent, version/conflict review, conditional transcription, and organization. Inspected `native-notes-page.tsx`, the ASR start panel, task view, routing view, existing focused tests, and the synthetic Playwright fixture. No API contract, Android, remote service, real account, provider, or billing path was touched.

## Findings

- No high-confidence product code defect was established in this pass, so no UI code was changed. Existing UI keeps the note library and primary actions first; legacy/import tools are in a disclosure; save remains disabled until explicit server-storage consent; audio-bearing notes expose transcription while text-only notes expose organization; destination, material scope, data egress, and possible charges are visible before cloud submission.
- Language, Batch mode, endpoint/diagnostics, recovery, and reprocessing/history are placed in collapsed disclosures in the inspected implementation. Credential configuration is not exposed in the ordinary-user task flow.
- The earlier browser artifact is not a passing run: it stopped waiting for a synthetic task row (`1/2 片`) after entering the processing flow. This is a fixture/flow symptom that needs browser diagnosis; it does not by itself establish a product defect.
- A fresh browser run could not be completed. The previous fixture targets port 3096, which is occupied, and a local Vite instance on port 3107 could not be reached by Playwright (`ERR_NETWORK_ACCESS_DENIED`, then `ERR_CONNECTION_TIMED_OUT` even after approval). Therefore 1440px and 390px visual layout, actual horizontal overflow, and end-to-end browser behavior remain NOT-VERIFIED. No claim of desktop/mobile browser acceptance is made.

## Verified checks

- Focused Web tests: `native-notes-page.test.tsx`, `note-asr-start-panel.test.tsx`, `note-asr-routing-view.test.tsx`, and `note-asr-tasks.test.tsx` — 4 files, 65 tests passed.
- `npm run build` — passed. Existing warnings remain for Vite `__dirname`, large chunks, and `INEFFECTIVE_DYNAMIC_IMPORT`.
- Focused tests cover the core consent and disclosure semantics, but are not a substitute for 1440px/390px browser layout verification.

## Follow-up

Run the synthetic Playwright fixture in an environment where the local browser can reach its isolated Vite server, then inspect both 1440px and 390px screenshots and overflow metrics. Resolve the fixture's missing-task-row symptom as either a test harness issue or a reproducible product defect before changing UI behavior.
