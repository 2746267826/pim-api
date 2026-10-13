#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 判定 GHCR 上某个镜像 tag 是否存在（用于"不可变 tag 只写一次"）。
#
# 关键语义：**只有明确"不存在"才允许创建同名 tag**。探测本身失败（鉴权/网络/
# token 服务故障/凭证助手缺失）必须报"未知"并让调用方报红 —— 否则一次网络抖动
# 就会被当成"tag 不存在"，把已经存在的不可变 tag 覆盖掉。
#
# 用法：dist-tag-state.sh <ghcr.io/owner/package:tag>
#   输出（stdout，一行）：exists | missing
#   退出码：0 = 判定成功（存在或明确不存在，见输出）；1 = 探测失败（未知，不许创建）
# ---------------------------------------------------------------------------
set -euo pipefail

REF="${1:-}"
[ -n "$REF" ] || { echo "[dist-tag][错误] 用法：dist-tag-state.sh <image:tag>" >&2; exit 2; }
DOCKER="${PIM_DOCKER:-docker}"

set +e
OUT="$("$DOCKER" manifest inspect "$REF" 2>&1)"
RC=$?
set -e

if [ "$RC" -eq 0 ]; then
  echo "exists"
  exit 0
fi

# 只认 registry 对"这个 manifest 不存在"的明确答复，不做宽泛的 "not found" 匹配：
# 后者会把 "Get https://ghcr.io/token: 404 Not Found"、"executable file not found in $PATH"
# 这类探测故障误判成"tag 不存在"。
if grep -qiE 'no such manifest|manifest unknown|manifest_unknown' <<<"$OUT"; then
  echo "missing"
  exit 0
fi

echo "[dist-tag][错误] 无法确认 $REF 是否存在（探测失败，拒绝按'不存在'处理）：${OUT}" >&2
exit 1
