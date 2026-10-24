#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 写产物内的前端版本文件 web-version.json（REQ-2，契约）。
#
# 字段固定三项：version（前端版本号）/ gitSha（前端 git sha，7 位短 SHA）/ builtAt（构建时间 UTC）。
# 版本号与 sha 必须与同一次构建注入的值**同源**：调用方传进来的就是 CI 注入构建的那两个值，
# 这里不另算一份（不允许出现「注入的是 A、文件里写的是 B」）。
#
# 用法：
#   scripts/ci/write-web-version.sh --out <path> \
#     [--version <V>] [--git-sha <7hex>] [--built-at <ISO8601Z>]
#   未显式给出的取环境变量 PIM_WEB_VERSION / PIM_WEB_GIT_SHA / PIM_WEB_BUILT_AT；
#   builtAt 默认取当前 UTC 时间（--built-at 既是为了可测，也让重放/本地构建可复现）。
# ---------------------------------------------------------------------------
set -euo pipefail

OUT=""
VERSION="${PIM_WEB_VERSION:-}"
GIT_SHA="${PIM_WEB_GIT_SHA:-}"
BUILT_AT="${PIM_WEB_BUILT_AT:-}"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --out) OUT="${2:-}"; shift 2 ;;
    --version) VERSION="${2:-}"; shift 2 ;;
    --git-sha) GIT_SHA="${2:-}"; shift 2 ;;
    --built-at) BUILT_AT="${2:-}"; shift 2 ;;
    *) echo "[web-version] 未知参数：$1" >&2; exit 2 ;;
  esac
done

[ -n "$OUT" ] || { echo "[web-version][错误] --out 必填" >&2; exit 2; }
[ -n "$VERSION" ] || { echo "[web-version][错误] 缺少前端版本号（--version 或 PIM_WEB_VERSION）" >&2; exit 2; }
if [[ ! "$GIT_SHA" =~ ^[0-9a-f]{7}$ ]]; then
  echo "[web-version][错误] 前端 git sha 必须是 7 位小写十六进制，实际为 '${GIT_SHA}'" >&2
  exit 2
fi
if [ -z "$BUILT_AT" ]; then
  BUILT_AT="$(date -u +%Y-%m-%dT%H:%M:%SZ)"
fi
if [[ ! "$BUILT_AT" =~ ^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z$ ]]; then
  echo "[web-version][错误] 构建时间必须是 UTC 的 ISO8601（YYYY-MM-DDTHH:MM:SSZ），实际为 '${BUILT_AT}'" >&2
  exit 2
fi

command -v node >/dev/null 2>&1 || { echo "[web-version][错误] 缺少 node（用于写 web-version.json）" >&2; exit 2; }

mkdir -p "$(dirname "$OUT")"
# 值只经环境变量传给 node（不进 argv），避免命令注入；字段固定三项。
OUT="$OUT" VERSION="$VERSION" GIT_SHA="$GIT_SHA" BUILT_AT="$BUILT_AT" node <<'JS'
const fs = require('fs');
const doc = {
  version: process.env.VERSION,
  gitSha: process.env.GIT_SHA,
  builtAt: process.env.BUILT_AT,
};
fs.writeFileSync(process.env.OUT, JSON.stringify(doc, null, 2) + '\n', 'utf8');
JS
echo "[web-version] 已写 $OUT：version=$VERSION gitSha=$GIT_SHA builtAt=$BUILT_AT" >&2
