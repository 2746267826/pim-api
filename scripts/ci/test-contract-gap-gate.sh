#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约缺口门禁 · 端到端行为测试（离线；前置：python 3.8+、bash）
#
# 证明这条门禁不是「永远 PASS 的摆设」：
#   ① 仓内现状（已登记缺口）→ audit-contract-gaps.sh 退出 0；
#   ② 人为制造一个**新类别**的 P1 缺口（写操作开始缺 requestBody，且该类别未登记）
#      → 退出 1，并指出具体端点；
#   ③ 同一条缺口在基线里登记为已批准后 → 退出 0（证明批准机制真的生效）；
#   ④ --strict 下 P2/P3 也算失败；
#   ⑤ 非法契约 → 退出 4（fail-closed，绝不当作「没有缺口」）；
#   ⑥ gate 包装脚本把 1/4 原样传出（CI Gate 才能被它拦下）。
#
# 用临时副本运行，**不修改仓内任何文件**。
# 用法：bash scripts/ci/test-contract-gap-gate.sh
# ---------------------------------------------------------------------------
set -uo pipefail
export PYTHONDONTWRITEBYTECODE=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"

PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 \
    && "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    PY="$candidate"; break
  fi
done
[ -n "$PY" ] || { echo "需要一个可用的 python 3.8+" >&2; exit 2; }

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-gap-gate.XXXXXX")"
cleanup() { [ "${PIM_GAP_GATE_KEEP:-0}" = "1" ] || rm -rf "$TMP"; }
trap cleanup EXIT

PASS=0; FAIL=0
pass() { PASS=$((PASS + 1)); echo "  ok   $1"; }
fail() { FAIL=$((FAIL + 1)); echo "  FAIL $1"; }
expect_rc() { [ "$3" = "$2" ] && pass "$1" || fail "$1（期望 $2，实际 $3）"; }

AUDIT="$HERE/audit-contract-gaps.py"
CONTRACT="$ROOT/contract/openapi.json"
CONFIG="$ROOT/docs/contract-baseline.json"

echo "== 契约缺口门禁 · 端到端测试 =="

# ① 现状
"$PY" "$AUDIT" --contract "$CONTRACT" --source-root "$ROOT/src" --config "$CONFIG" --fail-on >"$TMP/now.txt" 2>&1
expect_rc "① 已登记缺口的现状通过门禁" 0 $?
grep -q "已批准" "$TMP/now.txt" && pass "① 报告里保留了已批准条目的明细" || fail "① 报告缺少已批准明细"

# ② 制造新类别缺口：在临时契约里把 POST 的 requestBody 全部删掉，
#    并在临时基线里移除 request-body 的批准 → 应当失败
"$PY" - "$CONTRACT" "$TMP/bad.json" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
for item in doc["paths"].values():
    for method, op in item.items():
        if isinstance(op, dict) and method in ("post", "put", "patch"):
            op.pop("requestBody", None)
