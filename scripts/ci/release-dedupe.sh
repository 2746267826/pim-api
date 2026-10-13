#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 防重复发版判定（REQ-6）。
#
# 规则（顺序即优先级）：
#   1) 后端源码相对上一版 release 有变化            → 发版
#   2) 没有上一版 release（首次发布）                → 发版，并写明「按需发版处理」
#   3) 上一版 release 没有台账资产                   → 发版，并写明「按需发版处理」
#   4) 上一版台账不可读/缺字段                        → 发版，并写明「按需发版处理」（可见告警，不静默）
#   5) 前端版本(version+gitSha)与上一版台账逐字相同    → 不发版
#   6) 其余（前端版本变了）                          → 发版
#
# 判定必须可审计：summary 里固定出现三个事实——本次前端版本（冻结值）、
# 上一版台账里的前端版本、后端 diff 是否为空——以及结论与依据。
#
# 用法：
#   scripts/ci/release-dedupe.sh --web-version V --web-git-sha S --backend-changed true|false \
#     [--previous-release-tag TAG] [--ledger FILE] [--print-env]
# 输出：publish=true|false / dedupe_reason=<文本>
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=scripts/ci/web-dist-lib.sh
. "$HERE/web-dist-lib.sh"

WEB_VERSION=""
WEB_GIT_SHA=""
BACKEND_CHANGED=""
PREVIOUS_TAG=""
LEDGER=""
PRINT_ENV=false
while [[ $# -gt 0 ]]; do
  case "$1" in
    --web-version) WEB_VERSION="${2:-}"; shift 2 ;;
    --web-git-sha) WEB_GIT_SHA="${2:-}"; shift 2 ;;
    --backend-changed) BACKEND_CHANGED="${2:-}"; shift 2 ;;
    --previous-release-tag) PREVIOUS_TAG="${2:-}"; shift 2 ;;
    --ledger) LEDGER="${2:-}"; shift 2 ;;
    --print-env) PRINT_ENV=true; shift ;;
    *) pim_web_err "未知参数：$1"; exit 2 ;;
  esac
done

[ -n "$WEB_VERSION" ] || { pim_web_err "--web-version 必填"; exit 2; }
[ -n "$WEB_GIT_SHA" ] || { pim_web_err "--web-git-sha 必填"; exit 2; }
case "$BACKEND_CHANGED" in
  true|false) ;;
  *) pim_web_err "--backend-changed 必须是 true 或 false，实际为 '${BACKEND_CHANGED}'"; exit 2 ;;
esac

LEDGER_VERSION="<无>"
LEDGER_SHA="<无>"
LEDGER_STATE="无上一版台账（上一版 release 不存在）"
LEDGER_READABLE=false

if [ -n "$PREVIOUS_TAG" ] && [ -n "$LEDGER" ] && [ -s "$LEDGER" ]; then
  if jq -e 'type == "object"' "$LEDGER" >/dev/null 2>&1; then
    cand_version="$(jq -r '.version // ""' "$LEDGER")"
    cand_sha="$(jq -r '.gitSha // ""' "$LEDGER")"
    if [ -n "$cand_version" ] && [ -n "$cand_sha" ]; then
      LEDGER_VERSION="$cand_version"
      LEDGER_SHA="$cand_sha"
      LEDGER_STATE="已读取（来自 ${PREVIOUS_TAG}）"
      LEDGER_READABLE=true
    else
      LEDGER_STATE="不可读（缺少 version/gitSha 字段）"
    fi
  else
    LEDGER_STATE="不可读（不是 JSON 对象）"
  fi
elif [ -n "$PREVIOUS_TAG" ]; then
  LEDGER_STATE="不存在（上一版 release 没有 pim-web-version.json 资产）"
fi

PUBLISH=true
REASON=""
if [ "$BACKEND_CHANGED" = "true" ]; then
  REASON="后端源码相对上一版 release 有变化 → 正常发版"
elif [ -z "$PREVIOUS_TAG" ]; then
  REASON="无上一版 release（首次发布）→ 无上一版台账，按需发版处理"
elif [ "$LEDGER_READABLE" != "true" ]; then
  REASON="上一版台账${LEDGER_STATE} → 无上一版台账，按需发版处理"
elif [ "$LEDGER_VERSION" = "$WEB_VERSION" ] && [ "$LEDGER_SHA" = "$WEB_GIT_SHA" ]; then
  PUBLISH=false
  REASON="前端版本与上一版台账逐字一致，且后端源码无变化 → 不发版（复用既有产物与镜像 tag）"
else
  REASON="前端版本相对上一版台账有变化（${LEDGER_VERSION}/${LEDGER_SHA} → ${WEB_VERSION}/${WEB_GIT_SHA}）→ 正常发版"
fi

SUMMARY="$(cat <<EOF
## 防重复发版判定（REQ-6）

| 事实 | 值 |
| --- | --- |
| 本次前端版本（冻结值） | \`${WEB_VERSION}\` / \`${WEB_GIT_SHA}\` |
| 上一版台账里的前端版本 | \`${LEDGER_VERSION}\` / \`${LEDGER_SHA}\` （${LEDGER_STATE}${PREVIOUS_TAG:+，tag=${PREVIOUS_TAG}}） |
| 后端 diff 是否为空 | $([ "$BACKEND_CHANGED" = "true" ] && echo '否（后端有变化）' || echo '是（后端无变化）') |

**结论：$([ "$PUBLISH" = "true" ] && echo '发版' || echo '不发版')** —— ${REASON}
EOF
)"

pim_web_log "防重复发版判定：本次前端=${WEB_VERSION}/${WEB_GIT_SHA}；上一版台账=${LEDGER_VERSION}/${LEDGER_SHA}（${LEDGER_STATE}）；后端有变化=${BACKEND_CHANGED}"
printf '%s\n' "$SUMMARY" >&2
if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
  printf '%s\n' "$SUMMARY" >> "$GITHUB_STEP_SUMMARY"
fi

EMIT="$(cat <<EOF
publish=${PUBLISH}
dedupe_reason=${REASON}
EOF
)"

if [ "$PRINT_ENV" = true ] || [ -z "${GITHUB_OUTPUT:-}" ]; then
  printf '%s\n' "$EMIT"
else
  printf '%s\n' "$EMIT" >> "$GITHUB_OUTPUT"
fi
