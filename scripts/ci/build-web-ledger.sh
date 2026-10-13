#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 生成 release 台账 pim-web-version.json（REQ-7）。
#
# 关键点：三个前端字段（version / gitSha / builtAt）**必须原样转录**自
# 「本次真正烘焙进 pim-api 镜像」的 /app/wwwroot/web-version.json，
# 不允许重新计算——否则台账与镜像内容就会漂开。
#
# 用法：
#   scripts/ci/build-web-ledger.sh --api-image <ref> --dist-ref <full-ref> \
#     --dist-digest <sha256:...> --api-version <v> --out <file>
# 输出：写入 <file>（键排序，便于 diff）
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/ci/web-dist-lib.sh
. "$HERE/web-dist-lib.sh"

API_IMAGE=""
DIST_REF=""
DIST_DIGEST=""
API_VERSION=""
OUT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --api-image) API_IMAGE="${2:-}"; shift 2 ;;
    --dist-ref) DIST_REF="${2:-}"; shift 2 ;;
    --dist-digest) DIST_DIGEST="${2:-}"; shift 2 ;;
    --api-version) API_VERSION="${2:-}"; shift 2 ;;
    --out) OUT="${2:-}"; shift 2 ;;
    *) pim_web_err "未知参数：$1"; exit 2 ;;
  esac
done
for pair in "api-image:$API_IMAGE" "dist-ref:$DIST_REF" "dist-digest:$DIST_DIGEST" "api-version:$API_VERSION" "out:$OUT"; do
  [ -n "${pair#*:}" ] || { pim_web_err "--${pair%%:*} 必填"; exit 2; }
done

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-web-ledger.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

# 从**烘焙好的 api 镜像**里读，而不是从产物镜像读：台账记录的是"这个镜像里实际是什么"。
# 这个镜像刚刚在同一个 job 里 build/load 出来、还没推上去，所以必须限定读本地镜像
# （--local）：否则会去拉远端同名 tag（要么不存在而失败，要么把旧镜像当成本次构建结果）。
pim_image_cat "$API_IMAGE" "/app/wwwroot/${PIM_WEB_VERSION_FILE}" "$TMP/web-version.json" --local \
  || { pim_web_err "无法从 ${API_IMAGE}:/app/wwwroot/${PIM_WEB_VERSION_FILE} 读取前端版本文件"; exit 1; }

FIELDS="$(pim_web_validate_version_file "$TMP/web-version.json")" \
  || { pim_web_err "${API_IMAGE} 内的前端版本文件不合法"; exit 1; }
WEB_VERSION="$(sed -n 's/^web_version=//p' <<<"$FIELDS")"
WEB_GIT_SHA="$(sed -n 's/^web_git_sha=//p' <<<"$FIELDS")"
WEB_BUILT_AT="$(sed -n 's/^web_built_at=//p' <<<"$FIELDS")"

jq -S -n \
  --arg version "$WEB_VERSION" \
  --arg gitSha "$WEB_GIT_SHA" \
  --arg builtAt "$WEB_BUILT_AT" \
  --arg imageTag "$DIST_REF" \
  --arg imageDigest "$DIST_DIGEST" \
  --arg apiVersion "$API_VERSION" \
  '{version:$version, gitSha:$gitSha, builtAt:$builtAt, imageTag:$imageTag, imageDigest:$imageDigest, apiVersion:$apiVersion}' \
  > "$OUT"

pim_web_log "已写台账 $OUT：version=${WEB_VERSION} gitSha=${WEB_GIT_SHA} builtAt=${WEB_BUILT_AT} imageTag=${DIST_REF} imageDigest=${DIST_DIGEST} apiVersion=${API_VERSION}"
