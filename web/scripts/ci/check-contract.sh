#!/usr/bin/env bash
# 只读校验：根 contract/openapi.json 重生成后必须与 generated.ts 逐字节一致。
# 退出码：0 一致 / 1 陈旧 / 4 配置或输入非法。不写仓、不访问网络。
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=web/scripts/ci/contract-lib.sh
. "$HERE/contract-lib.sh"
pim_web_check "$ROOT"
exit $?