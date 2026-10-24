#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：GHCR 包匿名可拉探测（REQ-1 契约 / AC-10）。
# 用法：scripts/ci/test-check-ghcr-public.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/check-ghcr-public.sh"
export PIM_CURL="$HERE/test-helpers/fake-curl-ghcr.sh"
REF="ghcr.io/2746267826/pim-web-dist"

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }

echo "== check-ghcr-public.sh =="

# 1) public 且 tag 存在 → 0
FAKE_GHCR_TOKEN_JSON='{"token":"anon"}' FAKE_GHCR_TAGS_JSON='{"name":"pim-web-dist","tags":["sha-abc1234","latest"]}' \
  bash "$SCRIPT" "$REF" --expect-tag sha-abc1234 >/dev/null 2>&1 && rc=0 || rc=$?
check "public + tag 存在 → exit 0" "0" "$rc"

# 2) private（拿不到匿名 token）→ 非 0
FAKE_GHCR_TOKEN_JSON='{"errors":[{"code":"DENIED","message":"requested access to the resource is denied"}]}' \
  bash "$SCRIPT" "$REF" >/dev/null 2>&1 && rc=0 || rc=$?
check "private（匿名 token 被拒）→ 非 0" "1" "$([ "$rc" -ne 0 ] && echo 1 || echo 0)"

# 3) public 但期望的 tag 不存在 → 非 0（防止"包 public 了但这一版没推上去"被当成成功）
FAKE_GHCR_TOKEN_JSON='{"token":"anon"}' FAKE_GHCR_TAGS_JSON='{"tags":["latest"]}' \
  bash "$SCRIPT" "$REF" --expect-tag sha-abc1234 >/dev/null 2>&1 && rc=0 || rc=$?
check "public 但缺期望 tag → 非 0" "1" "$([ "$rc" -ne 0 ] && echo 1 || echo 0)"

echo ""
echo "check-ghcr-public: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
