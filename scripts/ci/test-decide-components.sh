#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：组件重建判定（REQ-5 / REQ-6）。
# 覆盖独立 review 指出的两条真问题：backend_changed 被 force_all 污染；
# rebuild_image_only 在"没有上一版 release"时走到了没有资产可搬的路径。
# 用法：scripts/ci/test-decide-components.sh
# ---------------------------------------------------------------------------
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

# run <env assignments...>  → key=value（stderr 里是日志/告警）
run() {
  env -u GITHUB_OUTPUT "$@" bash "$SCRIPT" 2>"$TMP/last.err"
}

echo "== decide-components.sh =="

# 1) 正常路径：后端变了 → 只构建变化的组件
out="$(run F_API=true F_DOCKER=false F_MCP=false F_INFRA=false)"
check "后端变了 → all=false" "false" "$(field "$out" all)"
check "后端变了 → api=true" "true" "$(field "$out" api)"
check "后端变了 → backend_changed=true" "true" "$(field "$out" backend_changed)"

# 2) 正常路径：都没变 → 全部跳过
out="$(run F_API=false F_DOCKER=false F_MCP=false F_INFRA=false)"
check "无变化 → all=false" "false" "$(field "$out" all)"
check "无变化 → backend_changed=false" "false" "$(field "$out" backend_changed)"

# 3) 自动触发（dispatch 默认 force_all 不传时= true）+ 前端有新版：
#    这是 review 抓到的真问题——backend_changed 必须保持"事实"为 false，
#    否则 REQ-6 的防重复发版在自动触发路径上永远不生效。
out="$(run FORCE_ALL=true REBUILD_IMAGE_ONLY=true F_API=false F_DOCKER=false F_MCP=false F_INFRA=false)"
check "force_all+只重建镜像 → all=true（全量）" "true" "$(field "$out" all)"
check "force_all 不污染 backend_changed（事实仍为 false）" "false" "$(field "$out" backend_changed)"

# 4) 只重建镜像 + 后端确实变了 → 自动转全量构建并告警
out="$(run REBUILD_IMAGE_ONLY=true F_API=true F_DOCKER=false F_MCP=false F_INFRA=false)"
check "只重建镜像 + 后端变了 → all=true" "true" "$(field "$out" all)"
check "只重建镜像 + 后端变了 → api=true" "true" "$(field "$out" api)"
contains "有可见告警" "自动转全量构建" "$(cat "$TMP/last.err")"

# 5) 只重建镜像 + 后端没变 → 只构建镜像
out="$(run REBUILD_IMAGE_ONLY=true F_API=false F_DOCKER=false F_MCP=false F_INFRA=false)"
check "只重建镜像 + 后端未变 → docker=true" "true" "$(field "$out" docker)"
check "只重建镜像 + 后端未变 → api=false" "false" "$(field "$out" api)"
check "只重建镜像 + 后端未变 → mcp=false" "false" "$(field "$out" mcp)"
check "只重建镜像 + 后端未变 → backend_changed=false" "false" "$(field "$out" backend_changed)"

# 6) 首次发布（没有可用基线）+ 只重建镜像 → 必须全量（否则没有资产可搬）
out="$(run REBUILD_IMAGE_ONLY=true BASELINE_OK=false PREVIOUS_TAG= F_API=false F_DOCKER=false F_MCP=false F_INFRA=false)"
check "首次发布 + 只重建镜像 → 全量构建" "true" "$(field "$out" all)"
contains "首次发布有可见说明" "全量构建" "$(cat "$TMP/last.err")"

# 7) 历史被改写（基线不是 HEAD 祖先）+ 只重建镜像 → 同样全量
out="$(run REBUILD_IMAGE_ONLY=true BASELINE_OK=false PREVIOUS_TAG=api-v1 F_API=false F_DOCKER=false F_MCP=false F_INFRA=false)"
check "历史重写 + 只重建镜像 → 全量构建" "true" "$(field "$out" all)"

# 8) 上一版 release 没有台账（过渡态，例如只改 MCP 的那次）→ 强制重烘镜像
out="$(run F_API=false F_DOCKER=false F_MCP=true F_INFRA=false PREVIOUS_TAG=api-v26.10.835 PREVIOUS_LEDGER=false)"
check "上一版无台账 → docker=true（重烘以产出台账）" "true" "$(field "$out" docker)"
check "上一版无台账 → mcp=true" "true" "$(field "$out" mcp)"
check "上一版无台账 → api 仍按路径过滤=false" "false" "$(field "$out" api)"
contains "无台账有可见告警" "没有 pim-web-version.json 台账" "$(cat "$TMP/last.err")"

# 9) 上一版有台账 → 不额外强制重烘
out="$(run F_API=false F_DOCKER=false F_MCP=true F_INFRA=false PREVIOUS_TAG=api-v26.10.835 PREVIOUS_LEDGER=true)"
check "上一版有台账 → docker=false" "false" "$(field "$out" docker)"

# 10) infra（改 .github/scripts）→ 全量
out="$(run F_INFRA=true F_API=false F_DOCKER=false F_MCP=false)"
check "infra 变化 → all=true" "true" "$(field "$out" all)"

echo ""
echo "decide-components: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
