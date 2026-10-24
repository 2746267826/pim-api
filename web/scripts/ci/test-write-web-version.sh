#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：产物内前端版本文件的生成（REQ-2 / AC-2）。
# 用法：scripts/ci/test-write-web-version.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/write-web-version.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-web-version-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }

echo "== write-web-version.sh =="

OUT="$TMP/web-version.json"
if bash "$SCRIPT" --out "$OUT" --version 26.10.831 --git-sha abc1234 --built-at 2026-10-13T10:00:00Z 2>"$TMP/log"; then
  echo "  ✅ 生成成功"; pass=$((pass+1))
else
  echo "  ❌ 生成失败：$(cat "$TMP/log")"; fail=$((fail+1))
fi
check "字段固定为三项" "3" "$(jq -r 'keys | length' "$OUT")"
check "version 原样" "26.10.831" "$(jq -r '.version' "$OUT")"
check "gitSha 原样" "abc1234" "$(jq -r '.gitSha' "$OUT")"
check "builtAt 原样" "2026-10-13T10:00:00Z" "$(jq -r '.builtAt' "$OUT")"

# 环境变量同源：CI 注入的两个值就是文件里的两个值
OUT2="$TMP/from-env.json"
PIM_WEB_VERSION="26.10.900" PIM_WEB_GIT_SHA="deadbee" bash "$SCRIPT" --out "$OUT2" --built-at 2026-10-13T11:00:00Z 2>/dev/null
check "从环境变量取版本号（与注入同源）" "26.10.900" "$(jq -r '.version' "$OUT2")"
check "从环境变量取 sha（与注入同源）" "deadbee" "$(jq -r '.gitSha' "$OUT2")"

# 不传 built-at 时用当前 UTC 时间（格式必须是 Z 结尾的 ISO8601）
OUT3="$TMP/now.json"
bash "$SCRIPT" --out "$OUT3" --version 1.2.3 --git-sha abc1234 2>/dev/null
check "builtAt 默认是 UTC ISO8601" "true" "$(jq -r '.builtAt | test("^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$")' "$OUT3")"

# 缺版本号 / 非法 sha / 非法时间 → 硬失败（不许产出半份信息）
for args in "--out $TMP/a.json --git-sha abc1234" \
            "--out $TMP/b.json --version 1 --git-sha ABC1234" \
            "--out $TMP/c.json --version 1 --git-sha abc123" \
            "--out $TMP/d.json --version 1 --git-sha abc1234 --built-at 2026-10-13"; do
  # shellcheck disable=SC2086
  if bash "$SCRIPT" $args >/dev/null 2>&1; then
    echo "  ❌ 非法输入（$args）应当失败"; fail=$((fail+1))
  else
    echo "  ✅ 非法输入被拒绝：$args"; pass=$((pass+1))
  fi
done

echo ""
echo "write-web-version: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
