#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/resolve-version.sh"
fail=0

run_case() {
  local name="$1"; shift
  local out
  out="$( "$@" )"
  echo "$out" > "/tmp/rv-$name.env"
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
  env GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=push \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "2026.07.42" /tmp/rv-master.env
assert_eq version_code "100042" /tmp/rv-master.env
assert_eq is_release "true" /tmp/rv-master.env
assert_eq git_sha_short "abcdef1" /tmp/rv-master.env
assert_eq year_month "2026.07" /tmp/rv-master.env
assert_eq artifact_slug "2026.07.42" /tmp/rv-master.env

# PR
run_case pr \
  env GITHUB_REF=refs/pull/12/merge GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=pull_request GITHUB_REF_NAME=12/merge \
      PR_NUMBER=12 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "2026.07.42-pr.12+abcdef1" /tmp/rv-pr.env
assert_eq version_code "100042" /tmp/rv-pr.env
assert_eq is_release "false" /tmp/rv-pr.env
assert_eq artifact_slug "2026.07.42-pr.12-abcdef1" /tmp/rv-pr.env

# dispatch non-master
run_case dev \
  env GITHUB_REF=refs/heads/codex/foo GITHUB_RUN_NUMBER=7 GITHUB_SHA=deadbeefcafebabe \
      GITHUB_EVENT_NAME=workflow_dispatch \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "2026.07.7-dev+deadbee" /tmp/rv-dev.env
assert_eq is_release "false" /tmp/rv-dev.env

# client patch on master
run_case patch \
  env GITHUB_REF=refs/heads/master GITHUB_RUN_NUMBER=42 GITHUB_SHA=abcdef1234567890 \
      GITHUB_EVENT_NAME=workflow_dispatch CLIENT_PATCH=android.1 \
      bash "$SCRIPT" --date 2026-07-12 --print-env
assert_eq version "2026.07.42+android.1" /tmp/rv-patch.env
assert_eq is_release "true" /tmp/rv-patch.env

if [[ "$fail" -ne 0 ]]; then
  echo "resolve-version tests failed"
  exit 1
fi
echo "resolve-version tests passed"
