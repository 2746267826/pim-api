#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/build-release-notes.sh"
PARSER="$ROOT/scripts/ci/parse_pr_body.py"

fail=0

assert_eq() {
  local label="$1" expected="$2" actual="$3"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL $label: expected [$expected] got [$actual]"
    fail=1
  else
    echo "OK $label: [$actual]"
  fi
}

echo "=== Testing PR Number Extraction Regex ==="

# 1. Test Merge commit pattern
input1="1788603019 Merge pull request #197 from 2746267826/opencode-linux/reminder-mcp-196"
res1="$(echo "$input1" | sed -nE -e 's/^[0-9]+ Merge pull request #([0-9]+).*/\1/p' -e 's/^[0-9]+ .*\(\#([0-9]+)\)[[:space:]]*$/\1/p')"
assert_eq "Merge commit PR #197" "197" "$res1"

# 2. Test Squash merge pattern
input2="1788708914 fix(client-windows,ops): resilient token restore on boot (#200)"
res2="$(echo "$input2" | sed -nE -e 's/^[0-9]+ Merge pull request #([0-9]+).*/\1/p' -e 's/^[0-9]+ .*\(\#([0-9]+)\)[[:space:]]*$/\1/p')"
assert_eq "Squash merge PR #200" "200" "$res2"

# 3. Test Squash merge pattern with trailing space
input3="1788708914 fix: something (#202)   "
res3="$(echo "$input3" | sed -nE -e 's/^[0-9]+ Merge pull request #([0-9]+).*/\1/p' -e 's/^[0-9]+ .*\(\#([0-9]+)\)[[:space:]]*$/\1/p')"
assert_eq "Squash merge with trailing spaces PR #202" "202" "$res3"

# 4. Test Direct commit (no PR)
input4="1788354417 Add files via upload"
res4="$(echo "$input4" | sed -nE -e 's/^[0-9]+ Merge pull request #([0-9]+).*/\1/p' -e 's/^[0-9]+ .*\(\#([0-9]+)\)[[:space:]]*$/\1/p')"
assert_eq "Direct commit without PR" "" "$res4"

echo "=== Testing Git Log Range Extraction on Historical Tags ==="

# Test v2026.09.442 to v2026.09.446 (PR 200)
prs_446="$(git log --first-parent --format='%ct %s' "v2026.09.442..v2026.09.446" \
  | sort -n \
  | sed -nE -e 's/^[0-9]+ Merge pull request #([0-9]+).*/\1/p' \
            -e 's/^[0-9]+ .*\(\#([0-9]+)\)[[:space:]]*$/\1/p' \
  | awk '!seen[$0]++')"
assert_eq "Window 442..446 extracts PR 200" "200" "$prs_446"

# Test v2026.09.446 to v2026.09.449 (PR 202)
prs_449="$(git log --first-parent --format='%ct %s' "v2026.09.446..v2026.09.449" \
  | sort -n \
  | sed -nE -e 's/^[0-9]+ Merge pull request #([0-9]+).*/\1/p' \
            -e 's/^[0-9]+ .*\(\#([0-9]+)\)[[:space:]]*$/\1/p' \
  | awk '!seen[$0]++')"
assert_eq "Window 446..449 extracts PR 202" "202" "$prs_449"

# Test v2026.09.432 to v2026.09.442 (PR 197 merge commit)
prs_442="$(git log --first-parent --format='%ct %s' "v2026.09.432..v2026.09.442" \
  | sort -n \
  | sed -nE -e 's/^[0-9]+ Merge pull request #([0-9]+).*/\1/p' \
            -e 's/^[0-9]+ .*\(\#([0-9]+)\)[[:space:]]*$/\1/p' \
  | awk '!seen[$0]++')"
assert_eq "Window 432..442 extracts PR 197" "197" "$prs_442"

echo "=== Testing parse_pr_body.py on Sample PR JSON ==="
sample_json='{
  "number": 999,
  "title": "feat: test feature",
  "url": "https://github.com/test/repo/pull/999",
  "body": "## 技术修改 / Technical changes\n- change 1\n## 功能变化 / Feature changes\n- feature 1\n## 如何体验 / How to try it\n- click button\n## 测试 / Tests\n- test 1"
}'
parsed_out="$(echo "$sample_json" | python3 "$PARSER")"
if ! echo "$parsed_out" | grep -q "### #999 — feat: test feature"; then
  echo "FAIL parser output missing header"
  fail=1
else
  echo "OK parser header"
fi
if ! echo "$parsed_out" | grep -q "技术修改 / Technical changes"; then
  echo "FAIL parser output missing technical changes"
  fail=1
else
  echo "OK parser technical changes"
fi

if [[ "$fail" -ne 0 ]]; then
  echo "build-release-notes tests failed"
  exit 1
fi
echo "All build-release-notes tests passed successfully!"
