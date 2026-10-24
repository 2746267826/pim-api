#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 同仓契约工具库（web/** 专用）
#
# 唯一权威输入：<repo-root>/contract/openapi.json
# 唯一生成物：  <repo-root>/web/client-web/src/api/generated.ts
#
# 不访问网络、不解析远端 ref、不使用 provenance。
# 所有平台先规范化为 LF 再哈希/生成，避免 CRLF 导致字节差异。
# ---------------------------------------------------------------------------
set -uo pipefail

pim_web_log() { printf '%s\n' "$*"; }
pim_web_err() { printf '::error::%s\n' "$*" >&2; }

pim_web_paths() { # <repo-root>
  PIM_WEB_ROOT="$1"
  PIM_WEB_CONTRACT="$1/contract/openapi.json"
  PIM_WEB_GENERATED="$1/web/client-web/src/api/generated.ts"
  PIM_WEB_CLI="$1/web/client-web/node_modules/.bin/openapi-typescript"
}

pim_web_sha256() {
  if command -v sha256sum >/dev/null 2>&1; then
    sha256sum "$1" | cut -d' ' -f1
  else
    shasum -a 256 "$1" | cut -d' ' -f1
  fi
}

pim_web_bytes() { wc -c < "$1" | tr -d ' '; }

# 规范化为 LF。JSON 字符串中不允许裸 CR，因此删除 CR 不会改变语义。
pim_web_normalize() { # <src> <dst>
  tr -d '\r' < "$1" > "$2"
}

pim_web_validate_payload() { # <file> -> 0 / 4
  local f="$1"
  if [ ! -s "$f" ]; then
    pim_web_err "契约文件为空或不存在：$f"
    return 4
  fi
  if ! command -v node >/dev/null 2>&1; then
    pim_web_err "缺少 node（用于契约 JSON 校验）"
    return 4
  fi
  node - "$f" <<'JS'
const fs = require('fs');
const p = process.argv[2];
let doc;
try {
  doc = JSON.parse(fs.readFileSync(p, 'utf8'));
} catch (e) {
  console.error('::error::契约不是合法 JSON：' + e.message);
  process.exit(4);
}
if (!doc || typeof doc !== 'object' || Array.isArray(doc)) {
  console.error('::error::契约顶层不是 JSON 对象');
  process.exit(4);
}
if (typeof doc.openapi !== 'string' || !doc.openapi.startsWith('3.')) {
  console.error('::error::契约不是 OpenAPI 3.x（openapi=' + String(doc.openapi) + '）');
  process.exit(4);
}
if (!doc.paths || typeof doc.paths !== 'object' || Object.keys(doc.paths).length === 0) {
  console.error('::error::契约 paths 为空');
  process.exit(4);
}
JS
}

pim_web_banner() { # <normalized-contract>
  printf '%s\n' \
    '// 本文件由 web/scripts/ci/update-contract.sh 自动生成，请勿手工编辑。' \
    "// 契约来源：contract/openapi.json（sha256 $(pim_web_sha256 "$1")，$(pim_web_bytes "$1") 字节）" \
    '// 更新方式：在仓库根执行 web/scripts/ci/update-contract.sh 并提交结果。'
}

pim_web_generate_from() { # <normalized-contract> <out>
  local contract="$1" out="$2"
  if [ ! -x "$PIM_WEB_CLI" ]; then
    pim_web_err "未找到仓内锁定的生成器 $PIM_WEB_CLI（请先在 web/client-web 执行 npm ci）"
    return 4
  fi
  {
    pim_web_banner "$contract"
    "$PIM_WEB_CLI" "$contract"
  } > "$out" || {
    pim_web_err "openapi-typescript 生成失败（契约：$contract）"
    return 4
  }
  [ -s "$out" ] || { pim_web_err "openapi-typescript 产物为空"; return 4; }
  return 0
}

