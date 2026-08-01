# Repository Git Hygiene

This repository is shared by multiple agent conversations. Keep `master` useful as the latest runnable version of the API, web client, and Windows daemon.

## Communication And Planning

- Communicate with the user in Simplified Chinese by default.
- When writing a plan, state the final objective at the top of the plan. If Goal mode is available in the current environment, create or update a Goal using that same objective.
- For user-facing UI, default visible text to Simplified Chinese. Keep code identifiers, API names, logs, protocol fields, and third-party product names in English unless localization is explicitly part of the task.

## Start Of Every Session

- Run `git status --short --branch` before changing files.
- Run `git fetch --all --prune` before deciding whether `master` is current.
- If `master` is behind `origin/master`, pull before making new work unless the user explicitly asks otherwise.
- Note existing dirty files and do not revert or overwrite work you did not create.

## Branch, PR, And GitHub Actions Workflow

- All file-changing work must happen on a non-`master` branch.
- Create branches with the `codex/` prefix unless the user asks for another branch name.
- Make focused commits at suitable checkpoints. Push the working branch to GitHub when creating a PR, enabling CI visibility, handing work off, or preserving a useful checkpoint.
- Open a pull request for all file-changing work.
- After opening or updating a PR, wait for triggered GitHub Actions checks and confirm they pass before calling the task complete.
- If no GitHub Actions workflow is triggered because the changed files do not match workflow path filters, state that explicitly instead of waiting.
- Do not modify `.github/workflows/*` unless the task is specifically about CI/release automation or the user explicitly asks for it. If a workflow change is unavoidable, explain why before editing it.
- Write PR titles and descriptions in both English and Simplified Chinese.

## Parallel Agent Workflow

- Prefer multiple subagents for independent investigation, implementation, review, and verification work when tasks can safely run in parallel.
- Do not use subagents for tightly coupled edits where coordination overhead would create risk.

## During Work

- Keep generated outputs out of commits: `bin/`, `obj/`, `build/`, `dist/`, `publish/PimDaemon/`, `publish/*.zip`, `.dotnet-*`, `.superpowers/brainstorm/`, npm caches, and API `wwwroot` build artifacts.
- Commit source changes, tests, scripts, and docs that are needed to reproduce the current runnable version.
- Keep API and daemon defaults aligned. The local API is expected at `http://127.0.0.1:5858`, and the Windows daemon default server URL should match it.
- Use focused commits with conventional messages such as `feat:`, `fix:`, `docs:`, or `chore:`. Write both commit messages (titles & descriptions) and PR titles/descriptions in bilingual format (English and Simplified Chinese).

## Before Pushing Or Opening A PR

- Run the relevant verification commands for the touched surface. Prefer `dotnet test Pim.sln` for backend/daemon changes and `npm --prefix src/client-web run build` for web changes.
- Android status UI changes must also run `src/client-android/gradlew.bat :app:connectedDebugAndroidTest --no-daemon` on a started emulator or physical device; this is a local gate because CI does not provide an emulator.
- Re-run `git status --short --branch` and confirm only intentional changes are staged.
- Push the working branch to `origin` and open a pull request. Do not push directly to `master` unless the user explicitly asks for a direct update and understands it bypasses the PR workflow.

## If Verification Fails

- Do not claim the branch is complete.
- Commit only if the failure is clearly unrelated and document the exact failure in the final response.
- Leave enough status detail for the next conversation to continue without rediscovering the same state.
