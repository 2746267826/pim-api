#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 探测一个 GHCR 容器包能否**匿名**拉取（契约：pim-web-dist 必须 public）。
#
# 为什么必须实测：GHCR 新建包默认 private，且包只继承仓库的访问权限、不继承可见性 ——
# "公开仓"不等于"包可匿名拉"。所以这里用未登录的方式取匿名 token 并核对 tag。
#
# 用法：
#   scripts/ci/check-ghcr-public.sh <ghcr.io/owner/package> [--expect-tag sha-<7>]
# 退出码：0 = 可匿名拉取（且若指定了 tag，该 tag 存在）；非 0 = 不可。
# ---------------------------------------------------------------------------
set -euo pipefail

REF="${1:-}"
[ -n "$REF" ] || { echo "[ghcr][错误] 用法：check-ghcr-public.sh <registry>/<owner>/<package> [--expect-tag <tag>]" >&2; exit 2; }
shift || true

EXPECT_TAG=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --expect-tag) EXPECT_TAG="${2:-}"; shift 2 ;;
    *) echo "[ghcr][错误] 未知参数：$1" >&2; exit 2 ;;
  esac
done

HOST="${REF%%/*}"
REPO_PATH="${REF#*/}"
CURL="${PIM_CURL:-curl}"

TOKEN_JSON="$("$CURL" -sS "https://${HOST}/token?service=${HOST}&scope=repository:${REPO_PATH}:pull")"
TOKEN="$(jq -r '.token // empty' <<<"$TOKEN_JSON" 2>/dev/null || true)"
if [ -z "$TOKEN" ]; then
  echo "[ghcr] ${REF} 不可匿名拉取（未取得匿名 pull token）：${TOKEN_JSON}" >&2
  exit 1
fi

if [ -n "$EXPECT_TAG" ]; then
  TAGS_JSON="$("$CURL" -sS -H "Authorization: Bearer ${TOKEN}" "https://${HOST}/v2/${REPO_PATH}/tags/list")"
  if ! jq -e --arg t "$EXPECT_TAG" '.tags | index($t)' <<<"$TAGS_JSON" >/dev/null 2>&1; then
    echo "[ghcr] ${REF} 可匿名访问，但 tag ${EXPECT_TAG} 不在 tags 列表里：${TAGS_JSON}" >&2
    exit 1
  fi
  echo "[ghcr] ${REF} 可匿名拉取，且 tag ${EXPECT_TAG} 存在"
else
  echo "[ghcr] ${REF} 可匿名拉取"
fi
