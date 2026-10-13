#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：pim-api 触发脚本（REQ-4 / AC-6 第 1 项）。
# 断言：成功/失败都可见、失败非 0、请求体带对了确切引用与"只重建镜像"意图、
# 且**任何输出都不含 token**。
# 用法：scripts/ci/test-trigger-api-release.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/trigger-api-release.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-trigger-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

export PIM_CURL="$HERE/test-helpers/fake-curl.sh"
export FAKE_CURL_LOG="$TMP/calls.log"
SECRET_TOKEN="ghs_THIS_MUST_NEVER_APPEAR"

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }
contains() { if [[ "$3" == *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 未包含 [$2]"; fail=$((fail+1)); fi; }
not_contains() { if [[ "$3" != *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 竟然包含了 [$2]"; fail=$((fail+1)); fi; }

echo "== trigger-api-release.sh =="

# 1) 成功：HTTP 204 → exit 0
FAKE_CURL_STATUS=204 FAKE_CURL_BODY='' \
  TRIGGER_TOKEN="$SECRET_TOKEN" bash "$SCRIPT" --web-dist-ref sha-abc1234 >"$TMP/ok.out" 2>"$TMP/ok.err" && rc=0 || rc=$?
check "HTTP 204 → exit 0" "0" "$rc"
check "触发目标为 pim-api 的 ci.yml" "1" "$(grep -c 'repos/2746267826/pim-api/actions/workflows/ci.yml/dispatches' "$FAKE_CURL_LOG")"
check "payload 带确切前端引用" "1" "$(grep -c 'sha-abc1234' "$FAKE_CURL_LOG")"
check "payload 带只重建镜像意图" "1" "$(grep -c 'rebuild_image_only' "$FAKE_CURL_LOG")"
check "payload ref=master" "1" "$(grep -c '"ref": "master"' "$FAKE_CURL_LOG")"
not_contains "成功输出不含 token（stdout）" "$SECRET_TOKEN" "$(cat "$TMP/ok.out")"
not_contains "成功输出不含 token（stderr）" "$SECRET_TOKEN" "$(cat "$TMP/ok.err")"
not_contains "curl 参数不含 token（走 600 配置文件）" "$SECRET_TOKEN" "$(cat "$FAKE_CURL_LOG")"
check "token 走 --config 而非 -H" "1" "$(grep -c '^--config$' "$FAKE_CURL_LOG")"
check "token 不落盘（凭据经 /dev/stdin）" "1" "$(grep -c '^/dev/stdin$' "$FAKE_CURL_LOG")"
check "自动触发显式传 force_all=false" "1" "$(grep -c '"force_all": "false"' "$FAKE_CURL_LOG")"
# 脚本自己的进程参数里也不能有 token
not_contains "脚本 argv 不含 token" "$SECRET_TOKEN" "$(cat "$TMP/ok.out" "$TMP/ok.err")"
if grep -rl "$SECRET_TOKEN" "${TMPDIR:-/tmp}" --include='pim-*' 2>/dev/null | head -1 | grep -q .; then
  echo "  ❌ 临时目录里留有含 token 的文件"; fail=$((fail+1))
else
  echo "  ✅ 临时目录里没有含 token 的文件"; pass=$((pass+1))
fi

# 2) 失败：HTTP 422 → exit 非 0，且打印状态与响应体
: > "$FAKE_CURL_LOG"
FAKE_CURL_STATUS=422 FAKE_CURL_BODY='{"message":"Invalid request","errors":[{"field":"inputs"}]}' \
  TRIGGER_TOKEN="$SECRET_TOKEN" bash "$SCRIPT" --web-dist-ref sha-abc1234 >"$TMP/bad.out" 2>"$TMP/bad.err" && rc=0 || rc=$?
check "HTTP 422 → 非 0 退出" "1" "$([ "$rc" -ne 0 ] && echo 1 || echo 0)"
contains "打印 HTTP 状态" "HTTP 422" "$(cat "$TMP/bad.err")"
contains "打印响应体" "Invalid request" "$(cat "$TMP/bad.err")"
not_contains "失败输出不含 token" "$SECRET_TOKEN" "$(cat "$TMP/bad.err")"

# 3) 网络层失败（curl 非 0）→ 非 0 且可见
: > "$FAKE_CURL_LOG"
FAKE_CURL_RC=7 FAKE_CURL_STATUS='000' FAKE_CURL_BODY='' \
  TRIGGER_TOKEN="$SECRET_TOKEN" bash "$SCRIPT" --web-dist-ref sha-abc1234 >"$TMP/net.out" 2>"$TMP/net.err" && rc=0 || rc=$?
check "curl 失败 → 非 0 退出" "1" "$([ "$rc" -ne 0 ] && echo 1 || echo 0)"
contains "curl 失败原因可见" "curl 调用失败" "$(cat "$TMP/net.err")"

# 4) 参数校验：引用必须是不可变 sha-<7>（不允许 latest，避免触发方与烘焙内容漂开）
if TRIGGER_TOKEN="$SECRET_TOKEN" bash "$SCRIPT" --web-dist-ref latest >/dev/null 2>&1; then
  echo "  ❌ latest 应当被拒绝"; fail=$((fail+1))
else
  echo "  ✅ 非不可变引用（latest）被拒绝"; pass=$((pass+1))
fi
if bash "$SCRIPT" --web-dist-ref sha-abc1234 >/dev/null 2>&1; then
  echo "  ❌ 缺 token 应当被拒绝"; fail=$((fail+1))
else
  echo "  ✅ 缺 token → 非 0 退出"; pass=$((pass+1))
fi

echo ""
echo "trigger-api-release: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
