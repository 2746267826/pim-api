#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 测试替身：模拟 `docker`，只实现 web-dist 相关脚本用到的子命令。
# 让 resolve-web-dist.sh / build-web-ledger.sh 的逻辑可以在**没有 registry、
# 没有真镜像**的情况下被断言（真镜像路径由 CI 里的真实构建覆盖）。
#
# 环境变量：
#   FAKE_DOCKER_STATE    记录 create 出来的 cid → 镜像引用的目录（必填）
#   FAKE_DOCKER_REFS     已知镜像引用（空格分隔）；pull / image inspect 只对这些成功
#   FAKE_DOCKER_CONTENT  内容根：<root>/<sanitized-ref>/<path 去掉前导 /> 为文件内容
#   FAKE_DOCKER_DIGEST   inspect --format 返回的 digest（默认 sha256:1111...）
#   FAKE_DOCKER_LOCAL_ONLY  只存在于本地镜像库、远端拉不到的引用（空格分隔）；pull 对这些失败
#   FAKE_DOCKER_REMOTE_ONLY 只存在于远端、本地镜像库没有的引用（空格分隔）；image inspect 对这些失败
#   FAKE_DOCKER_LOG      把每次调用记到这里（用于断言"确实拉过/确实没拉过"）
# ---------------------------------------------------------------------------
set -uo pipefail

STATE="${FAKE_DOCKER_STATE:?FAKE_DOCKER_STATE required}"
REFS="${FAKE_DOCKER_REFS:-}"
CONTENT="${FAKE_DOCKER_CONTENT:-}"
DIGEST="${FAKE_DOCKER_DIGEST:-sha256:0000000000000000000000000000000000000000000000000000000000000001}"

if [ -n "${FAKE_DOCKER_LOG:-}" ]; then
  { for a in "$@"; do echo "$a"; done; echo "---"; } >> "$FAKE_DOCKER_LOG"
fi
remote_only() {
  local needle="$1" r
  for r in ${FAKE_DOCKER_REMOTE_ONLY:-}; do [ "$r" = "$needle" ] && return 0; done
  return 1
}
local_only() {
  local needle="$1" r
  for r in ${FAKE_DOCKER_LOCAL_ONLY:-}; do [ "$r" = "$needle" ] && return 0; done
  return 1
}
unknown() { echo "fake-docker: unsupported invocation: $*" >&2; exit 2; }
known() {
  local needle="$1" r
  for r in $REFS; do [ "$r" = "$needle" ] && return 0; done
  return 1
}
sanitize() { echo "$1" | tr '/:' '__'; }

cmd="${1:-}"
shift || true

case "$cmd" in
  image)
    [ "${1:-}" = "inspect" ] || unknown "$cmd $*"
    shift
    ref="${1:-}"
    if remote_only "$ref"; then echo "fake-docker: no such local image $ref" >&2; exit 1; fi
    if known "$ref"; then exit 0; else echo "fake-docker: no such image $ref" >&2; exit 1; fi
    ;;
  pull)
    ref="${1:-}"
    if local_only "$ref"; then
      echo "fake-docker: no such manifest $ref (local-only)" >&2
      exit 1
    fi
    if known "$ref"; then echo "fake-docker: pulled $ref" >&2; exit 0; fi
    echo "fake-docker: pull denied $ref" >&2
    exit 1
    ;;
  create)
    ref="${1:-}"
    known "$ref" || { echo "fake-docker: create failed $ref" >&2; exit 1; }
    cid="fakecid$$-$RANDOM"
    echo "$ref" > "$STATE/$cid"
    echo "$cid"
    ;;
  cp)
    src="${1:-}"; dst="${2:-}"
    cid="${src%%:*}"; path="${src#*:}"
    [ -f "$STATE/$cid" ] || { echo "fake-docker: no such container $cid" >&2; exit 1; }
    ref="$(cat "$STATE/$cid")"
    fixture="$CONTENT/$(sanitize "$ref")/${path#/}"
    if [ ! -f "$fixture" ]; then
      echo "fake-docker: no such file in image: $path ($ref)" >&2
      exit 1
    fi
    cp "$fixture" "$dst"
    ;;
  rm)
    [ "${1:-}" = "-f" ] && shift
    rm -f "$STATE/${1:-}" 2>/dev/null
    ;;
  inspect)
    # 只支持 `inspect --format <fmt> <ref>`：回一行 "<ref>@<digest>"，调用方自行解析。
    [ "${1:-}" = "--format" ] || unknown "$cmd $*"
    shift 2
    ref="${1:-}"
    echo "${ref}@${DIGEST}"
    ;;
  *)
    unknown "$cmd $*"
    ;;
esac
