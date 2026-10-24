#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约变更通知：向客户端仓发 repository_dispatch（工单 §415 方案 A）。
#
# 这是「api 侧主动通知」真正出网的那一步。设计约束：
#   * token 只从环境变量 CONTRACT_SYNC_TOKEN 读（由 actions/create-github-app-token
#     现签的 installation token）—— **不进 argv**（否则会出现在进程参数表里）、
#     不落盘、任何输出里都不回显；
#   * 一个仓失败**不许拖住**另一个：所有仓都尝试完，再按失败数决定退出码；
#   * 5xx / 429 / 网络错误会重试，4xx 不重试（4xx 是配置或权限问题，重试无意义）；
#   * 通知里带 `ref`（契约所在 commit）+ `sha256` + `bytes`：客户端据此用
#     `PIM_CONTRACT_REF=<ref>` 精确取那个 commit 的字节，避免"收到通知时 master
#     已经又往前走了"导致刷出来一份与通知内容不同的快照。
#
# 用法：
#   CONTRACT_SYNC_TOKEN=<installation token> scripts/ci/notify-contract-sync.sh \
#     --owner 2746267826 --repos "pim-android pim-windows" \
#     --contract-sha256 <64hex> --contract-bytes <n> --contract-commit <40hex> \
#     [--contract-repo 2746267826/pim-api] [--source-path contract/openapi.json] \
#     [--event-type contract-updated] [--api-base https://api.github.com] \
#     [--max-attempts 3] [--dry-run]
#
# 退出码：0 = 所有仓都投递成功（或 --dry-run）；1 = 至少一个仓失败；2 = 参数/配置错误。
# ---------------------------------------------------------------------------
set -euo pipefail

OWNER="${PIM_CONTRACT_SYNC_OWNER:-2746267826}"
REPOS="${PIM_CONTRACT_SYNC_REPOS:-}"
CONTRACT_REPO="${PIM_CONTRACT_SYNC_CONTRACT_REPO:-2746267826/pim-api}"
SOURCE_PATH="${PIM_CONTRACT_SYNC_SOURCE_PATH:-contract/openapi.json}"
EVENT_TYPE="${PIM_CONTRACT_SYNC_EVENT_TYPE:-contract-updated}"
API_BASE="${PIM_CONTRACT_SYNC_API_BASE:-https://api.github.com}"
SHA256=""
BYTES=""
COMMIT=""
MAX_ATTEMPTS="${PIM_CONTRACT_SYNC_MAX_ATTEMPTS:-3}"
RETRY_DELAY="${PIM_CONTRACT_SYNC_RETRY_DELAY:-3}"
DRY_RUN="false"
# 运行所在的 GitHub ref（由 workflow 传 `${{ github.ref }}`）。契约的权威版本是
# **master 上的文件**，所以非 master 一律降级成 dry-run —— 这条规则放在脚本里
# 而不是 YAML 里，是为了它能有离线行为测试（YAML 里的分支逻辑测不到）。
GITHUB_REF_ARG="${PIM_CONTRACT_SYNC_GITHUB_REF:-}"
# 契约硬校验所在 job 的结果。只有 success 才允许通知客户端；判定放在脚本里（可测），
# 而不是 YAML 的 if 里 —— 在 YAML 里写成 `needs.build-api.result == 'success'` 会让
# job 被**静默跳过**：手动补发时若后端相对上一版无变化，build-api 就是 skipped，
# 于是"补发"什么也没发生、还不报错（独立 review 抓到的死锁）。
BUILD_API_RESULT="${PIM_CONTRACT_SYNC_BUILD_API_RESULT:-}"
FORCE="${PIM_CONTRACT_SYNC_FORCE:-false}"
CURL="${PIM_CURL:-curl}"

