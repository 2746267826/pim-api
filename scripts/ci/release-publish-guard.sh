#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 决定一次 CI run 是否可以创建/刷新 tag 对应的 release。
#
# 背景：旧 CI run 可以手动重跑。重跑如果照常发布，会在正式版已经提升后
# 把同一个 tag 改回 pre-release（/releases/latest 回退），或者为早已过时的
# commit 补发一条 dev release。
#
# 规则（全部 fail-closed）：
#   * release 已存在且是正式版 → publish=false（绝不碰它）；
#   * release 已存在但 targetCommitish 不是本次 commit / tag 的 run 号不是本次
#     run → 失败（不修改不属于本次 run 的 release）；
#   * release 是 pre-release → 首次尝试允许刷新；重跑只有在该 commit 仍是当前
#     master HEAD 时才允许，否则 publish=false；
#   * release 不存在 → 首次尝试允许创建；重跑只有在该 commit 仍是当前 master
#     HEAD 时才允许，否则 publish=false（不给历史 run 补发）；
#   * 任何 API 错误 → 退出非零，不把「读不到」当成「不存在」。
#
# 该脚本在 concurrency group pim-release-mutation 内运行；release.yml 的提升
# 作业使用同一 group，因此「检查后发布」不会与正式版提升并发。
#
# 用法：
#   scripts/ci/release-publish-guard.sh \
#     --repo owner/repo --tag api-v26.11.42 \
#     --sha <40-hex> --run-number 42 --run-attempt 1
#
# 输出：publish=true|false、reason=<...>；有 GITHUB_OUTPUT 时同时写入。
# ---------------------------------------------------------------------------
set -euo pipefail

REPO=""
TAG=""
SHA=""
RUN_NUMBER=""
RUN_ATTEMPT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="${2:-}"; shift 2 ;;
    --tag) TAG="${2:-}"; shift 2 ;;
    --sha) SHA="${2:-}"; shift 2 ;;
    --run-number) RUN_NUMBER="${2:-}"; shift 2 ;;
    --run-attempt) RUN_ATTEMPT="${2:-}"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

die() {
  echo "::error::$*" >&2
  echo "$*" >&2
  exit 1
}

[[ -n "$REPO" ]] || die "--repo is required"
[[ -n "$TAG" ]] || die "--tag is required"
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || die "--sha 必须是 40 位 commit sha（收到 '${SHA}'）"
[[ "$RUN_NUMBER" =~ ^[0-9]+$ ]] || die "--run-number 必须是数字（收到 '${RUN_NUMBER}'）"
[[ "$RUN_ATTEMPT" =~ ^[0-9]+$ ]] && (( 10#$RUN_ATTEMPT >= 1 )) \
  || die "--run-attempt 必须是 >=1 的整数（收到 '${RUN_ATTEMPT}'）"

TAG_BASE="${TAG%%+*}"
[[ "$TAG_BASE" =~ ^[a-z0-9-]+-v[0-9]{2}\.[0-9]{2}\.([0-9]+)$ ]] \
  || die "tag 不符合 <prefix>-vYY.MM.N 格式：'${TAG}'"
TAG_RUN_NUMBER="${BASH_REMATCH[1]}"
(( 10#$TAG_RUN_NUMBER == 10#$RUN_NUMBER )) \
  || die "tag 里的 run number ${TAG_RUN_NUMBER} 与本次 run ${RUN_NUMBER} 不一致"

command -v gh >/dev/null 2>&1 || die "gh is required"
command -v jq >/dev/null 2>&1 || die "jq is required"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-release-guard.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

RELEASE_FILE="$TMP/release.json"
RELEASE_ERR="$TMP/release.err"
RELEASE_EXISTS=false
if gh api "repos/${REPO}/releases/tags/${TAG}" >"$RELEASE_FILE" 2>"$RELEASE_ERR"; then
  RELEASE_EXISTS=true
elif grep -qE '\(HTTP 404\)|"status"[[:space:]]*:[[:space:]]*404' \
    "$RELEASE_FILE" "$RELEASE_ERR" 2>/dev/null; then
  RELEASE_EXISTS=false
else
  die "无法读取 release ${TAG}（fail-closed，不把 API 错误当成『不存在』）：$(tr '\n' ' ' < "$RELEASE_ERR")"
fi

emit_decision() {
  local publish="$1" reason="$2"
  printf 'publish=%s\nreason=%s\n' "$publish" "$reason"
  if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
    printf 'publish=%s\nreason=%s\n' "$publish" "$reason" >> "$GITHUB_OUTPUT"
  fi
  echo "release publish guard: publish=${publish} reason=${reason}" >&2
}

rerun_freshness() {
  local master_file="$TMP/master.json" master_err="$TMP/master.err"
  if ! gh api "repos/${REPO}/commits/master" >"$master_file" 2>"$master_err"; then
    die "无法读取当前 master HEAD（fail-closed）：$(tr '\n' ' ' < "$master_err")"
  fi
  local master_sha
  master_sha="$(jq -r '.sha // empty' "$master_file")"
  [[ "$master_sha" =~ ^[0-9a-f]{40}$ ]] \
    || die "master HEAD 返回的 sha 非法（收到 '${master_sha}'，fail-closed）"
  if [[ "$master_sha" == "$SHA" ]]; then
    emit_decision true "rerun-current-head"
  else
    emit_decision false "stale-rerun"
  fi
}

if [[ "$RELEASE_EXISTS" == "true" ]]; then
  RELEASE_TAG="$(jq -r '.tag_name // empty' "$RELEASE_FILE")"
  RELEASE_DRAFT="$(jq -r 'if has("draft") then .draft else "missing" end' "$RELEASE_FILE")"
  RELEASE_PRE="$(jq -r 'if has("prerelease") then .prerelease else "missing" end' "$RELEASE_FILE")"
  RELEASE_TARGET="$(jq -r '.target_commitish // empty' "$RELEASE_FILE")"

  [[ "$RELEASE_TAG" == "$TAG" ]] \
    || die "release 返回的 tag_name 是 '${RELEASE_TAG}'，不是 '${TAG}'"
  [[ "$RELEASE_DRAFT" == "false" ]] \
    || die "release ${TAG} 是 draft（收到 '${RELEASE_DRAFT}'）；拒绝由 CI 改写"
  [[ "$RELEASE_TARGET" =~ ^[0-9a-f]{40}$ ]] \
    || die "release ${TAG} 的 targetCommitish 不是 commit sha（收到 '${RELEASE_TARGET}'）；拒绝改写"
  [[ "$RELEASE_TARGET" == "$SHA" ]] \
    || die "release ${TAG} 属于 commit ${RELEASE_TARGET}，不是本次 ${SHA}；拒绝改写"
  [[ "$RELEASE_PRE" == "true" || "$RELEASE_PRE" == "false" ]] \
    || die "release ${TAG} 的 prerelease 不是布尔值（收到 '${RELEASE_PRE}'）"

  if [[ "$RELEASE_PRE" == "false" ]]; then
    emit_decision false "formal-exists"
    exit 0
  fi

  if (( 10#$RUN_ATTEMPT > 1 )); then
    rerun_freshness
  else
    emit_decision true "prerelease-refresh"
  fi
  exit 0
fi

if (( 10#$RUN_ATTEMPT > 1 )); then
  rerun_freshness
else
  emit_decision true "missing"
fi
