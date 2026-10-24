#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 从 pim-api 仓库刷新客户端契约快照与 TypeScript 类型（refresh 专用；会写仓）。
#
# 本脚本是**唯一**会写以下三个产物的入口：
#   contract/openapi.json                    （上游契约原始字节）
#   contract/provenance.json                 （确定性来源记录：repo/ref/resolvedRef/sourcePath/bytes/sha256）
#   client-web/src/api/generated.ts          （由仓内锁定的 openapi-typescript 生成）
#
# 只读校验请用 scripts/ci/check-contract-freshness.sh（CI 用的是它）。
#
# 失败语义：解析 ref、抓取字节、payload 健全性、生成类型、写 provenance 任一步失败 →
#           **三个产物一个字节都不写**，并以非 0 退出。
#
# 用法：
#   scripts/ci/fetch-contract.sh                       # 默认 2746267826/pim-api@master
#   PIM_CONTRACT_REF=<40-hex|branch|tag> scripts/ci/fetch-contract.sh
#
# 安全：raw 下载不带 Authorization 且不跟随重定向；不使用 token（git ls-remote 解析公开 ref）。
# ---------------------------------------------------------------------------
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
# shellcheck source=scripts/ci/contract-freshness-lib.sh
. "$HERE/contract-freshness-lib.sh"

if [ "$#" -ne 0 ]; then
  pim_contract_err "fetch-contract.sh 不接受位置参数（收到：$*）。本脚本是 refresh（会写仓）；只读校验请用 scripts/ci/check-contract-freshness.sh。"
  exit 2
fi

REPO="$(pim_contract_repo)"
REF="$(pim_contract_ref)"
RAW_BASE="$(pim_contract_raw_base)"
REMOTE="$(pim_contract_remote)"
SOURCE_PATH="$(pim_contract_source_path)"
SNAPSHOT="$ROOT/contract/openapi.json"
PROVENANCE="$ROOT/contract/provenance.json"
GENERATED="$ROOT/client-web/src/api/generated.ts"
CLI="$(pim_contract_contract_cli "$ROOT")"

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-refresh.XXXXXX")"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

# 先全部写到仓外临时目录，全部校验通过后才安装进仓。
install_file() { # <src> <dst>：同目录临时文件 + mv，按文件原子替换
  local src="$1" dst="$2" dir base
  dir="$(dirname "$dst")"
  base="$(basename "$dst")"
  mkdir -p "$dir"
  cp "$src" "$dir/.$base.tmp.$$"
  mv -f "$dir/.$base.tmp.$$" "$dst"
}

pim_contract_log "== 刷新契约 $REPO@$REF =="
pim_contract_require_python || exit 4

# --- 1) ref -> 不可变 40-hex SHA（同一次运行只解析一次） --------------------
RESOLVED_SHA=""
RESOLVED_SRC=""
if pim_contract_is_sha40 "$REF"; then
  RESOLVED_SHA="$REF"
  RESOLVED_SRC="ref-pinned"
  pim_contract_log "解析来源：PIM_CONTRACT_REF 已是 40 位 SHA，直接使用"
elif RESOLVED_SHA="$(pim_contract_resolve_ref_git "$REMOTE" "$REF")"; then
  RESOLVED_SRC="git-ls-remote"
  pim_contract_log "解析来源：git ls-remote $(pim_contract_redact_url "$REMOTE") $REF -> $RESOLVED_SHA"
elif RESOLVED_SHA="$(pim_contract_resolve_ref_api "$REPO" "$REF")"; then
  RESOLVED_SRC="api.github.com"
  pim_contract_log "解析来源：api.github.com /repos/$REPO/commits/$REF -> $RESOLVED_SHA"
else
  pim_contract_err "无法把 ref '$REF' 解析成不可变 commit SHA；未写入任何产物。"
  exit 3
fi
if ! pim_contract_is_sha40 "$RESOLVED_SHA"; then
  pim_contract_err "解析结果不是 40 位小写 commit SHA：'$RESOLVED_SHA'；未写入任何产物。"
  exit 3
fi

# --- 2) 按该 SHA 取字节（raw，不带 Auth，不跟随重定向） ---------------------
SOURCE_URL="$RAW_BASE/$REPO/$RESOLVED_SHA/$SOURCE_PATH"
RAW_FILE="$TMP/openapi.json"
if ! pim_contract_fetch_raw "$RAW_BASE" "$REPO" "$RESOLVED_SHA" "$SOURCE_PATH" "$RAW_FILE"; then
  pim_contract_err "抓取失败；未写入任何产物。"
  exit 3
fi

# --- 3) payload 健全性：JSON 对象 / OpenAPI 3.x / paths 非空 -----------------
if ! pim_contract_validate_payload "$RAW_FILE"; then
  pim_contract_err "上游 payload 不合法；未写入任何产物。"
  exit 4
fi
RAW_BYTES="$(pim_contract_bytes "$RAW_FILE")"
RAW_SHA="$(pim_contract_sha256 "$RAW_FILE")"
pim_contract_log "契约字节：$RAW_BYTES 字节 sha256=$RAW_SHA"

# --- 4) 生成类型（仓内锁定 CLI；banner 只由快照内容决定，不含 ref/移动 SHA）--
GEN_FILE="$TMP/generated.ts"
if ! pim_contract_generate_types "$CLI" "$RAW_FILE" "$GEN_FILE"; then
  pim_contract_err "生成失败；未写入任何产物。"
  exit 4
fi

# --- 5) 确定性 provenance（无时间戳） --------------------------------------
PROV_FILE="$TMP/provenance.json"
if ! pim_contract_write_provenance "$PROV_FILE" "$REPO" "$REF" "$RESOLVED_SHA" "$RESOLVED_SRC" \
     "$SOURCE_PATH" "$(pim_contract_redact_url "$SOURCE_URL")" "$RAW_BYTES" "$RAW_SHA"; then
  pim_contract_err "写 provenance 失败；未写入任何产物。"
  exit 4
fi

# --- 6) 全部校验通过后才安装三个产物 ---------------------------------------
install_file "$RAW_FILE"  "$SNAPSHOT"
install_file "$PROV_FILE" "$PROVENANCE"
install_file "$GEN_FILE"  "$GENERATED"

pim_contract_log "已写入 $SNAPSHOT（$RAW_BYTES 字节）"
pim_contract_log "已写入 $PROVENANCE（resolvedRef=$RESOLVED_SHA，来源=$RESOLVED_SRC）"
pim_contract_log "已写入 $GENERATED（$(pim_contract_bytes "$GENERATED") 字节）"
pim_contract_log "刷新完成：请提交上述三个文件。"
