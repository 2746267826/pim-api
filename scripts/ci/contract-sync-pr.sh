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
# 几条刻意做成 fail-closed 的前置条件（都被独立 review 指出过）：
#   * 工作区必须是干净的：脚本只暂存**契约产物路径**，但脏工作区本身说明调用方式不对，
#     宁可拒绝执行，也不要把无关文件带进刷新 PR。
#   * `origin` 必须就是 `GITHUB_REPOSITORY`：脚本按 cwd 定位仓库，若两者不一致，
#     就会在另一个仓里提交和推送。
#   * 刷新永远基于**最新的 base**（而不是上一次那条刷新分支的 HEAD）：否则新通知会在
#     旧脚本/旧生成器上跑，产物可能与当前 master 的规则不一致。
#   * 幂等靠**内容**而不是"本次有没有新提交"：内容相同就不重复提交/推送；
#     内容不同就重建到最新 base 上再推（force-with-lease，只动机器人自己的分支）。
#
# 退出码：0 = 完成（开/更新/复用 PR，或本来就没有差异）；1 = 失败；2 = 参数或前置条件错误。
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
# 产物路径：只暂存这些路径，绝不 `git add -A`。
ARTIFACT_PATHS=("contract/openapi.json" "contract/provenance.json" "client-web/src/api/generated.ts")
# PR 正文「测试」一节里逐条列出的、真正跑在本 PR 上的门禁（各仓不同，不要照抄别的仓）。
GATE_NOTES="$(cat <<'NOTES'
- `Contract freshness`（在本仓由 `build-web` 里的 `Verify client contract is up to date` 执行）：
  把仓内快照与**上游 `master` 当时的**字节**逐字节**比较，并用仓内锁定的 `openapi-typescript`
  从本仓快照重新生成一次，与随提交进仓的 `client-web/src/api/generated.ts` **逐字节**比较。
NOTES
)"

usage_err() { echo "[contract-sync][错误] $*" >&2; exit 2; }
log() { echo "[contract-sync] $*"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --branch) BRANCH="${2:-}"; shift 2 ;;
    --base) BASE_BRANCH="${2:-}"; shift 2 ;;
    --contract-ref) CONTRACT_REF="${2:-}"; shift 2 ;;
    --payload-sha256) PAYLOAD_SHA256="${2:-}"; shift 2 ;;
    --refresh-cmd) REFRESH_CMD="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,45p' "${BASH_SOURCE[0]}"; exit 0 ;;
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