usage_err() { echo "[contract-sync][错误] $*" >&2; exit 2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --owner) OWNER="${2:-}"; shift 2 ;;
    --repos) REPOS="${2:-}"; shift 2 ;;
    --contract-repo) CONTRACT_REPO="${2:-}"; shift 2 ;;
    --source-path) SOURCE_PATH="${2:-}"; shift 2 ;;
    --event-type) EVENT_TYPE="${2:-}"; shift 2 ;;
    --api-base) API_BASE="${2:-}"; shift 2 ;;
    --contract-sha256) SHA256="${2:-}"; shift 2 ;;
    --contract-bytes) BYTES="${2:-}"; shift 2 ;;
    --contract-commit) COMMIT="${2:-}"; shift 2 ;;
    --max-attempts) MAX_ATTEMPTS="${2:-}"; shift 2 ;;
    --github-ref) GITHUB_REF_ARG="${2:-}"; shift 2 ;;
    --build-api-result) BUILD_API_RESULT="${2:-}"; shift 2 ;;
    --force) FORCE="${2:-}"; shift 2 ;;
    --dry-run) DRY_RUN="true"; shift ;;
    -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) usage_err "未知参数：$1" ;;
  esac
done

[ -n "$OWNER" ] || usage_err "--owner 不能为空"
[[ "$SHA256" =~ ^[0-9a-f]{64}$ ]] || usage_err "--contract-sha256 必须是 64 位小写十六进制（收到 '$SHA256'）"
[[ "$COMMIT" =~ ^[0-9a-f]{40}$ ]] || usage_err "--contract-commit 必须是 40 位小写十六进制（收到 '$COMMIT'）"
[[ "$BYTES" =~ ^[1-9][0-9]*$ ]] || usage_err "--contract-bytes 必须是正整数（收到 '$BYTES'）"
[[ "$EVENT_TYPE" =~ ^[A-Za-z0-9._-]{1,100}$ ]] || usage_err "--event-type 只允许 [A-Za-z0-9._-]（≤100，收到 '$EVENT_TYPE'）"
[[ "$MAX_ATTEMPTS" =~ ^[1-9][0-9]*$ ]] || usage_err "--max-attempts 必须是正整数"
[[ "$API_BASE" =~ ^https?:// ]] || usage_err "--api-base 必须是 http(s) URL"
case "$FORCE" in true|false) ;; *) usage_err "--force 必须是 true 或 false（收到 '$FORCE'）" ;; esac
[ -n "$BUILD_API_RESULT" ] || usage_err "--build-api-result 必填（契约硬校验 job 的结果；写 \${{ needs.build-api.result }}）"

# --- 契约必须先通过硬校验（工单 #415 的核心约束）---------------------------
# 没被硬校验过的契约不许推给客户端。这里**不**用 YAML 的 job if 去挡，因为那会变成
# "静默跳过"：手动补发时 build-api 常因后端无变化而 skipped，于是补发悄悄没发生。
if [ "$BUILD_API_RESULT" != "success" ]; then
  if [ "$FORCE" = "true" ]; then
    echo "[contract-sync][错误] 你要求强制补发，但契约硬校验所在的 build-api 这次结果是 '$BUILD_API_RESULT'（多半是被跳过了：后端相对上一版没有变化）。未经硬校验的契约不推给客户端。" >&2
    echo "[contract-sync][错误] 请带上 force_all=true 重跑（它会真的跑一遍契约硬校验）：" >&2
    echo "[contract-sync][错误]   gh workflow run ci.yml --repo ${CONTRACT_REPO:-2746267826/pim-api} --ref master -f force_all=true -f force_contract_notify=true" >&2
    exit 2
  fi
  echo "::warning::契约相对上一版有变化，但 build-api 的结果是 '$BUILD_API_RESULT'（不是 success）→ 本次不通知客户端（未经硬校验的契约不推给它）。" >&2
  exit 0
fi

