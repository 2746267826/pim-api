# Repository Git Hygiene

This repository is shared by multiple agent conversations. Keep `master` useful as the latest runnable version of the API, web client, and Windows daemon.

## Communication And Planning

- Communicate with the user in Simplified Chinese by default.
- For user-facing UI, default visible text to Simplified Chinese. Keep code identifiers, API names, logs, protocol fields, and third-party product names in English unless localization is explicitly part of the task.

## Start Of Every Session

- Run `git status --short --branch` before changing files.
- Run `git fetch --all --prune`; if `master` is behind `origin/master`, pull before making new work unless the user explicitly asks otherwise.
- Move the main workspace to the latest `origin/master` at the start of every conversation, unless the user explicitly asks to base work on another branch (e.g. a handoff task). The main workspace is for read-only investigation; all file-changing work happens in worktrees — see the branch and worktree policy below.
- Note existing dirty files and do not revert or overwrite work you did not create.

## Branch, PR, And GitHub Actions Workflow

- All file-changing work must happen on a non-`master` branch.
- Create branches with an `{agent}-{os}/{topic}` prefix (e.g. `antigravity-linux/location-fix`, `xiaoi-linux/ci-release-serialization`) unless the user asks for another branch name. `agent` is the AI agent's name, `os` is the operating system the agent runs on (`win` or `linux`), and `topic` is a short kebab-case English summary of the branch's purpose. Branches opened automatically by tooling (e.g. `task/<n>`) do not follow this scheme.
- Make focused commits at suitable checkpoints. Push the working branch to GitHub when creating a PR, enabling CI visibility, handing work off, or preserving a useful checkpoint.
- Open a pull request for all file-changing work.
- After opening or updating a PR, wait for triggered GitHub Actions checks and confirm they pass before calling the task complete.
- If no GitHub Actions workflow is triggered because the changed files do not match workflow path filters, state that explicitly instead of waiting.
- Do not modify `.github/workflows/*` unless the task is specifically about CI/release automation or the user explicitly asks for it. If a workflow change is unavoidable, explain why before editing it.
- Write PR titles and descriptions in both English and Simplified Chinese.
- Create git worktrees under a single short root directory per platform, never scattered across filesystem roots:
  - **Windows**: `C:\pim-wt\{topic}`. Use short directory names (topic only, ≤ 12 chars) to avoid Windows MAX_PATH issues from long nested paths.
  - **Linux (codeg workspace)**: `/home/coder/workspace/pim-wt/{topic}`. **Never use `/tmp`** (wiped on rebuild, losing in-progress work). No MAX_PATH constraint, but keep names short for consistency. Other Linux agents use `pim-wt/{topic}` under their own persistent work directory.

### Branch And Worktree Policy (L0/L1/L2)

Apply this policy at the start of every session/task:

