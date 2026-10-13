#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 组件重建判定（REQ-5 / REQ-6 / REQ-12）。
#
# 这段逻辑原先散在 ci.yml 的 `changes.flags` 里，被独立 review 指出两个真问题：
#   1) 把「强制/回退」意图（FORCE_ALL / FIRST_PUSH）混进了 `backend_changed`，
#      导致自动触发时（dispatch 默认 force_all=true）防重复发版永远不可能生效；
#   2) `rebuild_image_only` 分支忽略了「没有上一版 release」的回退，会走到
#      "没有可搬运资产"的搬运失败路径。
# 抽成脚本是为了能被组合测试覆盖（见 test-decide-components.sh）。
#
# 输入（env，均为 'true'/'false'）：
#   FORCE_ALL           手动"强制全量构建"（dispatch 输入 force_all）
#   REBUILD_IMAGE_ONLY  手动"只重建镜像"（dispatch 输入 rebuild_image_only）
#   BASELINE_OK         上一版 release 是否是本次 HEAD 的祖先（false=首次/历史被改写）
#   PREVIOUS_TAG        上一版 release tag（'' 表示没有）
#   PREVIOUS_LEDGER     上一版 release 是否带 pim-web-version.json 台账
#   F_API / F_DOCKER / F_MCP / F_INFRA   路径过滤的原始结果
#
# 输出（stdout key=value；有 $GITHUB_OUTPUT 时写那里）：
#   all api docker mcp rebuild_image_only backend_changed
# ---------------------------------------------------------------------------
set -euo pipefail

: "${FORCE_ALL:=false}"
: "${REBUILD_IMAGE_ONLY:=false}"
: "${BASELINE_OK:=true}"
: "${PREVIOUS_TAG:=}"
: "${PREVIOUS_LEDGER:=false}"
: "${F_API:=false}"
: "${F_DOCKER:=false}"
: "${F_MCP:=false}"
: "${F_INFRA:=false}"

pim_log() { echo "$*" >&2; }
is_true() { [ "$1" = "true" ]; }

# backend_changed 只表示**事实**：后端/镜像定义相对上一版 release 有没有变化。
# 不含 force_all / 基线回退这类"意图"——意图决定要不要重建，但不能伪造"后端变了"这个事实，
# 否则 REQ-6 的防重复发版会被自动触发（dispatch 默认 force_all=true）永久绕过。
if is_true "$F_API" || is_true "$F_DOCKER" || is_true "$F_MCP" || is_true "$F_INFRA"; then
  backend_changed=true
else
  backend_changed=false
fi

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
elif is_true "$REBUILD_IMAGE_ONLY"; then
  pim_log "本次仅重建镜像（rebuild_image_only=true）"
  if is_true "$F_API" || is_true "$F_MCP"; then
    pim_log "::warning::请求「只重建镜像」，但后端（API/MCP）相对上一版 release 确有变化 → 自动转全量构建；绝不把旧 API/MCP 资产搬进新版本的 release"
    all=true; api=true; docker=true; mcp=true
  else
    all=false; api=false; mcp=false; docker=true
  fi
else
  all=false; api="$F_API"; docker="$F_DOCKER"; mcp="$F_MCP"
fi

# 台账守卫（REQ-7）：上一版 release 存在但没有台账资产时，必须重烘镜像才能产出本次台账；
# 否则 release job 只能去搬一个不存在的资产并失败。
if [ "$all" != "true" ] && [ -n "$PREVIOUS_TAG" ] && ! is_true "$PREVIOUS_LEDGER"; then
  pim_log "::warning::上一版 release(${PREVIOUS_TAG}) 没有 pim-web-version.json 台账 → 强制重烘镜像以产出本次台账"
  docker=true
fi

EMIT="$(cat <<EOF
all=${all}
api=${api}
docker=${docker}
mcp=${mcp}
rebuild_image_only=${REBUILD_IMAGE_ONLY}
backend_changed=${backend_changed}
EOF
)"

pim_log "组件判定：all=${all} api=${api} docker=${docker} mcp=${mcp}；仅重建镜像=${REBUILD_IMAGE_ONLY}；后端有变化=${backend_changed}；基线可用=${BASELINE_OK}；上一版台账=${PREVIOUS_LEDGER}"

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  printf '%s\n' "$EMIT" >> "$GITHUB_OUTPUT"
else
  printf '%s\n' "$EMIT"
fi
