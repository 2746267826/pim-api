#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 历史脚本 / Historical shim
#
# 旧的交付台账由 awk 直接从交付包功能表生成。自 2026-11-28 起，唯一事实源是
#   docs/product/delivery/ledger.json
# 当前视图由
#   scripts/ci/render-delivery-ledger.py
# 生成到 docs/product/pim-delivery-ledger.md。
#
# 本脚本不再解析功能表，也不再产出可编辑台账。保留它只为让旧调用得到明确提示，
# 并转发到新的 renderer；手工编辑生成视图会被 validate-delivery-ledger.py 拒绝。
#
# 用法：
#   scripts/ci/generate-pim-delivery-ledger.sh                    # 渲染当前视图
#   scripts/ci/generate-pim-delivery-ledger.sh <ignored> <out.md> # 兼容旧参数，忽略源表，输出到 <out.md>
# ---------------------------------------------------------------------------
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

echo "注意：generate-pim-delivery-ledger.sh 已改为历史兼容入口，台账事实源为 docs/product/delivery/*.json。" >&2

if [[ $# -eq 0 ]]; then
  exec python3 "$script_dir/render-delivery-ledger.py" --root "$repo_root"
fi

if [[ $# -ne 2 ]]; then
  echo "用法：$0 [<已忽略的源表> <输出台账.md>]" >&2
  exit 2
fi

echo "警告：源表参数 '$1' 已忽略；只按结构化源渲染。" >&2
exec python3 "$script_dir/render-delivery-ledger.py" --root "$repo_root" --output "$2"
