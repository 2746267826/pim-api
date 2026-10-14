#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：契约变更通知（repository_dispatch）派发脚本。
#
# 这是「api 侧真的把通知发出去」的那一步，所以测试必须是**真 HTTP**：
# 起一个本地 stub（python3 http.server）当 GitHub API，断言
#   * 真的 POST 到 /repos/<owner>/<repo>/dispatches，且 body 形状正确；
#   * 三个仓个个都发了（一个失败不许拖住另外两个）；
#   * 5xx 会重试、4xx 不重试；
#   * token 绝不出现在任何输出里；
#   * --dry-run 不发请求。
# mock 掉 curl 只能证明"我们以为调用了"，证明不了请求长什么样、失败怎么处置。
#
# 用法：scripts/ci/test-notify-contract-sync.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/notify-contract-sync.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-notify-test.XXXXXX")"
trap 'rm -rf "$TMP"; [ -n "${STUB_PID:-}" ] && kill "$STUB_PID" 2>/dev/null || true' EXIT

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }
contains() { if [[ "$3" == *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 未见 [$2]" ; fail=$((fail+1)); fi; }
not_contains() { if [[ "$3" != *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 出现了不该出现的 [$2]"; fail=$((fail+1)); fi; }

# --- 本地 GitHub API stub ---------------------------------------------------
cat > "$TMP/stub.py" <<'PY'
import json, os, sys, threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

cfg_path, req_path, port_path = sys.argv[1], sys.argv[2], sys.argv[3]
cfg = json.load(open(cfg_path, encoding="utf-8"))
lock = threading.Lock()
counts = {}

class H(BaseHTTPRequestHandler):
    def log_message(self, *a):  # 静音
        pass

    def do_POST(self):
        n = int(self.headers.get("Content-Length") or 0)
        body = self.rfile.read(n).decode("utf-8")
        auth = self.headers.get("Authorization", "")
        with lock:
            with open(req_path, "a", encoding="utf-8") as fh:
                fh.write(json.dumps({"path": self.path, "body": body,
                                     "auth_present": auth.startswith("Bearer "),
                                     "auth_len": len(auth)}) + "\n")
            counts[self.path] = counts.get(self.path, 0) + 1
            seq = cfg.get(self.path, [204])
            status = seq[min(counts[self.path] - 1, len(seq) - 1)]
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        payload = b"{}" if status < 400 else b'{"message":"stub failure"}'
        self.send_header("Content-Length", str(len(payload)))
        self.end_headers()
        self.wfile.write(payload)

srv = ThreadingHTTPServer(("127.0.0.1", 0), H)
with open(port_path, "w", encoding="utf-8") as fh:
    fh.write(str(srv.server_address[1]))
srv.serve_forever()
PY

OWNER="pimowner"
REQ_LOG="$TMP/requests.jsonl"
PORT_FILE="$TMP/port"
: > "$REQ_LOG"
echo '{}' > "$TMP/cfg.json"

# 每个用例按需重写 cfg 并重启 stub
start_stub() {
  if [ -n "${STUB_PID:-}" ]; then
    kill "$STUB_PID" 2>/dev/null || true
    wait "$STUB_PID" 2>/dev/null || true
  fi
  : > "$REQ_LOG"
  rm -f "$PORT_FILE"
  python3 "$TMP/stub.py" "$TMP/cfg.json" "$REQ_LOG" "$PORT_FILE" >/dev/null 2>&1 &
  STUB_PID=$!
  for _ in $(seq 1 50); do [ -s "$PORT_FILE" ] && break; sleep 0.1; done
  [ -s "$PORT_FILE" ] || { echo "stub 未启动" >&2; exit 1; }
  PORT="$(cat "$PORT_FILE")"
  API_BASE="http://127.0.0.1:$PORT"
}
start_stub

req_count() { [ -f "$REQ_LOG" ] && wc -l < "$REQ_LOG" | tr -d ' ' || echo 0; }
req_paths() { python3 -c "
import json,sys
for line in open('$REQ_LOG', encoding='utf-8'):
    print(json.loads(line)['path'])
"; }

TOKEN="secret-token-must-not-leak-0123456789"
SHA256="aa11bb22cc33dd44ee55ff6600778899aabbccddeeff00112233445566778899"
COMMIT="4088bc446d4d98bbe20bee8b9241be2dfeabe78f"

# run <args...>：stdout+stderr 一起捕获到 $OUT（token 泄漏检查需要看全部输出）
RC=0
run() {
  set +e
  OUT="$(env -u GITHUB_STEP_SUMMARY \
        CONTRACT_SYNC_TOKEN="$TOKEN" PIM_CONTRACT_SYNC_RETRY_DELAY=0 \
        bash "$SCRIPT" "$@" 2>&1)"
  RC=$?
  set -e
}
# run_base <extra args...>：带上不变的契约事实参数
run_base() {
  run --owner "$OWNER" --api-base "$API_BASE" \
    --contract-sha256 "$SHA256" --contract-bytes 941361 --contract-commit "$COMMIT" \
    --github-ref refs/heads/master --build-api-result success "$@"
}

echo "== notify-contract-sync.sh =="

# --- 1) 全部成功 → 三个仓各一次 POST，退出码 0 -----------------------------
echo '{}' > "$TMP/cfg.json"; start_stub
run_base --repos "pim-web pim-android pim-windows"
check "全成功 → 退出码 0" "0" "$RC"
check "全成功 → 发了 3 次 dispatch" "3" "$(req_count)"
check "全成功 → 三个仓都打到 dispatches 端点" "3" "$(req_paths | grep -c "/dispatches$")"
contains "全成功 → 打印 pim-web 的 204" "pim-web" "$OUT"

FIRST_REQ="$(python3 - "$REQ_LOG" <<'PY'
import json, os, sys
p = sys.argv[1]
if os.path.exists(p) and os.path.getsize(p) > 0:
    print(json.dumps(json.loads(open(p, encoding="utf-8").readline())))
else:
    print("{}")
PY
)"
BODY="$(python3 -c "
import json,sys
try: print(json.loads(sys.argv[1]).get('body',''))
except Exception: print('')
" "$FIRST_REQ")"
payload() { python3 -c "
import json,sys
try: print(json.loads(json.loads(sys.argv[1]).get('body',''))['client_payload'][sys.argv[2]])
except Exception: print('')
" "$FIRST_REQ" "$1"; }
check "body.event_type" "contract-updated" "$(python3 -c "
import json,sys
try: print(json.loads(json.loads(sys.argv[1]).get('body',''))['event_type'])
except Exception: print('')
" "$FIRST_REQ")"
check "payload.ref = 契约所在 commit（客户端据此精确取字节）" "$COMMIT" "$(payload ref)"
check "payload.sha256" "$SHA256" "$(payload sha256)"
check "payload.bytes 是数字而不是字符串" "941361" "$(payload bytes)"
check "Authorization 头带着（长度>0）" "True" "$(python3 -c "
import json,sys
try:
    d = json.loads(sys.argv[1]); print(d.get('auth_present') and d.get('auth_len', 0) > 0)
except Exception: print('False')
" "$FIRST_REQ")"
not_contains "token 不出现在任何输出里" "$TOKEN" "$OUT"

# --- 2) 一个仓 403（App 没装/权限不对）→ 仍发完另外两个，整体红 ------------
python3 - "$TMP/cfg.json" "$OWNER" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
cfg["/repos/%s/pim-android/dispatches" % sys.argv[2]] = [403]
json.dump(cfg, open(sys.argv[1], "w", encoding="utf-8"))
PY
start_stub
run_base --repos "pim-web pim-android pim-windows"
check "有仓 403 → 退出码非 0" "1" "$RC"
check "有仓 403 → 三个仓都尝试了（不早退）" "3" "$(req_count)"
contains "有仓 403 → 明确报出是哪个仓失败" "pim-android" "$OUT"
contains "有仓 403 → 报出 HTTP 状态" "403" "$OUT"
contains "有仓 403 → 给出手动补发命令" "force_contract_notify=true" "$OUT"
contains "有仓 403 → 说明自动补发不是万能的" "客户端" "$OUT"

# --- 3) 5xx 重试后成功 → 退出码 0，且真的重试了 ----------------------------
python3 - "$TMP/cfg.json" "$OWNER" <<'PY'
import json, sys
cfg = json.load(open(sys.argv[1], encoding="utf-8"))
cfg["/repos/%s/pim-web/dispatches" % sys.argv[2]] = [500, 204]
json.dump(cfg, open(sys.argv[1], "w", encoding="utf-8"))
PY
start_stub
run_base --repos "pim-web"
check "5xx 后重试成功 → 退出码 0" "0" "$RC"
check "5xx 后重试成功 → 共 2 次请求" "2" "$(req_count)"

# --- 4) --dry-run：不发请求，只打印计划 ------------------------------------
echo '{}' > "$TMP/cfg.json"; start_stub
run_base --repos "pim-web pim-android" --dry-run
check "dry-run → 退出码 0" "0" "$RC"
check "dry-run → 一次请求都没发" "0" "$(req_count)"
contains "dry-run → 打印了将发送的 payload sha256" "$SHA256" "$OUT"

# --- 5) 参数校验：坏的 sha256 / 空 repos / 缺 token → 退出码 2，不发请求 ----
run --owner "$OWNER" --api-base "$API_BASE" --contract-sha256 "nothex" --contract-bytes 1 --contract-commit "$COMMIT" --repos "pim-web"
check "坏 sha256 → 退出码 2" "2" "$RC"
check "坏 sha256 → 没发请求" "0" "$(req_count)"
run_base --repos "   "
check "空 repos → 退出码 2" "2" "$RC"
run --owner "$OWNER" --api-base "$API_BASE" --contract-sha256 "$SHA256" --contract-bytes 1 --contract-commit "$COMMIT" --repos "pim-web" --event-type "bad event type!"
check "非法 event_type → 退出码 2" "2" "$RC"
set +e
OUT="$(env -u GITHUB_STEP_SUMMARY -u CONTRACT_SYNC_TOKEN PIM_CONTRACT_SYNC_RETRY_DELAY=0 \
      bash "$SCRIPT" --owner "$OWNER" --api-base "$API_BASE" --contract-sha256 "$SHA256" \
      --contract-bytes 1 --contract-commit "$COMMIT" --repos "pim-web" \
      --github-ref refs/heads/master --build-api-result success 2>&1)"
RC=$?
set -e
check "缺 token → 退出码 2" "2" "$RC"
contains "缺 token → 提示走 secrets" "CONTRACT_SYNC_TOKEN" "$OUT"

# --- 5b) 非 master 的 ref → 强制 dry-run，一次请求都不发 --------------------
echo '{}' > "$TMP/cfg.json"; start_stub
run --owner "$OWNER" --api-base "$API_BASE" --contract-sha256 "$SHA256" --contract-bytes 1 \
    --contract-commit "$COMMIT" --repos "pim-web pim-android pim-windows" \
    --github-ref refs/heads/dsh-linux/some-feature --build-api-result success
check "非 master ref → 退出码 0（降级为 dry-run 而不是失败）" "0" "$RC"
check "非 master ref → 一次请求都没发" "0" "$(req_count)"
contains "非 master ref → 明确警告" "不是 refs/heads/master" "$OUT"
contains "非 master ref → 说明权威版本在 master" "master 上的文件" "$OUT"

# --- 5c) 真发通知却不声明 ref → 拒绝（漏传会绕过上面那条限制）--------------
run --owner "$OWNER" --api-base "$API_BASE" --contract-sha256 "$SHA256" --contract-bytes 1 \
    --contract-commit "$COMMIT" --repos "pim-web" --build-api-result success
check "真发但不传 --github-ref → 退出码 2" "2" "$RC"
check "  → 没发请求" "0" "$(req_count)"
contains "  → 提示必须声明 ref" "github-ref" "$OUT"

# --- 5d) build-api 没成功：不通知，且**不静默** -----------------------------
echo '{}' > "$TMP/cfg.json"; start_stub
run_base --repos "pim-web pim-android pim-windows" --build-api-result skipped
check "build-api 被跳过（非强制）→ 退出码 0 但明确不通知" "0" "$RC"
check "  → 一次请求都没发" "0" "$(req_count)"
contains "  → 日志说明未经硬校验的契约不推给客户端" "不通知客户端" "$OUT"

# --- 5e) 强制补发但 build-api 被跳过 → 必须报错并给出可用的补发命令 ----------
run_base --repos "pim-web pim-android pim-windows" --build-api-result skipped --force true
check "强制补发但 build-api 未成功 → 退出码 2（不是静默跳过）" "2" "$RC"
check "  → 没发请求" "0" "$(req_count)"
contains "  → 给出带 force_all=true 的补发命令" "force_all=true" "$OUT"
contains "  → 说明为什么必须带 force_all" "build-api" "$OUT"

# --- 5f) 缺 --build-api-result → 拒绝执行（漏传会让门槛失效）----------------
run --owner "$OWNER" --api-base "$API_BASE" --contract-sha256 "$SHA256" --contract-bytes 1 \
    --contract-commit "$COMMIT" --repos "pim-web" --github-ref refs/heads/master
check "漏传 --build-api-result → 退出码 2" "2" "$RC"
check "  → 没发请求" "0" "$(req_count)"

# --- 6) GITHUB_STEP_SUMMARY 存在时写出可读摘要（不泄漏 token） --------------
echo '{}' > "$TMP/cfg.json"; start_stub
: > "$TMP/summary.md"
set +e
OUT="$(CONTRACT_SYNC_TOKEN="$TOKEN" PIM_CONTRACT_SYNC_RETRY_DELAY=0 \
      GITHUB_STEP_SUMMARY="$TMP/summary.md" \
      bash "$SCRIPT" --owner "$OWNER" --api-base "$API_BASE" --contract-sha256 "$SHA256" --contract-bytes 1 --contract-commit "$COMMIT" --repos "pim-web" --github-ref refs/heads/master --build-api-result success 2>&1)"
RC=$?
set -e
check "写 summary → 退出码 0" "0" "$RC"
contains "summary 里记录了投递结果" "pim-web" "$(cat "$TMP/summary.md")"
not_contains "summary 里没有 token" "$TOKEN" "$(cat "$TMP/summary.md")"

echo ""
echo "notify-contract-sync: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
