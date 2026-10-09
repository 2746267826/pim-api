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
  TRUNCATED=false
  if [[ -n "$PR_NUMBERS" ]]; then
    # 每个 PR 渲染成若干行后作为 release body 传给发布 action —— 而 body 是作为
    # 命令行参数传入的，Linux 对单个参数有 128 KB 上限（MAX_ARG_STRLEN）。
    # 正常情况下窗口里只有上次发布以来的几个 PR；但当窗口覆盖全部历史
    # （首次发布，或历史被改写而基线不可比）时就会超出上限，报
    # "Argument list too long"。这里按字符预算渲染（从最新的 PR 开始），
    # 超出即停止并注明省略，保证这种退化情形仍能发出版本。
    BUDGET="${PIM_RELEASE_NOTES_BUDGET:-90000}"
    RENDERED_CHARS=0
    NEWEST_FIRST="$(printf '%s\n' "$PR_NUMBERS" | tac)"
    for N in $NEWEST_FIRST; do
      [[ -z "$N" ]] && continue
      PR_JSON="$(gh pr view "$N" --repo "$REPO" --json number,title,body,url,mergedAt 2>/dev/null || true)"
      [[ -z "$PR_JSON" ]] && continue
      BLOCK="$("$PYTHON_BIN" "$PARSER" <<< "$PR_JSON")"
      if (( RENDERED_CHARS + ${#BLOCK} > BUDGET )); then
        TRUNCATED=true
        break
      fi
      echo "$BLOCK"
      RENDERED_CHARS=$((RENDERED_CHARS + ${#BLOCK}))
      PRODUCED_ANY=true
    done
    if [[ "$TRUNCATED" == "true" ]]; then
      echo "> 本次窗口覆盖的 PR 过多，已按体积省略较早的部分（历史改写或首次发布时窗口会覆盖全部历史）。"
      echo ""
    fi
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
