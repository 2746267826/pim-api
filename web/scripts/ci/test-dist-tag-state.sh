#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：不可变 tag 存在性判定（REQ-1「不可变 tag 只写一次」）。
# 重点是**不许把探测故障当成"不存在"**：故障文本直接取自第 2/3 轮独立 review 的注入验证。
# 用法：scripts/ci/test-dist-tag-state.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/dist-tag-state.sh"
export PIM_DOCKER="$HERE/test-helpers/fake-docker-manifest.sh"
REF="ghcr.io/2746267826/pim-web-dist:sha-abc1234"

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }

run() { # <rc> <out>  → "rc:<exit> state:<stdout>"
  local rc="$1" out="$2" erc=0 sout=""
  sout="$(FAKE_MANIFEST_RC="$rc" FAKE_MANIFEST_OUT="$out" bash "$SCRIPT" "$REF" 2>/dev/null)" || erc=$?
  echo "rc:${erc} state:${sout}"
}

echo "== dist-tag-state.sh =="

check "tag 存在 → exists/退出 0" "rc:0 state:exists" "$(run 0 '{"schemaVersion":2}')"
check "tag 明确不存在（no such manifest）→ missing/退出 0" "rc:0 state:missing" \
  "$(run 1 'no such manifest: ghcr.io/2746267826/pim-web-dist:sha-abc1234')"
check "tag 明确不存在（manifest unknown）→ missing/退出 0" "rc:0 state:missing" \
  "$(run 1 'manifest unknown')"

# 以下都是"探测失败"，必须报红（退出 1），绝不能被当成"不存在"从而覆盖既有 tag
check "鉴权失败 → 退出 1（未知）" "rc:1 state:" "$(run 1 'unauthorized: authentication required')"
check "token 服务 404 → 退出 1（未知，不许命中 not found）" "rc:1 state:" \
  "$(run 1 'Get "https://ghcr.io/token?scope=repository%3A2746267826%2Fpim-web-dist%3Apull": 404 Not Found')"
check "凭证助手缺失 → 退出 1（未知）" "rc:1 state:" \
  "$(run 1 'error getting credentials - err: exec: "docker-credential-desktop": executable file not found in $PATH')"
check "网络超时 → 退出 1（未知）" "rc:1 state:" "$(run 1 'dial tcp: i/o timeout')"
check "权限不足 → 退出 1（未知）" "rc:1 state:" "$(run 1 'denied: requested access to the resource is denied')"

echo ""
echo "dist-tag-state: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
