#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约新鲜度设施 · 行为测试（离线；唯一的外部前置是仓内已 npm ci）
#
# 覆盖：
#   ① 同字节 → check 0 且仓内零写入          ② 合法 JSON 但内容不同 → 非 0
#   ③ 取源失败（不可达 / 404）→ 非 0          ④ payload HTML / 空体 → 非 0
#   ⑤ PIM_CONTRACT_REPO/REF 真实改变被访问路径（含 40-hex 直用不解析）
#   ⑥ check 全程零写入（git status + inode/mtime）   ⑦ 生成物陈旧两个分支都变红
#   ⑧ refresh 的 provenance/bytes/hash/可复现性       ⑨ refresh 失败不写三个产物
#   ⑩ 上游非契约提交但字节相同 → PASS         ⑪ CI 环境下测试旁路被拒（非静默放行）
#   ⑫ 原样抽取真实 CI YAML step 与 gate check 函数，证明失败传播
#
# 上游 fixture 一律是本机 git 仓 + 本机 HTTP 服务（127.0.0.1 随机端口），
# 生产旁路（本地文件当权威源）只在测试替身内可用，并被 ⑪ 证伪。
#
# 用法：bash scripts/ci/test-contract-freshness.sh
#   PIM_CONTRACT_TEST_KEEP=1   保留临时工作目录（默认删除）
#   PYTHONDONTWRITEBYTECODE=1  由脚本自身强制设置
# ---------------------------------------------------------------------------
set -uo pipefail
export PYTHONDONTWRITEBYTECODE=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
# shellcheck source=scripts/ci/contract-freshness-lib.sh
. "$HERE/contract-freshness-lib.sh"

CHECK="$HERE/check-contract-freshness.sh"
FETCH="$HERE/fetch-contract.sh"
CI_YML="$ROOT/.github/workflows/ci.yml"
CLI="$(pim_contract_contract_cli "$ROOT")"

PASS=0; FAIL=0
say()  { printf '%s\n' "$*"; }
sha()  { [ -f "$1" ] && sha256sum "$1" | cut -d' ' -f1 || echo ABSENT; }
bytes(){ [ -f "$1" ] && wc -c < "$1" | tr -d ' ' || echo ABSENT; }
fsig() { [ -e "$1" ] && stat -c '%i:%s:%Y' "$1" || echo ABSENT; }
record() { # record <name> <0=ok> <observed>
  if [ "$2" = "0" ]; then PASS=$((PASS+1)); say "PASS  $1 :: $3"
  else FAIL=$((FAIL+1)); say "FAIL  $1 :: $3"; fi
}
section() { say ""; say "-- $* --"; }

if [ ! -x "$CLI" ]; then
  say "缺少仓内锁定生成器 $CLI；请先在 client-web 下执行 npm ci。"
  exit 2
fi
if ! python3 -c 'import yaml' 2>/dev/null; then
  say "缺少 PyYAML（用于原样抽取真实 CI step）；无法完成 ⑫。"
  exit 2
fi

WORK="$(mktemp -d "${TMPDIR:-/tmp}/pim-contract-test.XXXXXX")"
SRV_PID=""
cleanup() {
  [ -n "$SRV_PID" ] && kill "$SRV_PID" 2>/dev/null
  if [ "${PIM_CONTRACT_TEST_KEEP:-0}" = "1" ]; then say "保留工作目录：$WORK"; else rm -rf "$WORK"; fi
}
trap cleanup EXIT

# =========================== fixtures ======================================
FIX="$WORK/fixtures"
mkdir -p "$FIX"

cat > "$FIX/openapi.master.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "Pim.Api", "version": "1.0" },
  "paths": {
    "/api/v1/ping": {
      "get": {
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": { "schema": { "$ref": "#/components/schemas/PingApiResponse" } }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "PingApiResponse": {
        "type": "object",
        "properties": {
          "code": { "type": "integer", "format": "int32" },
          "message": { "type": "string" },
          "data": { "type": "string" }
        }
      }
    }
  }
}
JSON

cat > "$FIX/openapi.v0.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "Pim.Api", "version": "1.0" },
  "paths": {
    "/api/v1/ping": {
      "get": {
        "responses": {
          "200": {
            "description": "OK",
            "content": {
              "application/json": { "schema": { "$ref": "#/components/schemas/PingApiResponse" } }
            }
          }
        }
      }
    }
  },
  "components": {
    "schemas": {
      "PingApiResponse": {
        "type": "object",
        "properties": { "code": { "type": "integer", "format": "int32" }, "data": { "type": "string" } }
      }
    }
  }
}
JSON

printf '<html>404 Not Found</html>\n' > "$FIX/openapi.html"
: > "$FIX/openapi.empty"

# ===================== 本机上游 git 仓（真实 40-hex SHA）====================
UPSTREAM_REPO="$WORK/upstream-src"
mkdir -p "$UPSTREAM_REPO/contract"
git -C "$UPSTREAM_REPO" init -q
git -C "$UPSTREAM_REPO" symbolic-ref HEAD refs/heads/master   # 不依赖宿主 init.defaultBranch
git -C "$UPSTREAM_REPO" config user.email fixture@local
git -C "$UPSTREAM_REPO" config user.name fixture
cp "$FIX/openapi.master.json" "$UPSTREAM_REPO/contract/openapi.json"
git -C "$UPSTREAM_REPO" add -A
git -C "$UPSTREAM_REPO" commit -qm 'c1: contract fixture'
SHA_C1="$(git -C "$UPSTREAM_REPO" rev-parse HEAD)"

