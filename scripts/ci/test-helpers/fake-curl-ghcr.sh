#!/usr/bin/env bash
# 测试替身：按 URL 返回不同的 GHCR 响应，供 check-ghcr-public.sh 的自检使用。
#   FAKE_GHCR_TOKEN_JSON  匿名 token 端点的响应
#   FAKE_GHCR_TAGS_JSON   tags/list 端点的响应
set -uo pipefail
for a in "$@"; do
  case "$a" in
    *"/token?"*) printf '%s' "${FAKE_GHCR_TOKEN_JSON:-}"; exit 0 ;;
    *"/tags/list"*) printf '%s' "${FAKE_GHCR_TAGS_JSON:-}"; exit 0 ;;
  esac
done
exit 0
