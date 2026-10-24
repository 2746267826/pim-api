#!/usr/bin/env bash
# 同仓契约 checker/update 行为测试（离线、隔离 fixture、不写真实工作树）。
# 前置：已在 web/client-web 执行 npm ci。
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../../.." && pwd)"
# shellcheck source=web/scripts/ci/contract-lib.sh
. "$HERE/contract-lib.sh"
pim_web_paths "$ROOT"

REAL_CLI="$PIM_WEB_CLI"
if [ ! -x "$REAL_CLI" ]; then
  pim_web_err "未找到仓内锁定的生成器 $REAL_CLI（请先在 web/client-web 执行 npm ci）"
  exit 4
fi

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-web-contract-test.XXXXXX")" || exit 4
FIX="$TMP/repo"
cleanup() { rm -rf "$TMP"; }
trap cleanup EXIT

PASS=0; FAIL=0
record() { # <name> <0=ok> <observed>
  if [ "$2" = "0" ]; then PASS=$((PASS+1)); printf 'PASS  %s :: %s\n' "$1" "$3"
  else FAIL=$((FAIL+1)); printf 'FAIL  %s :: %s\n' "$1" "$3"; fi
}
expect_rc() { # <name> <expected> <actual>
  if [ "$3" = "$2" ]; then record "$1" 0 "rc=$3"
  else record "$1" 1 "expected rc=$2, got rc=$3"; fi
}
check_fixture() { pim_web_check "$FIX" >/dev/null 2>&1; echo $?; }
update_fixture() { pim_web_update "$FIX" >/dev/null 2>&1; echo $?; }
hash_of() { pim_web_sha256 "$1"; }

# --- fixture：真实契约 + 真实生成的副本；生成器用 wrapper 调真实锁定 CLI ---
mkdir -p "$FIX/contract" "$FIX/web/client-web/src/api" "$FIX/web/client-web/node_modules/.bin"
pim_web_normalize "$PIM_WEB_CONTRACT" "$FIX/contract/openapi.json" || exit 4
cp "$PIM_WEB_GENERATED" "$FIX/web/client-web/src/api/generated.ts" || exit 4
cat > "$FIX/web/client-web/node_modules/.bin/openapi-typescript" <<WRAPPER
#!/usr/bin/env bash
exec "$REAL_CLI" "\$@"
WRAPPER
chmod +x "$FIX/web/client-web/node_modules/.bin/openapi-typescript"

# 1) 初始 fixture 必须是绿的
expect_rc "fixture starts green" 0 "$(check_fixture)"

# 2) 根契约增加一个合法 path（未重新生成）→ checker 必须失败
node - "$FIX/contract/openapi.json" <<'JS'
const fs = require('fs');
const p = process.argv[2];
const doc = JSON.parse(fs.readFileSync(p, 'utf8'));
doc.paths['/__fixture_only__'] = {
  get: { responses: { '200': { description: 'fixture only' } } },
};
fs.writeFileSync(p, JSON.stringify(doc, null, 2) + '\n', 'utf8');
JS
expect_rc "checker detects contract drift" 1 "$(check_fixture)"

# 3) update 恢复绿色
expect_rc "update after contract drift" 0 "$(update_fixture)"
expect_rc "checker green after update" 0 "$(check_fixture)"

# 4) 手工篡改 generated.ts → checker 必须失败
printf '\n// fixture tamper\n' >> "$FIX/web/client-web/src/api/generated.ts"
expect_rc "checker detects generated tamper" 1 "$(check_fixture)"

# 5) update 再次恢复绿色
expect_rc "update after generated tamper" 0 "$(update_fixture)"
expect_rc "checker green after second update" 0 "$(check_fixture)"

# 6) checker 前后文件 hash 不变（只读）
H_CONTRACT_BEFORE="$(hash_of "$FIX/contract/openapi.json")"
H_GENERATED_BEFORE="$(hash_of "$FIX/web/client-web/src/api/generated.ts")"
expect_rc "read-only check run" 0 "$(check_fixture)"
H_CONTRACT_AFTER="$(hash_of "$FIX/contract/openapi.json")"
H_GENERATED_AFTER="$(hash_of "$FIX/web/client-web/src/api/generated.ts")"
if [ "$H_CONTRACT_BEFORE" = "$H_CONTRACT_AFTER" ] && [ "$H_GENERATED_BEFORE" = "$H_GENERATED_AFTER" ]; then
  record "checker leaves hashes unchanged" 0 "contract+generated identical"
else
  record "checker leaves hashes unchanged" 1 "hash changed"
fi

# 7) 非法输入：update 必须失败且保留原生成物
printf '<html>not a contract</html>\n' > "$FIX/contract/openapi.json"
H_BEFORE_INVALID="$(hash_of "$FIX/web/client-web/src/api/generated.ts")"
expect_rc "update rejects invalid contract" 4 "$(update_fixture)"
H_AFTER_INVALID="$(hash_of "$FIX/web/client-web/src/api/generated.ts")"
if [ "$H_BEFORE_INVALID" = "$H_AFTER_INVALID" ]; then
  record "invalid update keeps generated.ts" 0 "hash unchanged"
else
  record "invalid update keeps generated.ts" 1 "generated.ts was modified"
fi

# 8) 非法输入：checker 也必须报配置/输入错误，而不是陈旧
expect_rc "checker rejects invalid contract" 4 "$(check_fixture)"

printf '\n%d passed, %d failed\n' "$PASS" "$FAIL"
[ "$FAIL" -eq 0 ]