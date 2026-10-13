#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：契约同步 PR 脚本（离线）。
#
# 用**真 git**（本地 bare 仓当 origin）+ **stub gh** 验证：
#   * 真的推了一个符合 {agent}-{os}/{topic} 的分支，提交里带了刷新后的产物；
#   * PR 标题双语、正文四段（AGENTS.md / pr-metadata 门禁的要求）；
#   * 幂等：同一份契约重复触发不产生空提交、不重复开 PR；
#   * 通知里的 sha256 对不上时不提交、不开 PR（fail-closed）；
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

# --- stub gh ----------------------------------------------------------------
mkdir -p "$TMP/bin"
cat > "$TMP/bin/gh" <<'STUB'
#!/usr/bin/env bash
# 记录调用参数；模拟 gh 的两种用法。
echo "$*" >> "$GH_LOG"
# 复制 --body-file 的内容，供测试断言四段正文
prev=""
for a in "$@"; do
  if [ "$prev" = "--body-file" ]; then cp "$a" "$GH_BODY_COPY" 2>/dev/null || true; fi
  prev="$a"
done
if [ "${1:-} ${2:-}" = "pr list" ]; then echo "${GH_PR_COUNT:-0}"; exit 0; fi
if [ "${1:-} ${2:-}" = "pr create" ]; then echo "0"; echo "https://example.invalid/pr/1"; exit 0; fi
echo ""; exit 0
STUB
chmod +x "$TMP/bin/gh"

# --- 真 git fixture：bare 仓当 origin ---------------------------------------
git init -q --bare "$TMP/origin.git"
git init -q "$TMP/work"
WORK="$TMP/work"
G="git -C $WORK"
$G config user.email "dev@pim.local"
$G config user.name "PIM Dev"
$G config commit.gpgsign false
mkdir -p "$WORK/contract" "$WORK/client-web/src/api"
printf '{\n  "openapi": "3.0.1",\n  "paths": {"/a": {}}\n}\n' > "$WORK/contract/openapi.json"
write_prov() { # <sha256> <bytes>
  cat > "$WORK/contract/provenance.json" <<EOF
{
  "schemaVersion": 1,
  "repo": "2746267826/pim-api",
  "ref": "master",
  "resolvedRef": "4088bc446d4d98bbe20bee8b9241be2dfeabe78f",
  "resolvedRefSource": "git-ls-remote",
  "sourcePath": "contract/openapi.json",
  "bytes": $2,
  "sha256": "$1"
}
EOF
}
OLD_SHA="$(sha256sum "$WORK/contract/openapi.json" | cut -d' ' -f1)"
write_prov "$OLD_SHA" "$(wc -c < "$WORK/contract/openapi.json" | tr -d ' ')"
echo "// generated" > "$WORK/client-web/src/api/generated.ts"
$G add -A && $G commit -qm "init"
$G branch -M master
$G remote add origin "$TMP/origin.git"
$G push -q -u origin master
$G checkout -q -b work-master

BRANCH="contract-sync-linux/refresh-contract"
export GH_LOG="$TMP/gh.log"; : > "$GH_LOG"
export GH_BODY_COPY="$TMP/pr_body.txt"; : > "$GH_BODY_COPY"
export GITHUB_REPOSITORY="2746267826/pim-web"
export GH_TOKEN="ghs_secret_installation_token_do_not_leak"
export PATH="$TMP/bin:$PATH"

# 模拟一次真实的"刷新"：快照 + provenance + 生成物一起变（生成物依赖快照 sha256）
REFRESH_NEW='printf "{\n  \"openapi\": \"3.0.1\",\n  \"paths\": {\"/a\": {}, \"/b\": {}}\n}\n" > contract/openapi.json; '
REFRESH_NEW+='NEW_SHA=$(sha256sum contract/openapi.json | cut -d" " -f1); NEW_BYTES=$(wc -c < contract/openapi.json | tr -d " "); '
REFRESH_NEW+='printf "{\n  \"schemaVersion\": 1,\n  \"repo\": \"2746267826/pim-api\",\n  \"ref\": \"master\",\n  \"resolvedRef\": \"4088bc446d4d98bbe20bee8b9241be2dfeabe78f\",\n  \"resolvedRefSource\": \"git-ls-remote\",\n  \"sourcePath\": \"contract/openapi.json\",\n  \"bytes\": %s,\n  \"sha256\": \"%s\"\n}\n" "$NEW_BYTES" "$NEW_SHA" > contract/provenance.json; '
REFRESH_NEW+='printf "// generated %s" "$NEW_SHA" > client-web/src/api/generated.ts'
REFRESH_NOOP='true'

RC=0
run() {
  set +e
  OUT="$(cd "$WORK" && GITHUB_OUTPUT="$TMP/out.txt" bash "$SCRIPT" "$@" 2>&1)"
  RC=$?
  set -e
}

echo "== contract-sync-pr.sh =="

