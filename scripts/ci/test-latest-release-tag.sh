#!/usr/bin/env bash
# Fixture tests for scripts/ci/latest-release-tag.sh (hermetic: no network, no gh).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
SCRIPT="$ROOT/scripts/ci/latest-release-tag.sh"
fail=0

check() {
  local name="$1" expected="$2" actual="$3"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL $name: expected [$expected] got [$actual]"
    fail=1
  else
    echo "OK   $name -> [$actual]"
  fi
}

run() { # run <tags-list> [extra args...]
  local tags="$1"; shift
  RELEASE_TAGS="$tags" bash "$SCRIPT" --repo owner/repo "$@"
}

# Picks the highest version number, regardless of the order gh returns them in
# (gh lists by created_at, which is exactly what we must NOT trust).
check "picks-highest" "v2026.09.489" \
  "$(run $'v2026.09.489\nv2026.09.488\nv2026.09.485')"
check "order-independent" "v2026.09.489" \
  "$(run $'v2026.09.485\nv2026.09.488\nv2026.09.489')"

# The two-merge burst from 2026-09-13. Runs are serialized, so each release's
# predecessor is simply the newest release other than its own tag: run 489 sees
# 488 (published while it waited in the queue), run 488 sees 485 (489 did not
# exist yet when it published).
check "burst-previous-of-489" "v2026.09.488" \
  "$(run $'v2026.09.489\nv2026.09.488\nv2026.09.485' --exclude v2026.09.489)"
check "burst-previous-of-488" "v2026.09.485" \
  "$(run $'v2026.09.488\nv2026.09.485' --exclude v2026.09.488)"
# Re-running an OLDER run resolves to a newer tag (v489 here). That is expected:
# ci.yml then rejects it with the "not an ancestor of HEAD" guard instead of
# publishing a release mixing a newer predecessor's assets with older code.
check "retro-run-resolves-newer-tag" "v2026.09.489" \
  "$(run $'v2026.09.489\nv2026.09.488\nv2026.09.485' --exclude v2026.09.488)"

# Numeric, not lexicographic: 1000 must beat 999.
check "numeric-compare" "v2026.09.1000" \
  "$(run $'v2026.09.999\nv2026.09.1000')"

# A client-patch release tag keeps its suffix but is ordered by the base version.
check "patch-suffix" "v2026.09.466+android.1" \
  "$(run $'v2026.09.457\nv2026.09.466+android.1\nv2026.09.449')"

# Tags that are not release versions are ignored.
check "ignores-non-version-tags" "v2026.09.42" \
  "$(run $'nightly\nv2026.09.42\nrelease-candidate-v2\nv2026.09.x')"

# No releases yet (first-ever master push) => empty output, success exit code.
check "empty-when-no-tags" "" "$(run '')"
check "empty-when-only-excluded" "" "$(run $'v2026.09.489' --exclude v2026.09.489)"

# Empty output must still exit 0 so callers under `set -e` can test for it.
set +e
RELEASE_TAGS="" bash "$SCRIPT" --repo owner/repo >/dev/null 2>&1
code=$?
set -e
if [[ "$code" -ne 0 ]]; then
  echo "FAIL empty-exit-code: expected 0 got $code"
  fail=1
else
  echo "OK   empty-exit-code -> [0]"
fi

# Missing --repo without the test hook is an error.
set +e
env -u RELEASE_TAGS bash "$SCRIPT" >/dev/null 2>&1
code=$?
set -e
if [[ "$code" -ne 2 ]]; then
  echo "FAIL missing-repo: expected exit 2 got $code"
  fail=1
else
  echo "OK   missing-repo -> [2]"
fi

exit "$fail"
