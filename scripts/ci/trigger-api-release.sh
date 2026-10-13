#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 触发 pim-api 的 workflow_dispatch（REQ-4）。
#
# 用 GitHub App 现签的临时 token 调 dispatch 端点，传本次前端的确切版本引用
# 与"只重建镜像"的意图。失败必须可见：打印 HTTP 状态 + 响应体并非 0 退出，
# 绝不吞异常。**禁止回显 token**（任何日志/输出里都不能出现）。
#
# 用法：
#   TRIGGER_TOKEN=<app token> scripts/ci/trigger-api-release.sh --web-dist-ref sha-<7> \
#     [--rebuild-image-only true|false] [--force-all true|false] [--repo owner/repo] \
#     [--workflow ci.yml] [--ref master]
#
# token 只从环境变量读：**不进命令行参数**（避免出现在进程参数表里），
# 也不落盘（curl 的认证头经 stdin 的 600 语义等价物传入，见下面 --config /dev/stdin）。
# ---------------------------------------------------------------------------
set -euo pipefail

WEB_DIST_REF=""
REBUILD_IMAGE_ONLY="true"
# 自动触发必须显式传 force_all=false：dispatch 输入的默认值是 true，
# 不传的话 changes 会按"强制全量"处理，防重复发版（REQ-6）就不会生效。
FORCE_ALL="false"
REPO="2746267826/pim-api"
WORKFLOW="ci.yml"
TARGET_REF="master"
CURL="${PIM_CURL:-curl}"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --web-dist-ref) WEB_DIST_REF="${2:-}"; shift 2 ;;
    --rebuild-image-only) REBUILD_IMAGE_ONLY="${2:-}"; shift 2 ;;
    --force-all) FORCE_ALL="${2:-}"; shift 2 ;;
    --repo) REPO="${2:-}"; shift 2 ;;
    --workflow) WORKFLOW="${2:-}"; shift 2 ;;
    --ref) TARGET_REF="${2:-}"; shift 2 ;;
    *) echo "[trigger-api][错误] 未知参数：$1" >&2; exit 2 ;;
  esac
done

TOKEN="${TRIGGER_TOKEN:-}"
[ -n "$TOKEN" ] || { echo "[trigger-api][错误] 环境变量 TRIGGER_TOKEN 必填（token 不走命令行参数）" >&2; exit 2; }
if [[ ! "$WEB_DIST_REF" =~ ^sha-[0-9a-f]{7}$ ]]; then
  echo "[trigger-api][错误] --web-dist-ref 必须是不可变引用 sha-<7 位短 SHA>，实际为 '${WEB_DIST_REF}'" >&2
  exit 2
fi
case "$REBUILD_IMAGE_ONLY" in
  true|false) ;;
  *) echo "[trigger-api][错误] --rebuild-image-only 必须是 true 或 false" >&2; exit 2 ;;
esac
case "$FORCE_ALL" in
  true|false) ;;
  *) echo "[trigger-api][错误] --force-all 必须是 true 或 false" >&2; exit 2 ;;
esac

PAYLOAD="$(jq -n \
  --arg ref "$TARGET_REF" \
  --arg web "$WEB_DIST_REF" \
  --arg only "$REBUILD_IMAGE_ONLY" \
  --arg force "$FORCE_ALL" \
  '{ref:$ref, inputs:{web_dist_ref:$web, rebuild_image_only:$only, force_all:$force}}')"

URL="https://api.github.com/repos/${REPO}/actions/workflows/${WORKFLOW}/dispatches"
BODY_FILE="$(mktemp "${TMPDIR:-/tmp}/pim-dispatch-body.XXXXXX")"
trap 'rm -f "$BODY_FILE"' EXIT

echo "[trigger-api] POST $URL ref=$TARGET_REF web_dist_ref=$WEB_DIST_REF rebuild_image_only=$REBUILD_IMAGE_ONLY force_all=$FORCE_ALL" >&2

# 认证头经 stdin 交给 curl 的 --config：既不在命令行参数里，也不落盘。
set +e
STATUS="$(printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" | "$CURL" -sS -o "$BODY_FILE" -w '%{http_code}' -X POST "$URL" \
  --config /dev/stdin \
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
