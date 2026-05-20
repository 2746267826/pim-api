# Repository Git Hygiene

This repository is shared by multiple agent conversations. Keep `master` useful as the latest runnable version of the API, web client, and Windows daemon.

## Start Of Every Session

- Run `git status --short --branch` before changing files.
- Run `git fetch --all --prune` before deciding whether `master` is current.
- If `master` is behind `origin/master`, pull before making new work unless the user explicitly asks otherwise.
- Note existing dirty files and do not revert or overwrite work you did not create.

## During Work

- Keep generated outputs out of commits: `bin/`, `obj/`, `build/`, `dist/`, `publish/PimDaemon/`, `publish/*.zip`, `.dotnet-*`, `.superpowers/brainstorm/`, npm caches, and API `wwwroot` build artifacts.
- Commit source changes, tests, scripts, and docs that are needed to reproduce the current runnable version.
- Keep API and daemon defaults aligned. The local API is expected at `http://127.0.0.1:5858`, and the Windows daemon default server URL should match it.
- Use focused commits with conventional messages such as `feat:`, `fix:`, `docs:`, or `chore:`.

## Before Pushing

- Run the relevant verification commands for the touched surface. Prefer `dotnet test Pim.sln` for backend/daemon changes and `npm --prefix src/client-web run build` for web changes.
- Re-run `git status --short --branch` and confirm only intentional changes are staged.
- Push `master` to `origin` after a successful commit when the user asks for the repository to be up to date on GitHub.

## If Verification Fails

- Do not claim the branch is complete.
- Commit only if the failure is clearly unrelated and document the exact failure in the final response.
- Leave enough status detail for the next conversation to continue without rediscovering the same state.
