#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 解析并「冻结」本次构建要内嵌的前端产物镜像引用（REQ-3）。
#
# 语义：
#   - 不给 --ref（默认取 latest）：解析一次 → 冻结成不可变引用 sha-<7 位短 SHA>；
#     镜像内容（web-version.json）决定那个 sha，并校验 sha-<7> 这个 tag 真实存在。
#   - 给了 --ref：必须用那一个；并且必须与镜像内 web-version.json 的 gitSha 同源，
#     否则硬失败——绝不允许「触发的是 A 版、实际烘进去的是 B 版」。
#
# 任何一步失败都硬失败（拉不到、文件缺失、字段缺失、tag 不存在、引用与内容不一致）。
#
# 用法：
#   scripts/ci/resolve-web-dist.sh [--ref latest|sha-<7>] [--image <repo>] [--print-env]
# 输出（--print-env 打印到 stdout；否则写 $GITHUB_OUTPUT）：
#   web_dist_image / web_dist_ref / web_dist_digest / web_version / web_git_sha / web_built_at
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/ci/web-dist-lib.sh
. "$HERE/web-dist-lib.sh"

REF="latest"
IMAGE=""
PRINT_ENV=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --ref) REF="${2:-}"; shift 2 ;;
    --image) IMAGE="${2:-}"; shift 2 ;;
    --print-env) PRINT_ENV=true; shift ;;
    *) pim_web_err "未知参数：$1"; exit 2 ;;
  esac
done
[ -n "$REF" ] || REF="latest"
[ -n "$IMAGE" ] || IMAGE="$(pim_web_dist_image)"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-web-dist-resolve.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

pim_web_log "解析前 tag: ${IMAGE}:${REF}"

REQUESTED="$REF"
# 显式刷新远端引用后再读：滚动 tag（latest）在本地缓存里可能是旧的，
# "本地已有就不拉"会让冻结值悄悄取自过期内容。
"$(pim_docker)" pull "${IMAGE}:${REQUESTED}" >&2 \
  || { pim_web_err "无法拉取 ${IMAGE}:${REQUESTED}"; exit 1; }
pim_image_cat "${IMAGE}:${REQUESTED}" "$PIM_WEB_VERSION_PATH_IN_DIST_IMAGE" "$TMP/web-version.json" \
  || { pim_web_err "无法从 ${IMAGE}:${REQUESTED} 读取 ${PIM_WEB_VERSION_PATH_IN_DIST_IMAGE}"; exit 1; }

FIELDS="$(pim_web_validate_version_file "$TMP/web-version.json")" \
  || { pim_web_err "${IMAGE}:${REQUESTED} 的前端版本文件不合法"; exit 1; }
WEB_VERSION="$(sed -n 's/^web_version=//p' <<<"$FIELDS")"
WEB_GIT_SHA="$(sed -n 's/^web_git_sha=//p' <<<"$FIELDS")"
WEB_BUILT_AT="$(sed -n 's/^web_built_at=//p' <<<"$FIELDS")"

FROZEN_REF="sha-${WEB_GIT_SHA}"

if pim_web_is_immutable_ref "$REQUESTED"; then
  # 触发方给了确切版本引用：必须与镜像内容同源。
  if [ "$REQUESTED" != "$FROZEN_REF" ]; then
    pim_web_err "请求的前端引用 ${REQUESTED} 与镜像内 web-version.json 的 gitSha(${WEB_GIT_SHA}) 不一致，拒绝烘焙"
    exit 1
  fi
  pim_web_log "触发方给定不可变引用，已核对与镜像内容一致：${REQUESTED}"
else
  # 默认取最新：冻结成不可变引用，并证明该 tag 真实可用。
  pim_web_log "解析前 tag: ${IMAGE}:${REQUESTED} → 冻结后引用: ${IMAGE}:${FROZEN_REF}"
  # 证明冻结出来的不可变引用真实可用（请求的是 latest 时这一次是必须的）。
  "$(pim_docker)" pull "${IMAGE}:${FROZEN_REF}" >&2 \
    || { pim_web_err "冻结后的不可变引用 ${IMAGE}:${FROZEN_REF} 不存在或不可匿名拉取"; exit 1; }
fi

# digest 取自冻结后的引用（上面已经拉过：请求的就是它，或刚拉过它）。
DIGEST="$(pim_image_digest "${IMAGE}:${FROZEN_REF}")"

pim_web_log "冻结结果：ref=${FROZEN_REF} digest=${DIGEST} version=${WEB_VERSION} builtAt=${WEB_BUILT_AT}"

EMIT="$(cat <<EOF
web_dist_image=${IMAGE}
web_dist_ref=${FROZEN_REF}
web_dist_digest=${DIGEST}
web_version=${WEB_VERSION}
web_git_sha=${WEB_GIT_SHA}
web_built_at=${WEB_BUILT_AT}
EOF
)"

if [ "$PRINT_ENV" = true ]; then
  printf '%s\n' "$EMIT"
elif [ -n "${GITHUB_OUTPUT:-}" ]; then
  printf '%s\n' "$EMIT" >> "$GITHUB_OUTPUT"
else
  printf '%s\n' "$EMIT"
fi
