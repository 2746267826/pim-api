#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# pim-web 产物镜像（ghcr.io/2746267826/pim-web-dist）的公共定义与校验。
#
# 「前端是哪一版」在本仓只有一个真相来源：产物镜像里的 web-version.json。
# 本文件只提供读取/校验原语，不写入任何产物。
# ---------------------------------------------------------------------------
set -euo pipefail

# 契约（工单 §5）：包名、tag 规则、版本文件与字段名。
PIM_WEB_DIST_IMAGE_DEFAULT="ghcr.io/2746267826/pim-web-dist"
PIM_WEB_VERSION_FILE="web-version.json"
# 镜像内路径：产物镜像的根目录就是 dist 的内容（只含静态文件）。
PIM_WEB_VERSION_PATH_IN_DIST_IMAGE="/${PIM_WEB_VERSION_FILE}"

pim_web_dist_image() { echo "${PIM_WEB_DIST_IMAGE:-$PIM_WEB_DIST_IMAGE_DEFAULT}"; }
pim_docker() { echo "${PIM_DOCKER:-docker}"; }

pim_web_log() { echo "[web-dist] $*" >&2; }
pim_web_err() { echo "[web-dist][错误] $*" >&2; }

# 不可变 tag：sha-<7 位短 SHA>（契约）。输入允许带或不带 sha- 前缀的 7 位十六进制。
pim_web_is_immutable_ref() { [[ "$1" =~ ^sha-[0-9a-f]{7}$ ]]; }

pim_web_short_sha() {
  local sha="$1"
  if [[ ! "$sha" =~ ^[0-9a-f]{7}$ ]]; then
    pim_web_err "前端 git sha 必须是 7 位小写十六进制，实际为 '${sha}'"
    return 1
  fi
  echo "$sha"
}

# 从任意镜像里取出一个文件（不运行镜像：产物镜像是 scratch，没有 shell）。
# 用法: pim_image_cat <image-ref> <path-in-image> <out-file>
pim_image_cat() {
  local image="$1" path="$2" out="$3" docker cid tmp rc=0
  docker="$(pim_docker)"
  tmp="$(mktemp "${TMPDIR:-/tmp}/pim-image-cat.XXXXXX")"
  if ! "$docker" image inspect "$image" >/dev/null 2>&1; then
    pim_web_log "本地无 $image，按匿名方式拉取（产物包必须是 public）"
    "$docker" pull "$image" >&2 || { pim_web_err "拉取失败：$image"; rm -f "$tmp"; return 1; }
  fi
  # scratch 镜像没有 CMD：给一个不会被执行的占位命令，才能 create/cp。
  cid="$("$docker" create "$image" /pim-web-dist-reader)" || { pim_web_err "create 失败：$image"; rm -f "$tmp"; return 1; }
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

# 校验 web-version.json 的三个契约字段并回显为一个 key=value 列表（stdout）。
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

# 取某个镜像引用的 registry digest（sha256:...）；取不到即失败，不许静默用空值。
pim_image_digest() {
  local image="$1" docker digest
  docker="$(pim_docker)"
  digest="$("$docker" inspect --format '{{range .RepoDigests}}{{println .}}{{end}}' "$image" 2>/dev/null \
    | sed -n 's/.*@\(sha256:[0-9a-f]\{64\}\)$/\1/p' | head -n1)"
  if [ -z "$digest" ]; then
    pim_web_err "无法解析镜像 digest（RepoDigests 为空）：$image"
    return 1
  fi
  echo "$digest"
}
