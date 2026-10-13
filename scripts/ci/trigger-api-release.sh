#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 触发 pim-api 的 workflow_dispatch（REQ-4）。
#
# 用 GitHub App 现签的临时 token 调 dispatch 端点，传本次前端的确切版本引用
# 与"只重建镜像"的意图。失败必须可见：打印 HTTP 状态 + 响应体并非 0 退出，
# 绝不吞异常。**禁止回显 token**（任何日志/输出里都不能出现）。
#
# 用法：
#   scripts/ci/trigger-api-release.sh --token <t> --web-dist-ref sha-<7> \
#     [--rebuild-image-only true|false] [--repo owner/repo] [--workflow ci.yml] [--ref master]
# ---------------------------------------------------------------------------
set -euo pipefail

TOKEN=""
WEB_DIST_REF=""
REBUILD_IMAGE_ONLY="true"
REPO="2746267826/pim-api"
WORKFLOW="ci.yml"
TARGET_REF="master"
CURL="${PIM_CURL:-curl}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --token) TOKEN="${2:-}"; shift 2 ;;
    --web-dist-ref) WEB_DIST_REF="${2:-}"; shift 2 ;;
    --rebuild-image-only) REBUILD_IMAGE_ONLY="${2:-}"; shift 2 ;;
    --repo) REPO="${2:-}"; shift 2 ;;
    --workflow) WORKFLOW="${2:-}"; shift 2 ;;
    --ref) TARGET_REF="${2:-}"; shift 2 ;;
    *) echo "[trigger-api][错误] 未知参数：$1" >&2; exit 2 ;;
  esac
done

[ -n "$TOKEN" ] || { echo "[trigger-api][错误] --token 必填" >&2; exit 2; }
if [[ ! "$WEB_DIST_REF" =~ ^sha-[0-9a-f]{7}$ ]]; then
  echo "[trigger-api][错误] --web-dist-ref 必须是不可变引用 sha-<7 位短 SHA>，实际为 '${WEB_DIST_REF}'" >&2
  exit 2
fi
case "$REBUILD_IMAGE_ONLY" in
  true|false) ;;
  *) echo "[trigger-api][错误] --rebuild-image-only 必须是 true 或 false" >&2; exit 2 ;;
esac

PAYLOAD="$(jq -n \
  --arg ref "$TARGET_REF" \
  --arg web "$WEB_DIST_REF" \
  --arg only "$REBUILD_IMAGE_ONLY" \
  '{ref:$ref, inputs:{web_dist_ref:$web, rebuild_image_only:$only}}')"

URL="https://api.github.com/repos/${REPO}/actions/workflows/${WORKFLOW}/dispatches"
BODY_FILE="$(mktemp "${TMPDIR:-/tmp}/pim-dispatch-body.XXXXXX")"
# token 走 curl 配置文件（600，随用随删），不放进命令行参数：避免出现在进程参数表里。
AUTH_FILE="$(mktemp "${TMPDIR:-/tmp}/pim-dispatch-auth.XXXXXX")"
chmod 600 "$AUTH_FILE"
printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" > "$AUTH_FILE"
trap 'rm -f "$BODY_FILE" "$AUTH_FILE"' EXIT

echo "[trigger-api] POST $URL ref=$TARGET_REF web_dist_ref=$WEB_DIST_REF rebuild_image_only=$REBUILD_IMAGE_ONLY" >&2

set +e
STATUS="$("$CURL" -sS -o "$BODY_FILE" -w '%{http_code}' -X POST "$URL" \
  --config "$AUTH_FILE" \
  -H "Accept: application/vnd.github+json" \
  -H "X-GitHub-Api-Version: 2022-11-28" \
  -H "Content-Type: application/json" \
  -d "$PAYLOAD")"
CURL_RC=$?
set -e

if [ "$CURL_RC" -ne 0 ]; then
  echo "[trigger-api][错误] curl 调用失败（exit=${CURL_RC}），HTTP 状态=${STATUS:-<none>}" >&2
  echo "[trigger-api][错误] 响应体：$(cat "$BODY_FILE" 2>/dev/null || echo '<empty>')" >&2
  exit 1
fi

if [ "$STATUS" != "204" ]; then
  echo "[trigger-api][错误] 触发 pim-api 失败：HTTP ${STATUS}" >&2
  echo "[trigger-api][错误] 响应体：$(cat "$BODY_FILE" 2>/dev/null || echo '<empty>')" >&2
  exit 1
fi

echo "[trigger-api] 已触发 ${REPO} ${WORKFLOW}（HTTP 204）" >&2
