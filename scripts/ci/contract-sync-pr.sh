#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约同步「一步到底」：刷新 → 提交 → 推送分支 → 开/更新 PR。
#
# 为什么要有这个脚本（#415 §5）：刷新契约**不是改一个 JSON**。契约生成物里嵌有
# 快照的 sha256，所以快照一变，生成物必须重新生成，provenance 也要同步更新；
# 只通知"刷新快照"会在生成物那一步卡住，一致性门禁照样红。因此整个刷新动作
# 必须脚本化、一步到底，并且**能被 CI / 自动化调用**（本脚本就是那个入口）。
#
# 为什么用 App installation token 推分支、开 PR（而不是默认的 GITHUB_TOKEN）：
# GitHub 的防递归机制决定「用 GITHUB_TOKEN 推的分支不会再触发后续工作流」，
# 刷新 PR 上会没有任何检查结果，而本仓规定必须 CI 绿才能合。脚本只从环境变量
# GH_TOKEN 读 token —— 不进 argv、不落盘、任何输出里都不回显。
#
# 幂等：同一份契约重复触发 → 分支上无差异 → 不产生新提交、不动 PR。
#
# 用法（CI 由 .github/workflows/contract-sync.yml 调用）：
#   GH_TOKEN=<installation token> scripts/ci/contract-sync-pr.sh \
#     [--branch contract-sync-linux/refresh-contract] [--base master] \
#     [--contract-ref <40hex|branch|tag>] [--payload-sha256 <64hex>] \
#     [--refresh-cmd "<刷新命令>"]
#
# --contract-ref 会被导出成 PIM_CONTRACT_REF 交给刷新命令（默认 master）。
# **刷新只在这里做一次**：通知里带的是"哪个 commit 的契约"，如果刷新之后再跑一遍
# 不带 ref 的默认刷新，快照会被刷回"当时的 master" —— 轻则与通知里的 sha256 对不上
# （本脚本会直接失败），重则静默刷成另一个版本。
#
# 退出码：0 = 完成（开/更新了 PR，或本来就没有差异）；1 = 失败；2 = 参数/配置错误。
# ---------------------------------------------------------------------------
set -euo pipefail

# 操作对象是**当前工作目录所在的仓库**（CI 里 run 步骤的 cwd 就是工作区根），
# 不是脚本文件所在的位置：脚本被拷到别处执行时，也不该去动另一个仓库。
# 这里刻意不提供"改根目录"的环境变量开关 —— 那种开关一旦被误设，
# 脚本就会在另一个仓里提交/推送，代价太大。测试的做法是在 fixture 目录里执行。
ROOT="$(git rev-parse --show-toplevel 2>/dev/null || true)"
[ -n "$ROOT" ] || { echo "[contract-sync][错误] 当前目录不在 git 仓库里（先 cd 到仓库工作区）" >&2; exit 2; }
cd "$ROOT"

BRANCH="contract-sync-linux/refresh-contract"
BASE_BRANCH="master"
# 本仓的刷新是「快照 + provenance + 由仓内锁定生成器产出的 TS 类型」三个产物，
# 由 fetch-contract.sh 一步写出（它自己就是"全成功才落盘"的语义）。
REFRESH_CMD="bash scripts/ci/fetch-contract.sh"
PAYLOAD_SHA256=""
# 契约来源 ref（默认 master）；会被导出成 PIM_CONTRACT_REF 交给刷新命令。
CONTRACT_REF="master"
# 生成物在仓库里的相对路径（PR 正文里列出受影响文件时用）。
ARTIFACT_PATHS=("contract/openapi.json" "contract/provenance.json" "client-web/src/api/generated.ts")

usage_err() { echo "[contract-sync][错误] $*" >&2; exit 2; }
log() { echo "[contract-sync] $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --branch) BRANCH="${2:-}"; shift 2 ;;
    --base) BASE_BRANCH="${2:-}"; shift 2 ;;
    --refresh-cmd) REFRESH_CMD="${2:-}"; shift 2 ;;
    --payload-sha256) PAYLOAD_SHA256="${2:-}"; shift 2 ;;
    --contract-ref) CONTRACT_REF="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) usage_err "未知参数：$1" ;;
  esac
done

[ -n "$BRANCH" ] || usage_err "--branch 不能为空"
[ -n "$BASE_BRANCH" ] || usage_err "--base 不能为空"
[ -n "$REFRESH_CMD" ] || usage_err "--refresh-cmd 不能为空"
[ -n "$CONTRACT_REF" ] || usage_err "--contract-ref 不能为空"
[[ -z "$PAYLOAD_SHA256" || "$PAYLOAD_SHA256" =~ ^[0-9a-f]{64}$ ]] \
  || usage_err "--payload-sha256 必须是 64 位小写十六进制"