json.dump(doc, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$PY" - "$CONFIG" "$TMP/cfg.json" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
cfg["approvedGaps"].pop("request-body", None)
json.dump(cfg, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$PY" "$AUDIT" --contract "$TMP/bad.json" --config "$TMP/cfg.json" --fail-on >"$TMP/bad.txt" 2>&1
expect_rc "② 新类别 P1 缺口让门禁失败" 1 $?
grep -q "POST /api/v1/calendar/events — 写操作没有声明 requestBody" "$TMP/bad.txt" \
  && pass "② 失败信息指出具体端点" || fail "② 失败信息没有指出端点"

# ③ 同一条缺口登记为已批准 → 放行。批准必须写清 owner/count/expires（review P1-3）：
#    数量按当前实际缺口数登记，新增实例会让 count 对不上。
"$PY" "$AUDIT" --contract "$TMP/bad.json" --config "$TMP/cfg.json" --json >"$TMP/bad-audit.json" 2>/dev/null
"$PY" - "$CONFIG" "$TMP/cfg2.json" "$TMP/bad-audit.json" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
report = json.load(open(sys.argv[3], encoding="utf-8"))
# 上限按「审计器实际产出的条数」登记，而不是自己另算一遍（否则会漏掉 is_api 等过滤条件）。
n = sum(1 for f in report["findings"] if f["category"] == "request-body")
cfg["approvedGaps"]["request-body"] = {"reason": "测试用：本用例在临时基线里登记该类别",
                                       "owner": "test", "count": n, "expires": "2999-12-31"}
json.dump(cfg, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$PY" "$AUDIT" --contract "$TMP/bad.json" --config "$TMP/cfg2.json" --fail-on >/dev/null 2>&1
expect_rc "③ 已批准后放行（批准机制生效）" 0 $?

# ③b 新增一个同类实例 → 超出批准上限，门禁必须失败（不是「这个类别早已批准」就永远放行）。
"$PY" - "$TMP/cfg2.json" "$TMP/cfg_exceeded.json" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
cfg["approvedGaps"]["request-body"]["count"] -= 1
json.dump(cfg, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$PY" "$AUDIT" --contract "$TMP/bad.json" --config "$TMP/cfg_exceeded.json" --fail-on >"$TMP/exceeded.txt" 2>&1
expect_rc "③b 新增实例超出批准上限 → 门禁失败" 1 $?
grep -q "批准上限" "$TMP/exceeded.txt" && pass "③b 指出超出上限的类别" || fail "③b 没有说明超出上限"

# ③c 过期的批准不再生效（review P2-4：批准会过期，避免永久豁免）。
"$PY" - "$TMP/cfg2.json" "$TMP/cfg_expired.json" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
cfg["approvedGaps"]["request-body"]["expires"] = "2000-01-01"
json.dump(cfg, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$PY" "$AUDIT" --contract "$TMP/bad.json" --config "$TMP/cfg_expired.json" --fail-on >"$TMP/expired.txt" 2>&1
expect_rc "③c 过期批准 → 门禁失败" 1 $?
grep -q "已于 2000-01-01 过期" "$TMP/expired.txt" && pass "③c 指出过期日期" || fail "③c 没有说明过期"

# ④ --strict：把某个 P3 类别（prop-desc）的批准项拿掉后，--strict 必须失败；
#    恢复批准后，--strict 也应放行（证明 strict 确实看 P2/P3，而不是永远红）。
"$PY" - "$CONFIG" "$TMP/cfg_strict.json" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
cfg["approvedGaps"].pop("prop-desc", None)
json.dump(cfg, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$PY" "$AUDIT" --contract "$CONTRACT" --source-root "$ROOT/src" --config "$TMP/cfg_strict.json" --fail-on --strict >/dev/null 2>&1
expect_rc "④ --strict 下未批准的 P3 缺口让门禁失败" 1 $?
"$PY" "$AUDIT" --contract "$CONTRACT" --source-root "$ROOT/src" --config "$TMP/cfg_strict.json" --fail-on >/dev/null 2>&1
expect_rc "④ 同样输入不加 --strict 时放行（默认只卡 P0/P1）" 0 $?
"$PY" "$AUDIT" --contract "$CONTRACT" --source-root "$ROOT/src" --config "$CONFIG" --fail-on --strict >/dev/null 2>&1
expect_rc "④ 全部登记后 --strict 放行" 0 $?

# ④b 端点删除检测（review I-2）：基线里记录了已发布的操作集；契约里少一条 → P0 必须变红。
#     这里删掉当前契约的第一个 operation，用**与 CI 相同**的参数（--config 基线，不传 --baseline）。
"$PY" - "$CONTRACT" "$TMP/removed.json" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
first = sorted(doc["paths"])[0]
item = doc["paths"][first]
for method in list(item):
    if method in ("get", "post", "put", "patch", "delete"):
        del item[method]
        print(first, method)
        break
json.dump(doc, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$PY" "$AUDIT" --contract "$TMP/removed.json" --config "$CONFIG" --fail-on >"$TMP/removed.txt" 2>&1
expect_rc "④b 基线里有、契约里没有的端点 → 门禁失败" 1 $?
grep -q "基线（已发布的客户端契约）里有、当前契约没有" "$TMP/removed.txt" \
  && pass "④b 失败原因写明「端点被删除」" || fail "④b 失败原因没有写明删除"

# ④c excludePaths 只用于「刻意不审」的路径，不能吞掉 P0（review P2-1）：
#     把被删端点加进 excludePaths，门禁仍必须失败。
FIRST_PATH="$("$PY" -c "import json,sys;print(sorted(json.load(open(sys.argv[1],encoding='utf-8'))['paths'])[0])" "$CONTRACT")"
"$PY" - "$CONFIG" "$TMP/cfg_excl.json" "$FIRST_PATH" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
cfg["excludePaths"] = sorted(set(cfg.get("excludePaths") or []) | {sys.argv[3]})
json.dump(cfg, open(sys.argv[2], "w", encoding="utf-8"), ensure_ascii=False)
PY
"$PY" "$AUDIT" --contract "$TMP/removed.json" --config "$TMP/cfg_excl.json" --fail-on >"$TMP/excl.txt" 2>&1
expect_rc "④c excludePaths 不能吞掉端点删除（P0）" 1 $?
grep -q "基线（已发布的客户端契约）里有" "$TMP/excl.txt" \
  && pass "④c 被排除的路径仍然报出删除" || fail "④c 删除被 excludePaths 吞掉了"

# ⑤ 非法输入 fail-closed
echo '{ not json' >"$TMP/broken.json"
"$PY" "$AUDIT" --contract "$TMP/broken.json" --config "$CONFIG" >/dev/null 2>&1
expect_rc "⑤ 非法契约退出 4" 4 $?
"$PY" "$AUDIT" --contract "$TMP/missing.json" --config "$CONFIG" >/dev/null 2>&1
expect_rc "⑤ 契约缺失退出 4" 4 $?
# review P2-2：显式给了不存在的 --config 时不能退化成「空基线」（否则 publicPaths/批准全空，
# 门禁会以「没有发现」通过），必须 fail-closed。
"$PY" "$AUDIT" --contract "$CONTRACT" --config "$TMP/no-such-config.json" >/dev/null 2>&1
expect_rc "⑤b 显式 --config 不存在 → 退出 4" 4 $?

# ⑥ 包装脚本透传退出码（用临时副本仓，避免改动真实仓）
mkdir -p "$TMP/repo/scripts/ci" "$TMP/repo/contract" "$TMP/repo/docs"
cp "$HERE/audit-contract-gaps.sh" "$AUDIT" "$TMP/repo/scripts/ci/"
cp "$CONFIG" "$TMP/repo/docs/contract-baseline.json"
cp "$TMP/broken.json" "$TMP/repo/contract/openapi.json"
bash "$TMP/repo/scripts/ci/audit-contract-gaps.sh" >/dev/null 2>&1
expect_rc "⑥ 包装脚本把「契约非法」原样传出（4）" 4 $?
cp "$TMP/bad.json" "$TMP/repo/contract/openapi.json"
cp "$TMP/cfg.json" "$TMP/repo/docs/contract-baseline.json"
mkdir -p "$TMP/repo/src"
bash "$TMP/repo/scripts/ci/audit-contract-gaps.sh" >/dev/null 2>&1
expect_rc "⑥ 包装脚本把「有缺口」原样传出（1）" 1 $?

echo ""
if [ "$FAIL" -ne 0 ]; then echo "FAILED：$FAIL 项失败，$PASS 项通过"; exit 1; fi
echo "PASSED：$PASS 项通过"
