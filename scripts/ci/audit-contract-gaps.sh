#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约缺口审计 · 只读校验（CI 调用的就是它）
#
# 语义：`contract/openapi.json` 是对客户端**唯一有约束力的承诺**。本脚本回答：
#       「只看契约，别人能不能据此写出一个客户端？不能的地方在哪里？」
#       —— 与「契约是否与代码同步」的新鲜度门禁互补，互不替代。
#
# 只读保证：不写任何仓内文件；不联网；不装依赖。
#           人工出报告：python3 scripts/ci/audit-contract-gaps.py --source-root src
#
# 门禁规则：
#   * P0（基线里有、当前没有的 operation）与 P1（客户端会猜错/写不出来）→ 必须为 0；
#   * 已识别的历史缺口在 docs/contract-baseline.json 的 approvedGaps 里登记，且必须写全
#     reason / owner / count / expires：count 是该类别当前的发现数，**新增同类实例会让 count
#     对不上而失败**（不会被「这个类别早已批准」静默放行），expires 到期后批准失效；
#     登记后不计入门禁，但报告里仍逐条列出并标「已批准」；
#   * 新出现的类别默认失败 —— 这条门禁的用途是「不变得更差」，而不是「立刻全绿」。
#
# 用法：
#   bash scripts/ci/audit-contract-gaps.sh                 # 门禁（P0/P1）
#   bash scripts/ci/audit-contract-gaps.sh --strict        # 连 P2/P3 也算失败
#   bash scripts/ci/audit-contract-gaps.sh --baseline <旧契约快照>   # 同时检查端点删除
#
# 退出码：
#   0  没有未批准的 P0/P1 发现
#   1  有未批准的 P0/P1 发现
#   2  环境缺失（没有可用的 python 3.8+）
#   4  输入非法（契约/基线缺失或格式不对）—— fail-closed
# ---------------------------------------------------------------------------
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
AUDIT="$HERE/audit-contract-gaps.py"
CONTRACT="$ROOT/contract/openapi.json"
CONFIG="$ROOT/docs/contract-baseline.json"

STRICT=0
BASELINE=()
while [ $# -gt 0 ]; do
  case "$1" in
    --strict) STRICT=1; shift ;;
    --baseline) BASELINE=(--baseline "$2"); shift 2 ;;
    -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}"; exit 0 ;;
    *) echo "未知参数：$1" >&2; exit 2 ;;
  esac
done

# 选一个「真能跑」的 python：Windows 上常有应用商店占位程序（任何调用都退出 49）。
PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 \
    && "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    PY="$candidate"; break
  fi
done
if [ -z "$PY" ]; then
  echo "需要一个可用的 python 3.8+（审计器只用标准库）。" >&2
  exit 2
fi

[ -f "$CONTRACT" ] || { echo "契约不存在：$CONTRACT" >&2; exit 4; }

echo "== 契约缺口审计（只读） =="
echo "仓        : $ROOT"
echo "契约      : $CONTRACT"
echo "审计基线  : $CONFIG"
echo "源码根    : $ROOT/src"
echo ""

ARGS=(--contract "$CONTRACT" --source-root "$ROOT/src" --config "$CONFIG" --fail-on)
[ "$STRICT" = "1" ] && ARGS+=(--strict)
[ "${#BASELINE[@]}" -gt 0 ] && ARGS+=("${BASELINE[@]}")

"$PY" "$AUDIT" "${ARGS[@]}"
RC=$?
echo ""
if [ "$RC" -eq 0 ]; then
  echo "契约缺口审计：通过（无未批准的 P0/P1 发现）"
elif [ "$RC" -eq 1 ]; then
  echo "契约缺口审计：失败 —— 有新出现的 P0/P1 缺口，见上方未标「已批准」的条目" >&2
fi
exit "$RC"
