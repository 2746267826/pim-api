#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 规格漂移检查器 · 行为测试（离线；前置：可用的 python 3.8+）
#
# 覆盖：
#   ① 规格与契约一致 → 退出 0；
#   ② 规格里写了契约中不存在的 METHOD+路径 → 退出 1，并指出该端点；
#   ③ 契约删掉某个端点（模拟后端下线）→ 规格里对应的引用立刻报红；
#   ④ 路径模板写法不同（{id:guid} / {deviceId} / {}）视为同一端点，不误报；
#   ⑤ 以 / 开头的具体前缀按前缀匹配（/api/v1/pc/ 能对上 /api/v1/pc/summary）；
#   ⑥ 契约里没有任何端点落在前缀下 → 报 missing-prefix；
#   ⑦ `<!-- drift-ignore: /path -->` 只豁免它点名的那条路径（同一行的其它端点仍检查）；
#   ⑧ 非法契约 → 退出 4（fail-closed）；规格或契约缺失 → 退出 4；规格里识别不到引用 → 退出 4；
#   ⑩ 路径存在但方法不存在 → missing-method（不是笼统的 missing-path）；
#   ⑪ 星号前缀 `/api/v1/pc/*` 按前缀判定；
#   ⑫ 句子里出现「服务端」等主语不再整行跳过（只认外部 URL 与明确指代）；
#   ⑬ 一份文档多个 drift-base，各自生效；
#   ⑭ 字面段不能用 {} 通配（`/items/upload` 不是 `/items/{id}`）；
#   ⑮ drift-ignore 不吞同行其它端点；
#   ⑯ 同一句里的外部 URL 不吞后面的真实声明；
#   ⑰ `GET, POST /path` 方法枚举至少检查被匹配到的方法；
#   ⑱ 带扩展名的路径（`.png`）照常检查；
#   ⑲ 空规格 / 只有标题 → 退出 4；
#   ⑳ 契约里的 404 兜底 `/api/{*path}` 不算真实端点；
#   ㉑ 契约里无约束的参数段能匹配文档的字面段（`{type}` 可取 `event`）；
#   ㉒ 文档写参数段、契约是字面段 → 不是同一端点（照抄会 404）；
#   ㉓ 路径级 ignore 不掩护契约里已存在路径上的方法删除（并报 stale-ignore）；
#   ㉔ 方法级 ignore 只豁免被点名的方法；
#   ㉕ 全部引用都被 ignore 的规格 → 退出 4（不能静默放行）；
#   ㉖ 具体裸路径不被更长的路径前缀掩护；
#   ㉗ 方法枚举 `GET, POST /path` 逐个方法检查；
#   ㉘ 真实兜底形状 `/api/{path}`（仅 404/500）同样不算端点；
#   ㉙ 转述「详见 `GET /x`」只在同一端点已正式声明时跳过；
#   ㉚/㉛ 行内代码里的 drift-ignore / drift-base 只是示例，不生效。
#
# 末尾断言用例总数（60），删测试会让门禁失败。
#
# 用法：bash scripts/ci/test-check-spec-drift.sh
# ---------------------------------------------------------------------------
set -uo pipefail
export PYTHONDONTWRITEBYTECODE=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
CHECK="$HERE/check-spec-drift.py"

PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 \
    && "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    PY="$candidate"; break
  fi
done
[ -n "$PY" ] || { echo "需要一个可用的 python 3.8+" >&2; exit 2; }

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-spec-drift.XXXXXX")"
cleanup() { [ "${PIM_SPEC_DRIFT_KEEP:-0}" = "1" ] || rm -rf "$TMP"; }
trap cleanup EXIT

PASS=0; FAIL=0
pass() { PASS=$((PASS + 1)); echo "  ok   $1"; }
fail() { FAIL=$((FAIL + 1)); echo "  FAIL $1"; }
expect_rc() { [ "$3" = "$2" ] && pass "$1" || fail "$1（期望 $2，实际 $3）"; }
has() { grep -qF -- "$2" "$3" && pass "$1" || { fail "$1（找不到：$2）"; sed 's/^/       | /' "$3" | head -20; }; }
hasnt() { grep -qF -- "$2" "$3" && { fail "$1（不该出现：$2）"; sed 's/^/       | /' "$3" | head -20; } || pass "$1"; }

