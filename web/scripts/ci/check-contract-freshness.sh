#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约新鲜度「只读」校验（pim-web）
#
# 语义：把本仓的契约快照与「契约来源仓当前上游」的字节做逐字节比较，并用仓内锁定
#       的 openapi-typescript 从本仓快照重新生成类型、与仓内 generated.ts 逐字节比较。
#       任一不一致 → 非 0，交由 CI Gate 拦截。
#
# 只读保证：本脚本不写任何仓内文件；所有中间产物都在仓外 mktemp 目录，退出时删除。
#           「谁写」= scripts/ci/fetch-contract.sh（人工 refresh）；「谁只读」= 本脚本。
#
# 用法：
#   bash scripts/ci/check-contract-freshness.sh
#   PIM_CONTRACT_REF=<40-hex|branch|tag> bash scripts/ci/check-contract-freshness.sh
#
# 退出码：
#   0 一致
#   1 陈旧或不一致（快照落后于上游 / generated.ts 与快照重生成结果不同 / provenance 与快照不一致）
#   3 解析 ref 或抓取上游字节失败（fail-closed：拿不到权威源一律不当作通过）
#   4 配置或 payload 非法（含 CI 环境下出现测试旁路、上游返回 HTML/404/空体/非 OpenAPI3）
# ---------------------------------------------------------------------------
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
# shellcheck source=scripts/ci/contract-freshness-lib.sh
. "$HERE/contract-freshness-lib.sh"

REPO="$(pim_contract_repo)"
REF="$(pim_contract_ref)"
RAW_BASE="$(pim_contract_raw_base)"
REMOTE="$(pim_contract_remote)"
SOURCE_PATH="$(pim_contract_source_path)"
SNAPSHOT="${PIM_CONTRACT_SNAPSHOT:-$ROOT/contract/openapi.json}"
PROVENANCE="${PIM_CONTRACT_PROVENANCE:-$ROOT/contract/provenance.json}"
GENERATED="${PIM_CONTRACT_GENERATED:-$ROOT/client-web/src/api/generated.ts}"
CLI="$(pim_contract_contract_cli "$ROOT")"
TEST_SOURCE_FILE="${PIM_CONTRACT_TEST_SOURCE_FILE:-}"

EXIT_STALE=$PIM_CONTRACT_EXIT_STALE
EXIT_FETCH=$PIM_CONTRACT_EXIT_FETCH
EXIT_CONFIG=$PIM_CONTRACT_EXIT_CONFIG

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-check.XXXXXX")"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

pim_contract_log "== pim-web 契约新鲜度校验（只读） =="
pim_contract_log "仓        : $ROOT"
pim_contract_log "来源仓    : $REPO"
pim_contract_log "来源 ref  : $REF"
pim_contract_log "来源路径  : $SOURCE_PATH"
pim_contract_log "抓取基址  : $(pim_contract_redact_url "$RAW_BASE")"
pim_contract_log "仓内快照  : $SNAPSHOT"
pim_contract_log ""

# --- 0) 生产路径禁止测试旁路 ------------------------------------------------
if ! pim_contract_guard_test_bypass; then
  exit $EXIT_CONFIG
fi

if ! pim_contract_require_python; then
  exit $EXIT_CONFIG
fi

# --- 1) 同一次运行只解析一次 ref -> 不可变 40-hex SHA ------------------------
RESOLVED_SHA=""
RESOLVED_SRC=""
if [ -n "$TEST_SOURCE_FILE" ]; then
  # 测试替身：只在 PIM_CONTRACT_TEST_MODE=1 且非 CI 时可达（上面已守卫）。
  RESOLVED_SHA="<test-stub>"
  RESOLVED_SRC="test-stub"
  pim_contract_log "解析     : 测试替身 local-file（非生产路径）"
elif pim_contract_is_sha40 "$REF"; then
  RESOLVED_SHA="$REF"
  RESOLVED_SRC="ref-pinned"
  pim_contract_log "解析     : PIM_CONTRACT_REF 已是 40 位 SHA，直接使用（跳过 ref 解析）"
else
  if RESOLVED_SHA="$(pim_contract_resolve_ref_git "$REMOTE" "$REF")"; then
    RESOLVED_SRC="git-ls-remote"
    pim_contract_log "解析     : git ls-remote -> $RESOLVED_SHA"
  elif RESOLVED_SHA="$(pim_contract_resolve_ref_api "$REPO" "$REF")"; then
    RESOLVED_SRC="api.github.com"
    pim_contract_log "解析     : api.github.com -> $RESOLVED_SHA"
  else
    pim_contract_err "无法把 ref '$REF' 解析成不可变 commit SHA（git ls-remote 与 api.github.com 均失败）"
    exit $EXIT_FETCH
  fi
  if ! pim_contract_is_sha40 "$RESOLVED_SHA"; then
    pim_contract_err "解析结果不是 40 位小写 commit SHA：'$RESOLVED_SHA'"
    exit $EXIT_FETCH
  fi
fi