# --- 1) 首次刷新 → 推分支 + 开 PR -------------------------------------------
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "$REFRESH_NEW"
check "首次刷新 → 退出码 0" "0" "$RC"
check "首次刷新 → result=pr-opened" "pr-opened" "$(sed -n 's/^result=//p' "$TMP/out.txt")"
check "首次刷新 → 分支名符合 {agent}-{os}/{topic}" "$BRANCH" "$(sed -n 's/^branch=//p' "$TMP/out.txt")"
contains "脚本声明的工作仓就是 fixture（防止跑错仓）" "工作仓：$WORK" "$OUT"
PUSHED="$(git -C "$TMP/origin.git" rev-parse --verify --quiet "refs/heads/$BRANCH" || echo none)"
check "首次刷新 → 分支真的推到 origin 了" "ok" "$([ "$PUSHED" != "none" ] && echo ok || echo none)"
check "首次刷新 → origin 上的分支包含 3 个刷新后的产物" "3" \
  "$(git -C "$TMP/origin.git" show --stat --oneline "refs/heads/$BRANCH" | grep -cE 'contract/openapi.json|contract/provenance.json|client-web/src/api/generated.ts')"
contains "PR 标题双语（含中文）" "刷新契约快照" "$(grep 'pr create' "$GH_LOG")"
contains "PR 标题双语（含英文）" "refresh contract snapshot" "$(grep 'pr create' "$GH_LOG")"
contains "PR 基于 master、head 是刷新分支" "--base master --head $BRANCH" "$(grep 'pr create' "$GH_LOG")"
BODY="$(cat "$GH_BODY_COPY")"
contains "正文四段：技术修改 / Technical changes" "## 技术修改 / Technical changes" "$BODY"
contains "正文四段：功能变化 / Feature changes" "## 功能变化 / Feature changes" "$BODY"
contains "正文四段：如何体验 / How to try it" "## 如何体验 / How to try it" "$BODY"
contains "正文四段：测试 / Tests" "## 测试 / Tests" "$BODY"
contains "正文写清了来源 ref" "4088bc446d4d98bbe20bee8b9241be2dfeabe78f" "$BODY"
not_contains "token 不出现在任何输出里" "$GH_TOKEN" "$OUT"
not_contains "token 不写进 PR 正文" "$GH_TOKEN" "$BODY"

# --- 2) 幂等：再跑一次（无差异）→ 不空提交、不重复开 PR，且报告已存在的 PR --
export GH_PR_COUNT=1
BEFORE_SHA="$(git -C "$TMP/origin.git" rev-parse "refs/heads/$BRANCH")"
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "$REFRESH_NOOP"
check "幂等重跑 → 退出码 0" "0" "$RC"
check "幂等重跑 → result=pr-exists" "pr-exists" "$(sed -n 's/^result=//p' "$TMP/out.txt")"
check "幂等重跑 → 分支没有被多推一个提交" "$BEFORE_SHA" "$(git -C "$TMP/origin.git" rev-parse "refs/heads/$BRANCH")"
check "幂等重跑 → 没有再调 pr create" "0" "$(grep -c 'pr create' "$GH_LOG" || true)"

# --- 3) 通知里的 sha256 对不上 → 不提交、不开 PR ----------------------------
export GH_PR_COUNT=0
BEFORE_SHA="$(git -C "$TMP/origin.git" rev-parse "refs/heads/$BRANCH")"
: > "$TMP/out.txt"; : > "$GH_LOG"
run --refresh-cmd "$REFRESH_NEW" --payload-sha256 "$(printf 'b%.0s' {1..64})"
check "sha256 不一致 → 退出码 1" "1" "$RC"
contains "sha256 不一致 → 明确报出原因" "不一致" "$OUT"
check "sha256 不一致 → 分支没动" "$BEFORE_SHA" "$(git -C "$TMP/origin.git" rev-parse "refs/heads/$BRANCH")"
check "sha256 不一致 → 没开 PR" "0" "$(grep -c 'pr create' "$GH_LOG" || true)"

# --- 4) 参数/配置错误 → 退出码 2 -------------------------------------------
run --branch "auto/contract-refresh" --refresh-cmd "$REFRESH_NOOP"
check "分支名不合规（会被 pr-metadata 拒）→ 退出码 2" "2" "$RC"
contains "  → 提示 pr-metadata 门禁" "pr-metadata" "$OUT"
run --refresh-cmd "$REFRESH_NOOP" --payload-sha256 "nothex"
check "坏 sha256 形参 → 退出码 2" "2" "$RC"
set +e
OUT="$(cd "$WORK" && env -u GH_TOKEN GITHUB_REPOSITORY="$GITHUB_REPOSITORY" bash "$SCRIPT" --refresh-cmd "$REFRESH_NOOP" 2>&1)"
RC=$?
set -e
check "缺 GH_TOKEN → 退出码 2" "2" "$RC"
contains "  → 提示走 secrets（别用默认 GITHUB_TOKEN）" "GITHUB_TOKEN" "$OUT"

echo ""
echo "contract-sync-pr: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