cat >"$TMP/contract.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "T", "version": "1" },
  "paths": {
    "/api/v1/pc/summary": { "get": { "responses": { "200": { "description": "OK" } } } },
    "/api/v1/mobile/devices/{deviceId}/export": { "get": { "responses": { "200": { "description": "OK" } } } },
    "/api/v1/reminders": { "get": { "responses": { "200": { "description": "OK" } } } }
  }
}
JSON

cat >"$TMP/spec-ok.md" <<'MD'
# 规格
- PC 汇总：`GET /api/v1/pc/summary`
- 设备导出：`GET /api/v1/mobile/devices/{deviceId:guid}/export`
- 前缀：`/api/v1/pc/`
- 本机协议：`GET /browser/ping` <!-- drift-ignore: /browser/ping -->
MD

cat >"$TMP/spec-bad.md" <<'MD'
# 规格
- 不存在的端点：`POST /api/v1/stats/upload`
- 不存在的前缀：`/api/v1/nothing-here/`
MD

cat >"$TMP/spec-ignore.md" <<'MD'
# 规格
- 正常端点：`GET /api/v1/pc/summary`
- `POST /api/v1/stats/upload` <!-- drift-ignore: /api/v1/stats/upload -->
MD

echo "== 规格漂移检查器 · 行为测试 =="

"$PY" "$CHECK" --spec "$TMP/spec-ok.md" --contract "$TMP/contract.json" >"$TMP/ok.txt" 2>&1
expect_rc "① 一致时退出 0" 0 $?
has "① 一致时明确说明全部存在" "规格引用的端点全部存在于契约中" "$TMP/ok.txt"
hasnt "④ 模板参数名不同（{deviceId:guid} vs {deviceId}）不误报" "devices" "$TMP/ok.txt"
hasnt "⑤ 前缀匹配（/api/v1/pc/ → /api/v1/pc/summary）不误报" "missing-prefix" "$TMP/ok.txt"
hasnt "⑦ drift-ignore 的本机协议路径不报" "/browser/ping" "$TMP/ok.txt"

"$PY" "$CHECK" --spec "$TMP/spec-bad.md" --contract "$TMP/contract.json" >"$TMP/bad.txt" 2>&1
expect_rc "② 不存在的端点退出 1" 1 $?
has "② 指出缺失的 METHOD+路径" "POST /api/v1/stats/upload" "$TMP/bad.txt"
has "⑥ 没有任何端点落下的前缀报 missing-prefix" "[missing-prefix] /api/v1/nothing-here —" "$TMP/bad.txt"

"$PY" "$CHECK" --spec "$TMP/spec-ignore.md" --contract "$TMP/contract.json" >/dev/null 2>&1
expect_rc "⑦ 被声明不属于本 API 的端点放行" 0 $?

# ③ 模拟后端下线：从契约删掉 reminders 后，引用它的规格立刻报红
"$PY" - "$TMP/contract.json" "$TMP/contract2.json" <<'PY'
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
doc["paths"].pop("/api/v1/reminders")
json.dump(doc, open(sys.argv[2], "w", encoding="utf-8"))
PY
printf '# 规格\n- 提醒：`GET /api/v1/reminders`\n' >"$TMP/spec-rem.md"
"$PY" "$CHECK" --spec "$TMP/spec-rem.md" --contract "$TMP/contract.json" >/dev/null 2>&1
expect_rc "③ 端点存在时放行" 0 $?
"$PY" "$CHECK" --spec "$TMP/spec-rem.md" --contract "$TMP/contract2.json" >"$TMP/rem.txt" 2>&1
expect_rc "③ 契约删除端点后立刻报红" 1 $?
has "③ 报出被删的端点" "GET /api/v1/reminders" "$TMP/rem.txt"

# ⑨ review M-4：具名模板参数写法不同视为同一端点；基地址声明与行序无关
cat >"$TMP/contract-tpl.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": { "/api/v1/pc/summary/{id}": { "get": { "responses": { "200": { "description": "OK" } } } } } }
JSON
printf '# 规格
- 相对路径在基地址声明之前：`GET /pc/summary/{taskId}`
<!-- drift-base: /api/v1 -->
' >"$TMP/spec-order.md"
"$PY" "$CHECK" --spec "$TMP/spec-order.md" --contract "$TMP/contract-tpl.json" >"$TMP/tpl.txt" 2>&1
expect_rc "⑨ 具名模板参数不同 + 基地址在行后 → 放行" 0 $?