# 只读校验：根契约 -> 临时生成 -> 与仓内 generated.ts 逐字节比较。
pim_web_check() { # <repo-root> -> 0 一致 / 1 陈旧 / 4 配置或输入非法
  pim_web_paths "$1"
  local tmp norm regen
  tmp="$(mktemp -d "${TMPDIR:-/tmp}/pim-web-contract-check.XXXXXX")" || return 4
  norm="$tmp/contract.normalized.json"
  regen="$tmp/generated.regen.ts"

  if [ ! -f "$PIM_WEB_CONTRACT" ]; then
    pim_web_err "根契约不存在：$PIM_WEB_CONTRACT"
    rm -rf "$tmp"
    return 4
  fi
  if ! pim_web_normalize "$PIM_WEB_CONTRACT" "$norm"; then
    pim_web_err "无法规范化根契约：$PIM_WEB_CONTRACT"
    rm -rf "$tmp"
    return 4
  fi
  if ! pim_web_validate_payload "$norm"; then
    rm -rf "$tmp"
    return 4
  fi
  if ! pim_web_generate_from "$norm" "$regen"; then
    rm -rf "$tmp"
    return 4
  fi
  if [ ! -f "$PIM_WEB_GENERATED" ]; then
    pim_web_err "生成物不存在：$PIM_WEB_GENERATED（请运行 bash web/scripts/ci/update-contract.sh）"
    rm -rf "$tmp"
    return 1
  fi
  if ! cmp -s "$regen" "$PIM_WEB_GENERATED"; then
    pim_web_err "generated.ts 与根契约重生成结果不一致（契约 sha256=$(pim_web_sha256 "$norm")，$(pim_web_bytes "$norm") 字节）。请运行 bash web/scripts/ci/update-contract.sh 并提交结果。"
    rm -rf "$tmp"
    return 1
  fi
  pim_web_log "✅ web 契约生成物与根 contract/openapi.json 一致（sha256=$(pim_web_sha256 "$norm")，$(pim_web_bytes "$norm") 字节）"
  rm -rf "$tmp"
  return 0
}

# 写入口：重新生成 generated.ts（先写临时文件，成功后原子替换）。
pim_web_update() { # <repo-root> -> 0 / 4
  pim_web_paths "$1"
  local tmp norm gen dir base
  tmp="$(mktemp -d "${TMPDIR:-/tmp}/pim-web-contract-update.XXXXXX")" || return 4
  norm="$tmp/contract.normalized.json"
  gen="$tmp/generated.ts"

  if [ ! -f "$PIM_WEB_CONTRACT" ]; then
    pim_web_err "根契约不存在：$PIM_WEB_CONTRACT"
    rm -rf "$tmp"
    return 4
  fi
  pim_web_normalize "$PIM_WEB_CONTRACT" "$norm" || {
    pim_web_err "无法规范化根契约：$PIM_WEB_CONTRACT"
    rm -rf "$tmp"
    return 4
  }
  pim_web_validate_payload "$norm" || { rm -rf "$tmp"; return 4; }
  pim_web_generate_from "$norm" "$gen" || { rm -rf "$tmp"; return 4; }

  dir="$(dirname "$PIM_WEB_GENERATED")"
  base="$(basename "$PIM_WEB_GENERATED")"
  mkdir -p "$dir"
  cp "$gen" "$dir/.$base.tmp.$$" && mv -f "$dir/.$base.tmp.$$" "$PIM_WEB_GENERATED" || {
    pim_web_err "写入 $PIM_WEB_GENERATED 失败"
    rm -rf "$tmp"
    return 4
  }
  pim_web_log "✅ 已从 contract/openapi.json 重新生成 $PIM_WEB_GENERATED（sha256=$(pim_web_sha256 "$norm")，$(pim_web_bytes "$norm") 字节）"
  pim_web_log "   请提交 generated.ts；不要手工编辑。"
  rm -rf "$tmp"
  return 0
}