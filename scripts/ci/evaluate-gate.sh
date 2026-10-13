#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# CI Gate 判定（REQ-12）：把「子任务失败 / 应跑却 skip」变成整体失败。
#
# 抽成脚本的原因：原先这段逻辑嵌在 ci.yml 里无法测试，独立 review 指出
# `publish != 'true'` 的写法会把**空值**当成"按设计不发版"从而放行一次本该红掉的漏跑。
# 现在语义收紧为：只有**显式 publish=false** 才算按设计跳过。
# 注意范围：这影响的是"镜像 job 被跳过（skipped）时怎么判"，空值/异常值会让跳过判红；
# 若镜像 job 本身是 success，则照常放行（它的产物另有校验）。
#
# 输入（env）：
#   R_RESOLVE R_CHANGES R_WEBDIST R_API R_DOCKER R_MCP   各 job 的 result
#   W_ALL W_API W_DOCKER W_MCP                          changes 的判定
#   IS_RELEASE                                          resolve-version 的输出
#   W_PUBLISH                                           web-dist 的判定（true/false/''）
# 退出码：0 = 通过；1 = 失败
# ---------------------------------------------------------------------------
set -uo pipefail

: "${R_RESOLVE:=}"; : "${R_CHANGES:=}"; : "${R_WEBDIST:=}"
: "${R_API:=}"; : "${R_DOCKER:=}"; : "${R_MCP:=}"
: "${W_ALL:=false}"; : "${W_API:=false}"; : "${W_DOCKER:=false}"; : "${W_MCP:=false}"
: "${IS_RELEASE:=}"; : "${W_PUBLISH:=}"

fail=0

# 该组件本次是否被判定为「应当重建」（任一 flag 为 true，或 all 为 true）
should() {
  if [ "$W_ALL" = "true" ]; then echo true; return; fi
  for flag in "$@"; do
    if [ "$flag" = "true" ]; then echo true; return; fi
  done
  echo false
}

# 镜像构建额外受「防重复发版」约束。只有**显式** publish=false 才豁免；
# publish 为空/异常值时按"应当重建"处理（fail-closed），避免漏跑被静默放过。
should_docker() {
  if [ "$(should "$W_API" "$W_DOCKER")" != "true" ]; then echo false; return; fi
  if [ "$IS_RELEASE" = "true" ] && [ "$W_PUBLISH" = "false" ]; then echo false; return; fi
  echo true
}

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
check "web-dist"        "$R_WEBDIST" "$(should "$W_API" "$W_DOCKER")"
check "build-api"       "$R_API"     "$(should "$W_API")"
check "build-docker"    "$R_DOCKER"  "$(should_docker)"
check "build-mcp"       "$R_MCP"     "$(should "$W_MCP")"

echo "（判定依据：all=$W_ALL api=$W_API docker=$W_DOCKER mcp=$W_MCP；is_release=${IS_RELEASE:-<empty>}；publish=${W_PUBLISH:-<empty>}）"

if [ "$fail" -ne 0 ]; then
  echo ""
  echo "::error::CI Gate 未通过：存在失败或疑似静默跳过的必需 job。"
  exit 1
fi

echo ""
echo "CI Gate 通过：所有必需 job 均成功，或因组件无变化而按设计跳过。"