# ⑩ 路径在、方法不在 → missing-method（照抄会 405，不能笼统报成「路径不存在」）
printf '# 规格\n- 错误方法：`POST /api/v1/pc/summary`\n' >"$TMP/spec-method.md"
"$PY" "$CHECK" --spec "$TMP/spec-method.md" --contract "$TMP/contract.json" >"$TMP/method.txt" 2>&1
expect_rc "⑩ 方法写错退出 1" 1 $?
has "⑩ 报 missing-method 并给出契约里的方法" "[missing-method] POST /api/v1/pc/summary — 路径存在，但契约里只有 GET" "$TMP/method.txt"
hasnt "⑩ 不再重复报同一条 missing-path" "[missing-path] POST /api/v1/pc/summary" "$TMP/method.txt"

# ⑪ 星号前缀（`/api/v1/pc/*`）按前缀判定，不误报
printf '# 规格\n- 前缀：`/api/v1/pc/*`\n' >"$TMP/spec-star.md"
"$PY" "$CHECK" --spec "$TMP/spec-star.md" --contract "$TMP/contract.json" >/dev/null 2>&1
expect_rc "⑪ 星号前缀能对上真实端点" 0 $?

# ⑫ 句子里提到「服务端」不应吞掉真声明（review：旧启发式会整行跳过）
printf '# 规格\n- 服务端提供 `GET /api/v1/pc/does-not-exist` 用于演示\n' >"$TMP/spec-server.md"
"$PY" "$CHECK" --spec "$TMP/spec-server.md" --contract "$TMP/contract.json" >"$TMP/server.txt" 2>&1
expect_rc "⑫ 含「服务端」的声明仍被检查" 1 $?
has "⑫ 报出该缺失端点" "GET /api/v1/pc/does-not-exist" "$TMP/server.txt"

# ⑬ 一份文档多个 drift-base：各自生效，互不串台
cat >"$TMP/contract-multi.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": {
    "/api/v1/calendar/tasks": { "get": { "responses": { "200": { "description": "OK" } } } },
    "/api/v1/files/items": { "get": { "responses": { "200": { "description": "OK" } } } } } }
JSON
cat >"$TMP/spec-multi.md" <<'MD'
# 规格
<!-- drift-base: /api/v1/calendar -->
- 任务：`GET /tasks`
<!-- drift-base: /api/v1/files -->
- 文件：`GET /items`
MD
"$PY" "$CHECK" --spec "$TMP/spec-multi.md" --contract "$TMP/contract-multi.json" >"$TMP/multi.txt" 2>&1
expect_rc "⑬ 多域 drift-base 各自生效" 0 $?

# ⑭ 字面段不能靠 {} 通配：运行时 `{id:guid}` 匹配不了 `upload`，照抄会 404/405
cat >"$TMP/contract-literal.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": { "/api/v1/files/items/{id}": { "delete": { "responses": { "200": { "description": "OK" } } } } } }
JSON
printf '<!-- drift-base: /api/v1 -->\n# 规格\n- 上传：`DELETE /files/items/upload`\n' >"$TMP/spec-literal.md"
"$PY" "$CHECK" --spec "$TMP/spec-literal.md" --contract "$TMP/contract-literal.json" >"$TMP/literal.txt" 2>&1
expect_rc "⑭ 字面段 vs 参数段必须报红" 1 $?
has "⑭ 报 missing-path（不是靠 {} 通配放行）" "[missing-path] DELETE /api/v1/files/items/upload" "$TMP/literal.txt"

# ⑮ drift-ignore 只豁免点名的那条路径：同一行的另一个端点仍要检查
printf '# 规格\n- `POST /api/v1/stats/upload` <!-- drift-ignore: /api/v1/stats/upload --> 另见 `DELETE /api/v1/stats/other`\n' \
  >"$TMP/spec-ignore-line.md"
"$PY" "$CHECK" --spec "$TMP/spec-ignore-line.md" --contract "$TMP/contract.json" >"$TMP/ignore-line.txt" 2>&1
expect_rc "⑮ 同行 ignore 不吞其它端点" 1 $?
has "⑮ 报出未豁免的那个端点" "DELETE /api/v1/stats/other" "$TMP/ignore-line.txt"
hasnt "⑮ 被豁免的端点仍不报" "POST /api/v1/stats/upload" "$TMP/ignore-line.txt"