# --- 前置 1：远端目标必须就是本仓（防止"校验通过却推到别处"）--------------
# 三种绕过向量都要挡：
#   1) `remote.origin.pushurl` 指向别的仓（可以有多条）；
#   2) `url.*.insteadOf` / `url.*.pushInsteadOf` 在传输层把目标重写到别的仓
#      —— 只看原始配置是**看不出**来的，必须比对 git 实际会用的 URL；
#   3) 别的 host 上的同名 `owner/repo`。
# （`remote.pushDefault` / `branch.*.pushRemote` 不影响本脚本：每次都显式指定 remote 名 origin。）
# 注意：URL 里可能带 token（例如 checkout 注入的 userinfo），所有输出都先脱敏。
# 边界如实说明：**本地路径**目标不受 host 白名单约束（`file://`、`/abs`、`../rel` 都没有 host）。
# 这是刻意的：本地路径只能写到本机，不构成"把提交推到另一个远端仓"；而测试 fixture 正是
# 用 `<tmp>/github.com/<owner>/<repo>.git` 这样的本地 bare 仓来跑真 git 的。
redact_url() {
  printf '%s' "$1" | sed -E \
    -e 's#^([a-zA-Z][a-zA-Z0-9+.-]*://)[^/@]*@#\1<redacted>@#' \
    -e 's#^[^/@:]+@#<redacted>@#'
}
slug_of() {
  printf '%s' "$1" \
    | sed -E 's#^[a-zA-Z][a-zA-Z0-9+.-]*://##; s#^[^@/]*@##' \
    | sed -E 's#[:/]+$##; s#\.git$##' \
    | awk -F'[/:]' '{print $(NF-1)"/"$NF}'
}
host_of() {
  # 按 **URL scheme / scp 语法**解析 host，不能靠"第一段里有没有点"来判断 ——
  # 单标签主机名（`ssh://git@evil/path`）会被那种写法当成"没有 host"从而绕过白名单。
  # 本地路径（`/abs/path`、`../rel`、`file://`）本来就没有 host，返回空串。
  local u="$1" head
  case "$u" in
    *://*)
      u="${u#*://}"
      head="${u%%/*}"
      head="${head##*@}"     # 去 userinfo
      head="${head%%:*}"     # 去端口
      printf '%s' "$head" ;;
    *)
      head="${u%%/*}"
      case "$head" in
        *:*)                   # scp 形式 user@host:path
          # Windows 盘符（`C:\...` / `C:/...`）才是路径，必须在**去掉 userinfo 之前**
          # 按"字母 + 冒号 + 斜杠"判定 —— 只看单个字母会把 `git@e:owner/repo.git` 这种
          # 单字母主机名当成盘符从而放过（独立 review 实测确实能写到别处）。
          case "$u" in
            [A-Za-z]:[\\/]*) printf '' ;;
            *) head="${u%%:*}"; head="${head##*@}"; printf '%s' "$head" ;;
          esac ;;
        *) printf '' ;;
      esac ;;
  esac
}
collect_lines() { # <cmd...>：把多行输出收集成去重后的数组
  local line
  COLLECTED=()
  while IFS= read -r line; do
    [ -n "$line" ] && COLLECTED+=("$line")
  done < <("$@" 2>/dev/null || true)
}

collect_lines git config --get-all remote.origin.url
RAW_TARGETS=("${COLLECTED[@]:-}")
collect_lines git config --get-all remote.origin.pushurl
RAW_TARGETS+=("${COLLECTED[@]:-}")
collect_lines git remote get-url --all origin
EFF_TARGETS=("${COLLECTED[@]:-}")
collect_lines git remote get-url --push --all origin
EFF_TARGETS+=("${COLLECTED[@]:-}")

RAW_UNIQ="$(printf '%s\n' "${RAW_TARGETS[@]:-}" | sed '/^$/d' | sort -u)"
EFF_UNIQ="$(printf '%s\n' "${EFF_TARGETS[@]:-}" | sed '/^$/d' | sort -u)"
[ -n "$RAW_UNIQ" ] || usage_err "取不到 remote.origin.url；无法确认本仓就是 $REPO_SLUG"
if [ "$RAW_UNIQ" != "$EFF_UNIQ" ]; then
  # 逐条脱敏后再打印：URL 里的 userinfo 可能是 checkout 注入的 token，不能直接进日志
  # （不依赖 GitHub 的自动 masking —— 那只是兜底，不是我们泄露的理由）。
  echo "[contract-sync][错误] 原始远端配置：$(printf '%s\n' "$RAW_UNIQ" | while IFS= read -r u; do redact_url "$u"; printf ' '; done)" >&2
  echo "[contract-sync][错误] git 实际会用的：$(printf '%s\n' "$EFF_UNIQ" | while IFS= read -r u; do redact_url "$u"; printf ' '; done)" >&2
  usage_err "检测到 URL 重写（url.*.insteadOf / url.*.pushInsteadOf）改变了 fetch/push 目标；无法确认目标就是 $REPO_SLUG，拒绝执行"
fi

while IFS= read -r target; do
  [ -n "$target" ] || continue
  target_slug="$(slug_of "$target")"
  target_host="$(host_of "$target")"
  if [ "${target_slug,,}" != "${REPO_SLUG,,}" ]; then
    usage_err "远端目标（$(redact_url "$target") → $target_slug）与 GITHUB_REPOSITORY（$REPO_SLUG）不一致；拒绝在不确定的仓库里提交/推送"
  fi
  if [ -n "$target_host" ] && [ "${target_host,,}" != "github.com" ]; then
    usage_err "远端目标（$(redact_url "$target")）的 host 是 $target_host，不是 github.com；拒绝把提交推到不受信任的主机"
  fi
