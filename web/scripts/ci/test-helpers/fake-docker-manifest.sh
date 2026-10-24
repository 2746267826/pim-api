#!/usr/bin/env bash
# 测试替身：模拟 `docker manifest inspect <ref>`，输出与退出码由环境变量决定。
#   FAKE_MANIFEST_OUT  输出内容
#   FAKE_MANIFEST_RC   退出码
#   FAKE_MANIFEST_LOG  把调用参数记到这里
set -uo pipefail
if [ -n "${FAKE_MANIFEST_LOG:-}" ]; then
  { for a in "$@"; do echo "$a"; done; echo "---"; } >> "$FAKE_MANIFEST_LOG"
fi
printf '%s' "${FAKE_MANIFEST_OUT:-}"
exit "${FAKE_MANIFEST_RC:-0}"
