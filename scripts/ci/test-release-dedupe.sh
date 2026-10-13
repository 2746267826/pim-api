#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：防重复发版判定（REQ-6 / AC-4）。
# 纯文件与固定输入，不依赖网络、docker、GITHUB_* 。
# 用法：scripts/ci/test-release-dedupe.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/release-dedupe.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-dedupe-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

pass=0
fail=0
check() { # <label> <expected> <actual>
  if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1));
  else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi
}
contains() { # <label> <needle> <haystack>
  if [[ "$3" == *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1));
  else echo "  ❌ $1 — 未包含 [$2]，实际 [$3]"; fail=$((fail+1)); fi
}
run() { # <args...> -> stdout key=value（summary 在 stderr）
  GITHUB_OUTPUT="" bash "$SCRIPT" "$@" --print-env 2>"$TMP/last.err"
}
summary() { cat "$TMP/last.err"; }

echo "== release-dedupe.sh =="

# 1) 后端有变化 → 必发版，不论前端是否相同
out="$(run --web-version 26.10.831 --web-git-sha abc1234 --backend-changed true \
  --previous-release-tag api-v26.10.830 --ledger /nonexistent)"
check "后端有变化 → publish=true" "true" "$(sed -n 's/^publish=//p' <<<"$out")"
contains "后端有变化 → 理由说明后端" "后端" "$(sed -n 's/^dedupe_reason=//p' <<<"$out")"

# 2) 首次发布：没有上一版 release
out="$(run --web-version 26.10.831 --web-git-sha abc1234 --backend-changed false)"
check "无上一版 release → publish=true" "true" "$(sed -n 's/^publish=//p' <<<"$out")"
contains "无上一版 release → summary 写明按需发版处理" "无上一版台账，按需发版处理" "$(summary)"

# 3) 有上一版 release 但没有台账资产
out="$(run --web-version 26.10.831 --web-git-sha abc1234 --backend-changed false \
  --previous-release-tag api-v26.10.830 --ledger "$TMP/absent.json")"
check "上一版无台账资产 → publish=true" "true" "$(sed -n 's/^publish=//p' <<<"$out")"
contains "上一版无台账资产 → summary 写明按需发版处理" "无上一版台账，按需发版处理" "$(summary)"

# 4) 台账不可读（不是 JSON）
printf 'not json at all' > "$TMP/bad.json"
out="$(run --web-version 26.10.831 --web-git-sha abc1234 --backend-changed false \
  --previous-release-tag api-v26.10.830 --ledger "$TMP/bad.json")"
check "台账不可读 → publish=true（不中断）" "true" "$(sed -n 's/^publish=//p' <<<"$out")"
contains "台账不可读 → summary 可见" "不可读" "$(summary)"

# 5) 台账缺字段
printf '{"version":"26.10.831"}' > "$TMP/partial.json"
out="$(run --web-version 26.10.831 --web-git-sha abc1234 --backend-changed false \
  --previous-release-tag api-v26.10.830 --ledger "$TMP/partial.json")"
check "台账缺字段 → publish=true" "true" "$(sed -n 's/^publish=//p' <<<"$out")"

# 6) 前端版本与台账逐字一致 + 后端无变化 → 不发版
cat > "$TMP/ledger.json" <<'JSON'
{"apiVersion":"26.10.830","builtAt":"2026-10-13T10:00:00Z","gitSha":"abc1234",
 "imageDigest":"sha256:aa","imageTag":"ghcr.io/2746267826/pim-web-dist:sha-abc1234","version":"26.10.831"}
JSON
out="$(run --web-version 26.10.831 --web-git-sha abc1234 --backend-changed false \
  --previous-release-tag api-v26.10.830 --ledger "$TMP/ledger.json")"
check "前端未变 + 后端未变 → publish=false" "false" "$(sed -n 's/^publish=//p' <<<"$out")"
contains "不发版 → summary 三个事实齐全(本次前端)" "26.10.831" "$(summary)"
contains "不发版 → summary 三个事实齐全(上版台账)" "26.10.830" "$(summary)"
contains "不发版 → summary 三个事实齐全(后端 diff)" "后端 diff 是否为空" "$(summary)"

# 7) 版本号相同但 sha 不同 → 仍要发版
out="$(run --web-version 26.10.831 --web-git-sha ffffffff --backend-changed false \
  --previous-release-tag api-v26.10.830 --ledger "$TMP/ledger.json")"
check "sha 不同 → publish=true" "true" "$(sed -n 's/^publish=//p' <<<"$out")"

# 8) sha 相同但版本号不同 → 仍要发版
out="$(run --web-version 26.10.999 --web-git-sha abc1234 --backend-changed false \
  --previous-release-tag api-v26.10.830 --ledger "$TMP/ledger.json")"
check "版本号不同 → publish=true" "true" "$(sed -n 's/^publish=//p' <<<"$out")"

# 9) 参数校验：backend-changed 非布尔 → 非 0 退出
if bash "$SCRIPT" --web-version 1 --web-git-sha abc1234 --backend-changed maybe --print-env >/dev/null 2>&1; then
  echo "  ❌ backend-changed 非法值应当失败"; fail=$((fail+1))
else
  echo "  ✅ backend-changed 非法值 → 非 0 退出"; pass=$((pass+1))
fi

echo ""
echo "release-dedupe: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