# ⑯ 同一句里先有外部 URL、随后声明端点：URL 不应吞掉后面的真声明
printf '# 规格\n- 见 https://example.com 的 `GET /api/v1/pc/also-missing` 端点\n' >"$TMP/spec-url.md"
"$PY" "$CHECK" --spec "$TMP/spec-url.md" --contract "$TMP/contract.json" >"$TMP/url.txt" 2>&1
expect_rc "⑯ 外部 URL 不吞后置声明" 1 $?
has "⑯ 报出该端点" "GET /api/v1/pc/also-missing" "$TMP/url.txt"

# ⑰ 方法枚举 `GET, POST /path`：至少检查被匹配到的方法（旧实现整条跳过）
printf '# 规格\n- 枚举：`GET, POST /api/v1/pc/missing`\n' >"$TMP/spec-enum.md"
"$PY" "$CHECK" --spec "$TMP/spec-enum.md" --contract "$TMP/contract.json" >"$TMP/enum.txt" 2>&1
expect_rc "⑰ 方法枚举仍被检查" 1 $?
has "⑰ 报出该端点" "/api/v1/pc/missing" "$TMP/enum.txt"

# ⑱ 带扩展名的路径是真实端点（契约里确有 /tiles/{z}/{x}/{y}.png），不能按「文件」跳过
printf '# 规格\n- 瓦片：`GET /api/v1/tiles/1/2/3.png`\n' >"$TMP/spec-png.md"
"$PY" "$CHECK" --spec "$TMP/spec-png.md" --contract "$TMP/contract.json" >"$TMP/png.txt" 2>&1
expect_rc "⑱ 带扩展名的路径照常检查" 1 $?
has "⑱ 报出该缺失端点" "GET /api/v1/tiles/1/2/3.png" "$TMP/png.txt"

# ⑲ 空规格 / 只有标题：门禁不能「0 findings」放行
: >"$TMP/spec-empty.md"
"$PY" "$CHECK" --spec "$TMP/spec-empty.md" --contract "$TMP/contract.json" >/dev/null 2>&1
expect_rc "⑲ 空规格退出 4" 4 $?
printf '# 规格\n' >"$TMP/spec-title.md"
"$PY" "$CHECK" --spec "$TMP/spec-title.md" --contract "$TMP/contract.json" >/dev/null 2>&1
expect_rc "⑲ 只有标题的规格退出 4" 4 $?

# ⑳ 契约的 404 兜底 `/api/{*path}` 不算真实端点（否则任意单段伪路径都放行）
cat >"$TMP/contract-catchall.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": { "/api/{*path}": { "get": { "responses": { "200": { "description": "OK" } } } } } }
JSON
printf '# 规格\n- 伪端点：`GET /api/does-not-exist`\n' >"$TMP/spec-catchall.md"
"$PY" "$CHECK" --spec "$TMP/spec-catchall.md" --contract "$TMP/contract-catchall.json" >"$TMP/catchall.txt" 2>&1
expect_rc "⑳ 兜底路由不匹配任意路径" 1 $?
has "⑳ 报出伪端点" "GET /api/does-not-exist" "$TMP/catchall.txt"

# ㉑ 无约束参数段可以取字面值：/calendar/recycle-bin/{type}/{id}/restore 能接住 event / task
cat >"$TMP/contract-freeparam.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": {
    "/api/v1/calendar/recycle-bin/{type}/{id}/restore": {
      "post": {
        "parameters": [
          { "name": "type", "in": "path", "schema": { "type": "string" } },
          { "name": "id", "in": "path", "schema": { "type": "string", "format": "uuid" } }
        ],
        "responses": { "200": { "description": "OK" } }
      } } } }
JSON
printf '<!-- drift-base: /api/v1 -->\n# 规格\n- 恢复事件：`POST /calendar/recycle-bin/event/{id}/restore`\n' >"$TMP/spec-freeparam.md"
"$PY" "$CHECK" --spec "$TMP/spec-freeparam.md" --contract "$TMP/contract-freeparam.json" >"$TMP/freeparam.txt" 2>&1
expect_rc "㉑ 无约束参数段可匹配字面段" 0 $?

# ㉒ 反过来不行：文档写 `{id}`、契约是固定字面段，运行时就是 404
printf '<!-- drift-base: /api/v1 -->\n# 规格\n- 伪参数：`GET /pc/summary/{id}`\n' >"$TMP/spec-revparam.md"
"$PY" "$CHECK" --spec "$TMP/spec-revparam.md" --contract "$TMP/contract.json" >"$TMP/revparam.txt" 2>&1
expect_rc "㉒ 文档参数段不能匹配契约字面段" 1 $?
has "㉒ 报 missing-path" "[missing-path] GET /api/v1/pc/summary/{}" "$TMP/revparam.txt"