# master 前进一个「非契约」提交：契约字节完全不变
printf '# fixture upstream\n' > "$UPSTREAM_REPO/README.md"
git -C "$UPSTREAM_REPO" add -A
git -C "$UPSTREAM_REPO" commit -qm 'c2: non-contract change only'
SHA_MASTER="$(git -C "$UPSTREAM_REPO" rev-parse HEAD)"

git -C "$UPSTREAM_REPO" checkout -q -b v0 "$SHA_C1"
cp "$FIX/openapi.v0.json" "$UPSTREAM_REPO/contract/openapi.json"
git -C "$UPSTREAM_REPO" commit -qam 'v0: older contract'
SHA_V0="$(git -C "$UPSTREAM_REPO" rev-parse HEAD)"
git -C "$UPSTREAM_REPO" checkout -q master

git -C "$UPSTREAM_REPO" checkout -q -b html "$SHA_C1"
cp "$FIX/openapi.html" "$UPSTREAM_REPO/contract/openapi.json"
git -C "$UPSTREAM_REPO" commit -qam 'html: error page payload'
SHA_HTML="$(git -C "$UPSTREAM_REPO" rev-parse HEAD)"

git -C "$UPSTREAM_REPO" checkout -q -b empty "$SHA_C1"
cp "$FIX/openapi.empty" "$UPSTREAM_REPO/contract/openapi.json"
git -C "$UPSTREAM_REPO" commit -qam 'empty: empty payload'
SHA_EMPTY="$(git -C "$UPSTREAM_REPO" rev-parse HEAD)"
git -C "$UPSTREAM_REPO" checkout -q master

# ===================== 本机 raw 源（静态目录树）=============================
SERVE="$WORK/serve"
lay() { # lay <repo> <sha> <file>
  mkdir -p "$SERVE/$1/$2/contract"
  cp "$3" "$SERVE/$1/$2/contract/openapi.json"
}
lay pim-api "$SHA_C1"     "$FIX/openapi.master.json"
lay pim-api "$SHA_MASTER" "$FIX/openapi.master.json"
lay pim-api "$SHA_V0"     "$FIX/openapi.v0.json"
lay pim-api "$SHA_HTML"   "$FIX/openapi.html"
lay pim-api "$SHA_EMPTY"  "$FIX/openapi.empty"
lay 2746267826/pim-api "$SHA_MASTER" "$FIX/openapi.master.json"   # REPO 覆写用例

PORT="$(python3 - <<'PY'
import socket
s = socket.socket(); s.bind(("127.0.0.1", 0)); print(s.getsockname()[1]); s.close()
PY
)"
( cd "$SERVE" && exec python3 -u -m http.server "$PORT" --bind 127.0.0.1 ) > "$WORK/http-server.log" 2>&1 &
SRV_PID=$!
RAW="http://127.0.0.1:$PORT"
for _ in $(seq 1 60); do
  curl -fsS -o /dev/null "$RAW/pim-api/$SHA_MASTER/contract/openapi.json" 2>/dev/null && break
  sleep 0.1
done

# =========================== sandbox =======================================
sandbox() { # sandbox <dir>
  local dir="$1"
  rm -rf "$dir"
  mkdir -p "$dir/scripts/ci" "$dir/contract" "$dir/client-web/src/api" "$dir/.github/workflows"
  cp "$HERE/contract-freshness-lib.sh" "$dir/scripts/ci/"
  cp "$CHECK" "$dir/scripts/ci/"
  cp "$FETCH" "$dir/scripts/ci/"
  cp "$CI_YML" "$dir/.github/workflows/ci.yml"
  ln -sfn "$ROOT/client-web/node_modules" "$dir/client-web/node_modules"
  printf 'node_modules\n' > "$dir/.gitignore"
  git -C "$dir" init -q
  git -C "$dir" config user.email test@local
  git -C "$dir" config user.name test
  git -C "$dir" add -A
  git -C "$dir" commit -qm sandbox-base
}

