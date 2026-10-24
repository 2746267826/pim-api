#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/resolve-version.sh"
fail=0

# Per-run scratch dir: a fixed /tmp path collides with other users/agents that
# run this test (the second run then fails with "Permission denied").
WORKDIR="$(mktemp -d)"
trap 'rm -rf "$WORKDIR"' EXIT

run_case() {
  local name="$1"; shift
  local out
  out="$( "$@" )"
  echo "$out" > "$WORKDIR/rv-$name.env"
}

assert_eq() {
  local key="$1" expected="$2" file="$3"
  local actual
  actual="$(grep "^${key}=" "$file" | cut -d= -f2-)"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL $file $key: expected [$expected] got [$actual]"
    fail=1
  else
    echo "OK $key=$actual"
  fi
}

# master
run_case master \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.42" $WORKDIR/rv-master.env
assert_eq release_tag "api-v26.07.42" $WORKDIR/rv-master.env
assert_eq version_code "100042" $WORKDIR/rv-master.env
assert_eq is_release "true" $WORKDIR/rv-master.env
assert_eq git_sha_short "abcdef1" $WORKDIR/rv-master.env
assert_eq year_month "26.07" $WORKDIR/rv-master.env
assert_eq artifact_slug "26.07.42" $WORKDIR/rv-master.env

# PR
run_case pr \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/pull/12/merge GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=pull_request GITHUB_REF_NAME=12/merge \
      PR_NUMBER=12 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.42-pr.12+abcdef1" $WORKDIR/rv-pr.env
assert_eq release_tag "api-v26.07.42" $WORKDIR/rv-pr.env
assert_eq version_code "100042" $WORKDIR/rv-pr.env
assert_eq is_release "false" $WORKDIR/rv-pr.env
assert_eq artifact_slug "26.07.42-pr.12-abcdef1" $WORKDIR/rv-pr.env

# dispatch non-master
run_case dev \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/codex/foo GITHUB_RUN_NUMBER=7 GITHUB_SHA=deadbeefcafebabe \
      GITHUB_EVENT_NAME=workflow_dispatch \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.7-dev+deadbee" $WORKDIR/rv-dev.env
assert_eq is_release "false" $WORKDIR/rv-dev.env

# client patch on master
run_case patch \
  env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=workflow_dispatch CLIENT_PATCH=android.1 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "26.07.42+android.1" $WORKDIR/rv-patch.env
assert_eq is_release "true" $WORKDIR/rv-patch.env

# ---- composite action 必须声明脚本输出的每一个键 ----
# action.yml 里没声明的 output 不会透出给调用方，上游拿到的是空字符串。
# 症状是发版时报「GitHub Releases requires a tag」这类看不懂的错，而且直接跑
# 这个脚本是测不出来的（脚本本身输出正常）。这里把两者钉在一起。
ACTION_YML="$ROOT/.github/actions/resolve-version/action.yml"
missing=""
declared_keys=0
while IFS= read -r key; do
  [[ -z "$key" ]] && continue
  declared_keys=$((declared_keys + 1))
  if ! grep -qE "^  ${key}:" "$ACTION_YML"; then
    missing="$missing $key"
  fi
done < <(env GITHUB_REPOSITORY=2746267826/pim-api GITHUB_REF=refs/heads/master \
             GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1 GITHUB_EVENT_NAME=push \
             bash "$SCRIPT" --date 2026-07-12 --print-env | sed 's/=.*//')
# 键数为 0 说明脚本没输出（例如它自己报错退出）—— 这同样是失败，不能当成"全都没漏"。
if [[ "$declared_keys" -eq 0 ]]; then
  echo "FAIL action-outputs-declared: 脚本没有输出任何键，检查本身失效"
  fail=1
elif [[ -n "$missing" ]]; then
  echo "FAIL action.yml 未声明脚本输出的键:$missing"
  fail=1
else
  echo "OK   action-outputs-declared ($declared_keys keys)"
fi

if [[ "$fail" -ne 0 ]]; then
  echo "resolve-version tests failed"
  exit 1
fi
echo "resolve-version tests passed"
