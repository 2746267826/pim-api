#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：契约同步 PR 脚本（离线）。
#
# 用**真 git**（本地 bare 仓当 origin，用 url.insteadOf 让 origin 的 URL 仍是
# https://github.com/<owner>/<repo>.git）+ **stub gh** 验证：
#   * 真的推了符合 {agent}-{os}/{topic} 的分支，提交里**只**有契约产物；
#   * PR 标题双语、正文四段（AGENTS.md / pr-metadata 门禁的要求）；
#   * 幂等：同一份内容重复触发不产生空提交、不重复开 PR；
#   * **补开 PR**：上次推送成功但开 PR 失败时，重跑必须把 PR 补出来；
#   * **吸收最新 base**：master 前进后，刷新基于最新 base（不是旧分支的 HEAD）重建；
#   * 通知里的 sha256 对不上时不提交、不开 PR；
#   * 脏工作区 / origin 与 GITHUB_REPOSITORY 不一致 / gh 查询失败 → 拒绝执行；
#   * token 不出现在任何输出里。
#
# 用法：scripts/ci/test-contract-sync-pr.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/contract-sync-pr.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-sync-pr-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }
contains() { if [[ "$3" == *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 未见 [$2]"; fail=$((fail+1)); fi; }
not_contains() { if [[ "$3" != *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 出现了不该出现的 [$2]"; fail=$((fail+1)); fi; }

REPO_SLUG="2746267826/pim-web"
ORIGIN_URL="https://github.com/${REPO_SLUG}.git"

# --- stub gh ----------------------------------------------------------------
mkdir -p "$TMP/bin"
cat > "$TMP/bin/gh" <<'STUB'
#!/usr/bin/env bash
echo "$*" >> "$GH_LOG"
prev=""
for a in "$@"; do
  if [ "$prev" = "--body-file" ]; then cp "$a" "$GH_BODY_COPY" 2>/dev/null || true; fi
  prev="$a"
done
case "${1:-} ${2:-}" in
  "pr list")
    [ "${GH_LIST_FAIL:-0}" = "1" ] && { echo "gh: 查询失败（stub）" >&2; exit 1; }
    [ -n "${GH_PR_NUMBER:-}" ] && echo "$GH_PR_NUMBER"
    exit 0 ;;
  "pr create")
    [ "${GH_CREATE_FAIL:-0}" = "1" ] && { echo "gh: 创建失败（stub）" >&2; exit 1; }
    echo "https://example.invalid/pr/1"; exit 0 ;;
  "pr edit") exit 0 ;;
esac
exit 0
STUB
chmod +x "$TMP/bin/gh"

# --- 真 git fixture：bare 仓当 origin，但 origin 的 URL 仍是 github 形式 -----
git init -q --bare "$TMP/origin.git"
git init -q "$TMP/work"
WORK="$TMP/work"
G="git -C $WORK"
$G config user.email "dev@pim.local"
$G config user.name "PIM Dev"
$G config commit.gpgsign false
$G config "url.$TMP/origin.git.insteadOf" "$ORIGIN_URL"
mkdir -p "$WORK/contract" "$WORK/client-web/src/api"
printf '{\n  "openapi": "3.0.1",\n  "paths": {"/a": {}}\n}\n' > "$WORK/contract/openapi.json"
OLD_SHA="$(sha256sum "$WORK/contract/openapi.json" | cut -d' ' -f1)"
cat > "$WORK/contract/provenance.json" <<EOF
{
  "schemaVersion": 1,
  "repo": "2746267826/pim-api",
  "ref": "master",
  "resolvedRef": "4088bc446d4d98bbe20bee8b9241be2dfeabe78f",
  "resolvedRefSource": "git-ls-remote",
  "sourcePath": "contract/openapi.json",
  "bytes": $(wc -c < "$WORK/contract/openapi.json" | tr -d ' '),
  "sha256": "$OLD_SHA"
}
EOF
echo "// generated" > "$WORK/client-web/src/api/generated.ts"
$G add -A && $G commit -qm "init"
$G branch -M master
$G remote add origin "$ORIGIN_URL"
$G push -q -u origin master

BRANCH="contract-sync-linux/refresh-contract"
PROBE_REF="ae3ed39e2827d1665ce9801e65f71bdea6663d75"
REF_SEEN="$TMP/ref_seen.txt"

export GH_LOG="$TMP/gh.log"; : > "$GH_LOG"
export GH_BODY_COPY="$TMP/pr_body.txt"; : > "$GH_BODY_COPY"
export GITHUB_REPOSITORY="$REPO_SLUG"
export GH_TOKEN="ghs_secret_installation_token_do_not_leak"
export PATH="$TMP/bin:$PATH"

# 模拟一次真实的"刷新"：快照 + provenance + 生成物一起变（生成物依赖快照 sha256），
# 并记录刷新命令看到的 PIM_CONTRACT_REF。MARKER 决定内容，方便造出"又变了"的场景。
REFRESH_TEMPLATE='printf "{\n  \"openapi\": \"3.0.1\",\n  \"paths\": {\"/MARKER\": {}}\n}\n" > contract/openapi.json
NEW_SHA=$(sha256sum contract/openapi.json | cut -d" " -f1)
NEW_BYTES=$(wc -c < contract/openapi.json | tr -d " ")
printf "{\n  \"schemaVersion\": 1,\n  \"repo\": \"2746267826/pim-api\",\n  \"ref\": \"master\",\n  \"resolvedRef\": \"4088bc446d4d98bbe20bee8b9241be2dfeabe78f\",\n  \"resolvedRefSource\": \"git-ls-remote\",\n  \"sourcePath\": \"contract/openapi.json\",\n  \"bytes\": %s,\n  \"sha256\": \"%s\"\n}\n" "$NEW_BYTES" "$NEW_SHA" > contract/provenance.json
printf "// generated %s" "$NEW_SHA" > client-web/src/api/generated.ts
printf "%s" "$PIM_CONTRACT_REF" > "@REFSEEN@"
'
refresh_cmd() { printf '%s' "$REFRESH_TEMPLATE" | sed "s/MARKER/$1/; s#@REFSEEN@#$REF_SEEN#"; }

RC=0
# 每个用例前把 fixture 工作区复位：脚本在"sha256 不一致"这类失败路径上会留下
# 未提交的刷新产物，不复位会让下一个用例撞上"工作区不干净"的前置检查。
reset_work() { git -C "$WORK" checkout -q -f HEAD >/dev/null 2>&1 || true; git -C "$WORK" clean -qfdx >/dev/null 2>&1 || true; }
run() { reset_work; run_no_reset "$@"; }
run_no_reset() {
  set +e
  OUT="$(cd "$WORK" && GITHUB_OUTPUT="$TMP/out.txt" bash "$SCRIPT" "$@" 2>&1)"
  RC=$?
  set -e
}
result() { sed -n 's/^result=//p' "$TMP/out.txt" 2>/dev/null; }
branch_sha() { git -C "$TMP/origin.git" rev-parse --verify --quiet "refs/heads/$BRANCH" || echo none; }
master_sha() { git -C "$TMP/origin.git" rev-parse refs/heads/master; }

echo "== contract-sync-pr.sh =="

# --- 1) 首次刷新 → 推分支 + 开 PR -------------------------------------------
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "$(refresh_cmd one)" --contract-ref "$PROBE_REF"
check "首次刷新 → 退出码 0" "0" "$RC"
check "首次刷新 → result=pr-opened" "pr-opened" "$(result)"
check "首次刷新 → 刷新命令收到的是通知里的 ref（不被默认 master 覆盖）" "$PROBE_REF" "$(cat "$REF_SEEN" 2>/dev/null)"
contains "脚本声明的工作仓就是 fixture（防止跑错仓）" "工作仓：$WORK" "$OUT"
check "首次刷新 → 分支真的推到 origin 了" "ok" "$([ "$(branch_sha)" != "none" ] && echo ok || echo none)"
ARTIFACT_RE='^(contract/openapi.json|contract/provenance.json|client-web/src/api/generated.ts)$'
check "首次刷新 → 提交里只有契约产物，没有无关文件" "0" \
  "$(git -C "$TMP/origin.git" show --name-only --format= "refs/heads/$BRANCH" | grep -vE "$ARTIFACT_RE" | grep -c . || true)"
contains "PR 标题双语（含中文）" "刷新契约快照" "$(grep 'pr create' "$GH_LOG")"
contains "PR 标题双语（含英文）" "refresh contract snapshot" "$(grep 'pr create' "$GH_LOG")"
contains "PR 基于 master、head 是刷新分支" "--base master --head $BRANCH" "$(grep 'pr create' "$GH_LOG")"
BODY="$(cat "$GH_BODY_COPY")"
contains "正文四段：技术修改 / Technical changes" "## 技术修改 / Technical changes" "$BODY"
contains "正文四段：功能变化 / Feature changes" "## 功能变化 / Feature changes" "$BODY"
contains "正文四段：如何体验 / How to try it" "## 如何体验 / How to try it" "$BODY"
contains "正文四段：测试 / Tests" "## 测试 / Tests" "$BODY"
contains "正文写清了来源 ref" "4088bc446d4d98bbe20bee8b9241be2dfeabe78f" "$BODY"
contains "正文不提本仓不存在的门禁" "check-contract-freshness.sh" "$BODY"
not_contains "正文不引用不存在的 check-contract-kotlin.sh" "check-contract-kotlin.sh" "$BODY"
not_contains "token 不出现在任何输出里" "$GH_TOKEN" "$OUT"
not_contains "token 不写进 PR 正文" "$GH_TOKEN" "$BODY"

# --- 2) 幂等：同一份内容重复触发 → 不空提交、不重复开 PR ---------------------
export GH_PR_NUMBER=1
BEFORE="$(branch_sha)"
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "true"
check "幂等重跑 → 退出码 0" "0" "$RC"
check "幂等重跑 → result=pr-exists" "pr-exists" "$(result)"
check "幂等重跑 → 分支没有被多推一个提交" "$BEFORE" "$(branch_sha)"
check "幂等重跑 → 没有再调 pr create" "0" "$(grep -c 'pr create' "$GH_LOG" || true)"

# --- 3) 推送成功但开 PR 失败 → 重跑必须把 PR 补出来 -------------------------
export GH_PR_NUMBER=""
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "true"
check "分支有未合并内容且没有 PR → 补开 PR" "pr-opened" "$(result)"
check "  → 补开时没有产生新提交" "$BEFORE" "$(branch_sha)"

# --- 4) pr create 失败：整体失败；恢复后重跑能补开 ---------------------------
export GH_CREATE_FAIL=1
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "$(refresh_cmd two)"
check "pr create 失败 → 退出码非 0" "1" "$RC"
PUSHED_SHA="$(branch_sha)"
check "  → 但分支已经推上去了（内容不丢）" "ok" "$([ "$PUSHED_SHA" != "$BEFORE" ] && echo ok || echo none)"
export GH_CREATE_FAIL=0
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "true"
check "恢复后重跑 → 把上次没开出来的 PR 补开" "pr-opened" "$(result)"
check "  → 没有重复推送（分支 SHA 不变）" "$PUSHED_SHA" "$(branch_sha)"

# --- 5) master 前进后，刷新基于最新 base 重建（不是旧分支的 HEAD）-----------
git -C "$TMP/origin.git" clone -q "$TMP/origin.git" "$TMP/upstream-work"
UP="$TMP/upstream-work"
git -C "$UP" config user.email up@pim.local
git -C "$UP" config user.name "Upstream"
echo "# master moved on" > "$UP/README.md"
git -C "$UP" add -A && git -C "$UP" commit -qm "master moves on"
git -C "$UP" push -q origin master
NEW_MASTER="$(master_sha)"
export GH_PR_NUMBER=""
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "$(refresh_cmd three)"
check "master 前进后 → 退出码 0" "0" "$RC"
check "master 前进后 → result=pr-opened（内容变了，重新开）" "pr-opened" "$(result)"
check "master 前进后 → 刷新分支的父提交就是最新 master（吸收了最新 base）" "$NEW_MASTER" \
  "$(git -C "$TMP/origin.git" rev-parse "refs/heads/$BRANCH^")"
check "master 前进后 → 提交里仍然只有契约产物（没把 master 的 README 混进来）" "0" \
  "$(git -C "$TMP/origin.git" show --name-only --format= "refs/heads/$BRANCH" | grep -vE "$ARTIFACT_RE" | grep -c . || true)"

# --- 6) 已有 PR 时推送 → 同时更新标题与正文（不是只推代码）------------------
export GH_PR_NUMBER=7
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "$(refresh_cmd four)"
check "已有 PR 且推送 → result=pr-updated" "pr-updated" "$(result)"
contains "  → 调用了 gh pr edit 更新元数据" "pr edit 7" "$(grep 'pr edit' "$GH_LOG")"

# --- 7) 通知里的 sha256 对不上 → 不提交、不开 PR ----------------------------
export GH_PR_NUMBER=""
BEFORE="$(branch_sha)"
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "$(refresh_cmd five)" --contract-ref "$PROBE_REF" --payload-sha256 "$(printf 'b%.0s' {1..64})"
check "sha256 不一致 → 退出码 1" "1" "$RC"
contains "sha256 不一致 → 明确报出原因" "不一致" "$OUT"
check "sha256 不一致 → 分支没动" "$BEFORE" "$(branch_sha)"
check "sha256 不一致 → 没开 PR" "0" "$(grep -c 'pr create' "$GH_LOG" || true)"

# --- 8) 脏工作区 → 拒绝执行（不许把无关文件带进刷新 PR）---------------------
echo "unrelated" > "$WORK/unrelated.txt"
: > "$TMP/out.txt"; : > "$GH_LOG"
run_no_reset --refresh-cmd "true"
check "脏工作区 → 退出码 2" "2" "$RC"
contains "  → 报出工作区不干净" "工作区不干净" "$OUT"
rm -f "$WORK/unrelated.txt"

# --- 9) origin 与 GITHUB_REPOSITORY 不一致 → 拒绝执行 -----------------------
$G remote set-url origin "https://github.com/2746267826/some-other-repo.git"
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "true"
check "origin 与 GITHUB_REPOSITORY 不一致 → 退出码 2" "2" "$RC"
contains "  → 报出不一致" "不一致" "$OUT"
$G remote set-url origin "$ORIGIN_URL"

# --- 10) gh 查询失败 → 不当成"没有 PR"，直接失败 ----------------------------
export GH_LIST_FAIL=1
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "true"
check "gh pr list 失败 → 退出码 1（不猜）" "1" "$RC"
export GH_LIST_FAIL=0

# --- 11) 参数/配置错误 → 退出码 2 -------------------------------------------
run --branch "auto/contract-refresh" --refresh-cmd "true"
check "分支名不合规（会被 pr-metadata 拒）→ 退出码 2" "2" "$RC"
contains "  → 提示 pr-metadata 门禁" "pr-metadata" "$OUT"
run --refresh-cmd "true" --payload-sha256 "nothex"
check "坏 sha256 形参 → 退出码 2" "2" "$RC"
reset_work
set +e
OUT="$(cd "$WORK" && env -u GH_TOKEN GITHUB_REPOSITORY="$GITHUB_REPOSITORY" bash "$SCRIPT" --refresh-cmd "true" 2>&1)"
RC=$?
set -e
check "缺 GH_TOKEN → 退出码 2" "2" "$RC"
contains "  → 提示走 secrets（别用默认 GITHUB_TOKEN）" "GITHUB_TOKEN" "$OUT"

echo ""
echo "contract-sync-pr: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
