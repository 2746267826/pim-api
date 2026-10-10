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
# 对外显示的版本号用两位年份（26.10.42）；程序集版本（assembly_version）仍用四位
# 年份，因为 .NET 程序集版本不值得为了好看去动。
YEAR_MONTH="${YEAR:2}.${MONTH}"
BASE="${YEAR_MONTH}.${N}"
VERSION_CODE=$((100000 + N))

# Release tag 带仓库前缀：pim-android -> android-v26.10.42
# 前缀由仓库名推导（去掉 pim-），四个仓共用同一份脚本而无需各自传参。
# GITHUB_REPOSITORY 是 "owner/repo"，先取最后一段；再以 :- 兜底，因为脚本
# 在 set -u 下运行，本地直接跑（无该变量）时裸引用会以 unbound variable 崩掉。
REPO_NAME="${GITHUB_REPOSITORY:-}"
REPO_NAME="${REPO_NAME##*/}"
PREFIX="${REPO_NAME#pim-}"
if [[ -z "$PREFIX" || "$PREFIX" == "$REPO_NAME" ]]; then
  # 本地运行（无 GITHUB_REPOSITORY）或仓库名不带 pim- 前缀时的兜底
  PREFIX="${PIM_REPO_PREFIX:-pim}"
fi
RELEASE_TAG="${PREFIX}-v${BASE}"

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
release_tag=$RELEASE_TAG
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
    echo "release_tag=$RELEASE_TAG"
  } >> "$GITHUB_OUTPUT"
fi

echo "Resolved version=$version code=$VERSION_CODE tag=$RELEASE_TAG release=$is_release"
