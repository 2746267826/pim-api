# Repository Git Hygiene

This repository contains the API, MCP and Web application. Keep `master` runnable.

## Scope

- The repository governs the **result** (code, contract, security, CI, release artifacts), not the **process** used to produce a change. Choice of development tools, models, review rounds, TDD ordering, subagents or worktrees belongs to each developer.
- CI can only prove what it actually runs. Do not present process steps, extra review rounds or test quantity as substitute evidence for real user-facing verification.

## Same-Repository API And Web

- API source remains under `src/`; Web source is under `web/client-web/` (imported from pim-web with original Git history preserved).
- `contract/openapi.json` is the only OpenAPI authority. Do not guess endpoints, fields or enum values. Implement API and Web consumers in the same PR when they belong to one feature.
- API shape changes require exporting the contract in the same PR: run the API in `ASPNETCORE_ENVIRONMENT=Test`, fetch `/swagger/v1/swagger.json`, normalize with `jq -S .`.
- Regenerate Web types with `bash web/scripts/ci/update-contract.sh`; verify with `bash web/scripts/ci/check-contract.sh`. Do not edit generated types manually or fetch a second Web contract snapshot.
- Android and Windows remain separate consumers. Contract notification targets only those two repositories. Notification failure is visible but is not a release blocker; retry on master with `gh workflow run ci.yml --repo 2746267826/pim-api --ref master -f force_all=true -f force_contract_notify=true`.

## CI And Release

- Required merge check is `CI Gate`, through a PR. The owner removed the Independent Acceptance App; no external stamp is required. Do not add replacement approval loops or weaken CI to get a green check.
- The server Dockerfile builds `web/client-web` directly from the same checkout and embeds the result in `/app/wwwroot`. Do not restore external `pim-web-dist` image resolution or cross-repository release dispatch.
- CI verifies frontend lint/tests/build and generated types, then builds and smoke-tests the embedded image. Web-only changes must rebuild that image; backend changes must not reuse stale API/MCP assets.
- `/app/wwwroot/web-version.json`, `/api/version`, image labels and release `pim-web-version.json` identify the actual embedded source/version. The ledger reads the built image, not a separate calculation.
- Docs-only changes need no product rebuild. A failed, cancelled or unexpectedly skipped required build must not pass `CI Gate`.
- Preserve production isolation, required PRs, no force-push/delete protection and existing release asset formats. Never use admin bypass.

## Security Boundary

- Never point a development or test session at the production database `pim_prod`. Production runs on the home Docker host: API in the `pim-pim-1` container, database `pim_prod` in the 1Panel PostgreSQL container (`127.0.0.1:5432`). Work that needs real data uses the mirrored `pim_test` database (restored from a sanitized `pim_prod` snapshot); the mirror is disposable and its contents are never written back to production.
- Production logs are JSONL files inside the running container — `/data/pim/logs/pim-api-<YYYYMMDD>[_NNN].jsonl` (Serilog compact JSON; `@t` is UTC, files roll at ~1 GB so newest data may live in a `_NNN` file). Read them with `docker exec pim-pim-1 tail` / `grep`; `docker logs` only carries startup and crash output.
- Never copy production dumps, log extracts, or credentials into a PR, an issue, or any artifact. Tokens live in memory or encrypted storage only — never in WebView or uploads. Logs and confirmation pages must not leak tokens, GraphEventId, ChangeKey, etc. Re-check whitelist-style URL validation against "follow what the server returns" semantics.

## Communication And Language

- Communicate with the user in Simplified Chinese by default.
- For user-facing UI, default visible text to Simplified Chinese. Keep code identifiers, API names, logs, protocol fields, and third-party product names in English unless localization is explicitly part of the task.

## Branches, PRs, And Verification

- All file-changing work goes through a pull request on a non-`master` branch. Do not push directly to `master` unless the user explicitly asks for a direct update and understands it bypasses the PR workflow.
- Branches use the `{agent}-{os}/{topic}` prefix (e.g. `antigravity-linux/location-fix`): `agent` is the developer's agent name, `os` is `win` or `linux`, `topic` is a short kebab-case summary. Branches opened automatically by tooling (e.g. `task/<n>`) do not follow this scheme.
- Do not modify `.github/workflows/*` unless the task is specifically about CI/release automation or the user explicitly asks for it. If a workflow change is unavoidable, explain why before editing it.
- After opening or updating a PR, wait for triggered GitHub Actions checks and confirm they pass before calling the task complete. If no workflow triggers because of path filters, state that explicitly.
- Local commands help you verify before pushing; they are not a required pre-CI ritual. Backend changes: `dotnet test Pim.sln`. Contract changes require re-exporting `contract/openapi.json` in the same PR. CI owns the complete required checks — do not claim a branch is complete based on stale output or with verification failing; document unrelated failures explicitly instead.

## Pull Request Descriptions Feed The Release Changelog

- Every PR description MUST include the four bilingual sections below, keeping the exact heading format. CI extracts these into the GitHub Release changelog (`scripts/ci/build-release-notes.sh`):
  - `## 技术修改 / Technical changes` — modules/files touched, key design decisions, API or schema changes, dependency changes
  - `## 功能变化 / Feature changes` — user-visible changes; write `无 / None` if none
  - `## 如何体验 / How to try it` — how a user experiences the feature: which screen, what actions, what visible effect to expect (step-by-step is fine). This is an experience guide for users, NOT deployment/build instructions — build/run/verify commands belong in 测试 / Tests
  - `## 测试 / Tests` — verification commands run and their results
- Every PR must also fill the `## 需求与验收映射 / Requirement & Acceptance Mapping` table from the PR template. Without an external work order, describe the requirements and acceptance criteria directly in the PR. Map each item to actual evidence; mark unverified items honestly, never as `PASS`.
- Docs-only merges skip all platform builds and do not produce a GitHub Release (path-filtered).

## During Work

- Keep generated outputs out of commits: `bin/`, `obj/`, `build/`, `build/artifacts/` (docker image tarballs), `dist/`, `publish/`, `.dotnet-*`, npm caches, and API `wwwroot` build artifacts.
- Commit source changes, tests, scripts, and docs that are needed to reproduce the current runnable version.
- Keep API and daemon defaults aligned. The local API is expected at `http://127.0.0.1:5858`, and the Windows daemon default server URL should match it.
- Use focused commits with conventional messages such as `feat:`, `fix:`, `docs:`, or `chore:`. Write both commit messages (titles & descriptions) and PR titles/descriptions in bilingual format (English and Simplified Chinese).