# ㉓ 路径级 ignore 不能掩护契约里已存在路径上的方法删除（review P1-1）
cat >"$TMP/contract-ignore-method.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": { "/api/v1/pc/summary": {
    "get": { "responses": { "200": { "description": "OK" } } },
    "post": { "responses": { "200": { "description": "OK" } } } } } }
JSON
printf '# 规格\n- 删除：`DELETE /api/v1/pc/summary` <!-- drift-ignore: /api/v1/pc/summary -->\n' \
  >"$TMP/spec-ignore-method.md"
"$PY" "$CHECK" --spec "$TMP/spec-ignore-method.md" --contract "$TMP/contract-ignore-method.json" \
  >"$TMP/ignore-method.txt" 2>&1
expect_rc "㉓ 路径级 ignore 不掩护已存在路径的方法删除" 1 $?
has "㉓ 仍报 missing-method" "[missing-method] DELETE /api/v1/pc/summary" "$TMP/ignore-method.txt"
has "㉓ 并提示该 ignore 已过期" "[stale-ignore] /api/v1/pc/summary" "$TMP/ignore-method.txt"

# ㉔ 方法级 ignore 只豁免被点名的方法
cat >"$TMP/contract-ignore-one.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": { "/api/v1/pc/summary": { "get": { "responses": { "200": { "description": "OK" } } } } } }
JSON
printf '# 规格\n- 正常：`GET /api/v1/pc/summary`\n- 不适用：`POST /api/v1/pc/summary` <!-- drift-ignore: POST /api/v1/pc/summary -->\n' \
  >"$TMP/spec-ignore-one.md"
"$PY" "$CHECK" --spec "$TMP/spec-ignore-one.md" --contract "$TMP/contract-ignore-one.json" \
  >"$TMP/ignore-one.txt" 2>&1
expect_rc "㉔ 方法级 ignore 只豁免该方法" 0 $?
hasnt "㉔ 不再报被豁免的方法" "POST /api/v1/pc/summary" "$TMP/ignore-one.txt"

# ㉕ 全部引用都被 ignore → fail-closed（旧实现会 0 findings 放行，review P1-2）
printf '# 规格\n- 本机协议：`GET /browser/ping` <!-- drift-ignore: /browser/ping -->\n' \
  >"$TMP/spec-all-ignored.md"
"$PY" "$CHECK" --spec "$TMP/spec-all-ignored.md" --contract "$TMP/contract.json" >/dev/null 2>&1
expect_rc "㉕ 全部被豁免的规格退出 4" 4 $?

# ㉖ 具体裸路径不能被更长的路径前缀掩护（review P2-1）
cat >"$TMP/contract-prefix-cover.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": { "/api/v1/pc/summary/detail": { "get": { "responses": { "200": { "description": "OK" } } } } } }
JSON
printf '# 规格\n- 集合端点：`/api/v1/pc/summary`\n' >"$TMP/spec-prefix-cover.md"
"$PY" "$CHECK" --spec "$TMP/spec-prefix-cover.md" --contract "$TMP/contract-prefix-cover.json" \
  >"$TMP/prefix-cover.txt" 2>&1
expect_rc "㉖ 具体裸路径删除后报红" 1 $?
has "㉖ 报 missing-prefix" "[missing-prefix] /api/v1/pc/summary" "$TMP/prefix-cover.txt"

# ㉗ 方法枚举逐个检查：契约只有 GET 时 `GET, POST` 必须报 POST（review P2-2）
printf '# 规格\n- 枚举：`GET, POST /api/v1/pc/summary`\n' >"$TMP/spec-enum2.md"
"$PY" "$CHECK" --spec "$TMP/spec-enum2.md" --contract "$TMP/contract.json" >"$TMP/enum2.txt" 2>&1
expect_rc "㉗ 枚举里缺失的方法报红" 1 $?
has "㉗ 报出 POST 缺失" "[missing-method] POST /api/v1/pc/summary" "$TMP/enum2.txt"
hasnt "㉗ 已有的 GET 不误报" "[missing-method] GET /api/v1/pc/summary" "$TMP/enum2.txt"

# ㉘ 真实契约的兜底形状 `/api/{path}`（无 *、仅 404/500）同样不算端点（review P2-4）
cat >"$TMP/contract-catchall-real.json" <<'JSON'
{ "openapi": "3.0.1", "info": { "title": "T", "version": "1" },
  "paths": { "/api/{path}": {
    "get": { "responses": { "404": { "description": "Not Found" }, "500": { "description": "Error" } } },
    "post": { "responses": { "404": { "description": "Not Found" }, "500": { "description": "Error" } } } } } }
