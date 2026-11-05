# Notes browser evidence (2026-11-04; 11/5 closeout)

Update: 2026-11-05 (11/5 closeout).

This is the original UI checkpoint. Follow the [continuation record](../product/notes-continuation-20261105.md) for later material compatibility changes and their verification.

## Scope

This record covers only attempts to run the isolated synthetic Playwright flow against the final local Web UI. The runner uses mocked API responses and synthetic note/media fixtures. It does not verify product/API behavior against a real service, Android, sync, provider, billing, or a real account. A runner assertion passing is not product acceptance.

## Runner changes

- Updated the ordinary-user configuration expectations to match the current page: users are told to contact an administrator to enable a shared target; they are not expected to see a credential-settings route.
- Updated the derived-task disclosure expectation. With no available shared target, the current “诊断与高级限制” disclosure is not rendered, so the runner now expects its absence.
- Made the ASR task-history disclosure check tolerate it being opened by the user-flow setup, then explicitly collapse and reopen it for the assertion.
- Added a viewport selector (`all`, `desktop`, or `narrow`) as the fifth CLI argument. This permits an isolated 390px run without desktop assertions blocking it. Example: `node scratch/qa/notes-user-flow/notes-user-flow.playwright.mjs http://127.0.0.1:3000 scratch/qa/notes-user-flow/artifacts "C:\Program Files\Google\Chrome\Application\chrome.exe" narrow`.
- Added semantic zero-audio assertions (no transcription region and no ASR selector), per-state horizontal overflow samples, and browser console error capture. These avoid binding acceptance to mutable step-heading copy.

## Evidence and outcome

- The new runner used the actual `npm run build` dist, with Playwright `route.fulfill` serving local static resources to avoid loopback-network dependence. The API layer used synthetic mocks only; no real service, provider, production, billing, device, or cross-client acceptance was performed.
- Browser execution passed at both 1440x1000 and 390x844. Each viewport covered 11 states without horizontal overflow; `unmatched`, `external`, `pageErrors`, and `consoleErrors` were empty. The report covered library create/search, note reading/editing, explicit save, read-only conflict candidate and explicit promotion, recording/photo/ink reference preservation, ASR no-duplicate-start and success reuse, collapsed re-transcription, return/re-entry, zero-audio organize entry, and collapsed legacy/import sections.
- No real generation service was invoked and no new paid ASR task was created; the unavailable generation target was shown as such.
- The current Web worktree is branch `codex-win/notes-user-flow-20261104`, HEAD `302210428b471e5add970988d2b7f32c10ab98c4`; page/test files have uncommitted UX changes and this round does not commit them.

## Verification status

Final non-browser code validation reports focused checks `49/49` (`derived 23/23`, `native 26/26`), the real 5-second polling state transition and A→B→A authorization-reset regression passed, `npm run build` passed, local `npm run lint --` for the four source/test files passed with 2 pre-existing React warnings, and `git diff --check` plus Node check passed. The product behavior is fixed to 5-second ASR polling, no duplicate task start for `pending`/`error`/`active`, success reuse by default, a collapsed retry-transcription control, and the “生成完整记录与分析” button. Switching transcription results requires reconfirmation even when switching back from A to the original A; the generation target and durable submission are preserved. The final source SHA-256 is `BD5CE53A065D898A6F673CB2F1DEBDE3768425AE917F4AE987EBF6535BEBD045`. The final source/hash receipt is [validation-20261105.json](../../scratch/qa/notes-user-flow/validation-20261105.json); it records the verified frozen uncommitted HEAD and source hashes.

- Desktop 1440x1000: PASS. See [final browser report](../../scratch/qa/notes-user-flow/offline-20261105/run-2026-11-05T03-33-28-452Z/report.json).
- Mobile 390x844: PASS. See [final browser report](../../scratch/qa/notes-user-flow/offline-20261105/run-2026-11-05T03-33-28-452Z/report.json).
- Horizontal overflow and browser diagnostics: PASS for both viewports; `unmatched`, `external`, `pageErrors`, and `consoleErrors` are empty. This remains synthetic browser evidence, not cross-client real-service acceptance.
- No product UI, API, Android, or roadmap files were changed as part of this evidence update. Nothing in this record should be read as product acceptance.
