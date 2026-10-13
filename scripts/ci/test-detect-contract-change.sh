#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：契约变化检测（工单 §9.2「契约是否变化的判定不依赖现有路径过滤器」）。
#
# 为什么必须能测：这段判定决定「要不要通知客户端仓」。如果它漏判，机制不会报错，
# 只会静默地永远不通知 —— 正是本工单要消灭的那种失败模式。
#
# 用真实 git 仓库做 fixture（不是 mock git）：判定的语义就是「两个 commit 的
# contract blob 是否不同」，只有真 git 才能证明这件事，包括"路径在 base 不存在"
# 与"ref 解析失败"这两种必须 fail-open 的情形。
#
# 用法：scripts/ci/test-detect-contract-change.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/detect-contract-change.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-change-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

REPO="$TMP/repo"
mkdir -p "$REPO"
GIT="git -C $REPO"
$GIT init -q
$GIT config user.email "test@pim.local"
$GIT config user.name "PIM Test"
$GIT config commit.gpgsign false

ZERO40="0000000000000000000000000000000000000000"

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }
contains() { if [[ "$3" == *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — [$3] 未包含 [$2]"; fail=$((fail+1)); fi; }
field() { sed -n "s/^$2=//p" <<<"$1"; }

# run <args...> → stdout（stderr 落到 $TMP/last.err）；退出码放 $RC
RC=0
run() {
  set +e
  OUT="$(env -u GITHUB_OUTPUT PIM_CONTRACT_CHANGE_REPO="$REPO" bash "$SCRIPT" "$@" 2>"$TMP/last.err")"
  RC=$?
  set -e
}

echo "== detect-contract-change.sh =="
echo "  （fixture: 真实 git 仓库 $REPO）"

# --- fixture 历史 -----------------------------------------------------------
# c0: 只有 README（契约路径尚不存在）
# c1: 加入 contract/openapi.json（内容 A）
# c2: 只改 README（契约不变）
# c3: 改 contract/openapi.json（内容 B）
echo "readme-0" > "$REPO/README.md"
$GIT add -A && $GIT commit -qm "c0: readme only"
C0="$($GIT rev-parse HEAD)"

mkdir -p "$REPO/contract"
printf '{\n  "openapi": "3.0.1",\n  "paths": {"/a": {}}\n}\n' > "$REPO/contract/openapi.json"
$GIT add -A && $GIT commit -qm "c1: add contract A"
C1="$($GIT rev-parse HEAD)"

echo "readme-1" > "$REPO/README.md"
$GIT add -A && $GIT commit -qm "c2: readme only"
C2="$($GIT rev-parse HEAD)"

printf '{\n  "openapi": "3.0.1",\n  "paths": {"/a": {}, "/b": {}}\n}\n' > "$REPO/contract/openapi.json"
$GIT add -A && $GIT commit -qm "c3: contract B"
C3="$($GIT rev-parse HEAD)"

# 内容 A 的摘要（c1/c2 上生效）：从 **快照对象** 取，避免被后面 c3 覆盖后的工作区误用。
SHA_A="$($GIT cat-file blob "$C2:contract/openapi.json" | sha256sum | cut -d' ' -f1)"
SHA_B="$(sha256sum "$REPO/contract/openapi.json" | cut -d' ' -f1)"
BYTES_B="$(wc -c < "$REPO/contract/openapi.json" | tr -d ' ')"
[ "$SHA_A" != "$SHA_B" ] || { echo "  ❌ fixture 有误：内容 A 与 B 的 sha256 相同" >&2; exit 1; }

# --- 1) 契约字节没变（只改了别的文件）→ changed=false -----------------------
run --base "$C1" --head "$C2"
check "只改别的文件 → 退出码 0" "0" "$RC"
check "只改别的文件 → changed=false" "false" "$(field "$OUT" changed)"
check "  → 仍报告快照 sha256（供下游比对）" "$SHA_A" "$(field "$OUT" contract_sha256)"
check "  → head blob 与 base blob 相同" "$(field "$OUT" base_blob)" "$(field "$OUT" head_blob)"

# --- 2) 契约字节变了 → changed=true + 完整事实 ------------------------------
run --base "$C2" --head "$C3"
check "契约变了 → changed=true" "true" "$(field "$OUT" changed)"
check "契约变了 → sha256 是 head 上的字节" "$SHA_B" "$(field "$OUT" contract_sha256)"
check "契约变了 → bytes 是 head 上的字节数" "$BYTES_B" "$(field "$OUT" contract_bytes)"
check "契约变了 → contract_commit 是 head 提交" "$C3" "$(field "$OUT" contract_commit)"

# --- 3) base 上还没有契约文件（新加入）→ fail-open：算变化 -------------------
run --base "$C0" --head "$C1"
check "base 无该路径 → changed=true" "true" "$(field "$OUT" changed)"
check "base 无该路径 → base_blob=none" "none" "$(field "$OUT" base_blob)"
contains "base 无该路径 → 原因可见" "不存在" "$(cat "$TMP/last.err")"

# --- 4) base 无法解析（例如 release tag 还没 fetch 到）→ fail-open：算变化 ----
run --base "no-such-ref-xyz" --head "$C3"
check "base 无法解析 → changed=true" "true" "$(field "$OUT" changed)"
contains "base 无法解析 → 原因可见" "无法解析" "$(cat "$TMP/last.err")"

# --- 5) base 为空 / 全零（首次推送、强推）→ fail-open：算变化 ---------------
run --base "" --head "$C3"
check "base 为空 → changed=true" "true" "$(field "$OUT" changed)"
run --base "$ZERO40" --head "$C3"
check "base 为全零 → changed=true" "true" "$(field "$OUT" changed)"

# --- 6) force=true → 无论是否变化都算变化（手动补发/重发通路）---------------
run --base "$C2" --head "$C2" --force true
check "force=true → 即使字节相同也 changed=true" "true" "$(field "$OUT" changed)"
contains "force=true → 原因写明是强制" "force" "$(cat "$TMP/last.err")"

# --- 7) 未显式传 --base/--head → 默认 head=HEAD；base 空 → fail-open --------
run
check "省略参数 → changed=true（无 base，宁可通知不可漏通知）" "true" "$(field "$OUT" changed)"

# --- 8) 契约在 head 上不存在 → 配置错误，必须可见地失败（不是静默 false）----
run --base "$C1" --head "$C0"
check "head 无契约文件 → 退出码 4" "4" "$RC"
contains "head 无契约文件 → error 可见" "head" "$(cat "$TMP/last.err")"

# --- 9) 自定义路径（--path）生效 -------------------------------------------
printf 'x\n' > "$REPO/contract/other.json"
$GIT add -A && $GIT commit -qm "c4: add other.json"
C4="$($GIT rev-parse HEAD)"
run --base "$C3" --head "$C4" --path contract/other.json
check "--path 指向的文件变了 → changed=true" "true" "$(field "$OUT" changed)"
check "  → sha256 是 --path 指定的文件" "$(sha256sum "$REPO/contract/other.json" | cut -d' ' -f1)" "$(field "$OUT" contract_sha256)"

# --- 10) GITHUB_OUTPUT 写入的是同一份判定 --------------------------------
printf '' > "$TMP/gh_output"
set +e
env PIM_CONTRACT_CHANGE_REPO="$REPO" GITHUB_OUTPUT="$TMP/gh_output" \
  bash "$SCRIPT" --base "$C2" --head "$C3" >"$TMP/stdout.txt" 2>"$TMP/last.err"
RC2=$?
set -e
check "写 GITHUB_OUTPUT 时退出码 0" "0" "$RC2"
check "GITHUB_OUTPUT 有 changed=true" "true" "$(sed -n 's/^changed=//p' "$TMP/gh_output")"
check "stdout 与 GITHUB_OUTPUT 一致" "true" "$(sed -n 's/^changed=//p' "$TMP/stdout.txt")"

# --- 11) 参数错误 → 退出码 2 ----------------------------------------------
run --base "$C1" --head "$C2" --force maybe
check "force 非 true/false → 退出码 2" "2" "$RC"
run --nope
check "未知参数 → 退出码 2" "2" "$RC"

echo ""
echo "detect-contract-change: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