refresh_sandbox() { # refresh_sandbox <dir> <ref>  -> rc；不 commit
  local dir="$1" ref="$2"
  ( cd "$dir" && PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$ref" \
      bash scripts/ci/fetch-contract.sh ) > "$dir.refresh.log" 2>&1
}

commit_sandbox() { local dir="$1"; git -C "$dir" add -A; git -C "$dir" commit -qm "$2"; }

run_check() { # run_check <dir> <ref> [raw_base] -> 设置 CHECK_RC / CHECK_LOG
  local dir="$1" ref="$2" base="${3:-$RAW}"
  ( cd "$dir" && PIM_CONTRACT_RAW_BASE="$base" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$ref" \
      bash scripts/ci/check-contract-freshness.sh ) > "$dir.check.log" 2>&1
  CHECK_RC=$?
  CHECK_LOG="$dir.check.log"
}
run_check_env() { # run_check_env <dir> <ref> <env...>  额外 env 前缀（用于旁路/CI 用例）
  local dir="$1" ref="$2"; shift 2
  ( cd "$dir" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$ref" "$@" \
      bash scripts/ci/check-contract-freshness.sh ) > "$dir.check.log" 2>&1
  CHECK_RC=$?
  CHECK_LOG="$dir.check.log"
}
artifacts_sig() { # 三个产物的 inode:size:mtime
  printf '%s | %s | %s' "$(fsig "$1/contract/openapi.json")" "$(fsig "$1/contract/provenance.json")" "$(fsig "$1/client-web/src/api/generated.ts")"
}
artifacts_sha() {
  printf '%s | %s | %s' "$(sha "$1/contract/openapi.json")" "$(sha "$1/contract/provenance.json")" "$(sha "$1/client-web/src/api/generated.ts")"
}

# raw 服务的访问日志：按「调用前 mark → 调用后切片」断言真实请求路径
ACCESS_LOG="$WORK/http-server.log"
log_mark()  { wc -l < "$ACCESS_LOG" | tr -d ' '; }
log_since() { tail -n "+$(( $1 + 1 ))" "$ACCESS_LOG" 2>/dev/null; }
has_req()   { case "$(log_since "$1")" in *"$2"*) return 0;; *) return 1;; esac; }

say "== pim-web 契约新鲜度行为测试（离线 fixture 上游）=="
say "仓          : $ROOT"
say "fixture 上游: $UPSTREAM_REPO  (master=$SHA_MASTER c1=$SHA_C1 v0=$SHA_V0)"
say "fixture raw : $RAW"
say "工作目录    : $WORK"

# ======================= ① 一致 → 0，且零写入 ==============================
section "① 同字节 → check 0 且仓内零写入"
sandbox "$WORK/c1"
refresh_sandbox "$WORK/c1" master >/dev/null 2>&1; R_RC=$?
commit_sandbox "$WORK/c1" 'synced at master'
record "refresh_synced_sandbox" $([ "$R_RC" -eq 0 ] && echo 0 || echo 1) "refresh rc=$R_RC"
SIG_BEFORE="$(artifacts_sig "$WORK/c1")"
STATUS_BEFORE="$(git -C "$WORK/c1" status --porcelain)"
run_check "$WORK/c1" master
SIG_AFTER="$(artifacts_sig "$WORK/c1")"
STATUS_AFTER="$(git -C "$WORK/c1" status --porcelain)"
say "   rc=$CHECK_RC  artifacts $SIG_BEFORE -> $SIG_AFTER"
record "check_consistent_exit_zero" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC on identical upstream bytes"
record "check_consistent_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"
record "check_consistent_clean_tree" $([ "$STATUS_BEFORE" = "$STATUS_AFTER" ] && echo 0 || echo 1) "git status --porcelain unchanged ('${STATUS_AFTER}')"
record "check_consistent_clean_tree_empty" $([ -z "$STATUS_AFTER" ] && echo 0 || echo 1) "git status --porcelain is empty"

# ======================= ② 合法 JSON 但内容不同 → 非 0 =====================
section "② 合法 JSON、内容不同（陈旧快照）→ 非 0"
sandbox "$WORK/c2"
refresh_sandbox "$WORK/c2" v0 >/dev/null 2>&1
commit_sandbox "$WORK/c2" 'synced at v0'
SIG_BEFORE="$(artifacts_sig "$WORK/c2")"
run_check "$WORK/c2" master
SIG_AFTER="$(artifacts_sig "$WORK/c2")"
say "   rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
record "check_stale_snapshot_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC with a legal-but-different snapshot"
record "check_stale_snapshot_exit_1" $([ "$CHECK_RC" -eq 1 ] && echo 0 || echo 1) "rc=$CHECK_RC (documented stale code = 1)"
record "check_stale_snapshot_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

# ======================= ③ 取源失败 → 非 0（fail-closed）===================
section "③ 取不到权威源 → 非 0（fail-closed）"
sandbox "$WORK/c3"
refresh_sandbox "$WORK/c3" master >/dev/null 2>&1
commit_sandbox "$WORK/c3" 'synced at master'
SIG_BEFORE="$(artifacts_sig "$WORK/c3")"
run_check "$WORK/c3" master "http://127.0.0.1:1"
SIG_AFTER="$(artifacts_sig "$WORK/c3")"
say "   unreachable rc=$CHECK_RC"
record "check_unreachable_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC when the raw base is unreachable"
record "check_unreachable_exit_3" $([ "$CHECK_RC" -eq 3 ] && echo 0 || echo 1) "rc=$CHECK_RC (documented fetch-failure code = 3)"
record "check_unreachable_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

# 404：源上根本没有该 SHA 的字节
mkdir -p "$SERVE/pim-api/0000000000000000000000000000000000000000/contract"
rm -f "$SERVE/pim-api/0000000000000000000000000000000000000000/contract/openapi.json"
SIG_BEFORE="$(artifacts_sig "$WORK/c3")"
run_check "$WORK/c3" "0000000000000000000000000000000000000000"
SIG_AFTER="$(artifacts_sig "$WORK/c3")"
say "   404 rc=$CHECK_RC"
record "check_http404_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC when the source returns 404"
record "check_http404_exit_3" $([ "$CHECK_RC" -eq 3 ] && echo 0 || echo 1) "rc=$CHECK_RC (documented fetch-failure code = 3)"
record "check_http404_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

# ======================= ④ payload HTML / 空体 → 非 0 ======================
section "④ 权威源返回 HTML / 空体 → 非 0（不得当成契约比对）"
for kind in html empty; do
  case "$kind" in html) REFCASE="$SHA_HTML"; EXPECT=4;; empty) REFCASE="$SHA_EMPTY"; EXPECT=4;; esac
  SIG_BEFORE="$(artifacts_sig "$WORK/c3")"
  run_check "$WORK/c3" "$REFCASE"
  SIG_AFTER="$(artifacts_sig "$WORK/c3")"
  say "   $kind rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
  record "check_payload_${kind}_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC for a $kind payload"
  record "check_payload_${kind}_exit_${EXPECT}" $([ "$CHECK_RC" -eq "$EXPECT" ] && echo 0 || echo 1) "rc=$CHECK_RC (documented payload code = $EXPECT)"
  record "check_payload_${kind}_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"
