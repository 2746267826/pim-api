#!/usr/bin/env bash
# 测试替身：模拟 curl，供 trigger-api-release.sh 的自检使用。
# 行为由环境变量控制：
#   FAKE_CURL_STATUS  返回的 HTTP 状态码（默认 204）
#   FAKE_CURL_RC      退出码（默认 0；非 0 模拟网络失败）
#   FAKE_CURL_BODY    写进 -o 文件的内容
#   FAKE_CURL_LOG     把调用参数逐行记到这里
set -uo pipefail

out=""
body="${FAKE_CURL_BODY:-}"
status="${FAKE_CURL_STATUS:-204}"
rc="${FAKE_CURL_RC:-0}"

if [ -n "${FAKE_CURL_LOG:-}" ]; then
  { echo "--- call ---"; for a in "$@"; do echo "$a"; done; } >> "$FAKE_CURL_LOG"
fi

while [[ $# -gt 0 ]]; do
  case "$1" in
    -o) out="${2:-}"; shift 2 ;;
    *) shift ;;
  esac
done
[ -n "$out" ] && printf '%s' "$body" > "$out"
printf '%s' "$status"
exit "$rc"
