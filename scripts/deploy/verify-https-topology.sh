#!/usr/bin/env bash
# Q04 isolated deployment probe: start the repository nginx.conf with a generated
# self-signed certificate and a local mock upstream, then verify real TLS, the
# 80->443 redirect and X-Forwarded-* headers for both proxy locations.
#
# This proves the repository nginx topology only. It does NOT prove a real public
# certificate, production OpenResty/1Panel routing, or a deployed PIM instance.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

for command in docker curl openssl python3; do
  if ! command -v "$command" >/dev/null 2>&1; then
    echo "SKIP：缺少依赖 $command" >&2
    exit 2
  fi
done
if ! docker info >/dev/null 2>&1; then
  echo "SKIP：Docker daemon 不可用" >&2
  exit 2
fi

pick_port() {
  python3 - <<'PY'
import socket
s = socket.socket()
s.bind(("127.0.0.1", 0))
print(s.getsockname()[1])
s.close()
PY
}

http_port="${PIM_Q04_HTTP_PORT:-$(pick_port)}"
https_port="${PIM_Q04_HTTPS_PORT:-$(pick_port)}"
upstream_port="${PIM_Q04_UPSTREAM_PORT:-$(pick_port)}"
if [[ "$http_port" == "$https_port" || "$http_port" == "$upstream_port" || "$https_port" == "$upstream_port" ]]; then
  echo "FAIL：测试端口重复" >&2
  exit 1
fi

work="$(mktemp -d)"
container="pim-q04-nginx-$$"
mock_pid=""
cleanup() {
  if [[ -n "$mock_pid" ]]; then
    kill "$mock_pid" >/dev/null 2>&1 || true
    wait "$mock_pid" >/dev/null 2>&1 || true
  fi
  docker rm -f "$container" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

mkdir -p "$work/certs"
openssl req -x509 -newkey rsa:2048 -sha256 -nodes -days 2 \
  -keyout "$work/certs/privkey.pem" \
  -out "$work/certs/fullchain.pem" \
  -subj "/CN=localhost" \
  -addext "subjectAltName=DNS:localhost,IP:127.0.0.1" \
  -addext "basicConstraints=critical,CA:TRUE" >/dev/null 2>&1

cat > "$work/mock_upstream.py" <<'PY'
import json
import sys
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

port = int(sys.argv[1])

class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        payload = {
            "version": "q04-topology-test",
            "path": self.path,
            "xForwardedProto": self.headers.get("X-Forwarded-Proto"),
            "xForwardedHost": self.headers.get("X-Forwarded-Host"),
            "xForwardedPort": self.headers.get("X-Forwarded-Port"),
        }
        body = json.dumps(payload).encode()
        self.send_response(200)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def log_message(self, *_):
        pass

ThreadingHTTPServer(("127.0.0.1", port), Handler).serve_forever()
PY
python3 "$work/mock_upstream.py" "$upstream_port" &
mock_pid=$!

for _ in $(seq 1 50); do
  if curl --silent --show-error --max-time 1 "http://127.0.0.1:$upstream_port/health" >/dev/null 2>&1; then
    break
  fi
  sleep 0.1
done
if ! kill -0 "$mock_pid" >/dev/null 2>&1; then
  echo "FAIL：本地 mock upstream 未能启动" >&2
  exit 1
fi

sed \
  -e "s|server pim:5000;|server 127.0.0.1:${upstream_port};|" \
  -e "s|listen 80;|listen ${http_port};|" \
  -e "s|listen 443 ssl;|listen ${https_port} ssl;|" \
  "$root/nginx.conf" > "$work/nginx.conf"

if ! grep -q "server 127.0.0.1:${upstream_port};" "$work/nginx.conf"; then
  echo "FAIL：未能把 nginx upstream 指向本地 mock" >&2
  exit 1
fi

docker run --rm -d --name "$container" --network host \
  -v "$work/nginx.conf:/etc/nginx/nginx.conf:ro" \
  -v "$work/certs:/etc/nginx/ssl:ro" \
  nginx:alpine >/dev/null

ready=0
for _ in $(seq 1 50); do
  if curl --silent --show-error --max-time 1 --cacert "$work/certs/fullchain.pem" \
    "https://localhost:$https_port/api/version" >/dev/null 2>&1; then
    ready=1
    break
  fi
  sleep 0.2
done
if [[ "$ready" != "1" ]]; then
  echo "FAIL：nginx HTTPS 未就绪" >&2
  docker logs "$container" >&2 || true
  exit 1
fi

http_headers="$(curl --silent --show-error --max-time 5 --dump-header - --output /dev/null \
  "http://127.0.0.1:$http_port/api/version")"
if ! grep -Eq '^HTTP/[0-9.]+ 301' <<<"$http_headers"; then
  echo "FAIL：HTTP 入口未返回 301" >&2
  exit 1
fi
if ! grep -Eiq '^location: https://' <<<"$http_headers"; then
  echo "FAIL：HTTP 301 未指向 HTTPS" >&2
  exit 1
fi

https_json="$(curl --silent --show-error --max-time 5 \
  --cacert "$work/certs/fullchain.pem" \
  "https://localhost:$https_port/api/version")"
printf '%s' "$https_json" | python3 -c '
import json, sys
payload = json.load(sys.stdin)
port = sys.argv[1]
assert payload["version"] == "q04-topology-test", payload
assert payload["path"] == "/api/version", payload
assert payload["xForwardedProto"] == "https", payload
assert payload["xForwardedHost"], payload
assert payload["xForwardedPort"] == port, payload
' "$https_port"

if curl --silent --show-error --max-time 5 --tls-max 1.1 \
  --cacert "$work/certs/fullchain.pem" \
  "https://localhost:$https_port/api/version" >/dev/null 2>&1; then
  echo "FAIL：TLS 1.1 仍可连接" >&2
  exit 1
fi

echo "PASS：nginx HTTP→HTTPS、TLS1.2+、X-Forwarded-Proto/Host/Port 隔离拓扑验证通过"
echo "边界：使用自签名证书与本地 mock upstream；不代表公网证书、生产 OpenResty/1Panel 或真实 API 部署。"