- **L0 — read-only sessions** (investigation, discussion, review, inspection): make no file changes; work directly in the main workspace. Before investigating, run `git fetch --all --prune` and fast-forward local `master` to `origin/master` — the remote is the source of truth, so the main workspace must always read the latest `master`. The main workspace is a valid baseline only after it has been fast-forwarded to `origin/master` this session; otherwise read baseline content via `git show origin/master:<path>` or an up-to-date worktree. Targeted reads (PR head/diff, handoff source branch, local changes under review) read their own target refs and are compared against the latest `origin/master`. No branch or worktree is created.
- **L1 — file-changing tasks**: before editing any file, fetch the latest `origin/master`, create a branch `{agent}-{os}/{topic}` based on it, and add a worktree for that branch. Do all edits inside that worktree; the main workspace never receives file changes.
- **L2 — handoff tasks**: when new work must build on an unmerged branch (e.g. continuing another agent's PR), base the new branch on that branch's latest HEAD and state the base in the PR description. If the source branch is already merged, base on `origin/master` instead.
- Cleaning is part of the definition of done: when a PR is merged or work is abandoned, remove the worktree (`git worktree remove`) and delete the local branch in the same task. Dead worktrees/branches from earlier tasks must be cleaned when noticed — e.g. if the main workspace is found on a stale or merged branch, move it back to `origin/master` before starting new work.

## Pull Request Descriptions Feed The Release Changelog

- Every PR description MUST include the four bilingual sections below, keeping the exact heading format. CI extracts these into the GitHub Release changelog (`scripts/ci/build-release-notes.sh`):
  - `## 技术修改 / Technical changes` — modules/files touched, key design decisions, API or schema changes, dependency changes
  - `## 功能变化 / Feature changes` — user-visible changes; write `无 / None` if none
  - `## 如何体验 / How to try it` — how a user experiences the feature: which screen, what actions, what visible effect to expect (step-by-step is fine). This is an experience guide for users, NOT deployment/build instructions — build/run/verify commands belong in 测试 / Tests
  - `## 测试 / Tests` — verification commands run and their results
- PRs without these sections still merge, but their release entry falls back to a bare title link — filling them in keeps the changelog useful.
- Docs-only merges skip all platform builds and do not produce a GitHub Release (path-filtered); the sections above are still expected for accurate history.

## During Work

- Keep generated outputs out of commits: `bin/`, `obj/`, `build/`, `build/artifacts/` (docker image tarballs), `dist/`, `publish/PimDaemon/`, `publish/*.zip`, `.dotnet-*`, `.superpowers/brainstorm/`, npm caches, and API `wwwroot` build artifacts.
- Commit source changes, tests, scripts, and docs that are needed to reproduce the current runnable version.
- Keep API and daemon defaults aligned. The local API is expected at `http://127.0.0.1:5858`, and the Windows daemon default server URL should match it.
- Use focused commits with conventional messages such as `feat:`, `fix:`, `docs:`, or `chore:`. Write both commit messages (titles & descriptions) and PR titles/descriptions in bilingual format (English and Simplified Chinese).

## Before Pushing Or Opening A PR

- Run the relevant verification commands for the touched surface. Prefer `dotnet test Pim.sln` for backend/daemon changes and `npm --prefix src/client-web run build` for web changes.
- Android status UI changes must also run the connected Android test gate on a started emulator or physical device — Windows: `src/client-android/gradlew.bat :app:connectedDebugAndroidTest --no-daemon`; Linux: `src/client-android/gradlew :app:connectedDebugAndroidTest --no-daemon`. This is a local gate because CI does not provide an emulator.
- Re-run `git status --short --branch` and confirm only intentional changes are staged.
- Push the working branch to `origin` and open a pull request. Do not push directly to `master` unless the user explicitly asks for a direct update and understands it bypasses the PR workflow.

## If Verification Fails

- Do not claim the branch is complete.
- Commit only if the failure is clearly unrelated and document the exact failure in the final response.

## Working Practices (derived from session experience)

### Process discipline

- **A1. Mandatory TDD (RED-GREEN-REFACTOR).** Write a failing test and watch it fail before writing the minimal implementation. Never substitute a "looks right" implementation for real concurrency/timing correctness.
- **A2. Verify before claiming done, using fresh output only.** Re-run the relevant gate commands and base claims on that run, never on stale results. Re-run the full suite once before commit and once before opening a PR.
- **A3. Review is a mandatory gate: severity-graded, zero-blockers to proceed.** Classify findings Critical/Important/Minor; no Important+ findings may remain before commit/merge. After fixing, always re-review the changed result.
- **A4. Review cadence.** Small changes: single review round, then re-review after fixes. Large stages: one holistic review at stage end.
- **A5. Deliverables carry explicit acceptance criteria.** State in the PR both how success will be judged after merge and how to test it, written in plain language (avoid overly technical control names and jargon).

### Quality habits

- **B1. Use injected clocks in tests; never fixed timestamps.** Prefer TimeProvider/clock injection over direct UtcNow and hard-coded dates.
- **B2. Classify test failures before fixing.** Failures may be environmental: wrong cwd (relative-path false positives), build-tool injection (Playwright evaluate rewritten by esbuild), or CI-only races. Confirm the failure's ownership before changing product code or tests.
- **B3. Bug fixing is log/data-driven; disproving assumptions is normal.** Locate issues from logs, data, and precise timelines before touching code. When an assumption is falsified, restart that line of investigation.
- **B4. Verify external sub-agent output.** Delegated workers may fabricate, exit early, or return unreliable conclusions. Check their actual tool-call traces and require reproducible evidence; re-dispatch on anomalies.
- **B5. Security review for anything touching external URLs, credentials, or privacy.** Whitelist-style validation must be re-checked against "follow what the server returns" semantics. Logs/confirmation pages must not leak tokens, GraphEventId, ChangeKey, etc. Tokens live in memory or encrypted storage only — never in WebView or uploads.

### Collaboration

- **C1. Maximize parallelism, serialize writes.** Read/investigation/review tasks run in parallel; file-writing tasks serialize or declare non-overlapping paths. Do not use subagents for tightly coupled edits where coordination overhead would create risk.
- **C2. Leave resumable state when interrupted.** End sessions by stating the current step and what the next agent should do first.

## Production Database And Logs

- Production runs on the home Docker host: the API in the `pim-pim-1` container, the database `pim_prod` in the 1Panel PostgreSQL container (`127.0.0.1:5432`).
- **Never point an agent session at `pim_prod`.** Work that needs real data uses the mirrored database `pim_test`, restored from a `pim_prod` snapshot (`pg_dump -Fc` → `pg_restore`) with credentials and personal tokens sanitized. The mirror is disposable and safe to mutate; its contents are "as of the snapshot time", so never write results back to production.
- Production logs are JSONL files inside the running container — `/data/pim/logs/pim-api-<YYYYMMDD>[_NNN].jsonl` (Serilog compact JSON; `@t` is UTC, and files roll at ~1 GB so the newest data may live in a `_NNN` file). Read them with `docker exec pim-pim-1 tail` / `grep`; `docker logs` only carries startup and crash output.
- Never copy production dumps, log extracts, or credentials into a PR, an issue, or any artifact.
