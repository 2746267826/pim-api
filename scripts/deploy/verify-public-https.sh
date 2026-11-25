#!/usr/bin/env bash
# Q04 deployment probe: verify the public HTTPS entry independently from the
# container-internal /health check. It validates the certificate chain/hostname/
# validity through curl's default TLS verification, the effective request scheme
# reported by the API (X-Forwarded-Proto trust), /api/version, and that plain HTTP
# does not serve PIM content directly.
set -euo pipefail

base="${1:-${PIM_PUBLIC_URL:-}}"
if [[ -z "$base" ]]; then
  echo "用法：PIM_PUBLIC_URL=https://pim.example.com $0 [https://pim.example.com]" >&2
  exit 2
fi
if [[ ! "$base" =~ ^https://([^/?#]+)$ ]]; then
  echo "公网探测地址必须是 HTTPS 源地址（不含路径、查询或片段）：$base" >&2
  exit 2
fi

authority="${BASH_REMATCH[1]}"
if [[ "$authority" == *"@"* ]]; then
  echo "公网探测地址不能包含账号或密码：$base" >&2
  exit 2
fi
base="https://$authority"

tmpdir="$(mktemp -d)"
trap 'rm -rf "$tmpdir"' EXIT
headers="$tmpdir/headers"
body="$tmpdir/body"

# --proto '=https' + default CA verification: certificate chain, hostname and
# validity period all have to pass before the API is reached.
if ! curl --proto '=https' --tlsv1.2 --fail-with-body --silent --show-error \
  --max-time 15 --dump-header "$headers" --output "$body" \
  "$base/api/version"; then
  echo "FAIL：公网 HTTPS 证书或 /api/version 请求失败（$base）" >&2
  exit 1
fi

scheme="$(awk 'BEGIN { IGNORECASE = 1 } /^x-pim-request-scheme:/ { gsub(/[[:space:]\r]/, "", $2); print tolower($2) }' "$headers" | tail -n 1)"
if [[ "$scheme" != "https" ]]; then
  echo "FAIL：API 未看到 HTTPS（X-Pim-Request-Scheme=${scheme:-missing}）。" >&2
  echo "检查反代是否设置 X-Forwarded-Proto: https，以及 PIM_TRUSTED_PROXIES 是否包含反代实际地址。" >&2
  exit 1
fi

transport="$(awk 'BEGIN { IGNORECASE = 1 } /^x-pim-transport:/ { gsub(/[[:space:]\r]/, "", $2); print tolower($2) }' "$headers" | tail -n 1)"
if [[ "$transport" == "cleartext" ]]; then
  echo "FAIL：API 在公网入口仍按明文 HTTP 处理（X-Pim-Transport=cleartext）。" >&2
  echo "检查反代是否覆盖客户端伪造的 X-Forwarded-Proto，并正确转发 https。" >&2
  exit 1
fi

if ! grep -q '"version"' "$body"; then
  echo "FAIL：/api/version 响应缺少 version 字段（$base）" >&2
  exit 1
fi

# The plain HTTP entry must be closed or redirect to HTTPS; a 200 on either
# endpoint means the API/reverse proxy is also serving an unencrypted public entry.
http_base="${PIM_PUBLIC_HTTP_URL:-}"
if [[ -n "$http_base" ]]; then
  if [[ ! "$http_base" =~ ^http://([^/?#]+)$ || "$http_base" == *"@"* ]]; then
    echo "PIM_PUBLIC_HTTP_URL 必须是 HTTP 源地址（不含路径、查询、片段或账号）：$http_base" >&2
    exit 2
  fi
  http_base="${http_base%/}"
else
  # 仅探测默认 HTTP 端口；HTTPS 使用自定义端口时请显式设置 PIM_PUBLIC_HTTP_URL。
  if [[ "$authority" == \[* ]]; then
    http_host="${authority%%]*}]"
  else
    http_host="${authority%%:*}"
  fi
  http_base="http://$http_host"
fi

for path in /health /api/version; do
  http_url="$http_base$path"
  http_code="$(curl --silent --show-error --max-time 10 --output /dev/null \
    --write-out '%{http_code}' "$http_url" 2>/dev/null || true)"
  if [[ "$http_code" == 2* ]]; then
    echo "FAIL：公网 HTTP 入口仍可直接返回 $http_code（$http_url）" >&2
    exit 1
  fi
  if [[ "$http_code" == 3* ]]; then
    location="$(curl --silent --show-error --max-time 10 --dump-header - --output /dev/null \
      "$http_url" 2>/dev/null | awk 'BEGIN { IGNORECASE = 1 } /^location:/ { print $2 }' | tr -d '\r' | tail -n 1)"
    if [[ "$location" != https://* ]]; then
      echo "FAIL：公网 HTTP 入口重定向目标不是 HTTPS（$http_url -> ${location:-missing}）" >&2
      exit 1
    fi
  fi
done

echo "PASS：公网 HTTPS 证书、可信 X-Forwarded-Proto、/api/version 与 HTTP 关闭/跳转校验通过（$base）"