done

# ======================= ⑤ REPO/REF 真实改变被访问路径 =====================
section "⑤ PIM_CONTRACT_REPO / REF 真实改变被访问路径（不是「变量存在」）"
ACCESS_LOG="$WORK/http-server.log"
sandbox "$WORK/c5"
refresh_sandbox "$WORK/c5" master >/dev/null 2>&1
commit_sandbox "$WORK/c5" 'synced at master'

M="$(log_mark)"
run_check "$WORK/c5" master                       # REF=master -> SHA_MASTER
if has_req "$M" "GET /pim-api/$SHA_MASTER/contract/openapi.json"; then
  record "ref_master_accesses_resolved_sha_path" 0 "REF=master requested /pim-api/$SHA_MASTER/contract/openapi.json (rc=$CHECK_RC)"
else
  record "ref_master_accesses_resolved_sha_path" 1 "no such request in the raw-server log"
fi

M="$(log_mark)"
run_check "$WORK/c5" "$SHA_MASTER"                # REF=40-hex 直接使用
if has_req "$M" "GET /pim-api/$SHA_MASTER/contract/openapi.json"; then
  record "ref_pinned_sha_accesses_same_path" 0 "REF=<40-hex> reused verbatim in the request path (rc=$CHECK_RC)"
else
  record "ref_pinned_sha_accesses_same_path" 1 "no such request in the raw-server log"
fi

