#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 校验「给人看的」发行说明（release notes）。
#
# 这份说明由外部（Hermes / 维护者）写好再传进来，正式版正文就是它。它能写得很
# 好看，但**不许编造**：所以这里只做机械校验，不做润色——
#   1. 不能为空、不能是占位符；
#   2. 必须是人能读的结构（至少一条列表项），不是一段糊在一起的文本；
#   3. 至少要引用一个合并进来的 PR（#123），且**每个**被引用的 PR 都必须真的
#      合并过。对不上号 → 直接失败，绝不把编造的条目发进正式版。
#
# 用法：
#   validate-release-notes.sh --file notes.md --repo owner/repo
#   (--file 省略时从 stdin 读；--repo 省略时跳过 PR 存在性校验，仅做结构校验)
#
# 输出：成功打印统计；失败逐条打印 ::error:: 并以 1 退出。
# ---------------------------------------------------------------------------
set -euo pipefail

FILE=""
REPO=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --file) FILE="$2"; shift 2 ;;
    --repo) REPO="$2"; shift 2 ;;
    *) echo "Unknown arg: $1" >&2; exit 2 ;;
  esac
done

WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

NOTES="$WORKDIR/notes.md"
if [[ -n "$FILE" ]]; then
  if [[ ! -f "$FILE" ]]; then
    echo "::error::notes file not found: $FILE" >&2
    echo "notes file not found: $FILE" >&2
    exit 1
  fi
  cp "$FILE" "$NOTES"
else
  cat > "$NOTES"
fi

fail=0
err() { echo "::error::$*" >&2; echo "$*" >&2; fail=1; }

# ---- 1. 非空 ----
if ! grep -qE '[^[:space:]]' "$NOTES"; then
  err "release notes are empty"
fi

# ---- 2. 真实内容，不是占位符 ----
if grep -qiE '\bTODO\b|\bTBD\b|待补充|待填写|FIXME|xxx' "$NOTES"; then
  err "release notes still contain a placeholder (TODO / TBD / 待补充 / FIXME)"
fi

# ---- 3. 人能读的结构：至少一条列表项 ----
if ! grep -qE '^[[:space:]]*[-*][[:space:]]+[^[:space:]]' "$NOTES"; then
  err "release notes must contain at least one bullet line like '- 修复…（#123）'"
fi

# ---- 4. 必须引用已合并的 PR ----
if grep -oE '#[0-9]+' "$NOTES" | sed 's/#//' | sort -un > "$WORKDIR/prs.txt" && [[ -s "$WORKDIR/prs.txt" ]]; then
  PR_COUNT="$(wc -l < "$WORKDIR/prs.txt" | tr -d ' ')"
else
  PR_COUNT=0
  err "release notes must reference at least one merged PR, e.g. '…（#123）'"
fi

if [[ "$PR_COUNT" -gt 0 && -n "$REPO" ]]; then
  while IFS= read -r N; do
    [[ -z "$N" ]] && continue
    if ! MERGED_AT="$(gh api "repos/${REPO}/pulls/${N}" --jq '.merged_at' 2>/dev/null)"; then
      err "PR #${N} does not exist in ${REPO}"
      continue
    fi
    if [[ -z "$MERGED_AT" || "$MERGED_AT" == "null" ]]; then
      err "PR #${N} in ${REPO} is not merged; only merged PRs may appear in release notes"
    fi
  done < "$WORKDIR/prs.txt"
fi

if [[ "$fail" -ne 0 ]]; then
  echo "release notes validation failed" >&2
  exit 1
fi

echo "release notes OK: ${PR_COUNT} merged PR reference(s) verified${REPO:+ against $REPO}"
