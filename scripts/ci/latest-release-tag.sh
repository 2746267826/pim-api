#!/usr/bin/env bash
# Resolve the NEWEST GitHub release tag by VERSION NUMBER (v<year>.<month>.<run>),
# not by release creation time.
#
# Why this exists: version numbers come from GITHUB_RUN_NUMBER, which is
# monotonic per workflow, so the highest version number is always the release
# that logically precedes the one being published. "Newest release by
# created_at" (`gh release list --limit 1`) instead depends on how the publish
# jobs happened to finish, which is wrong in two situations:
#   * two runs overlap (merges a minute apart): the reference can be a release
#     from a *newer* run - carried-over assets then hold artifacts built from a
#     commit this release does not contain, and the changelog window is reversed
#     or empty;
#   * a re-run of an older run: the reference becomes that older run's own
#     newest sibling instead of the release it actually supersedes.
#
# Usage:
#   latest-release-tag.sh --repo owner/repo [--exclude v2026.09.489]
#
# Output: the winning tag on stdout (empty output when nothing matches).
#
# Test hook: set RELEASE_TAGS to a newline-separated tag list to bypass `gh`
# (used by scripts/ci/test-latest-release-tag.sh).
set -euo pipefail

REPO=""
EXCLUDE=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="$2"; shift 2 ;;
    --exclude) EXCLUDE="$2"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -z "${RELEASE_TAGS:-}" && -z "$REPO" ]]; then
  echo "error: --repo owner/repo is required" >&2
  exit 2
fi

if [[ -n "${RELEASE_TAGS:-}" ]]; then
  TAGS="$RELEASE_TAGS"
else
  # Draft releases are excluded by `gh release list`; never fail the caller just
  # because the lookup had a hiccup - an empty result is handled explicitly.
  TAGS="$(gh release list --repo "$REPO" --limit 200 --json tagName -q '.[].tagName' 2>/dev/null || true)"
fi

BEST_NUM=-1
BEST_TAG=""

while IFS= read -r TAG; do
  [[ -z "$TAG" ]] && continue
  [[ -n "$EXCLUDE" && "$TAG" == "$EXCLUDE" ]] && continue
  # Strip a client-patch suffix (v2026.09.466+android.1) before parsing.
  BASE="${TAG%%+*}"
  if [[ ! "$BASE" =~ ^v[0-9]{4}\.[0-9]{2}\.([0-9]+)$ ]]; then
    continue
  fi
  NUM="${BASH_REMATCH[1]}"
  if (( NUM > BEST_NUM )); then
    BEST_NUM="$NUM"
    BEST_TAG="$TAG"
  fi
done <<< "$TAGS"

if [[ -n "$BEST_TAG" ]]; then
  echo "$BEST_TAG"
fi