done <<<"$RAW_UNIQ"

# --- 前置 2：工作区必须干净（不许把无关的本地文件带进刷新 PR）--------------
DIRTY="$(git status --porcelain)"
if [ -n "$DIRTY" ]; then
  printf '%s\n' "$DIRTY" | head -20 >&2
  usage_err "工作区不干净（见上，最多 20 行）；本脚本会提交刷新产物，拒绝在脏工作区上运行"
fi

log "工作仓：$ROOT（repo=$REPO_SLUG，分支=$BRANCH，基线=$BASE_BRANCH，契约 ref=$CONTRACT_REF）"

# --- 1) 取最新的 base，以及（若存在）刷新分支的当前值 ----------------------
git fetch --depth=1 origin "+refs/heads/${BASE_BRANCH}:refs/remotes/origin/${BASE_BRANCH}" >/dev/null 2>&1 \
  || usage_err "无法从 origin 取到基线分支 $BASE_BRANCH"
BASE_SHA="$(git rev-parse "refs/remotes/origin/${BASE_BRANCH}")"

BRANCH_EXISTS="false"
PREV_BRANCH_SHA=""
if git fetch --depth=1 origin "+refs/heads/${BRANCH}:refs/remotes/origin/${BRANCH}" >/dev/null 2>&1; then
  BRANCH_EXISTS="true"
  PREV_BRANCH_SHA="$(git rev-parse "refs/remotes/origin/${BRANCH}")"
  log "刷新分支 $BRANCH 已存在（$PREV_BRANCH_SHA）"
else
  log "刷新分支 $BRANCH 不存在"
fi

# --- 2) 刷新永远基于最新 base（不基于旧刷新分支的 HEAD）--------------------
# 为什么：上一次刷新 PR 还没合、或合并后分支没删时，直接沿用旧分支的 HEAD 会在
# **旧的刷新脚本 / 旧的生成器**上跑，产物可能与当前 master 的规则不一致。
git checkout -B "$BRANCH" "$BASE_SHA" >/dev/null 2>&1
log "已基于最新 base 重建工作区：$BASE_SHA"

# --- 3) 刷新（快照 + 生成物 + provenance，一步到底，只做一次）--------------
log "执行刷新：$REFRESH_CMD（PIM_CONTRACT_REF=$CONTRACT_REF）"
PIM_CONTRACT_REF="$CONTRACT_REF" bash -c "$REFRESH_CMD"

# --- 4) 通知里带了 sha256 就必须对得上（防"刷出来一份不是它通知的那份"）----
if [ -n "$PAYLOAD_SHA256" ]; then
  ACTUAL="$(sha256sum contract/openapi.json | cut -d' ' -f1)"
  if [ "$ACTUAL" != "$PAYLOAD_SHA256" ]; then
    echo "[contract-sync][错误] 刷新得到的快照 sha256=$ACTUAL，与通知里的 $PAYLOAD_SHA256 不一致；不提交、不开 PR" >&2
    exit 1
  fi
  log "快照 sha256 与通知一致：$ACTUAL"
fi

# --- 5) 只暂存契约产物（绝不 `git add -A`）---------------------------------
PRESENT_ARTIFACTS=()
for p in "${ARTIFACT_PATHS[@]}"; do
  if [ -e "$p" ]; then PRESENT_ARTIFACTS+=("$p"); fi
done
[ "${#PRESENT_ARTIFACTS[@]}" -gt 0 ] \
  || { echo "[contract-sync][错误] 一个契约产物都不存在（${ARTIFACT_PATHS[*]}）；刷新命令可能没跑对" >&2; exit 1; }
git add -- "${PRESENT_ARTIFACTS[@]}"

CHANGED="true"
if git diff --cached --quiet; then CHANGED="false"; fi

