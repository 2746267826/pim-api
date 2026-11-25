#!/usr/bin/env bash
# 发行说明校验的行为测试：每条规则都要有一个「必须失败」的反例，
# 否则校验器本身可能一直是绿的（假牙）。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/validate-release-notes.sh"
fail=0

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

# ---- 用 stub gh 代替真网络：按 PR 号返回「已合并 / 未合并 / 不存在」----
mkdir -p "$WORKDIR/bin"
cat > "$WORKDIR/bin/gh" <<'STUB'
#!/usr/bin/env bash
# 支持: gh api repos/<owner>/<repo>/pulls/<N> --jq .merged_at
PR="${2##*/}"
case "$PR" in
  101|102|103) echo "2026-11-01T00:00:00Z" ;;
  201)         echo "null" ;;
  *)           echo "gh: Not Found (HTTP 404)" >&2; exit 1 ;;
esac
STUB
chmod +x "$WORKDIR/bin/gh"
PATH="$WORKDIR/bin:$PATH"

expect() {
  local name="$1" want="$2" file="$3"
  local out rc
  set +e
  out="$(bash "$SCRIPT" --file "$file" --repo 2746267826/pim-api 2>&1)"
  rc=$?
  set -e
  if [[ "$want" == "pass" ]]; then
    if [[ "$rc" -ne 0 ]]; then
      echo "FAIL $name: expected pass, got rc=$rc [$out]"
      fail=1
    else
      echo "OK   $name (pass)"
    fi
  else
    if [[ "$rc" -eq 0 ]]; then
      echo "FAIL $name: expected failure, got pass [$out]"
      fail=1
    elif ! grep -q "$want" <<<"$out"; then
      echo "FAIL $name: expected message containing [$want], got [$out]"
      fail=1
    else
      echo "OK   $name (rc=$rc)"
    fi
  fi
}

# 正例：两条真实合并的 PR
cat > "$WORKDIR/good.md" <<'MD'
## PIM API v1.1.0

这个版本把发版拆成了两条通道：日常自动出 dev 版，正式版手动提升。

- 新增显示版本号与版本文件（#101）
- 修复发版说明误带旧资产的问题（#102）
MD
expect good pass "$WORKDIR/good.md"

# 空文件
: > "$WORKDIR/empty.md"
expect empty "release notes are empty" "$WORKDIR/empty.md"

# 没有列表项（一整段糊在一起）
printf '我们这次改了很多东西（#101）\n' > "$WORKDIR/nobullet.md"
expect no-bullet "must contain at least one bullet line" "$WORKDIR/nobullet.md"

# 一个 PR 号都没有 → 无法核对，等于可以随便写
printf -- '- 优化了一些体验\n' > "$WORKDIR/nopr.md"
expect no-pr-ref "must reference at least one merged PR" "$WORKDIR/nopr.md"

# 引用了还没合并的 PR
printf -- '- 新增某功能（#201）\n' > "$WORKDIR/unmerged.md"
expect unmerged-pr "is not merged" "$WORKDIR/unmerged.md"

# 引用了不存在的 PR
printf -- '- 新增某功能（#999）\n' > "$WORKDIR/missing.md"
expect missing-pr "does not exist" "$WORKDIR/missing.md"

# 占位符没删干净
printf -- '- TODO 补一下这版改了什么（#101）\n' > "$WORKDIR/placeholder.md"
expect placeholder "placeholder" "$WORKDIR/placeholder.md"

# 多个 PR 里只要有一个对不上号就必须失败（不能只看第一个）
printf -- '- 甲（#101）\n- 乙（#999）\n' > "$WORKDIR/mixed.md"
expect mixed-prs "does not exist" "$WORKDIR/mixed.md"

if [[ "$fail" -ne 0 ]]; then
  echo "validate-release-notes tests failed"
  exit 1
fi
echo "validate-release-notes tests passed"