# 允许空格/逗号/换行分隔；去空项。
read -r -a REPO_LIST <<<"$(printf '%s' "$REPOS" | tr ',\n' '  ')"
CLEAN_REPOS=()
for r in "${REPO_LIST[@]:-}"; do [ -n "$r" ] && CLEAN_REPOS+=("$r"); done
[ "${#CLEAN_REPOS[@]}" -gt 0 ] || usage_err "--repos 不能为空（要通知哪些客户端仓）"
for r in "${CLEAN_REPOS[@]}"; do
  [[ "$r" =~ ^[A-Za-z0-9._-]+$ ]] || usage_err "仓名非法：'$r'"
done

TOKEN="${CONTRACT_SYNC_TOKEN:-}"

# 非 master 的 ref 一律降级为 dry-run：绝不把一份可能永远不进 master 的契约推给客户端仓。
if [ -n "$GITHUB_REF_ARG" ] && [ "$GITHUB_REF_ARG" != "refs/heads/master" ]; then
  echo "::warning::当前 ref 是 ${GITHUB_REF_ARG}，不是 refs/heads/master → 只做 dry-run，不向客户端仓真发通知（契约的权威版本是 master 上的文件）。" >&2
  DRY_RUN="true"
fi

if [ "$DRY_RUN" != "true" ]; then
  [ -n "$TOKEN" ] || usage_err "环境变量 CONTRACT_SYNC_TOKEN 必填（由 secrets 里的 App 私钥现签，勿硬编码）"
  # 真发通知时必须显式声明运行在哪个 ref 上：漏传就会绕过上面那条 master 限制。
  [ -n "$GITHUB_REF_ARG" ] || usage_err "真发通知时必须传 --github-ref（workflow 里是 \${{ github.ref }}）：契约只允许从 master 推送"
fi

PAYLOAD="$(jq -n \
  --arg event "$EVENT_TYPE" \
  --arg repo "$CONTRACT_REPO" \
  --arg ref "$COMMIT" \
  --arg sha256 "$SHA256" \
  --argjson bytes "$BYTES" \
  --arg sourcePath "$SOURCE_PATH" \
  '{event_type:$event,
    client_payload:{
      contractRepo:$repo,
      ref:$ref,
      sha256:$sha256,
      bytes:$bytes,
      sourcePath:$sourcePath,
      note:"contract/openapi.json 在上游 master 发生了变化；请刷新快照（并用 PIM_CONTRACT_REF 取上面这个 ref 的字节）后开 PR。"
    }}')"

echo "[contract-sync] 通知目标：${CLEAN_REPOS[*]}；event_type=${EVENT_TYPE}；ref=${COMMIT}；sha256=${SHA256:0:12}；${BYTES}B"

FAILED=()
DELIVERED=()
SUMMARY_ROWS=()