# 分支名必须能过各客户端仓的 pr-metadata 门禁：{agent}-{os}/{topic}
[[ "$BRANCH" =~ ^[a-z0-9]+(-[a-z0-9]+)*-(win|linux)/[a-z0-9][a-z0-9.-]*$ ]] \
  || usage_err "--branch 不符合 {agent}-{os}/{topic}（pr-metadata 门禁会拒）：'$BRANCH'"

GH_TOKEN_VALUE="${GH_TOKEN:-}"
[ -n "$GH_TOKEN_VALUE" ] || usage_err "环境变量 GH_TOKEN 必填（App installation token，走 secrets；勿硬编码、勿用默认 GITHUB_TOKEN）"
REPO_SLUG="${GITHUB_REPOSITORY:-}"
[ -n "$REPO_SLUG" ] || usage_err "环境变量 GITHUB_REPOSITORY 必填（owner/repo）"

command -v gh >/dev/null 2>&1 || usage_err "缺少 gh CLI"

log "工作仓：$ROOT（repo=$REPO_SLUG，分支=$BRANCH，基线=$BASE_BRANCH）"

# --- 1) 把工作区放到刷新分支上（不碰 master，也不碰别人的分支） -------------
if git ls-remote --exit-code --heads origin "$BRANCH" >/dev/null 2>&1; then
  log "刷新分支 $BRANCH 已存在 → 在其最新提交上增量刷新（保持 push 是 fast-forward）"
  git fetch --depth=1 origin "$BRANCH" >/dev/null 2>&1
  git checkout -B "$BRANCH" FETCH_HEAD >/dev/null 2>&1
else
  log "刷新分支 $BRANCH 不存在 → 从当前 ref 新建"
  git checkout -B "$BRANCH" >/dev/null 2>&1
fi

# --- 2) 刷新（快照 + 生成物 + provenance，一步到底） ------------------------
log "执行刷新：$REFRESH_CMD（PIM_CONTRACT_REF=$CONTRACT_REF）"
# 只在这里刷一次；ref 通过环境变量交给刷新脚本（它自己的语义就是"取该 ref 的字节"）。
PIM_CONTRACT_REF="$CONTRACT_REF" bash -c "$REFRESH_CMD"

# --- 3) 通知里带了 sha256 就必须对得上（防"刷出来一份不是它通知的那份"）----
if [ -n "$PAYLOAD_SHA256" ]; then
  ACTUAL="$(sha256sum contract/openapi.json | cut -d' ' -f1)"
  if [ "$ACTUAL" != "$PAYLOAD_SHA256" ]; then
    echo "[contract-sync][错误] 刷新得到的快照 sha256=$ACTUAL，与通知里的 $PAYLOAD_SHA256 不一致；不提交、不开 PR" >&2
    exit 1
  fi
  log "快照 sha256 与通知一致：$ACTUAL"
fi

# --- 4) 有没有差异 ----------------------------------------------------------
git add -A
CHANGED="true"
if git diff --cached --quiet; then CHANGED="false"; fi

RESOLVED_REF=""
BYTES=""
SHA256=""
if [ -f contract/provenance.json ]; then
  RESOLVED_REF="$(python3 -c "import json;print(json.load(open('contract/provenance.json'))['resolvedRef'])" 2>/dev/null || true)"
  BYTES="$(python3 -c "import json;print(json.load(open('contract/provenance.json'))['bytes'])" 2>/dev/null || true)"
  SHA256="$(python3 -c "import json;print(json.load(open('contract/provenance.json'))['sha256'])" 2>/dev/null || true)"
fi
SHORT_REF="${RESOLVED_REF:0:7}"
[ -n "$SHORT_REF" ] || SHORT_REF="${CONTRACT_REF:0:7}"

# --- 5) 分支名/标题/正文都按 AGENTS.md 的 PR 约定生成（pr-metadata 门禁会查）-
TITLE="chore(contract): 刷新契约快照至 pim-api@${SHORT_REF} / refresh contract snapshot"
BODY_FILE="$(mktemp "${TMPDIR:-/tmp}/pim-contract-sync-body.XXXXXX")"
trap 'rm -f "$BODY_FILE"' EXIT

FILE_LIST=""
for p in "${ARTIFACT_PATHS[@]}"; do
  [ -e "$p" ] && FILE_LIST="${FILE_LIST}- \`${p}\`
"
done
[ -n "$FILE_LIST" ] || FILE_LIST="- （无生成物路径记录）
"

