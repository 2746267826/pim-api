#!/usr/bin/env bash
# Q04 production-gate smoke against an already-running API.
#
# Verifies the real transport middleware without touching production data:
#   * /health and /api/version stay usable over cleartext;
#   * /metrics stays usable over cleartext but still requires an OpsKey;
#   * a data path is rejected with 426/42600 over cleartext;
#   * when probing a loopback API, a trusted X-Forwarded-Proto: https unlocks
#     the same data path (proving the proxy trust boundary works).
#
# Usage:
#   PIM_OPS_KEY=... bash scripts/deploy/verify-transport-gate.sh http://127.0.0.1:5858
#
# This is a read-only probe. It does not log in, write data, or read pim_prod.
set -euo pipefail

base="${1:-${PIM_API_BASE_URL:-http://127.0.0.1:5858}}"
if [[ ! "$base" =~ ^http://([^/?#]+)$ ]]; then
  echo "FAIL：探测地址必须是 HTTP 源地址（不含路径、查询或片段）：$base" >&2
  exit 2
fi
authority="${BASH_REMATCH[1]}"
if [[ "$authority" == *"@"* ]]; then
  echo "FAIL：探测地址不能包含账号或密码：$base" >&2
  exit 2
fi
base="http://$authority"

ops_key="${PIM_OPS_KEY:-}"
if [[ -z "$ops_key" ]]; then
  echo "FAIL：需要 PIM_OPS_KEY（与 API 的 PIM_OPS_KEY 之一相同）来验证 /metrics 鉴权。" >&2
  exit 2
fi

tmp="$(mktemp -d)"
trap 'rm -rf "$tmp"' EXIT

request() {
  local path="$1" body="$2" headers="$3"
  shift 3
  curl --silent --show-error --max-time 15 \
    --dump-header "$headers" --output "$body" --write-out '%{http_code}' \
    "$@" "$base$path" || true
}

header() {
  awk -v name="$1" 'BEGIN { IGNORECASE = 1 } $1 == name ":" { gsub(/\r/, "", $2); print $2; exit }' "$2"
}

expect_code() {
  local expected="$1" actual="$2" label="$3" body="$4"
  if [[ "$actual" != "$expected" ]]; then
    echo "FAIL：$label 期望 HTTP $expected，实际 $actual。" >&2
    sed -n '1,20p' "$body" >&2 || true
    exit 1
  fi
}

health="$(request /health "$tmp/health.body" "$tmp/health.headers")"
expect_code 200 "$health" "/health" "$tmp/health.body"

version="$(request /api/version "$tmp/version.body" "$tmp/version.headers")"
expect_code 200 "$version" "/api/version" "$tmp/version.body"

metrics_without_key="$(request /metrics "$tmp/metrics-no-key.body" "$tmp/metrics-no-key.headers")"
expect_code 401 "$metrics_without_key" "/metrics 无 OpsKey" "$tmp/metrics-no-key.body"

metrics_with_key="$(request /metrics "$tmp/metrics.body" "$tmp/metrics.headers" \
  -H "X-PIM-Ops-Key: $ops_key")"
expect_code 200 "$metrics_with_key" "/metrics 带 OpsKey" "$tmp/metrics.body"

data_cleartext="$(request /api/v1/native-notes "$tmp/data.body" "$tmp/data.headers")"
expect_code 426 "$data_cleartext" "数据路径明文请求" "$tmp/data.body"
transport="$(header x-pim-transport "$tmp/data.headers")"
if [[ "$transport" != "https-required" ]]; then
  echo "FAIL：数据路径 426 响应缺少 X-Pim-Transport: https-required（实际：${transport:-missing}）。" >&2
  exit 1
fi
if ! grep -q '42600' "$tmp/data.body"; then
  echo "FAIL：数据路径 426 响应体缺少错误码 42600。" >&2
  exit 1
fi

if [[ "$authority" =~ ^(127\.0\.0\.1|localhost|\[::1\])(:[0-9]+)?$ || "${PIM_PROBE_TRUSTED_FORWARD:-false}" == "true" ]]; then
  data_forwarded="$(request /api/v1/native-notes "$tmp/data-forwarded.body" "$tmp/data-forwarded.headers" \
    -H 'X-Forwarded-Proto: https')"
  if [[ "$data_forwarded" == "426" ]]; then
    echo "FAIL：可信转发头未放行数据路径。检查 PIM_TRUSTED_PROXIES 是否包含探测来源，以及 PIM_ALLOW_LOOPBACK_CLEARTEXT 的值。" >&2
    exit 1
  fi
  expect_code 401 "$data_forwarded" "可信转发头下的数据路径（应进入鉴权）" "$tmp/data-forwarded.body"
else
  echo "SKIP：非回环探测地址，未验证可信转发头放行（可用 PIM_PROBE_TRUSTED_FORWARD=true 强制）。"
fi

echo "PASS：/health、/api/version、OpsKey /metrics、数据路径 426 与可信转发边界校验通过（$base）"
echo "边界：只读探测；不代表公网证书、1Panel/OpenResty 路由或真实生产数据。"