# 分支上是否已经是同一份内容（内容一致就不必再提交/推送，保持幂等）
BRANCH_SAME="false"
if [ "$BRANCH_EXISTS" = "true" ]; then
  if git diff --quiet "refs/remotes/origin/${BRANCH}" -- "${PRESENT_ARTIFACTS[@]}"; then BRANCH_SAME="true"; fi
fi

# --- 6) provenance → PR 正文用的事实 ---------------------------------------
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

# --- 7) 分支名/标题/正文都按 AGENTS.md 的 PR 约定生成（pr-metadata 门禁会查）-
TITLE="chore(contract): 刷新契约快照至 pim-api@${SHORT_REF} / refresh contract snapshot"
BODY_FILE="$(mktemp "${TMPDIR:-/tmp}/pim-contract-sync-body.XXXXXX")"
trap 'rm -f "$BODY_FILE"' EXIT

FILE_LIST=""
for p in "${PRESENT_ARTIFACTS[@]}"; do
  FILE_LIST="${FILE_LIST}  - \`${p}\`
"
done

cat > "$BODY_FILE" <<EOF
本 PR 由 \`.github/workflows/contract-sync.yml\` 自动创建，**请人工 review 后再合并**（不自动合并）。
分支由机器人维护，请不要手工往这条分支上提交（下一次同步会基于最新 master 重建它）。

## 技术修改 / Technical changes

- 契约快照同步到上游 \`2746267826/pim-api\` 的 \`${RESOLVED_REF:-<未知>}\`（${BYTES:-?} 字节，sha256 \`${SHA256:-?}\`；本次请求的 ref：\`${CONTRACT_REF}\`）。
- 受影响文件：
${FILE_LIST}- 快照与生成物**同时**刷新：生成物里嵌有快照的 sha256，只换快照不重新生成会被本仓的一致性门禁判红。
- 来源记录写在 \`contract/provenance.json\`（无时间戳，可复现）。

## 功能变化 / Feature changes

无 / None —— 本次只同步接口契约快照与由它生成的类型，不改产品行为。

## 如何体验 / How to try it

1. 打开本 PR 的 Files changed，确认契约差异与上游这次接口变化一致、没有与本仓无关的改动。
2. 需要本地复核时跑只读门禁（不改仓）：\`scripts/ci/check-contract-freshness.sh\`。
3. 合并后，客户端代码即可按新契约里的字段/端点开发；契约里没有的东西仍然不许猜。

## 测试 / Tests

自动化（本 PR 上由 CI 执行，必须全绿才能合）：

${GATE_NOTES}
- 本仓常规构建与单测。

> 说明：本 PR 由机器人创建，正文与分支名按 \`AGENTS.md\` 的约定生成（双语标题、四段正文、
> \`{agent}-{os}/{topic}\` 分支名），以便通过 \`pr-metadata\` 门禁。
EOF

# --- 8) 提交 + 推送（内容一致就不推；要推就重建到最新 base 上再推）--------
PUSHED="false"
if [ "$CHANGED" = "false" ]; then
  log "刷新后相对最新 base（$BASE_BRANCH）无差异 → 没有要发布的东西"
elif [ "$BRANCH_SAME" = "true" ]; then
  log "刷新分支上已经是同一份内容（只是基于更旧的 base）→ 不重复提交/推送"
  git checkout -q -f "refs/remotes/origin/${BRANCH}"
else
  git config user.name "pim-contract-sync[bot]" >/dev/null
  git config user.email "pim-contract-sync[bot]@users.noreply.github.com" >/dev/null
  git commit -q -m "chore(contract): 刷新契约快照至 pim-api@${SHORT_REF} / refresh contract snapshot

由 .github/workflows/contract-sync.yml 自动生成（pim-api 契约变更通知）。
快照：${BYTES:-?} 字节，sha256 ${SHA256:-?}，来源 2746267826/pim-api@${RESOLVED_REF:-?}。" \
    -m "Refs 2746267826/pim-api#415"
  log "已提交：$(git rev-parse --short HEAD)"
  if [ "$BRANCH_EXISTS" = "true" ]; then
    # 分支是机器人自己的：重建到最新 base 后必须 force；lease 指向我们刚取到的那个值，
    # 万一有人在这中间推了东西，push 会被拒绝而不是覆盖别人的提交。
    git push origin --force-with-lease="refs/heads/${BRANCH}:${PREV_BRANCH_SHA}" "HEAD:refs/heads/${BRANCH}"
  else
    git push origin "HEAD:refs/heads/${BRANCH}"
  fi
  PUSHED="true"
  log "已推送分支 $BRANCH"
fi

# --- 9) 开 PR / 更新已存在的 PR ---------------------------------------------
# 查询失败**不能**当成"没有 PR"：那会让"上次推成功但开 PR 失败"的情形永远补不开 PR。
set +e
PR_NUMBER="$(GH_TOKEN="$GH_TOKEN_VALUE" gh pr list --repo "$REPO_SLUG" --head "$BRANCH" --state open --json number --jq '.[0].number // empty' 2>/dev/null)"
PR_RC=$?
set -e
if [ "$PR_RC" -ne 0 ]; then
  echo "[contract-sync][错误] 查询本分支是否已有 PR 失败（gh pr list 退出码 $PR_RC）；不猜、不当成没有 PR，直接失败" >&2
  exit 1
fi

# 分支上是否还有相对 base 未合并的刷新内容（用于"没有新提交但 PR 还没开出来"的补开）
BRANCH_DIFF="false"
if [ "$BRANCH_EXISTS" = "true" ]; then
  if ! git diff --quiet "refs/remotes/origin/${BASE_BRANCH}" "refs/remotes/origin/${BRANCH}" -- "${PRESENT_ARTIFACTS[@]}"; then
    BRANCH_DIFF="true"
  fi
fi

if [ -n "$PR_NUMBER" ]; then
  if [ "$PUSHED" = "true" ]; then
    GH_TOKEN="$GH_TOKEN_VALUE" gh pr edit "$PR_NUMBER" --repo "$REPO_SLUG" \
      --title "$TITLE" --body-file "$BODY_FILE" >/dev/null
    RESULT="pr-updated"
    log "已更新刷新 PR #$PR_NUMBER（标题与正文一起更新，避免正文还写着旧契约）"
  else
    RESULT="pr-exists"
    log "已存在刷新的 PR #$PR_NUMBER，且本次无需改动"
  fi
elif [ "$PUSHED" = "true" ] || { [ "$BRANCH_DIFF" = "true" ] && [ "$BRANCH_SAME" = "true" ]; }; then
  if [ "$PUSHED" = "false" ]; then
    log "本次没有新提交，但刷新分支上的内容**就是**本次刷新结果、且相对 $BASE_BRANCH 还没合并、也没有开着的 PR → 补开 PR"
  fi
  GH_TOKEN="$GH_TOKEN_VALUE" gh pr create \
    --repo "$REPO_SLUG" \
    --base "$BASE_BRANCH" \
    --head "$BRANCH" \
    --title "$TITLE" \
    --body-file "$BODY_FILE" >/dev/null
  RESULT="pr-opened"
  log "已创建刷新 PR：$TITLE"
else
  RESULT="no-change"
  if [ "$BRANCH_DIFF" = "true" ] && [ "$BRANCH_SAME" = "false" ]; then
    # 远端刷新分支是**残留**（内容与本次刷新结果不同、也没有开着的 PR）：
    # 这时候开 PR 会得到"正文写着本次契约、内容却是旧的"的过时 PR，宁可什么都不做。
    echo "::warning::远端分支 $BRANCH 的内容与本次刷新结果不一致，且没有开着的 PR → 不补开 PR（避免开出内容过时的 PR）。它应被视为残留分支。" >&2
  else
    log "无差异且没有已开的 PR → 什么都不做"
  fi
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
