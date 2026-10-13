#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约变化检测（工单 §415 §9.2：契约是否变化的判定不依赖现有路径过滤器）。
#
# 为什么单独一个脚本、而不是复用 ci.yml 里 dorny/paths-filter 的结果：
#   1) paths-filter 的语义是「相对 base 的**变更文件列表**」，而这里要问的是
#      「`contract/openapi.json` 的**字节**是否与上一个基线不同」——字节比路径
#      更强：路径出现在列表里但内容回退成一致，不该通知客户端（客户端 refresh
#      会得到一个空 diff 的 PR）；
#   2) §9 的假绿根因就是「变更检测漏掉了 contract/**」。把判定做成独立脚本，
#      它就不再依赖那份可能漏项的过滤器，也不会因为过滤器将来被改窄而静默失效。
#
# 判定口径（fail-open 的方向是**宁可通知，不可漏通知**）：
#   * base 为空 / 全零 SHA / ref 无法解析 / 该路径在 base 上不存在 → changed=true，
#     原因写进日志。客户端侧收到通知后会先比对字节：字节相同就不开 PR，
#     所以多发一次通知的代价只是多跑一次客户端 workflow，而漏发的代价是
#     客户端一直对着陈旧契约开发（本工单要消灭的正是这个）。
#   * 只有「base 与 head 上该路径的 blob 相同」才判 changed=false。
#
# 用法：
#   scripts/ci/detect-contract-change.sh [--base <ref>] [--head <ref>]
#                                        [--path <path>] [--force true|false]
#                                        [--repo <git-dir>]
# 环境变量等价物（CI 里更常用，避免长命令行）：
#   PIM_CONTRACT_CHANGE_BASE / _HEAD / _PATH / _FORCE / _REPO
#
# 输出（stdout；有 $GITHUB_OUTPUT 时同时追加到那里，两者内容完全一致）：
#   changed=true|false     要不要通知客户端仓
#   reason=<一行原因>       给人看/给 job summary 用
#   base_ref / base_blob / head_blob
#   contract_sha256 / contract_bytes / contract_commit
#
# 退出码：
#   0 判定完成（changed 为 true 或 false 都算完成）
#   2 参数错误
#   3 git 仓库/ref 不可用（配置错误）
#   4 契约路径在 head 上不存在（配置错误，必须可见 —— 静默 false 会让机制永远不通知）
# ---------------------------------------------------------------------------
set -euo pipefail

BASE_REF="${PIM_CONTRACT_CHANGE_BASE:-}"
HEAD_REF="${PIM_CONTRACT_CHANGE_HEAD:-HEAD}"
CONTRACT_PATH="${PIM_CONTRACT_CHANGE_PATH:-contract/openapi.json}"
FORCE="${PIM_CONTRACT_CHANGE_FORCE:-false}"
REPO_DIR="${PIM_CONTRACT_CHANGE_REPO:-.}"

ZERO40="0000000000000000000000000000000000000000"

usage_err() { echo "[contract-change][错误] $*" >&2; exit 2; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --base)  BASE_REF="${2:-}"; shift 2 ;;
    --head)  HEAD_REF="${2:-}"; shift 2 ;;
    --path)  CONTRACT_PATH="${2:-}"; shift 2 ;;
    --force) FORCE="${2:-}"; shift 2 ;;
    --repo)  REPO_DIR="${2:-}"; shift 2 ;;
    -h|--help) sed -n '2,45p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) usage_err "未知参数：$1" ;;
  esac
done

case "$FORCE" in true|false) ;; *) usage_err "--force 必须是 true 或 false（收到 '$FORCE'）" ;; esac
[ -n "$HEAD_REF" ] || usage_err "--head 不能为空"
[ -n "$CONTRACT_PATH" ] || usage_err "--path 不能为空"

git -C "$REPO_DIR" rev-parse --git-dir >/dev/null 2>&1 \
  || { echo "[contract-change][错误] '$REPO_DIR' 不是可用的 git 仓库" >&2; exit 3; }

# 解析 head：head 解析不了是配置错误（没有 head 就无从谈契约），直接红。
HEAD_COMMIT="$(git -C "$REPO_DIR" rev-parse --verify --quiet "${HEAD_REF}^{commit}" || true)"
[ -n "$HEAD_COMMIT" ] || { echo "[contract-change][错误] 无法解析 --head '$HEAD_REF' 为 commit" >&2; exit 3; }

