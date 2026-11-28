#!/usr/bin/env bash
# 本地交付台账门禁：从结构化源重新渲染 → 校验 → 跑聚焦测试。
# 纯本地文件操作，不访问网络；CI 接入留给单独工单。
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "$script_dir/../.." && pwd)"

python3 "$script_dir/render-delivery-ledger.py" --root "$repo_root"
python3 "$script_dir/validate-delivery-ledger.py" --root "$repo_root"
python3 "$script_dir/test-validate-delivery-ledger.py"
