#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：CI Gate 判定（REQ-12）。
# 重点覆盖独立 review 指出的 fail-open：publish 为空时不许把"漏跑"放行。
# 用法：scripts/ci/test-evaluate-gate.sh
# ---------------------------------------------------------------------------
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

ALL_OK=(R_RESOLVE=success R_CHANGES=success R_WEBDIST=success R_API=success R_DOCKER=success R_MCP=success R_GAP_AUDIT=success)

echo "== evaluate-gate.sh =="

# 1) 全绿
expect_rc "全 success → 通过" 0 "${ALL_OK[@]}" W_ALL=true W_API=true W_DOCKER=true W_MCP=true IS_RELEASE=true W_PUBLISH=true

# 2) 组件无变化、按设计跳过 → 通过（门禁 job 本身仍是 success）
expect_rc "组件无变化、全部按设计 skipped → 通过" 0 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=skipped R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=success \
  W_ALL=false W_API=false W_DOCKER=false W_MCP=false IS_RELEASE=false W_PUBLISH=

# 3) 该跑却 skipped → 红（不许静默漏跑）
expect_rc "应当重建却 skipped → 失败" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=success R_API=skipped R_DOCKER=success R_MCP=skipped R_GAP_AUDIT=success \
  W_ALL=false W_API=true W_DOCKER=false W_MCP=false IS_RELEASE=true W_PUBLISH=true

# 4) 防重复发版：显式 publish=false → 镜像按设计跳过，通过
expect_rc "显式 publish=false → 镜像 skip 通过" 0 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=success R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=success \
  W_ALL=false W_API=false W_DOCKER=true W_MCP=false IS_RELEASE=true W_PUBLISH=false

# 5) publish 为空（web-dist 没给出判定）→ 不许当成"不发版"，应判红
expect_rc "publish 为空 → 镜像 skip 判红（fail-closed）" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=success R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=success \
  W_ALL=false W_API=false W_DOCKER=true W_MCP=false IS_RELEASE=true W_PUBLISH=

# 6) publish 是异常值 → 同样判红
expect_rc "publish 异常值 → 镜像 skip 判红" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=success R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=success \
  W_ALL=false W_API=false W_DOCKER=true W_MCP=false IS_RELEASE=true W_PUBLISH=unknown

# 7) 非 release 运行 publish 为空是正常的
expect_rc "非 release 运行 publish 为空 → 通过" 0 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=skipped R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=success \
  W_ALL=false W_API=false W_DOCKER=false W_MCP=false IS_RELEASE=false W_PUBLISH=

# 8) web-dist 自己失败 → 红
expect_rc "web-dist 失败 → 红" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=failure R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=success \
  W_ALL=true W_API=true W_DOCKER=true W_MCP=true IS_RELEASE=true W_PUBLISH=

# 9) 子任务失败 → 红
expect_rc "build-api 失败 → 红" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=success R_API=failure R_DOCKER=success R_MCP=success R_GAP_AUDIT=success \
  W_ALL=true W_API=true W_DOCKER=true W_MCP=true IS_RELEASE=true W_PUBLISH=true

# 10) changes 失败 → 红
expect_rc "changes 失败 → 红" 1 \
  R_RESOLVE=success R_CHANGES=failure R_WEBDIST=skipped R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=success \
  W_ALL= W_API= W_DOCKER= W_MCP= IS_RELEASE=false W_PUBLISH=

# 11) 契约缺口审计是无条件 job：skipped 即视为漏跑
expect_rc "contract-gap-audit 被 skipped → 红" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=skipped R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=skipped \
  W_ALL=false W_API=false W_DOCKER=false W_MCP=false IS_RELEASE=false W_PUBLISH=

# 12) 契约缺口审计失败 → 红
expect_rc "contract-gap-audit 失败 → 红" 1 \
  R_RESOLVE=success R_CHANGES=success R_WEBDIST=skipped R_API=skipped R_DOCKER=skipped R_MCP=skipped R_GAP_AUDIT=failure \
  W_ALL=false W_API=false W_DOCKER=false W_MCP=false IS_RELEASE=false W_PUBLISH=

echo ""
echo "evaluate-gate: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