for repo in "${CLEAN_REPOS[@]}"; do
  URL="${API_BASE}/repos/${OWNER}/${repo}/dispatches"

  if [ "$DRY_RUN" = "true" ]; then
    echo "[contract-sync][dry-run] 将 POST ${URL}"
    echo "[contract-sync][dry-run] body=${PAYLOAD}"
    DELIVERED+=("$repo")
    SUMMARY_ROWS+=("| ${repo} | dry-run | 未发送 |")
    continue
  fi

  STATUS=""
  BODY_FILE="$(mktemp "${TMPDIR:-/tmp}/pim-contract-dispatch-${repo}.XXXXXX")"
  attempt=1
  ok="false"
  while [ "$attempt" -le "$MAX_ATTEMPTS" ]; do
    set +e
    # 认证头经 stdin 交给 curl 的 --config：不在命令行参数里，也不落盘。
    STATUS="$(printf 'header = "Authorization: Bearer %s"\n' "$TOKEN" | "$CURL" -sS -o "$BODY_FILE" -w '%{http_code}' \
      -X POST "$URL" \
      --config /dev/stdin \
      -H "Accept: application/vnd.github+json" \
      -H "X-GitHub-Api-Version: 2022-11-28" \
      -H "Content-Type: application/json" \
      -d "$PAYLOAD")"
    CURL_RC=$?
    set -e

    if [ "$CURL_RC" -eq 0 ] && [[ "$STATUS" =~ ^2[0-9][0-9]$ ]]; then
      ok="true"
      break
    fi

    # 只对可恢复的失败重试：网络错误（curl 非 0）、5xx、429。
    RETRYABLE="false"
    if [ "$CURL_RC" -ne 0 ]; then RETRYABLE="true"; fi
    if [[ "$STATUS" =~ ^5[0-9][0-9]$ ]] || [ "$STATUS" = "429" ]; then RETRYABLE="true"; fi

    if [ "$RETRYABLE" = "true" ] && [ "$attempt" -lt "$MAX_ATTEMPTS" ]; then
      echo "[contract-sync][警告] ${repo} 第 ${attempt}/${MAX_ATTEMPTS} 次失败（HTTP ${STATUS:-<none>}，curl=${CURL_RC}），${RETRY_DELAY}s 后重试" >&2
      sleep "$RETRY_DELAY"
      attempt=$((attempt+1))
      continue
    fi
    break
  done

  if [ "$ok" = "true" ]; then
    echo "[contract-sync] ✅ ${repo} → HTTP ${STATUS}（已投递 ${attempt} 次尝试）"
    DELIVERED+=("$repo")
    SUMMARY_ROWS+=("| ${repo} | \`${STATUS}\` | ✅ 已投递 |")
  else
    BODY_TEXT="$(head -c 500 "$BODY_FILE" 2>/dev/null || echo '<empty>')"
    echo "[contract-sync][错误] ❌ ${repo} → HTTP ${STATUS:-<none>}（curl=${CURL_RC}）" >&2
    echo "[contract-sync][错误] 响应体：${BODY_TEXT:-<empty>}" >&2
    FAILED+=("$repo")
    SUMMARY_ROWS+=("| ${repo} | \`${STATUS:-none}\` | ❌ 未投递（${BODY_TEXT:-empty}） |")
  fi
  rm -f "$BODY_FILE"
done

if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  {
    echo "### 契约变更通知（repository_dispatch）"
    echo ""
    echo "| 客户端仓 | HTTP | 结果 |"
    echo "| --- | --- | --- |"
    printf '%s\n' "${SUMMARY_ROWS[@]:-| <none> | - | - |}"
    echo ""
    echo "契约：\`${CONTRACT_REPO}\` ref=\`${COMMIT}\` sha256=\`${SHA256}\`（${BYTES} 字节），event_type=\`${EVENT_TYPE}\`"
  } >> "$GITHUB_STEP_SUMMARY"
fi

if [ "${#FAILED[@]}" -gt 0 ]; then
  echo "[contract-sync][错误] 未投递成功的仓：${FAILED[*]}（已投递：${DELIVERED[*]:-无}）" >&2
  # 补发通道必须写清楚，因为**自动补发只在契约相对上一版 release 仍不同时成立**：
  # 如果本次 CI 仍然发布了 release，检测基线会前进到包含这份契约的提交，
  # 之后普通 push 会算出 changed=false，不会再自动补发。这时只能手动补发：
  echo "[contract-sync][错误] 手动补发（在 ${CONTRACT_REPO} 的 master 上触发一次；客户端按字节去重，不会开出重复 PR）：" >&2
  echo "[contract-sync][错误]   gh workflow run ci.yml --repo ${CONTRACT_REPO} --ref master -f force_all=true -f force_contract_notify=true" >&2
  echo "[contract-sync][错误] （必须带 force_all=true：不带它、且后端相对上一版无变化时，契约硬校验所在的 build-api 会被跳过，补发会被拒绝。）" >&2
  echo "[contract-sync][错误] 另外：客户端的契约新鲜度门禁会继续报红 —— 漂移不会被静默放过。" >&2
  exit 1
fi

echo "[contract-sync] 全部投递成功：${DELIVERED[*]}"