# REF 换成 40-hex 时必须不再走 ls-remote：把 REMOTE 指向不存在的路径，仍应成功
( cd "$WORK/c5" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$WORK/does-not-exist.git" PIM_CONTRACT_REF="$SHA_MASTER" \
    bash scripts/ci/check-contract-freshness.sh ) > "$WORK/c5.check.log" 2>&1
PIN_RC=$?
record "ref_pinned_skips_ls_remote" $([ "$PIN_RC" -eq 0 ] && echo 0 || echo 1) "rc=$PIN_RC with an unusable PIM_CONTRACT_REMOTE (ls-remote not called)"

# 换 REF -> v0，请求路径随解析出的 SHA 改变（沙箱快照也换成 v0 才可能一致）
sandbox "$WORK/c5b"
refresh_sandbox "$WORK/c5b" v0 >/dev/null 2>&1
commit_sandbox "$WORK/c5b" 'synced at v0'
M="$(log_mark)"
run_check "$WORK/c5b" v0
if has_req "$M" "GET /pim-api/$SHA_V0/contract/openapi.json"; then
  record "ref_change_changes_access_path" 0 "REF=v0 requested /pim-api/$SHA_V0/contract/openapi.json (rc=$CHECK_RC)"
else
  record "ref_change_changes_access_path" 1 "no such request in the raw-server log"
fi
if has_req "$M" "GET /pim-api/$SHA_MASTER/contract/openapi.json"; then
  record "ref_change_not_using_master_path" 1 "REF=v0 still requested the master SHA path"
else
  record "ref_change_not_using_master_path" 0 "REF=v0 did not request the master SHA path"
fi

# 换 REPO -> 2746267826/pim-api，请求路径随 REPO 改变
sandbox "$WORK/c5c"
( cd "$WORK/c5c" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=2746267826/pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=master \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c5c.refresh.log" 2>&1
commit_sandbox "$WORK/c5c" 'synced with overridden repo'
M="$(log_mark)"
( cd "$WORK/c5c" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=2746267826/pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=master \
    bash scripts/ci/check-contract-freshness.sh ) > "$WORK/c5c.check.log" 2>&1
REPO_RC=$?
if has_req "$M" "GET /2746267826/pim-api/$SHA_MASTER/contract/openapi.json"; then
  record "repo_change_changes_access_path" 0 "REPO=2746267826/pim-api requested /2746267826/pim-api/$SHA_MASTER/... (rc=$REPO_RC)"
else
  record "repo_change_changes_access_path" 1 "no such request in the raw-server log (rc=$REPO_RC)"
fi

# ======================= ⑥ check 静态只读守卫 ==============================
section "⑥ check 只读（运行时零写入已由 ①②③④ 覆盖；此处补静态守卫）"
if grep -nE "(^|[^a-zA-Z_])cp[[:space:]].*(contract/openapi\.json|generated\.ts|provenance\.json)|>[[:space:]]*[\"']?\S*(contract/openapi\.json|generated\.ts|provenance\.json)|mv[[:space:]].*(contract/openapi\.json|generated\.ts|provenance\.json)" "$CHECK" >/dev/null 2>&1; then
  record "check_script_has_no_repo_write_ops" 1 "found a write-looking operation in check-contract-freshness.sh"
else
  record "check_script_has_no_repo_write_ops" 0 "no cp/>/mv targeting the three artifacts in check-contract-freshness.sh"
fi
ALL_STATUS="$(git -C "$WORK/c1" status --porcelain; git -C "$WORK/c2" status --porcelain; git -C "$WORK/c3" status --porcelain; git -C "$WORK/c5" status --porcelain)"
record "no_sandbox_dirtied_by_any_check_run" $([ -z "$ALL_STATUS" ] && echo 0 || echo 1) "all sandboxes still clean after every check invocation"

# ======================= ⑦ 生成物陈旧两个分支 ==============================
section "⑦ 生成物陈旧：只 snapshot 陈旧 / 只 generated 陈旧 —— 两个分支都必须红"
# 分支 A：只 generated.ts 陈旧（快照与上游一致）
sandbox "$WORK/c7a"
refresh_sandbox "$WORK/c7a" master >/dev/null 2>&1
printf '\n// drift\n' >> "$WORK/c7a/client-web/src/api/generated.ts"
commit_sandbox "$WORK/c7a" 'only generated.ts drifted'
SIG_BEFORE="$(artifacts_sig "$WORK/c7a")"
run_check "$WORK/c7a" master
SIG_AFTER="$(artifacts_sig "$WORK/c7a")"
say "   only-generated rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
record "check_only_generated_stale_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC"
record "check_only_generated_stale_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

# 分支 B：只 snapshot 陈旧，而 generated.ts 是「与上游一致」的（= RED-2 的真实场景）
sandbox "$WORK/c7b"
refresh_sandbox "$WORK/c7b" v0 >/dev/null 2>&1            # snapshot=v0, generated=gen(v0)
cp "$WORK/c1/client-web/src/api/generated.ts" "$WORK/c7b/client-web/src/api/generated.ts"  # generated 跟上上游
commit_sandbox "$WORK/c7b" 'generated.ts current, snapshot stale'
say "   only-snapshot precondition: snapshot=$(sha "$WORK/c7b/contract/openapi.json" | cut -c1-12) == v0, generated==gen(master)"
record "only_snapshot_precondition_generated_matches_upstream" \
  $([ "$(sha "$WORK/c7b/client-web/src/api/generated.ts")" = "$(sha "$WORK/c1/client-web/src/api/generated.ts")" ] && echo 0 || echo 1) \
  "generated.ts equals the upstream-consistent one"
SIG_BEFORE="$(artifacts_sig "$WORK/c7b")"
run_check "$WORK/c7b" master
SIG_AFTER="$(artifacts_sig "$WORK/c7b")"
say "   only-snapshot rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
record "check_only_snapshot_stale_exit_nonzero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC (this is the RED-2 scenario)"
record "check_only_snapshot_stale_no_write" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "inode/size/mtime unchanged"

# ======================= ⑧ refresh 产物与可复现性 ==========================
section "⑧ refresh：provenance / bytes / hash / 可复现"
sandbox "$WORK/c8"
refresh_sandbox "$WORK/c8" master; R_RC=$?
record "refresh_exit_zero" $([ "$R_RC" -eq 0 ] && echo 0 || echo 1) "refresh rc=$R_RC against the fixture upstream"
PROV="$WORK/c8/contract/provenance.json"
SNAP="$WORK/c8/contract/openapi.json"
GEN="$WORK/c8/client-web/src/api/generated.ts"
record "refresh_writes_provenance" $([ -f "$PROV" ] && echo 0 || echo 1) "contract/provenance.json present"
record "refresh_writes_snapshot_bytes" \
  $([ "$(sha "$SNAP")" = "$(sha "$FIX/openapi.master.json")" ] && echo 0 || echo 1) \
  "snapshot bytes == upstream master bytes"
if [ -f "$PROV" ]; then
  EFFECTIVE="$(python3 - "$PROV" "$SNAP" "$SHA_MASTER" <<'PY'
import json, sys, hashlib, re
prov, snap_path, expect_sha = sys.argv[1:4]
doc = json.load(open(prov, encoding="utf-8"))
snap = open(snap_path, "rb").read()
problems = []
if doc.get("schemaVersion") != 1: problems.append("schemaVersion")
if doc.get("repo") != "pim-api": problems.append("repo=%r" % doc.get("repo"))
if doc.get("ref") != "master": problems.append("ref=%r" % doc.get("ref"))
if not re.match(r"^[0-9a-f]{40}$", str(doc.get("resolvedRef"))): problems.append("resolvedRef")
if not doc.get("resolvedRefSource"): problems.append("resolvedRefSource")
if doc.get("sourcePath") != "contract/openapi.json": problems.append("sourcePath")
if doc.get("bytes") != len(snap): problems.append("bytes")
if doc.get("sha256") != hashlib.sha256(snap).hexdigest(): problems.append("sha256")
for key in doc:
    if re.search(r"(AtUtc|timestamp|Timestamp|fetchedAt|generatedAt|date)", key):
        problems.append("non-deterministic field %s" % key)
print("|".join(problems) if problems else "OK")
PY
)"
  record "refresh_provenance_shape_ok" $([ "$EFFECTIVE" = "OK" ] && echo 0 || echo 1) "provenance fields: $EFFECTIVE"
  RESOLVED="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["resolvedRef"])' "$PROV")"
  record "refresh_provenance_resolved_ref" $([ "$RESOLVED" = "$SHA_MASTER" ] && echo 0 || echo 1) "resolvedRef=$RESOLVED == fixture master=$SHA_MASTER"
else
  record "refresh_provenance_shape_ok" 1 "provenance.json missing"
  record "refresh_provenance_resolved_ref" 1 "provenance.json missing"
fi

# 生成物可由快照逐字节复现（整文件比较，不排除 banner）
REGEN="$WORK/c8.regen.ts"
{ pim_contract_banner "$SNAP"; "$CLI" "$SNAP"; } > "$REGEN" 2>/dev/null
record "refresh_generated_reproducible_full_file" \
  $([ "$(sha "$REGEN")" = "$(sha "$GEN")" ] && echo 0 || echo 1) \
  "regenerate(snapshot) sha=$(sha "$REGEN") == committed generated sha=$(sha "$GEN")"
if grep -qE '(^|[^0-9a-f])[0-9a-f]{40}([^0-9a-f]|$)' "$GEN"; then
  record "refresh_banner_has_no_movable_sha" 1 "generated.ts contains a standalone 40-hex token: $(grep -oE '(^|[^0-9a-f])[0-9a-f]{40}([^0-9a-f]|$)' "$GEN" | head -1)"
else
  record "refresh_banner_has_no_movable_sha" 0 "generated.ts contains no movable 40-hex ref/sha (only the snapshot's 64-hex content digest)"
fi

# 可复现：再刷一次，三个产物必须逐字节相同
SIG1="$(artifacts_sha "$WORK/c8")"
refresh_sandbox "$WORK/c8" master >/dev/null 2>&1; R2_RC=$?
SIG2="$(artifacts_sha "$WORK/c8")"
record "refresh_deterministic_second_run" $([ "$SIG1" = "$SIG2" ] && echo 0 || echo 1) "second refresh rc=$R2_RC left the three artifacts byte-identical"
say "   artifacts sha: $SIG2"

# ======================= ⑨ refresh 失败不写三个产物 ========================
section "⑨ refresh 失败：三个产物一个字节都不写"
sandbox "$WORK/c9"
refresh_sandbox "$WORK/c9" master >/dev/null 2>&1  # sanity: 成功刷新不改字节
SIG_BEFORE="$(artifacts_sha "$WORK/c9")"
P_BEFORE="$(fsig "$WORK/c9/contract/provenance.json")"
( cd "$WORK/c9" && env PIM_CONTRACT_RAW_BASE="http://127.0.0.1:1" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=master \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c9.fail1.log" 2>&1
F1RC=$?
SIG_AFTER="$(artifacts_sha "$WORK/c9")"
record "refresh_fetch_failure_nonzero" $([ "$F1RC" -ne 0 ] && echo 0 || echo 1) "rc=$F1RC when the source is unreachable"
record "refresh_fetch_failure_writes_nothing" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "three artifacts unchanged"

( cd "$WORK/c9" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=html \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c9.fail2.log" 2>&1
F2RC=$?
SIG_AFTER="$(artifacts_sha "$WORK/c9")"
record "refresh_bad_payload_nonzero" $([ "$F2RC" -ne 0 ] && echo 0 || echo 1) "rc=$F2RC for an HTML payload"
record "refresh_bad_payload_writes_nothing" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "three artifacts unchanged"

( cd "$WORK/c9" && env PIM_CONTRACT_RAW_BASE="$RAW" PIM_CONTRACT_REPO=pim-api \
    PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF=empty \
    bash scripts/ci/fetch-contract.sh ) > "$WORK/c9.fail3.log" 2>&1
F3RC=$?
SIG_AFTER="$(artifacts_sha "$WORK/c9")"
record "refresh_empty_payload_nonzero" $([ "$F3RC" -ne 0 ] && echo 0 || echo 1) "rc=$F3RC for an empty payload"
record "refresh_empty_payload_writes_nothing" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "three artifacts unchanged"

# 未知位置参数必须报错且不写（RED-1 残余加固，Minor M1）
( cd "$WORK/c9" && bash scripts/ci/fetch-contract.sh --check ) > "$WORK/c9.fail4.log" 2>&1
F4RC=$?
SIG_AFTER="$(artifacts_sha "$WORK/c9")"
record "refresh_unknown_arg_nonzero" $([ "$F4RC" -ne 0 ] && echo 0 || echo 1) "rc=$F4RC for the unknown arg '--check'"
record "refresh_unknown_arg_writes_nothing" $([ "$SIG_BEFORE" = "$SIG_AFTER" ] && echo 0 || echo 1) "three artifacts unchanged (and provenance mtime unchanged: $([ "$P_BEFORE" = "$(fsig "$WORK/c9/contract/provenance.json")" ] && echo yes || echo no))"

# ======================= ⑩ 非契约提交但字节相同 → PASS =====================
section "⑩ 上游出现非契约提交、契约字节不变 → 必须 PASS"
sandbox "$WORK/c10"
refresh_sandbox "$WORK/c10" "$SHA_C1" >/dev/null 2>&1      # provenance.resolvedRef = c1
commit_sandbox "$WORK/c10" 'synced at c1'
PROV_SHA="$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1]))["resolvedRef"])' "$WORK/c10/contract/provenance.json" 2>/dev/null || echo missing)"
run_check "$WORK/c10" master                               # 上游 HEAD 已是 c2（非契约提交）
say "   rc=$CHECK_RC  provenance.resolvedRef=$PROV_SHA  今天解析出的上游=$SHA_MASTER"
record "noncontract_commit_snapshot_unchanged_pass" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC although upstream HEAD moved c1->c2"
record "noncontract_commit_head_really_moved" $([ "$SHA_C1" != "$SHA_MASTER" ] && echo 0 || echo 1) "c1=$SHA_C1 != c2=$SHA_MASTER"
record "provenance_may_lag_head" $([ "$PROV_SHA" = "$SHA_C1" ] && echo 0 || echo 1) "provenance.resolvedRef stayed $SHA_C1 (lag allowed, not compared to today's HEAD)"

# ======================= ⑪ CI 环境下测试旁路被拒 ===========================
section "⑪ CI 环境下把仓内文件当权威源的测试旁路必须被拒（不得静默变绿）"
sandbox "$WORK/c11"
refresh_sandbox "$WORK/c11" v0 >/dev/null 2>&1
commit_sandbox "$WORK/c11" 'synced at v0 (stale vs master)'
# 快照本身是陈旧的；若旁路生效会「自己和自己比」而假绿。
run_check_env "$WORK/c11" master CI=true PIM_CONTRACT_TEST_MODE=1 \
  PIM_CONTRACT_TEST_SOURCE_FILE="$WORK/c11/contract/openapi.json"
say "   CI+bypass rc=$CHECK_RC  tail: $(tail -1 "$CHECK_LOG")"
record "ci_refuses_test_bypass_exit_4" $([ "$CHECK_RC" -eq 4 ] && echo 0 || echo 1) "rc=$CHECK_RC (config error, not a silent pass)"
record "ci_refuses_test_bypass_not_zero" $([ "$CHECK_RC" -ne 0 ] && echo 0 || echo 1) "rc=$CHECK_RC != 0"

run_check_env "$WORK/c11" master GITHUB_ACTIONS=true PIM_CONTRACT_TEST_SOURCE_FILE="$WORK/c11/contract/openapi.json"
record "gha_refuses_source_file_without_test_mode" $([ "$CHECK_RC" -eq 4 ] && echo 0 || echo 1) "rc=$CHECK_RC when GITHUB_ACTIONS=true"

# CI 下用环境变量把「仓内产物路径」指向别的文件也必须被拒（否则等于换掉被校验对象）
run_check_env "$WORK/c11" master CI=true PIM_CONTRACT_SNAPSHOT="$WORK/c11/contract/openapi.json"
record "ci_refuses_snapshot_path_override" $([ "$CHECK_RC" -eq 4 ] && echo 0 || echo 1) "rc=$CHECK_RC with PIM_CONTRACT_SNAPSHOT set under CI"
run_check_env "$WORK/c11" master PIM_CONTRACT_STRICT=1 PIM_CONTRACT_GENERATED="$WORK/c11/client-web/src/api/generated.ts"
record "strict_mode_refuses_generated_path_override" $([ "$CHECK_RC" -eq 4 ] && echo 0 || echo 1) "rc=$CHECK_RC with PIM_CONTRACT_STRICT=1 and a generated path override"

# 非 CI 下、且显式声明测试模式时旁路才可用（测试替身自身的正面对照）
run_check_env "$WORK/c11" master PIM_CONTRACT_TEST_MODE=1 \
  PIM_CONTRACT_TEST_SOURCE_FILE="$WORK/c11/contract/openapi.json"
record "test_stub_seam_reachable_only_in_test_mode" $([ "$CHECK_RC" -eq 0 ] && echo 0 || echo 1) "rc=$CHECK_RC with PIM_CONTRACT_TEST_MODE=1 outside CI"

# ======================= ⑫ 真实 CI YAML step + gate 传播 ===================
section "⑫ 原样抽取真实 CI step 与 gate check 函数，证明失败传播"
STEP="$WORK/ci-step-body.sh"
GATE="$WORK/ci-gate-body.sh"
python3 - "$CI_YML" "$STEP" "$GATE" <<'PY'
import sys, yaml
src, step_out, gate_out = sys.argv[1:4]
doc = yaml.safe_load(open(src, encoding="utf-8"))
want = "Verify client contract is up to date"
found = None
for job, spec in (doc.get("jobs") or {}).items():
    for step in (spec.get("steps") or []):
        if step.get("name") == want:
            found = (job, step)
if not found:
    print("step not found", file=sys.stderr); sys.exit(1)
job, step = found
open(step_out, "w", encoding="utf-8").write(step["run"])
# gate
gate = doc["jobs"]["gate"]
body = None
for step in (gate.get("steps") or []):
    if "run" in step:
        body = step["run"]
open(gate_out, "w", encoding="utf-8").write(body or "")
print("extracted step '%s' from job '%s'" % (want, job))
PY
[ -s "$STEP" ] || { record "yaml_step_extracted" 1 "cannot extract CI step body"; }
[ -s "$STEP" ] && record "yaml_step_extracted" 0 "step body extracted verbatim from .github/workflows/ci.yml"

# 结构断言：step 所在 job 被 gate 消费；无 if / continue-on-error / env 覆盖
STRUCT="$(python3 - "$CI_YML" <<'PY'
import sys, yaml
doc = yaml.safe_load(open(sys.argv[1], encoding="utf-8"))
jobs = doc["jobs"]
want = "Verify client contract is up to date"
problems = []
owner = None
for job, spec in jobs.items():
    for step in (spec.get("steps") or []):
        if step.get("name") == want:
            owner = job
            if "if" in step: problems.append("step has if:")
            if step.get("continue-on-error") in (True, "true"): problems.append("step has continue-on-error")
            if step.get("env"): problems.append("step sets env: %s" % sorted(step["env"]))
if owner is None: problems.append("step missing")
else:
    if owner not in (jobs["gate"].get("needs") or []): problems.append("owner job %s not in gate.needs" % owner)
    if "if" in jobs[owner]: problems.append("owner job has if:")
    if jobs[owner].get("continue-on-error") in (True, "true"): problems.append("owner job has continue-on-error")
on = doc.get("on") or doc.get(True) or {}
triggers = sorted(on.keys())
if "push" not in triggers: problems.append("push trigger missing")
if "pull_request" not in triggers: problems.append("pull_request trigger missing")
if "workflow_dispatch" not in on: problems.append("workflow_dispatch trigger missing")
# gate 必须逐个 check 消费 owner job
import re
gate_body = " ".join(s.get("run","") for s in jobs["gate"]["steps"] if "run" in s)
if owner and ('check "%s"' % owner) not in gate_body:
    problems.append("gate does not call check \"%s\"" % owner)
print("|".join(problems) if problems else "OK")
PY
)"
record "ci_step_wiring_ok" $([ "$STRUCT" = "OK" ] && echo 0 || echo 1) "structure: $STRUCT"
say "   CI triggers: $(python3 -c 'import yaml,sys;d=yaml.safe_load(open(sys.argv[1]));print(sorted((d.get("on") or d.get(True)).keys()))' "$CI_YML")"

# 步骤体在三个失败分支都必须非 0（GitHub 用 bash -e -o pipefail 调 run）
step_run() { # step_run <dir> <ref> [raw_base]
  local dir="$1" ref="$2" base="${3:-$RAW}"
  ( cd "$dir" && env PIM_CONTRACT_RAW_BASE="$base" PIM_CONTRACT_REPO=pim-api \
      PIM_CONTRACT_REMOTE="$UPSTREAM_REPO" PIM_CONTRACT_REF="$ref" \
      bash -e -o pipefail "$STEP" ) > "$dir.step.log" 2>&1
  STEP_RC=$?
}
step_run "$WORK/c1" master
record "ci_step_green_on_consistent_repo" $([ "$STEP_RC" -eq 0 ] && echo 0 || echo 1) "rc=$STEP_RC on the consistent sandbox"
step_run "$WORK/c7a" master
record "ci_step_red_on_generated_stale" $([ "$STEP_RC" -ne 0 ] && echo 0 || echo 1) "rc=$STEP_RC when only generated.ts is stale"
step_run "$WORK/c7b" master
record "ci_step_red_on_snapshot_stale" $([ "$STEP_RC" -ne 0 ] && echo 0 || echo 1) "rc=$STEP_RC when only the snapshot is stale"
step_run "$WORK/c1" master "http://127.0.0.1:1"
record "ci_step_red_on_fetch_failure" $([ "$STEP_RC" -ne 0 ] && echo 0 || echo 1) "rc=$STEP_RC when the source is unreachable"

# gate：把 build-web 置为 failure，gate 必须非 0（失败传播到 required check）
gate_run() { # gate_run <R_RESOLVE> <R_WEB>
  ( env R_RESOLVE="$1" R_WEB="$2" bash -e -o pipefail "$GATE" ) > "$WORK/gate.log" 2>&1
  GATE_RC=$?
}
gate_run success success
record "gate_green_when_jobs_success" $([ "$GATE_RC" -eq 0 ] && echo 0 || echo 1) "rc=$GATE_RC with R_WEB=success"
gate_run success failure
record "gate_red_when_build_web_failed" $([ "$GATE_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GATE_RC with R_WEB=failure (freshness failure propagates)"
gate_run success skipped
record "gate_red_when_build_web_skipped" $([ "$GATE_RC" -ne 0 ] && echo 0 || echo 1) "rc=$GATE_RC with R_WEB=skipped"

# ============================== 汇总 =======================================
say ""
say "== summary: PASS=$PASS FAIL=$FAIL =="
if [ "$FAIL" -eq 0 ]; then
  say "RESULT: 全部断言通过（exit 0）"
  exit 0
fi
say "RESULT: 存在失败断言（exit 1）"
exit 1
