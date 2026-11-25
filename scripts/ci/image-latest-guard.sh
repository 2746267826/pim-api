#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 决定本次 run 是否可以移动可变的镜像 tag `:latest`。
#
# 背景：CI run 可以重跑。重跑旧 commit 时，若照常 docker push :latest，
# 会把生产默认拉取的镜像 tag 指回旧构建（docker-compose.prod.yml 的
# PIM_IMAGE_TAG 默认是 latest）。版本号固定的 tag 不会冲突，只有可变的
# :latest 必须只跟随当前 master HEAD。
#
# 规则：只有本次 GITHUB_SHA 等于当前 master HEAD 才允许 push :latest；
# 读不到 master HEAD、返回非法 sha 或 API 出错一律失败（fail-closed）。
#
# 用法：
#   scripts/ci/image-latest-guard.sh --repo owner/repo --sha <40-hex>
#
# 输出：push_latest=true|false、reason=<...>；有 GITHUB_OUTPUT 时同时写入。
# ---------------------------------------------------------------------------
set -euo pipefail

REPO=""
SHA=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --repo) REPO="${2:-}"; shift 2 ;;
    --sha) SHA="${2:-}"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

die() {
  echo "::error::$*" >&2
  echo "$*" >&2
  exit 1
}

[[ -n "$REPO" ]] || die "--repo is required"
[[ "$SHA" =~ ^[0-9a-f]{40}$ ]] || die "--sha 必须是 40 位 commit sha（收到 '${SHA}'）"
command -v gh >/dev/null 2>&1 || die "gh is required"
command -v jq >/dev/null 2>&1 || die "jq is required"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-image-latest.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

MASTER_FILE="$TMP/master.json"
MASTER_ERR="$TMP/master.err"
if ! gh api "repos/${REPO}/commits/master" >"$MASTER_FILE" 2>"$MASTER_ERR"; then
  die "无法读取当前 master HEAD（fail-closed，不移动 :latest）：$(tr '\n' ' ' < "$MASTER_ERR")"
fi
MASTER_SHA="$(jq -r '.sha // empty' "$MASTER_FILE")"
[[ "$MASTER_SHA" =~ ^[0-9a-f]{40}$ ]] \
  || die "master HEAD 返回的 sha 非法（收到 '${MASTER_SHA}'，fail-closed）"

if [[ "$MASTER_SHA" == "$SHA" ]]; then
  PUSH_LATEST=true
  REASON=current-master-head
else
  PUSH_LATEST=false
  REASON=not-current-master-head
fi

printf 'push_latest=%s\nreason=%s\n' "$PUSH_LATEST" "$REASON"
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  printf 'push_latest=%s\nreason=%s\n' "$PUSH_LATEST" "$REASON" >> "$GITHUB_OUTPUT"
fi
echo "image latest guard: push_latest=${PUSH_LATEST} reason=${REASON} (run=${SHA} master=${MASTER_SHA})" >&2