# --- 2) 按该 SHA 取权威字节（raw，不带 Auth，不跟随重定向） -----------------
UPSTREAM="$TMP/upstream-openapi.json"
if [ -n "$TEST_SOURCE_FILE" ]; then
  if [ ! -f "$TEST_SOURCE_FILE" ]; then
    pim_contract_err "测试替身文件不存在：$TEST_SOURCE_FILE"
    exit $EXIT_FETCH
  fi
  cp "$TEST_SOURCE_FILE" "$UPSTREAM" || exit $EXIT_FETCH
else
  SOURCE_URL="$RAW_BASE/$REPO/$RESOLVED_SHA/$SOURCE_PATH"
  if ! pim_contract_fetch_raw "$RAW_BASE" "$REPO" "$RESOLVED_SHA" "$SOURCE_PATH" "$UPSTREAM"; then
    exit $EXIT_FETCH
  fi
fi

# --- 3) payload 健全性：JSON 对象 / OpenAPI 3.x / paths 非空 / 非 HTML·404·空体
if ! pim_contract_validate_payload "$UPSTREAM"; then
  pim_contract_err "上游 payload 未通过健全性检查（来源 $(pim_contract_redact_url "${SOURCE_URL:-$TEST_SOURCE_FILE}")）；不把它当契约比对。"
  exit $EXIT_CONFIG
fi

UP_BYTES="$(pim_contract_bytes "$UPSTREAM")"
UP_SHA="$(pim_contract_sha256 "$UPSTREAM")"
pim_contract_log "上游字节  : $UP_BYTES 字节 sha256=$UP_SHA $(pim_contract_payload_summary "$UPSTREAM")"

if [ ! -f "$SNAPSHOT" ]; then
  pim_contract_err "仓内快照不存在：$SNAPSHOT（陈旧：需要运行 scripts/ci/fetch-contract.sh 并提交）"
  exit $EXIT_STALE
fi
SNAP_BYTES="$(pim_contract_bytes "$SNAPSHOT")"
SNAP_SHA="$(pim_contract_sha256 "$SNAPSHOT")"
pim_contract_log "仓内快照  : $SNAP_BYTES 字节 sha256=$SNAP_SHA"

# --- 4) 核心判据：逐字节比较（只有字节/结构差异决定退出码） ------------------
if ! cmp -s "$UPSTREAM" "$SNAPSHOT"; then
  pim_contract_err "契约快照已陈旧：contract/openapi.json（$SNAP_BYTES 字节 sha256=$SNAP_SHA）≠ 上游 $REPO@$RESOLVED_SHA（$UP_BYTES 字节 sha256=$UP_SHA）。请运行 scripts/ci/fetch-contract.sh 并提交刷新结果。"
  pim_contract_log "  上游 $(pim_contract_payload_summary "$UPSTREAM")"
  pim_contract_log "  快照 $(pim_contract_payload_summary "$SNAPSHOT")"
  exit $EXIT_STALE
fi
pim_contract_log "逐字节比对: 一致 ✅"

# --- 5) provenance 存在性与形状（允许滞后于上游 HEAD，不与今天 SHA 强比） ----
PROV_MSG=""
if ! PROV_MSG="$(pim_contract_validate_provenance "$PROVENANCE" "$SNAPSHOT" "$REPO" "$SOURCE_PATH")"; then
  pim_contract_err "$PROV_MSG"
  exit $EXIT_STALE
fi
pim_contract_log "provenance: $PROV_MSG"

# --- 6) 生成物新鲜度：用仓内锁定 CLI 从「仓内快照」重生成后逐字节比较 -------
#    注意：这里重新生成的是仓内快照，而不是上游字节 —— 所以上游出现「非契约提交」
#    但契约字节不变时，本步骤不会把客户端判红。
if [ ! -f "$GENERATED" ]; then
  pim_contract_err "生成物不存在：$GENERATED（请运行 scripts/ci/fetch-contract.sh 并提交结果）"
  exit $EXIT_STALE
fi
REGEN="$TMP/generated.regen.ts"
if ! pim_contract_generate_types "$CLI" "$SNAPSHOT" "$REGEN"; then
  exit $EXIT_CONFIG
fi
if ! cmp -s "$REGEN" "$GENERATED"; then
  pim_contract_err "生成类型已陈旧：client-web/src/api/generated.ts 与由仓内快照重新生成的产物不一致（重生成 $(pim_contract_bytes "$REGEN") 字节 sha256=$(pim_contract_sha256 "$REGEN")，仓内 $(pim_contract_bytes "$GENERATED") 字节 sha256=$(pim_contract_sha256 "$GENERATED")）。请运行 scripts/ci/fetch-contract.sh 并提交结果。"
  exit $EXIT_STALE
fi
pim_contract_log "生成物重生成: 与仓内 generated.ts 逐字节一致 ✅"

pim_contract_log ""
pim_contract_log "✅ 契约快照、provenance 与生成类型均与 pim-api 当前上游一致（上游 $REPO@$RESOLVED_SHA，$UP_BYTES 字节）。"
exit $PIM_CONTRACT_EXIT_OK
