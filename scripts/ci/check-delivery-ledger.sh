#!/usr/bin/env bash
# 本地交付台账门禁：校验结构化源 → 重新渲染 → 再校验 → 跑负例测试。
# 纯本地文件操作，不访问网络；CI 接入留给单独工单。
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

python3 "$script_dir/validate-delivery-ledger.py" --root "$repo_root"
python3 "$script_dir/render-delivery-ledger.py" --root "$repo_root"
python3 "$script_dir/validate-delivery-ledger.py" --root "$repo_root"
python3 "$script_dir/test-validate-delivery-ledger.py"
