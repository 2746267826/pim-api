#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 组件重建判定。
#
# 输入（env，均为 'true'/'false'）：
#   FORCE_ALL           手动“强制全量构建”（dispatch 输入 force_all）
#   BASELINE_OK         上一版 release 是否是本次 HEAD 的祖先（false=首次/历史被改写）
#   PREVIOUS_TAG        上一版 release tag（'' 表示没有）
#   PREVIOUS_LEDGER     上一版 release 是否带 pim-web-version.json 台账
#   F_API / F_DOCKER / F_MCP / F_INFRA   路径过滤的原始结果
#
# 输出（stdout key=value；有 $GITHUB_OUTPUT 时写那里）：
#   all api docker mcp
#
# 前端已与 API 同仓：web/client-web/** 命中 docker 过滤器，前端变化只重烘镜像，
# 不再需要跨仓“只重建镜像”开关。
# ---------------------------------------------------------------------------
set -euo pipefail

: "${FORCE_ALL:=false}"
: "${BASELINE_OK:=true}"
: "${PREVIOUS_TAG:=}"
: "${PREVIOUS_LEDGER:=false}"
: "${F_API:=false}"
: "${F_DOCKER:=false}"
: "${F_MCP:=false}"
: "${F_INFRA:=false}"

pim_log() { echo "$*" >&2; }
is_true() { [ "$1" = "true" ]; }

all=false; api=false; docker=false; mcp=false

if is_true "$FORCE_ALL" || ! is_true "$BASELINE_OK" || is_true "$F_INFRA"; then
  if ! is_true "$BASELINE_OK"; then
    pim_log "没有可用的上一版 release 基线（首次发布或历史被改写）→ 全量构建"
  elif is_true "$F_INFRA"; then
    pim_log "CI/脚本等基础设施有变化（infra=true）→ 全量构建"
  else
    pim_log "force_all=true → 全量构建"
  fi
  all=true; api=true; docker=true; mcp=true
else
  all=false; api="$F_API"; docker="$F_DOCKER"; mcp="$F_MCP"
fi

# 台账守卫：上一版 release 存在但没有台账资产时，必须重烘镜像才能产出本次台账。
if [ "$all" != "true" ] && [ -n "$PREVIOUS_TAG" ] && ! is_true "$PREVIOUS_LEDGER"; then
  pim_log "::warning::上一版 release(${PREVIOUS_TAG}) 没有 pim-web-version.json 台账 → 强制重烘镜像以产出本次台账"
  docker=true
fi

EMIT="$(cat <<EOF
all=${all}
api=${api}
docker=${docker}
mcp=${mcp}
EOF
)"

pim_log "组件判定：all=${all} api=${api} docker=${docker} mcp=${mcp}；基线可用=${BASELINE_OK}；上一版台账=${PREVIOUS_LEDGER}"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  printf '%s\n' "$EMIT" >> "$GITHUB_OUTPUT"
else
  printf '%s\n' "$EMIT"
fi