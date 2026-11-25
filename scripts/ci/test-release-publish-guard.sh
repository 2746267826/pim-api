#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：CI release 发布守卫（旧 run 重跑边界、正式版保护、fail-closed）。
# ---------------------------------------------------------------------------
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/release-publish-guard.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-release-guard-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

pass=0
fail=0
ok() { echo "  ✅ $1"; pass=$((pass + 1)); }
bad() { echo "  ❌ $1"; fail=$((fail + 1)); }

mkdir -p "$TMP/bin"
cat > "$TMP/bin/gh" <<'STUB'
#!/usr/bin/env bash
set -euo pipefail

not_found() {
  printf '{"message":"Not Found","status":"404"}\n'
  echo "gh: Not Found (HTTP 404)" >&2
  exit 1
}

cmd="${1:-}"; shift || true
[[ "$cmd" == "api" ]] || { echo "stub gh: unsupported command $cmd" >&2; exit 2; }
path="${1:-}"
case "$path" in
  */releases/tags/*)
    case "${FAKE_RELEASE_MODE:-missing}" in
      missing) not_found ;;
      error) echo "gh: Server Error (HTTP 500)" >&2; exit 1 ;;
      malformed) echo '{"tag_name":' ;;
      formal|prerelease|draft|wrongcommit)
        pre=true; draft=false; target="${FAKE_SHA:?}"
        if [[ "$FAKE_RELEASE_MODE" == "formal" ]]; then pre=false; fi
        if [[ "$FAKE_RELEASE_MODE" == "draft" ]]; then draft=true; fi
        if [[ "$FAKE_RELEASE_MODE" == "wrongcommit" ]]; then
          target=1111111111111111111111111111111111111111
        fi
        jq -n \
          --arg tag "${FAKE_TAG:?}" \
          --argjson pre "$pre" \
          --argjson draft "$draft" \
          --arg target "$target" \
          '{id:1,tag_name:$tag,prerelease:$pre,draft:$draft,target_commitish:$target,assets:[]}'
        ;;
      *) echo "stub gh: unknown FAKE_RELEASE_MODE" >&2; exit 2 ;;
    esac
    ;;
  */commits/master)
    [[ "${FAKE_MASTER_MODE:-json}" == "json" ]] || { echo "gh: Server Error (HTTP 500)" >&2; exit 1; }
    jq -n --arg sha "${FAKE_MASTER_SHA:?}" '{sha:$sha}'
    ;;
  *) echo "stub gh: unsupported api $path" >&2; exit 2 ;;
esac
STUB
chmod +x "$TMP/bin/gh"
export PATH="$TMP/bin:$PATH"

SHA_OLD="aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
SHA_HEAD="bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
TAG="api-v26.11.42"

run_case() { # <label> <expected> <out> <env...>
  local label="$1" want="$2" out="$3"; shift 3
  : > "$out"
  local rc=0
  env FAKE_TAG="$TAG" FAKE_SHA="${CASE_SHA:-$SHA_OLD}" "$@" bash "$SCRIPT" \
    --repo 2746267826/pim-api --tag "$TAG" \
    --sha "${CASE_SHA:-$SHA_OLD}" --run-number 42 --run-attempt "${CASE_ATTEMPT:-1}" \
    > "$out" 2>&1 || rc=$?
  if [[ "$rc" -ne 0 ]]; then
    if [[ "$want" == "fail" ]]; then ok "$label"; else bad "$label：期望成功，实际 rc=$rc：$(cat "$out")"; fi
    return
  fi
  if [[ "$want" == "fail" ]]; then
    bad "$label：期望失败，实际通过：$(cat "$out")"
    return
  fi
  if grep -qx "$want" "$out"; then ok "$label"; else bad "$label：期望 [$want]，实际：$(cat "$out")"; fi
}

echo "== release-publish-guard.sh =="

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=1 run_case "首次运行、release 不存在 → 允许创建" \
  "publish=true" "$TMP/o1" FAKE_RELEASE_MODE=missing

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=2 run_case "旧 run 重跑、release 不存在、不是 master HEAD → 不补发" \
  "publish=false" "$TMP/o2" \
  FAKE_RELEASE_MODE=missing FAKE_MASTER_MODE=json FAKE_MASTER_SHA="$SHA_HEAD"

CASE_SHA="$SHA_HEAD" CASE_ATTEMPT=2 run_case "重跑当前 master HEAD、release 不存在 → 允许补发" \
  "publish=true" "$TMP/o3" \
  FAKE_RELEASE_MODE=missing FAKE_MASTER_MODE=json FAKE_MASTER_SHA="$SHA_HEAD"

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=1 run_case "首次运行、release 已是正式版 → 不碰" \
  "publish=false" "$TMP/o4" FAKE_RELEASE_MODE=formal

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=1 run_case "首次运行、release 是 pre-release → 允许刷新" \
  "publish=true" "$TMP/o5" FAKE_RELEASE_MODE=prerelease

CASE_SHA="$SHA_HEAD" CASE_ATTEMPT=2 run_case "重跑当前 master HEAD、pre-release → 允许刷新" \
  "publish=true" "$TMP/o6" \
  FAKE_RELEASE_MODE=prerelease FAKE_MASTER_MODE=json FAKE_MASTER_SHA="$SHA_HEAD"

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=2 run_case "旧 run 重跑、pre-release 已过时 → 不刷新" \
  "publish=false" "$TMP/o7" \
  FAKE_RELEASE_MODE=prerelease FAKE_MASTER_MODE=json FAKE_MASTER_SHA="$SHA_HEAD"

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=2 run_case "重跑时读不到 master HEAD → fail-closed" \
  fail "$TMP/o8" FAKE_RELEASE_MODE=prerelease FAKE_MASTER_MODE=error

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=1 run_case "release API 500 → fail-closed（不当成不存在）" \
  fail "$TMP/o9" FAKE_RELEASE_MODE=error

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=1 run_case "release 属于别的 commit → 拒绝改写" \
  fail "$TMP/o10" FAKE_RELEASE_MODE=wrongcommit

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=1 run_case "release 是 draft → 拒绝改写" \
  fail "$TMP/o11" FAKE_RELEASE_MODE=draft

CASE_SHA="$SHA_OLD" CASE_ATTEMPT=1 run_case "release JSON 损坏 → 失败" \
  fail "$TMP/o12" FAKE_RELEASE_MODE=malformed

if grep -q '^publish=false$' "$TMP/o2" && grep -q '^reason=stale-rerun$' "$TMP/o2"; then
  ok "stale rerun 输出原因"
else
  bad "stale rerun 缺少 reason：$(cat "$TMP/o2")"
fi

if grep -q '^publish=false$' "$TMP/o4" && grep -q '^reason=formal-exists$' "$TMP/o4"; then
  ok "正式版 skip 输出原因"
else
  bad "正式版 skip 缺少 reason：$(cat "$TMP/o4")"
fi

if [[ "$fail" -ne 0 ]]; then
  echo "release-publish-guard tests failed: ${fail} failed, ${pass} passed" >&2
  exit 1
fi
echo "release-publish-guard tests passed (${pass} checks)"