JSON
printf '# 规格\n- 伪端点：`GET /api/does-not-exist`\n' >"$TMP/spec-catchall-real.md"
"$PY" "$CHECK" --spec "$TMP/spec-catchall-real.md" --contract "$TMP/contract-catchall-real.json" \
  >"$TMP/catchall-real.txt" 2>&1
expect_rc "㉘ 真实兜底形状不匹配任意路径" 1 $?
has "㉘ 报出伪端点" "GET /api/does-not-exist" "$TMP/catchall-real.txt"

# ㉙ 转述（「详见 `GET /x`」）只在同一端点已正式声明时跳过（review P3-4）
printf '# 规格\n- 详见 `GET /api/v1/pc/nowhere`\n' >"$TMP/spec-quoted-only.md"
"$PY" "$CHECK" --spec "$TMP/spec-quoted-only.md" --contract "$TMP/contract.json" >"$TMP/quoted-only.txt" 2>&1
expect_rc "㉙ 只有转述的端点仍被检查" 1 $?
has "㉙ 报出转述中的缺失端点" "GET /api/v1/pc/nowhere" "$TMP/quoted-only.txt"

printf '# 规格\n- 正式：`GET /api/v1/pc/summary`\n- 详见 `GET /api/v1/pc/summary`\n' \
  >"$TMP/spec-quoted-dup.md"
"$PY" "$CHECK" --spec "$TMP/spec-quoted-dup.md" --contract "$TMP/contract.json" >/dev/null 2>&1
expect_rc "㉙ 已正式声明的端点允许转述" 0 $?

# ㉚ 行内代码里的指令只是示例：反引号内的 drift-ignore 不能豁免真实引用（review P2-3）
printf '# 规格\n- 写法示例：`<!-- drift-ignore: /api/v1/pc/missing -->`\n- 真实引用：`GET /api/v1/pc/missing`\n' \
  >"$TMP/spec-quoted-ignore.md"
"$PY" "$CHECK" --spec "$TMP/spec-quoted-ignore.md" --contract "$TMP/contract.json" >"$TMP/quoted-ignore.txt" 2>&1
expect_rc "㉚ 反引号里的 drift-ignore 不生效" 1 $?
has "㉚ 真实引用照常报红" "GET /api/v1/pc/missing" "$TMP/quoted-ignore.txt"

# ㉛ 反引号里的 drift-base 只是示例，不能给全文套基地址（否则一句示例制造大量假引用）
printf '# 规格\n- 示例：`<!-- drift-base: /api/v1 -->`\n- 相对引用：`GET /pc/summary/{id}`\n' \
  >"$TMP/spec-quoted-base.md"
"$PY" "$CHECK" --spec "$TMP/spec-quoted-base.md" --contract "$TMP/contract-tpl.json" >/dev/null 2>&1
expect_rc "㉛ 反引号里的 drift-base 不生效" 1 $?

# ⑧ fail-closed
echo '{ not json' >"$TMP/broken.json"
"$PY" "$CHECK" --spec "$TMP/spec-ok.md" --contract "$TMP/broken.json" >/dev/null 2>&1
expect_rc "⑧ 非法契约退出 4" 4 $?
"$PY" "$CHECK" --spec "$TMP/missing.md" --contract "$TMP/contract.json" >/dev/null 2>&1
expect_rc "⑧ 规格缺失退出 4" 4 $?
"$PY" "$CHECK" --spec "$TMP/spec-ok.md" --contract "$TMP/missing.json" >/dev/null 2>&1
expect_rc "⑧ 契约缺失退出 4" 4 $?

echo ""
if [ "$FAIL" -ne 0 ]; then echo "FAILED：$FAIL 项失败，$PASS 项通过"; exit 1; fi
# 断言用例数量：删掉任意一条测试（例如 fail-closed 或 missing-method）时，
# PASS 会变小但退出码仍是 0，门禁覆盖会静默缩小（review P2-5）。
EXPECTED_PASS=60
if [ "$PASS" -ne "$EXPECTED_PASS" ]; then
  echo "FAILED：用例数量不符（期望 $EXPECTED_PASS，实际 $PASS）" >&2
  exit 1
fi
echo "PASSED：$PASS 项通过"
