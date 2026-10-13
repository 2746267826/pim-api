#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：前端产物镜像引用的解析与冻结（REQ-3 / AC-6 第 2 项）。
# 用 test-helpers/fake-docker.sh 顶替 docker：真镜像路径由 CI 的真实构建覆盖。
# 用法：scripts/ci/test-resolve-web-dist.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/resolve-web-dist.sh"
FAKE_DOCKER="$HERE/test-helpers/fake-docker.sh"
IMAGE="ghcr.io/example/pim-web-dist"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-web-dist-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

export FAKE_DOCKER_STATE="$TMP/state"
export FAKE_DOCKER_CONTENT="$TMP/content"
mkdir -p "$FAKE_DOCKER_STATE" "$FAKE_DOCKER_CONTENT"
export PIM_DOCKER="$FAKE_DOCKER"
export FAKE_DOCKER_DIGEST="sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
export PIM_WEB_DIST_IMAGE="$IMAGE"

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }
contains() { if [[ "$3" == *"$2"* ]]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 未包含 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }

put() { # <ref-suffix> <json>
  local dir="$FAKE_DOCKER_CONTENT/$(echo "$IMAGE:$1" | tr '/:' '__')"
  mkdir -p "$dir"
  printf '%s' "$2" > "$dir/web-version.json"
}
field() { sed -n "s/^$2=//p" <<<"$1"; }
run() { bash "$SCRIPT" "$@" --print-env 2>"$TMP/last.err"; }

put latest '{"version":"26.10.831","gitSha":"abc1234","builtAt":"2026-10-13T10:00:00Z"}'
put sha-abc1234 '{"version":"26.10.831","gitSha":"abc1234","builtAt":"2026-10-13T10:00:00Z"}'
put sha-bbbbbbb '{"version":"26.10.831","gitSha":"ccccccc","builtAt":"2026-10-13T10:00:00Z"}'
put latest-nofreeze '{"version":"26.10.831","gitSha":"ddddddd","builtAt":"2026-10-13T10:00:00Z"}'
put latest-partial '{"version":"26.10.831"}'
put latest-malformed 'this is not json'
put latest-badsha '{"version":"26.10.831","gitSha":"ZZZZZZZ","builtAt":"2026-10-13T10:00:00Z"}'
make_ref() { mkdir -p "$FAKE_DOCKER_CONTENT/$(echo "$IMAGE:$1" | tr '/:' '__')"; }
make_ref latest-nofile

export FAKE_DOCKER_REFS="$IMAGE:latest $IMAGE:sha-abc1234 $IMAGE:sha-bbbbbbb $IMAGE:latest-nofreeze $IMAGE:latest-nofile $IMAGE:latest-partial $IMAGE:latest-malformed $IMAGE:latest-badsha"

echo "== resolve-web-dist.sh =="

# 1) 默认取最新：冻结成 sha-<7>，并打印「解析前 tag → 冻结后引用」
out="$(run)" && rc=0 || rc=$?
check "默认 latest 解析成功（exit 0）" "0" "$rc"
check "冻结为不可变引用" "sha-abc1234" "$(field "$out" web_dist_ref)"
check "带出 digest" "$FAKE_DOCKER_DIGEST" "$(field "$out" web_dist_digest)"
check "带出前端版本号" "26.10.831" "$(field "$out" web_version)"
check "带出前端短 sha" "abc1234" "$(field "$out" web_git_sha)"
check "带出构建时间" "2026-10-13T10:00:00Z" "$(field "$out" web_built_at)"
contains "打印解析前 tag → 冻结后引用" "解析前 tag: $IMAGE:latest → 冻结后引用: $IMAGE:sha-abc1234" "$(cat "$TMP/last.err")"

# 2) 触发方给出确切引用且与镜像内容同源 → 用那一个
out="$(run --ref sha-abc1234)" && rc=0 || rc=$?
check "给定 sha-abc1234 成功" "0" "$rc"
check "给定引用被原样采用" "sha-abc1234" "$(field "$out" web_dist_ref)"

# 3) 给定引用与镜像内容不同源 → 硬失败（不许触发 A 版烘进去 B 版）
if run --ref sha-bbbbbbb >/dev/null 2>"$TMP/mismatch.err"; then
  echo "  ❌ 引用与镜像内容不一致应当失败"; fail=$((fail+1))
else
  echo "  ✅ 引用与镜像内容不一致 → 硬失败"; pass=$((pass+1))
  contains "不一致原因可见" "不一致" "$(cat "$TMP/last.err")"
fi

# 4) 默认取最新但冻结后的不可变 tag 不存在 → 硬失败
if run --ref latest-nofreeze >/dev/null 2>"$TMP/nofreeze.err"; then
  echo "  ❌ 冻结 tag 缺失应当失败"; fail=$((fail+1))
else
  echo "  ✅ 冻结 tag 缺失 → 硬失败"; pass=$((pass+1))
  contains "冻结 tag 缺失原因可见" "不存在或不可匿名拉取" "$(cat "$TMP/last.err")"
fi

# 5) 镜像存在但没有 web-version.json → 硬失败
if run --ref latest-nofile >/dev/null 2>"$TMP/nofile.err"; then
  echo "  ❌ 缺版本文件应当失败"; fail=$((fail+1))
else
  echo "  ✅ 缺 web-version.json → 硬失败"; pass=$((pass+1))
  contains "缺文件原因可见" "web-version.json" "$(cat "$TMP/last.err")"
fi

# 6) 版本文件缺契约字段 → 硬失败
if run --ref latest-partial >/dev/null 2>"$TMP/partial.err"; then
  echo "  ❌ 缺字段应当失败"; fail=$((fail+1))
else
  echo "  ✅ 版本文件缺字段 → 硬失败"; pass=$((pass+1))
fi

# 7) 版本文件不是 JSON → 硬失败
if run --ref latest-malformed >/dev/null 2>&1; then
  echo "  ❌ 非法 JSON 应当失败"; fail=$((fail+1))
else
  echo "  ✅ 版本文件非法 JSON → 硬失败"; pass=$((pass+1))
fi

# 8) gitSha 不是 7 位小写十六进制 → 硬失败
if run --ref latest-badsha >/dev/null 2>"$TMP/badsha.err"; then
  echo "  ❌ 非法 sha 应当失败"; fail=$((fail+1))
else
  echo "  ✅ gitSha 非 7 位小写十六进制 → 硬失败"; pass=$((pass+1))
fi

# 9) 镜像拉不到（不在已知引用里）→ 硬失败
if run --ref sha-deadbee >/dev/null 2>"$TMP/pull.err"; then
  echo "  ❌ 拉取失败应当硬失败"; fail=$((fail+1))
else
  echo "  ✅ 镜像拉不到 → 硬失败"; pass=$((pass+1))
fi

echo ""
echo "resolve-web-dist: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
