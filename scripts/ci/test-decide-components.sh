#!/usr/bin/env bash
# 自检：组件重建判定。
# 重点：组件判定与上一版缺台账时强制重烘镜像。
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/decide-components.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-decide-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }
contains() { if [[ "$3" == *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 未包含 [$2]"; fail=$((fail+1)); fi; }
field() { sed -n "s/^$2=//p" <<<"$1"; }

run() { env -u GITHUB_OUTPUT "$@" bash "$SCRIPT" 2>"$TMP/last.err"; }

echo "== decide-components.sh =="

out="$(run F_API=true F_DOCKER=false F_MCP=false F_INFRA=false)"
check "后端变了 → all=false" "false" "$(field "$out" all)"
check "后端变了 → api=true" "true" "$(field "$out" api)"

out="$(run F_API=false F_DOCKER=false F_MCP=false F_INFRA=false)"
check "无变化 → all=false" "false" "$(field "$out" all)"

# 仅前端变化：web/client-web/** 命中 docker 过滤器 → 只重烘镜像。
out="$(run F_DOCKER=true F_API=false F_MCP=false F_INFRA=false)"
check "仅前端变化 → docker=true" "true" "$(field "$out" docker)"
check "仅前端变化 → api=false" "false" "$(field "$out" api)"
check "仅前端变化 → mcp=false" "false" "$(field "$out" mcp)"

out="$(run FORCE_ALL=true F_API=false F_DOCKER=false F_MCP=false F_INFRA=false)"
check "force_all → all=true" "true" "$(field "$out" all)"

out="$(run BASELINE_OK=false PREVIOUS_TAG= F_API=false F_DOCKER=false F_MCP=false F_INFRA=false)"
check "首次发布 → 全量构建" "true" "$(field "$out" all)"
contains "首次发布有可见说明" "全量构建" "$(cat "$TMP/last.err")"

out="$(run F_API=false F_DOCKER=false F_MCP=true F_INFRA=false PREVIOUS_TAG=api-v26.10.835 PREVIOUS_LEDGER=false)"
check "上一版无台账 → docker=true（重烘以产出台账）" "true" "$(field "$out" docker)"
check "上一版无台账 → mcp=true" "true" "$(field "$out" mcp)"
check "上一版无台账 → api 仍按路径过滤=false" "false" "$(field "$out" api)"
contains "无台账有可见告警" "没有 pim-web-version.json 台账" "$(cat "$TMP/last.err")"

out="$(run F_API=false F_DOCKER=false F_MCP=true F_INFRA=false PREVIOUS_TAG=api-v26.10.835 PREVIOUS_LEDGER=true)"
check "上一版有台账 → docker=false" "false" "$(field "$out" docker)"

out="$(run F_INFRA=true F_API=false F_DOCKER=false F_MCP=false)"
check "infra 变化 → all=true" "true" "$(field "$out" all)"

echo ""
echo "decide-components: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1