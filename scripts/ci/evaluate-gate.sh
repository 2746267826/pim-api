#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# CI Gate 判定：把「子任务失败 / 应跑却 skip」变成整体失败。
#
# 输入（env）：
#   R_RESOLVE R_CHANGES R_WEBBUILD R_API R_DOCKER R_MCP   各 job 的 result
#   W_ALL W_API W_DOCKER W_MCP                            changes 的判定
# 退出码：0 = 通过；1 = 失败
# ---------------------------------------------------------------------------
set -uo pipefail

: "${R_RESOLVE:=}"; : "${R_CHANGES:=}"; : "${R_WEBBUILD:=}"
: "${R_API:=}"; : "${R_DOCKER:=}"; : "${R_MCP:=}"
: "${W_ALL:=false}"; : "${W_API:=false}"; : "${W_DOCKER:=false}"; : "${W_MCP:=false}"

fail=0

should() {
  if [ "$W_ALL" = "true" ]; then echo true; return; fi
  for flag in "$@"; do
    if [ "$flag" = "true" ]; then echo true; return; fi
  done
  echo false
}

should_docker() { should "$W_API" "$W_DOCKER"; }
should_web() { should "$W_API" "$W_DOCKER"; }

check() {
  local label="$1" result="$2" wanted="$3"
  if [ "$result" = "success" ]; then
    echo "✅ $label — success"
  elif [ "$result" = "skipped" ] && [ "$wanted" != "true" ]; then
    echo "⏭️  $label — skipped（该组件本次无变化，按设计跳过）"
  elif [ "$result" = "skipped" ]; then
    echo "❌ $label — 判定应当重建，却以 skipped 结束（疑似静默漏跑）"
    fail=1
  else
    echo "❌ $label — ${result:-<missing>}"
    fail=1
  fi
}

check "resolve-version" "$R_RESOLVE" "true"
check "changes"         "$R_CHANGES" "true"
check "web-build"       "$R_WEBBUILD" "$(should_web)"
check "build-api"       "$R_API"     "$(should "$W_API")"
check "build-docker"    "$R_DOCKER"  "$(should_docker)"
check "build-mcp"       "$R_MCP"     "$(should "$W_MCP")"

echo "（判定依据：all=$W_ALL api=$W_API docker=$W_DOCKER mcp=$W_MCP）"

if [ "$fail" -ne 0 ]; then
  echo ""
  echo "::error::CI Gate 未通过：存在失败或疑似静默跳过的必需 job。"
  exit 1
fi

echo ""
echo "CI Gate 通过：所有必需 job 均成功，或因组件无变化而按设计跳过。"