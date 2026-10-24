#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：release 台账 pim-web-version.json 的生成（REQ-7 / AC-2）。
# 断言「三个前端字段来自烘焙好的 api 镜像内的文件，原样转录」这件事真的成立。
# 用法：scripts/ci/test-build-web-ledger.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCRIPT="$HERE/build-web-ledger.sh"
IMAGE="ghcr.io/example/pim-api-server:v26.10.835"
TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-ledger-test.XXXXXX")"
trap 'rm -rf "$TMP"' EXIT

export FAKE_DOCKER_STATE="$TMP/state"
export FAKE_DOCKER_CONTENT="$TMP/content"
mkdir -p "$FAKE_DOCKER_STATE" "$FAKE_DOCKER_CONTENT"
export PIM_DOCKER="$HERE/test-helpers/fake-docker.sh"
export FAKE_DOCKER_REFS="$IMAGE ghcr.io/example/pim-api-server:v-empty"
export FAKE_DOCKER_DIGEST="sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

# 烘焙好的 api 镜像里，wwwroot 下的版本文件（脚本必须从**这个**文件转录）
DIR="$FAKE_DOCKER_CONTENT/$(echo "$IMAGE" | tr '/:' '__')/app/wwwroot"
mkdir -p "$DIR"
printf '%s' '{"version":"26.10.831","gitSha":"abc1234","builtAt":"2026-10-13T10:00:00Z"}' > "$DIR/web-version.json"
mkdir -p "$FAKE_DOCKER_CONTENT/$(echo "ghcr.io/example/pim-api-server:v-empty" | tr '/:' '__')"

pass=0
fail=0
check() { if [ "$2" = "$3" ]; then echo "  ✅ $1"; pass=$((pass+1)); else echo "  ❌ $1 — 期望 [$2]，实际 [$3]"; fail=$((fail+1)); fi; }

echo "== build-web-ledger.sh =="

OUT="$TMP/pim-web-version.json"
if bash "$SCRIPT" --api-image "$IMAGE" \
  --api-version "26.10.835" --out "$OUT" 2>"$TMP/last.err"; then
  echo "  ✅ 生成成功"; pass=$((pass+1))
else
  echo "  ❌ 生成失败：$(cat "$TMP/last.err")"; fail=$((fail+1))
fi

check "前端三字段原样转录-version" "26.10.831" "$(jq -r '.version' "$OUT")"
check "前端三字段原样转录-gitSha" "abc1234" "$(jq -r '.gitSha' "$OUT")"
check "前端三字段原样转录-builtAt" "2026-10-13T10:00:00Z" "$(jq -r '.builtAt' "$OUT")"
check "含本次 API 版本" "26.10.835" "$(jq -r '.apiVersion' "$OUT")"
check "字段集合固定为 4 项" "4" "$(jq -r 'keys | length' "$OUT")"

# 镜像里没有版本文件（未内嵌前端）→ 必须硬失败，不许产出空壳台账
if bash "$SCRIPT" --api-image "ghcr.io/example/pim-api-server:v-empty" \
  --api-version "26.10.835" --out "$TMP/should-not-exist.json" >/dev/null 2>&1; then
  echo "  ❌ 缺版本文件应当硬失败"; fail=$((fail+1))
else
  echo "  ✅ 镜像内缺 web-version.json → 硬失败"; pass=$((pass+1))
fi

# 关键回归：api 镜像是"刚刚构建、还没推"的，只能读本地。
# 如果本地镜像库里没有它（例如同一 run 重跑、本地构建产物丢了），绝不能回退去拉远端同名镜像
# —— 那会把上一版推上去的旧镜像当成本次烘焙结果写进台账。
if FAKE_DOCKER_REMOTE_ONLY="$IMAGE" bash "$SCRIPT" --api-image "$IMAGE" \
  --api-version "26.10.835" --out "$TMP/remote-only.json" >/dev/null 2>&1; then
  echo "  ❌ 本地镜像不存在时应当硬失败，不许去拉远端"; fail=$((fail+1))
else
  echo "  ✅ 本地镜像不存在（远端有同名）→ 硬失败，绝不读远端"; pass=$((pass+1))
fi

# 参数缺失 → 用法错误（非 0）
if bash "$SCRIPT" --api-image "$IMAGE" --out "$TMP/x.json" >/dev/null 2>&1; then
  echo "  ❌ 缺参数应当失败"; fail=$((fail+1))
else
  echo "  ✅ 缺必填参数 → 非 0 退出"; pass=$((pass+1))
fi

echo ""
echo "build-web-ledger: passed=$pass failed=$fail"
[ "$fail" -eq 0 ] || exit 1
