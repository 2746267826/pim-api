#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 前端版本台账（web-version.json）的公共原语。
#
# 前端与 API 同仓构建，「前端是哪一版」只有一个真相来源：本次烘焙好的
# api 镜像内 /app/wwwroot/web-version.json。本文件只读镜像/校验字段，
# 不访问 registry、不写任何产物。
# ---------------------------------------------------------------------------
set -euo pipefail

PIM_WEB_VERSION_FILE="web-version.json"

pim_docker() { echo "${PIM_DOCKER:-docker}"; }

pim_web_log() { echo "[web-version] $*" >&2; }
pim_web_err() { echo "[web-version][错误] $*" >&2; }

pim_web_short_sha() {
  local sha="$1"
  if [[ ! "$sha" =~ ^[0-9a-f]{7}$ ]]; then
    pim_web_err "前端 git sha 必须是 7 位小写十六进制，实际为 '${sha}'"
    return 1
  fi
  echo "$sha"
}

# 从本地镜像里取出一个文件（不运行镜像）。
#
# 用法: pim_image_cat <image-ref> <path-in-image> <out-file> --local
#   只接受本地镜像：台账描述的是“刚刚 build/load 出来、还没推”的镜像。
#   本地不存在就硬失败，绝不回退去拉远端同名 tag。
pim_image_cat() {
  local image="$1" path="$2" out="$3" mode="${4:-}" docker cid tmp rc=0
  [ "$mode" = "--local" ] || { pim_web_err "只支持 --local（台账必须读本次本地烘焙的镜像）"; return 2; }
  docker="$(pim_docker)"
  tmp="$(mktemp "${TMPDIR:-/tmp}/pim-image-cat.XXXXXX")"
  if ! "$docker" image inspect "$image" >/dev/null 2>&1; then
    pim_web_err "本地不存在该镜像（要求 --local 时不许改成拉远端）：$image"
    rm -f "$tmp"
    return 1
  fi
  cid="$("$docker" create "$image" /pim-image-reader)" || { pim_web_err "create 失败：$image"; rm -f "$tmp"; return 1; }
  if ! "$docker" cp "${cid}:${path}" "$tmp" >&2; then
    pim_web_err "镜像内缺少 ${path}：$image"
    rc=1
  else
    mv -f "$tmp" "$out"
    tmp=""
  fi
  "$docker" rm -f "$cid" >/dev/null 2>&1 || true
  [ -n "$tmp" ] && rm -f "$tmp"
  return $rc
}

# 校验 web-version.json 的三个契约字段并回显 key=value 列表（stdout）。
pim_web_validate_version_file() {
  local file="$1" version sha built
  if [ ! -s "$file" ]; then
    pim_web_err "前端版本文件为空或不存在：$file"
    return 1
  fi
  if ! jq -e 'type == "object"' "$file" >/dev/null 2>&1; then
    pim_web_err "前端版本文件不是 JSON 对象：$file"
    return 1
  fi
  version="$(jq -r '.version // ""' "$file")"
  sha="$(jq -r '.gitSha // ""' "$file")"
  built="$(jq -r '.builtAt // ""' "$file")"
  if [ -z "$version" ] || [ -z "$sha" ] || [ -z "$built" ]; then
    pim_web_err "前端版本文件缺少契约字段（version/gitSha/builtAt）：$(jq -c . "$file" 2>/dev/null || echo '<unparseable>')"
    return 1
  fi
  pim_web_short_sha "$sha" >/dev/null
  printf 'web_version=%s\n' "$version"
  printf 'web_git_sha=%s\n' "$sha"
  printf 'web_built_at=%s\n' "$built"
}