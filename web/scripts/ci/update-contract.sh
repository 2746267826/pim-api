#!/usr/bin/env bash
# 写入口：从根 contract/openapi.json 重新生成 web/client-web/src/api/generated.ts。
# 契约变更后运行本脚本并提交结果；不要手工编辑 generated.ts。
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=web/scripts/ci/contract-lib.sh
. "$HERE/contract-lib.sh"
pim_web_update "$ROOT"
exit $?