HEAD_BLOB="$(git -C "$REPO_DIR" rev-parse --verify --quiet "${HEAD_COMMIT}:${CONTRACT_PATH}" || true)"
if [ -z "$HEAD_BLOB" ]; then
  echo "[contract-change][错误] 契约路径 '${CONTRACT_PATH}' 在 head（${HEAD_REF} -> ${HEAD_COMMIT}）上不存在；检测无法进行（不静默判 false）" >&2
  exit 4
fi

CONTRACT_SHA256="$(git -C "$REPO_DIR" cat-file blob "$HEAD_BLOB" | sha256sum | cut -d' ' -f1)"
CONTRACT_BYTES="$(git -C "$REPO_DIR" cat-file -s "$HEAD_BLOB")"

CHANGED=""
REASON=""
BASE_BLOB="none"
declare -a NOTES=()

if [ "$FORCE" = "true" ]; then
  CHANGED="true"
  REASON="force=true（手动要求重新通知；跳过字节比较）"
  NOTES+=("强制通知：--force=true，不做 base/head 比较")
else
  BASE_USABLE="true"
  if [ -z "$BASE_REF" ]; then
    BASE_USABLE="false"; NOTES+=("没有基线 ref（首次推送/未提供 --base）")
    REASON="没有基线 ref，无法比较 → 按契约可能已变化处理"
  elif [ "$BASE_REF" = "$ZERO40" ]; then
    BASE_USABLE="false"; NOTES+=("基线是全零 SHA（强推 / 新建分支的 push.before）")
    REASON="基线是全零 SHA，无法比较 → 按契约可能已变化处理"
  else
    BASE_COMMIT="$(git -C "$REPO_DIR" rev-parse --verify --quiet "${BASE_REF}^{commit}" || true)"
    if [ -z "$BASE_COMMIT" ]; then
      BASE_USABLE="false"; NOTES+=("无法解析基线 ref '$BASE_REF' 为 commit")
      REASON="基线 ref '$BASE_REF' 无法解析成 commit，无法比较 → 按契约可能已变化处理"
    else
      BASE_BLOB="$(git -C "$REPO_DIR" rev-parse --verify --quiet "${BASE_COMMIT}:${CONTRACT_PATH}" || true)"
      if [ -z "$BASE_BLOB" ]; then
        BASE_BLOB="none"
        BASE_USABLE="false"; NOTES+=("契约路径 '$CONTRACT_PATH' 在基线 ${BASE_REF} 上不存在（本次新加入）")
        REASON="契约路径在基线 ${BASE_REF} 上不存在（本次新加入）→ 按已变化处理"
      fi
    fi
  fi

  if [ "$BASE_USABLE" = "true" ]; then
    if [ "$BASE_BLOB" = "$HEAD_BLOB" ]; then
      CHANGED="false"
      REASON="契约字节与基线一致（blob ${HEAD_BLOB:0:12}）→ 不通知"
    else
      CHANGED="true"
      REASON="契约字节与基线不同（base blob ${BASE_BLOB:0:12} → head blob ${HEAD_BLOB:0:12}）"
    fi
  else
    CHANGED="true"
  fi
fi

EMIT="$(cat <<EOF
changed=${CHANGED}
reason=${REASON}
base_ref=${BASE_REF}
base_blob=${BASE_BLOB}
head_blob=${HEAD_BLOB}
contract_sha256=${CONTRACT_SHA256}
contract_bytes=${CONTRACT_BYTES}
contract_commit=${HEAD_COMMIT}
EOF
)"

for n in "${NOTES[@]:-}"; do [ -n "$n" ] && echo "[contract-change] $n" >&2; done
echo "[contract-change] 判定：changed=${CHANGED}；路径=${CONTRACT_PATH}；head=${HEAD_COMMIT}；base=${BASE_REF:-<none>}；size=${CONTRACT_BYTES}B；sha256=${CONTRACT_SHA256:0:12}" >&2

if [ -n "${GITHUB_OUTPUT:-}" ]; then
  printf '%s\n' "$EMIT" >> "$GITHUB_OUTPUT"
fi
printf '%s\n' "$EMIT"
