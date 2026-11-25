#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：promote_tag 的来源校验。
#
# 重点：tag 不能只凭名字就提升。必须同时证明
#   * tag 指向的 commit 在当前 master 历史里；
#   * 该 commit 的 VERSION 就是输入的显示版本；
#   * tag 里的 run 号能在该 commit 上找到一个成功的 master push 构建；
#   * release 资产齐全，且镜像台账里的显示版本与输入一致。
# 用 stub gh + 临时 git 仓覆盖正例和每个反例，避免"校验只写在注释里"。
# ---------------------------------------------------------------------------
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/validate-promote-target.sh"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-promote-target.XXXXXX")"
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
case "$cmd" in
  release)
    sub="${1:-}"; shift || true
    case "$sub" in
      view)
        jq -n \
          --arg tagTag "${FAKE_TAG_NAME:-api-v26.11.42}" \
          --argjson pre "${FAKE_IS_PRE:-true}" \
          --argjson draft "${FAKE_IS_DRAFT:-false}" \
          --argjson latest "${FAKE_IS_LATEST:-false}" \
          --arg target "${FAKE_TARGET:-${FAKE_SHA:-}}" \
          --argjson assets "${FAKE_ASSETS:-[]}" \
          '{tagName:$tagTag,isPrerelease:$pre,isDraft:$draft,isLatest:$latest,targetCommitish:$target,assets:$assets}'
        ;;
      download)
        dir=""
        while [[ $# -gt 0 ]]; do
          case "$1" in
            --dir) dir="$2"; shift 2 ;;
            *) shift ;;
          esac
        done
        [[ -n "$dir" ]] || { echo "stub gh: --dir required" >&2; exit 2; }
        jq -n --arg v "${FAKE_LEDGER_VERSION:-1.1.0}" '{version:$v,gitSha:"abc1234",builtAt:"2026-11-25T00:00:00Z",apiVersion:"26.11.42"}' > "$dir/pim-web-version.json"
        ;;
      *) echo "stub gh: unsupported release $sub" >&2; exit 2 ;;
    esac
    ;;
  api)
    echo "{\"object\":{\"type\":\"commit\",\"sha\":\"${FAKE_SHA:?FAKE_SHA required}\"}}"
    ;;
  run)
    sub="${1:-}"
    [[ "$sub" = "list" ]] || { echo "stub gh: unsupported run $sub" >&2; exit 2; }
    jq -n \
      --argjson id "${FAKE_RUN_ID:-999}" \
      --argjson number "${FAKE_RUN_NUMBER:-42}" \
      '[{databaseId:$id,event:"push",headBranch:"master",conclusion:"success",number:$number}]'
    ;;
  *) echo "stub gh: unsupported command $cmd" >&2; exit 2 ;;
esac
STUB
chmod +x "$TMP/bin/gh"
export PATH="$TMP/bin:$PATH"

make_repo() { # <dir> <display-version>
  git init -q "$1"
  git -C "$1" config user.email test@example.com
  git -C "$1" config user.name test
  printf '%s\n' "$2" > "$1/VERSION"
  git -C "$1" add VERSION
  git -C "$1" commit -q -m "init"
}

ASSETS='[{"name":"pim-api-v26.11.42.tar.gz"},{"name":"pim-api-server-v26.11.42.tar.gz"},{"name":"pim-mcp-server-v26.11.42.tar.gz"},{"name":"pim-web-version.json"}]'

run_case() { # <label> <expected rc> <out-file> <env...>
  local label="$1" want="$2" out="$3"; shift 3
  local rc=0
  env "$@" bash "$SCRIPT" --repo 2746267826/pim-api --tag api-v26.11.42 --version 1.1.0 > "$out" 2>&1 || rc=$?
  if [[ "$want" == "pass" ]]; then
    if [[ "$rc" -eq 0 ]]; then ok "$label"; else bad "$label（rc=$rc）：$(cat "$out")"; fi
  else
    if [[ "$rc" -eq 0 ]]; then
      bad "$label：期望失败，实际通过"
    elif ! grep -q "$want" "$out"; then
      bad "$label：期望错误包含 [$want]，实际：$(cat "$out")"
    else
      ok "$label"
    fi
  fi
}

echo "== validate-promote-target.sh =="

REPO_GOOD="$TMP/repo-good"
make_repo "$REPO_GOOD" 1.1.0
SHA_GOOD="$(git -C "$REPO_GOOD" rev-parse HEAD)"

pushd "$REPO_GOOD" >/dev/null
run_case "完整来源证明 → 通过" pass "$TMP/out-good" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_IS_LATEST=false \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 FAKE_LEDGER_VERSION=1.1.0
if grep -q '^run_id=999$' "$TMP/out-good" && grep -q '^tag=api-v26.11.42$' "$TMP/out-good"; then
  ok "输出 tag / run_id"
else
  bad "输出缺少 tag / run_id：$(cat "$TMP/out-good")"
fi

run_case "已正式版重入 → 通过（交给 promote 收敛 latest）" pass "$TMP/out-formal" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=false FAKE_IS_LATEST=true \
  FAKE_ASSETS="$ASSETS" FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 FAKE_LEDGER_VERSION=1.1.0

run_case "台账显示版本不符 → 失败" "台账" "$TMP/out-ledger" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 FAKE_LEDGER_VERSION=26.11.42

run_case "run 号与 tag 不符 → 失败" "run" "$TMP/out-run" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_NUMBER=41 FAKE_RUN_ID=999 FAKE_LEDGER_VERSION=1.1.0

run_case "缺 mcp 资产 → 失败" "pim-mcp-server" "$TMP/out-assets" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true \
  FAKE_ASSETS='[{"name":"pim-api-v26.11.42.tar.gz"},{"name":"pim-api-server-v26.11.42.tar.gz"},{"name":"pim-web-version.json"}]' \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 FAKE_LEDGER_VERSION=1.1.0

run_case "release targetCommitish 指向别的 commit → 失败" "targetCommitish" "$TMP/out-target" \
  FAKE_SHA="$SHA_GOOD" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_TARGET=1111111111111111111111111111111111111111 \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 FAKE_LEDGER_VERSION=1.1.0
popd >/dev/null

REPO_BAD_VERSION="$TMP/repo-bad-version"
make_repo "$REPO_BAD_VERSION" 2.0.0
SHA_BAD_VERSION="$(git -C "$REPO_BAD_VERSION" rev-parse HEAD)"
pushd "$REPO_BAD_VERSION" >/dev/null
run_case "tag commit 的 VERSION 不是目标版本 → 失败" "VERSION" "$TMP/out-version" \
  FAKE_SHA="$SHA_BAD_VERSION" FAKE_IS_PRE=true FAKE_ASSETS="$ASSETS" \
  FAKE_RUN_NUMBER=42 FAKE_RUN_ID=999 FAKE_LEDGER_VERSION=1.1.0
popd >/dev/null

if [[ "$fail" -ne 0 ]]; then
  echo "validate-promote-target tests failed: ${fail} failed, ${pass} passed" >&2
  exit 1
fi
echo "validate-promote-target tests passed (${pass} checks)"
