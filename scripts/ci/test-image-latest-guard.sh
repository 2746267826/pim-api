#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：镜像 :latest 不能被旧 CI 重跑指回旧 commit。
# ---------------------------------------------------------------------------
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/image-latest-guard.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-image-latest-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

pass=0
fail=0
ok() { echo "  ✅ $1"; pass=$((pass + 1)); }
bad() { echo "  ❌ $1"; fail=$((fail + 1)); }

mkdir -p "$TMP/bin"
cat > "$TMP/bin/gh" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail
cmd="${1:-}"; shift || true
[[ "$cmd" == "api" ]] || { echo "stub gh: unsupported command $cmd" >&2; exit 2; }
path="${1:-}"
case "$path" in
  */commits/master)
    [[ "${FAKE_MODE:-json}" == "json" ]] || { echo "gh: Server Error (HTTP 500)" >&2; exit 1; }
    jq -n --arg sha "${FAKE_MASTER_SHA:?}" '{sha:$sha}'
    ;;
  *) echo "stub gh: unsupported api $path" >&2; exit 2 ;;
esac
STUB
chmod +x "$TMP/bin/gh"
export PATH="$TMP/bin:$PATH"

RUN_SHA="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
MASTER_SHA="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

run_case() { # <label> <want|fail> <env...>
  local label="$1" want="$2"; shift 2
  local out="$TMP/out" rc=0
  env "$@" bash "$SCRIPT" --repo 2746267826/pim-api --sha "$RUN_SHA" > "$out" 2>&1 || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    if [[ "$want" == "fail" ]]; then ok "$label"; else bad "$label：期望成功，实际 rc=$rc：$(cat "$out")"; fi
  elif [[ "$want" == "fail" ]]; then
    bad "$label：期望失败，实际通过：$(cat "$out")"
  elif grep -qx "$want" "$out"; then
    ok "$label"
  else
    bad "$label：期望 [$want]，实际：$(cat "$out")"
  fi
}

echo "== image-latest-guard.sh =="

run_case "run 就是当前 master HEAD → 允许移动 :latest" \
  "push_latest=true" FAKE_MODE=json FAKE_MASTER_SHA="$RUN_SHA"

run_case "run 不是当前 master HEAD → 不移动 :latest" \
  "push_latest=false" FAKE_MODE=json FAKE_MASTER_SHA="$MASTER_SHA"

run_case "master HEAD API 错误 → fail-closed" \
  fail FAKE_MODE=error FAKE_MASTER_SHA="$MASTER_SHA"

run_case "master HEAD 返回非法 sha → fail-closed" \
  fail FAKE_MODE=json FAKE_MASTER_SHA=not-a-sha

if [[ "$fail" -ne 0 ]]; then
  echo "image-latest-guard tests failed: ${fail} failed, ${pass} passed" >&2
  exit 1
fi
echo "image-latest-guard tests passed (${pass} checks)"
