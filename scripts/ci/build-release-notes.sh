#!/usr/bin/env bash
# Build a structured release changelog from merged PRs in the current release
# window, extracting the 【技术修改 / Technical changes】【功能变化 / Feature
# changes】【如何体验 / How to try it】 sections that PR authors fill in via
# .github/pull_request_template.md. Outputs markdown on stdout.
#
# Requirements: gh CLI with a token (GH_TOKEN / GITHUB_TOKEN), full git history
# (fetch-depth: 0), python3 (falls back to `python`).
#
# Usage:
#   build-release-notes.sh --repo owner/repo [--from-tag vX.Y.Z] [--to-tag vX.Y.Z]
set -euo pipefail

REPO=""
FROM_TAG=""
TO_TAG=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="$2"; shift 2 ;;
    --from-tag) FROM_TAG="$2"; shift 2 ;;
    --to-tag) TO_TAG="$2"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "$REPO" ]]; then
  echo "error: --repo owner/repo is required" >&2
  exit 2
fi
if [[ -z "${GH_TOKEN:-}" && -z "${GITHUB_TOKEN:-}" ]]; then
  echo "error: GH_TOKEN or GITHUB_TOKEN is required for gh" >&2
  exit 2
fi

# Prefer python3 (preinstalled on GitHub runners); fall back to `python`.
# Verify the interpreter actually runs: on Windows, `python3` may resolve to a
# Microsoft Store app-execution stub that exits without producing output.
PYTHON_BIN=""
if command -v python3 >/dev/null 2>&1 && python3 -c 'import sys' >/dev/null 2>&1; then
  PYTHON_BIN="python3"
else
  PYTHON_BIN="$(command -v python || true)"
fi
if [[ -z "$PYTHON_BIN" ]]; then
  echo "error: python3/python not found" >&2
  exit 2
fi
export PYTHONIOENCODING="${PYTHONIOENCODING:-utf-8}"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PARSER="$SCRIPT_DIR/parse_pr_body.py"

# 1. Resolve the previous release tag (exclude drafts, include prereleases)
if [[ -z "$FROM_TAG" ]]; then
  FROM_TAG="$(gh api "repos/${REPO}/releases" --jq '[.[] | select(.draft == false)][0].tag_name' 2>/dev/null || true)"
fi

PREV_SHA=""
if [[ -n "$FROM_TAG" ]]; then
  PREV_SHA="$(git rev-parse "${FROM_TAG}^{commit}" 2>/dev/null || true)"
fi
if [[ -z "$PREV_SHA" ]]; then
  PREV_SHA="$(git rev-list --max-parents=0 HEAD | tail -1)"
  FROM_TAG="(initial)"
fi

TO_SHA="HEAD"
if [[ -n "$TO_TAG" ]] && git rev-parse "${TO_TAG}^{commit}" >/dev/null 2>&1; then
  TO_SHA="$(git rev-parse "${TO_TAG}^{commit}")"
fi

# 2. Collect merged PR numbers in the window, ordered by merge commit time
#    Supports both merge commits ("Merge pull request #123 ...") and squash
#    merges ("... (#123)"). Uses --first-parent so we only inspect commits that
#    landed directly on the release branch and avoid internal branch commits.
PR_NUMBERS="$(git log --first-parent --format='%ct %s' "${PREV_SHA}..${TO_SHA}" \
  | sort -n \
  | sed -nE -e 's/^[0-9]+ Merge pull request #([0-9]+).*/\1/p' \
            -e 's/^[0-9]+ .*\(\#([0-9]+)\)[[:space:]]*$/\1/p' \
  | awk '!seen[$0]++')"

# 3. Render the changelog
{
  echo "## What's Changed / 更新内容"
  echo ""

  PRODUCED_ANY=false
  if [[ -n "$PR_NUMBERS" ]]; then
    while IFS= read -r N; do
      [[ -z "$N" ]] && continue
      PR_JSON="$(gh pr view "$N" --repo "$REPO" --json number,title,body,url,mergedAt 2>/dev/null || true)"
      if [[ -n "$PR_JSON" ]]; then
        echo "$PR_JSON" | "$PYTHON_BIN" "$PARSER"
        PRODUCED_ANY=true
      fi
    done <<< "$PR_NUMBERS"
  fi

  if [[ "$PRODUCED_ANY" != "true" ]]; then
    echo "No merged pull requests in this window (direct commits only):"
    echo ""
    git log --first-parent --format='- %h %s' "${PREV_SHA}..${TO_SHA}"
    echo ""
  fi

  # Full changelog / compare link
  if [[ -n "$TO_TAG" && "$FROM_TAG" != "(initial)" ]]; then
    echo "**完整变更 / Full Changelog**: https://github.com/${REPO}/compare/${FROM_TAG}...${TO_TAG}"
  elif [[ -n "$TO_TAG" ]]; then
    echo "**完整变更 / Full Changelog**: https://github.com/${REPO}/releases/tag/${TO_TAG}"
  fi
}
