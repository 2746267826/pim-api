#!/usr/bin/env bash
# 自检：CI Gate 判定。覆盖“应跑却 skipped 必须红”与“无变化允许跳过”。
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/evaluate-gate.sh"

pass=0
fail=0
expect_rc() { # <label> <expected rc> <env...>
  local label="$1" want="$2"; shift 2
  local rc=0
  env "$@" bash "$SCRIPT" >/dev/null 2>&1 || rc=$?
  if [ "$rc" = "$want" ]; then echo "  ✅ $label"; pass=$((pass+1));
  else echo "  ❌ $label — 期望 rc=$want，实际 rc=$rc"; fail=$((fail+1)); fi
}

echo "== evaluate-gate.sh =="

expect_rc "全 success → 通过" 0 \
  R_RESOLVE=success R_CHANGES=success R_WEBBUILD=success R_API=success R_DOCKER=success R_MCP=success \
  W_ALL=true W_API=true W_DOCKER=true W_MCP=true

expect_rc "无变化 → skipped 按设计跳过，通过" 0 \
  R_RESOLVE=success R_CHANGES=success R_WEBBUILD=skipped R_API=skipped R_DOCKER=skipped R_MCP=skipped \
  W_ALL=false W_API=false W_DOCKER=false W_MCP=false

expect_rc "仅前端变化：web-build/docker 成功、api/mcp 跳过 → 通过" 0 \
  R_RESOLVE=success R_CHANGES=success R_WEBBUILD=success R_API=skipped R_DOCKER=success R_MCP=skipped \
  W_ALL=false W_API=false W_DOCKER=true W_MCP=false

expect_rc "应当重建却 skipped → 失败" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBBUILD=success R_API=skipped R_DOCKER=success R_MCP=skipped \
  W_ALL=false W_API=true W_DOCKER=false W_MCP=false

expect_rc "web-build 失败 → 红" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBBUILD=failure R_API=skipped R_DOCKER=skipped R_MCP=skipped \
  W_ALL=true W_API=true W_DOCKER=true W_MCP=true

expect_rc "build-api 失败 → 红" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBBUILD=success R_API=failure R_DOCKER=success R_MCP=success \
  W_ALL=true W_API=true W_DOCKER=true W_MCP=true

expect_rc "changes 失败 → 红" 1 \
  R_RESOLVE=success R_CHANGES=failure R_WEBBUILD=skipped R_API=skipped R_DOCKER=skipped R_MCP=skipped \
  W_ALL= W_API= W_DOCKER= W_MCP=

echo ""
echo "evaluate-gate: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1