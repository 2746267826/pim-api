#!/usr/bin/env bash
set -euo pipefail

DATE_OVERRIDE=""
PRINT_ENV=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --date) DATE_OVERRIDE="$2"; shift 2 ;;
    --print-env) PRINT_ENV=true; shift ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

if [[ -n "$DATE_OVERRIDE" ]]; then
  YEAR="${DATE_OVERRIDE:0:4}"
  MONTH="${DATE_OVERRIDE:5:2}"
else
  YEAR="$(date -u +%Y)"
  MONTH="$(date -u +%m)"
fi

N="${GITHUB_RUN_NUMBER:-0}"
if ! [[ "$N" =~ ^[0-9]+$ ]] || [[ "$N" -lt 1 ]]; then
  echo "GITHUB_RUN_NUMBER must be positive integer, got: ${GITHUB_RUN_NUMBER-}" >&2
  exit 1
fi

SHA_FULL="${GITHUB_SHA:-0000000000000000000000000000000000000000}"
SHA7="${SHA_FULL:0:7}"
YEAR_MONTH="${YEAR}.${MONTH}"
BASE="${YEAR_MONTH}.${N}"
VERSION_CODE=$((100000 + N))

REF="${GITHUB_REF:-}"
EVENT="${GITHUB_EVENT_NAME:-}"
CLIENT_PATCH="${CLIENT_PATCH:-}"

is_release=false
version="$BASE"
if [[ "$REF" == "refs/heads/master" ]]; then
  is_release=true
  if [[ -n "$CLIENT_PATCH" ]]; then
    version="${BASE}+${CLIENT_PATCH}"
  fi
elif [[ "$EVENT" == "pull_request" ]]; then
  PR_NUMBER="${PR_NUMBER:-${GITHUB_PR_NUMBER:-}}"
  if [[ -z "$PR_NUMBER" && "$REF" =~ refs/pull/([0-9]+)/ ]]; then
    PR_NUMBER="${BASH_REMATCH[1]}"
  fi
  if [[ -z "$PR_NUMBER" ]]; then
    echo "PR_NUMBER required for pull_request" >&2
    exit 1
  fi
  version="${BASE}-pr.${PR_NUMBER}+${SHA7}"
else
  version="${BASE}-dev+${SHA7}"
fi

# filesystem-safe slug
artifact_slug="$(echo "$version" | sed 's/+/-/g')"

assembly_version="${YEAR}.$((10#$MONTH)).${N}.0"

if [[ "$PRINT_ENV" == true ]]; then
  cat <<EOF
version=$version
version_code=$VERSION_CODE
artifact_slug=$artifact_slug
git_sha_short=$SHA7
is_release=$is_release
year_month=$YEAR_MONTH
assembly_version=$assembly_version
base_version=$BASE
EOF
  exit 0
fi

if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  {
    echo "version=$version"
    echo "version_code=$VERSION_CODE"
    echo "artifact_slug=$artifact_slug"
    echo "git_sha_short=$SHA7"
    echo "is_release=$is_release"
    echo "year_month=$YEAR_MONTH"
    echo "assembly_version=$assembly_version"
    echo "base_version=$BASE"
  } >> "$GITHUB_OUTPUT"
fi

echo "Resolved version=$version code=$VERSION_CODE release=$is_release"