cat > "$BODY_FILE" <<EOF
本 PR 由 \`.github/workflows/contract-sync.yml\` 自动创建，**请人工 review 后再合并**（不自动合并）。

## 技术修改 / Technical changes

- 契约快照同步到上游 \`2746267826/pim-api\` 的 \`${RESOLVED_REF:-<未知>}\`（${BYTES:-?} 字节，sha256 \`${SHA256:-?}\`；请求的 ref：\`${CONTRACT_REF}\`）。
- 受影响文件：
${FILE_LIST}- 快照与生成物**同时**刷新：生成物里嵌有快照的 sha256，只换快照不重新生成会被本仓的一致性门禁判红。
- 来源记录写在 \`contract/provenance.json\`（无时间戳，可复现）。

## 功能变化 / Feature changes

无 / None —— 本次只同步接口契约快照与由它生成的类型，不改产品行为。

## 如何体验 / How to try it

1. 打开本 PR 的 Files changed，确认契约差异与上游这次接口变化一致、没有与本仓无关的改动。
2. 切到本分支本地跑一次只读门禁（不改仓）：
   \`scripts/ci/check-contract-freshness.sh\` 与 \`scripts/ci/check-contract-kotlin.sh\`（若本仓有生成物）。
3. 合并后，客户端代码即可按新契约里的字段/端点开发；契约里没有的东西仍然不许猜。

## 测试 / Tests

自动化（本 PR 上由 CI 执行，必须全绿才能合）：

- \`Contract freshness\` / \`Verify client contract is up to date\`：把仓内快照与上游该 ref 的字节**逐字节**比较。
- 生成物一致性门禁：用仓内快照重新生成一次，与随提交进仓的生成物**逐字节**比较。
- 本仓常规构建与单测。

> 说明：本 PR 由机器人创建，正文与分支名按 \`AGENTS.md\` 的约定生成（双语标题、四段正文、\`{agent}-{os}/{topic}\` 分支名），以便通过 \`pr-metadata\` 门禁。
EOF

# --- 6) 提交 + 推送 ---------------------------------------------------------
if [ "$CHANGED" = "false" ]; then
  log "快照与生成物已是最新（相对 $BRANCH 无差异）→ 不产生提交、不动 PR"
else
  git config user.name "pim-contract-sync[bot]" >/dev/null
  git config user.email "pim-contract-sync[bot]@users.noreply.github.com" >/dev/null
  git commit -q -m "chore(contract): 刷新契约快照至 pim-api@${SHORT_REF} / refresh contract snapshot

由 .github/workflows/contract-sync.yml 自动生成（pim-api 契约变更通知）。
快照：${BYTES:-?} 字节，sha256 ${SHA256:-?}，来源 2746267826/pim-api@${RESOLVED_REF:-?}。" \
    -m "Refs 2746267826/pim-api#415"
  log "已提交：$(git rev-parse --short HEAD)"
  git push origin "HEAD:refs/heads/${BRANCH}"
  log "已推送分支 $BRANCH"
fi

# --- 7) 开 PR / 更新已存在的 PR ---------------------------------------------
EXISTING="$(GH_TOKEN="$GH_TOKEN_VALUE" gh pr list --repo "$REPO_SLUG" --head "$BRANCH" --state open --json number --jq 'length' 2>/dev/null || echo 0)"
[ -n "$EXISTING" ] || EXISTING="0"
if [ "$CHANGED" = "false" ]; then
  if [ "$EXISTING" != "0" ]; then
    RESULT="pr-exists"
    log "无差异；已开的刷新 PR 保持不变（幂等：同一份契约重复触发不会产生空提交）"
  else
    RESULT="no-change"
    log "无差异且没有已开的 PR → 什么都不做"
  fi
elif [ "$EXISTING" != "0" ]; then
  RESULT="pr-updated"
  log "已存在刷新的 PR（$EXISTING 个）→ 本次提交已更新它"
else
  GH_TOKEN="$GH_TOKEN_VALUE" gh pr create \
    --repo "$REPO_SLUG" \
    --base "$BASE_BRANCH" \
    --head "$BRANCH" \
    --title "$TITLE" \
    --body-file "$BODY_FILE" >/dev/null
  RESULT="pr-opened"
  log "已创建刷新 PR：$TITLE"
fi

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  {
    echo "result=${RESULT}"
    echo "branch=${BRANCH}"
    echo "resolved_ref=${RESOLVED_REF}"
    echo "sha256=${SHA256}"
    echo "bytes=${BYTES}"
  } >> "$GITHUB_OUTPUT"
fi
log "完成：result=$RESULT branch=$BRANCH resolvedRef=${RESOLVED_REF:-<unknown>}"
