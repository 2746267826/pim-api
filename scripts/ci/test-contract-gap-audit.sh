#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 契约缺口审计器 · 行为测试（离线；唯一外部前置是 python3 标准库）
#
# 用内联 fixture 构造「已知有缺口」与「已知干净」两种契约，断言审计器：
#   ① 干净契约 → findings 为空、退出 0；
#   ② 缺 operationId/summary → 每个 operation 一条；
#   ③ 缺 schema/属性描述 → 逐 schema / 逐属性计数；
#   ④ query 参数缺 default/enum/description → 逐参数；
#   ⑤ 枚举型字段（status/kind/priority…）没有 enum → 逐属性；
#   ⑥ POST/PUT/PATCH 缺 requestBody → 逐 operation；
#   ⑦ multipart 符号（[FromForm]/IFormFile/BinaryFile）未在契约里声明 → 逐字段；
#   ⑧ 5xx 响应未声明 → 逐 operation；
#   ⑨ 业务错误码出现在契约文本里但未登记到业务错误码表 → 逐码；
#   ⑩ PascalCase query 参数 → 逐参数；
#   ⑪ 第二份契约与基线做 operation 差异（缺失 / 多出）→ 逐条；
#   ⑫ 输出是确定性的（同输入两次运行逐字节相同），且 --json 可以被 json 解析；
#   ⑬ 门禁模式：--fail-on 命中时退出 1，未命中退出 0（不是「永远 PASS」的摆设）；
#   ⑭ 读取失败（文件不存在 / 非法 JSON）→ 退出 4，绝不当作「没有缺口」。
#
# 用法：bash scripts/ci/test-contract-gap-audit.sh
#   PIM_GAP_TEST_KEEP=1  保留临时工作目录（默认删除）
# ---------------------------------------------------------------------------
set -uo pipefail
export PYTHONDONTWRITEBYTECODE=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
AUDIT="$ROOT/scripts/ci/audit-contract-gaps.py"
CONTRACT="$ROOT/contract/openapi.json"

# 选一个「真能跑」的 python3：Windows 上常有应用商店占位程序（任何调用都退出 49），
# 不能只凭 `command -v python3` 判定可用，否则测试会全部误红。
PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 \
    && "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    PY="$candidate"
    break
  fi
done
if [ -z "$PY" ]; then
  echo "需要一个可用的 python 3.8+（审计器只用标准库）。" >&2
  exit 2
fi

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-gap-audit-test.XXXXXX")"
cleanup() {
  if [ "${PIM_GAP_TEST_KEEP:-0}" = "1" ]; then
    echo "保留临时目录：$TMP"
  else
    rm -rf "$TMP"
  fi
}
trap cleanup EXIT

PASS=0
FAIL=0
pass() { PASS=$((PASS + 1)); echo "  ok   $1"; }
fail() { FAIL=$((FAIL + 1)); echo "  FAIL $1"; }

# 断言：$1 描述；stdout 里必须含 $2（固定串）
expect_contains() {
  local desc="$1" needle="$2" out="$3"
  if grep -qF -- "$needle" <<<"$out"; then pass "$desc"; else
    fail "$desc（输出里找不到：$needle）"
    echo "$out" | sed 's/^/       | /' | head -40
  fi
}
expect_not_contains() {
  local desc="$1" needle="$2" out="$3"
  if grep -qF -- "$needle" <<<"$out"; then
    fail "$desc（不该出现：$needle）"
    echo "$out" | sed 's/^/       | /' | head -40
  else pass "$desc"; fi
}
expect_exit() {
  local desc="$1" want="$2" got="$3"
  if [ "$want" = "$got" ]; then pass "$desc"; else fail "$desc（期望退出 $want，实际 $got）"; fi
}

# --------------------------------------------------------------------------
# fixture：干净契约（审计器应零发现）
# --------------------------------------------------------------------------
cat >"$TMP/clean.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "Clean", "version": "1.0" },
  "paths": {
    "/api/v1/things": {
      "get": {
        "operationId": "listThings",
        "summary": "列出事项",
        "security": [{ "bearerAuth": [] }],
        "parameters": [
          { "in": "query", "name": "page", "schema": { "type": "integer", "format": "int32", "default": 1, "minimum": 1 }, "description": "页码" }
        ],
        "responses": {
          "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ThingListApiResponse" } } } },
          "401": { "description": "令牌失效：客户端应刷新令牌后重试", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/StringApiResponse" } } } },
          "500": { "description": "Internal Server Error", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/StringApiResponse" } } } }
        }
      },
      "post": {
        "operationId": "createThing",
        "summary": "创建事项",
        "security": [{ "bearerAuth": [] }],
        "requestBody": { "required": true, "content": { "application/json": { "schema": { "$ref": "#/components/schemas/CreateThingRequest" } } } },
        "responses": {
          "201": { "description": "Created", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ThingApiResponse" } } } },
          "500": { "description": "Internal Server Error", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/StringApiResponse" } } } }
        }
      }
    }
  },
  "components": {
    "securitySchemes": { "bearerAuth": { "type": "http", "scheme": "bearer" } },
    "schemas": {
      "CreateThingRequest": {
        "type": "object",
        "description": "创建事项的请求体",
        "required": ["name", "status"],
        "properties": {
          "name": { "type": "string", "description": "名称" },
          "status": { "type": "string", "enum": ["active", "archived"], "description": "状态" }
        }
      },
      "Thing": {
        "type": "object",
        "description": "事项",
        "required": ["id"],
        "properties": { "id": { "type": "string", "format": "uuid", "description": "主键" } }
      },
      "ThingApiResponse": {
        "type": "object",
        "description": "封装",
        "properties": {
          "code": { "type": "integer", "format": "int32", "description": "业务码，0 成功" },
          "message": { "type": "string", "description": "消息" },
          "timestamp": { "type": "string", "format": "date-time", "description": "时间" },
          "data": { "$ref": "#/components/schemas/Thing" }
        }
      },
      "ThingListApiResponse": {
        "type": "object",
        "description": "封装",
        "properties": {
          "code": { "type": "integer", "format": "int32", "description": "业务码" },
          "message": { "type": "string", "description": "消息" },
          "timestamp": { "type": "string", "format": "date-time", "description": "时间" },
          "data": { "type": "array", "description": "列表", "items": { "$ref": "#/components/schemas/Thing" } }
        }
      },
      "StringApiResponse": {
        "type": "object",
        "description": "错误封装",
        "properties": {
          "code": { "type": "integer", "format": "int32", "description": "业务码" },
          "message": { "type": "string", "description": "消息" },
          "timestamp": { "type": "string", "format": "date-time", "description": "时间" },
          "data": { "type": "string", "nullable": true, "description": "错误详情" }
        }
      }
    }
  }
}
JSON

# --------------------------------------------------------------------------
# fixture：全缺口契约（每一类缺口各埋一处，且互相不掩盖）
# --------------------------------------------------------------------------
cat >"$TMP/gappy.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "Gappy", "version": "1.0" },
  "paths": {
    "/api/v1/things": {
      "get": {
        "parameters": [
          { "in": "query", "name": "pageSize", "schema": { "type": "integer", "format": "int32" } },
          { "in": "query", "name": "RangeStartUtc", "schema": { "type": "string", "format": "date-time" } }
        ],
        "responses": {
          "200": { "description": "OK", "content": { "application/json": { "schema": { "$ref": "#/components/schemas/ThingApiResponse" } } } }
        }
      },
      "put": {
        "operationId": "replaceThing",
        "summary": "覆盖事项",
        "responses": { "200": { "description": "OK" }, "500": { "description": "Internal Server Error" } }
      }
    },
    "/api/v1/things/{id}": {
      "delete": {
        "operationId": "deleteThing",
        "summary": "删除事项",
        "parameters": [ { "in": "path", "name": "id", "required": true, "schema": { "type": "string", "format": "uuid" }, "description": "主键" } ],
        "responses": { "204": { "description": "No Content" } }
      }
    },
    "/api/v1/things/upload": {
      "post": {
        "operationId": "uploadThing",
        "summary": "上传",
        "security": [{ "bearerAuth": [] }],
        "responses": { "200": { "description": "OK" }, "500": { "description": "Internal Server Error" } }
      }
    },
    "/api/v1/things/import": {
      "post": {
        "operationId": "importThings",
        "summary": "导入",
        "security": [{ "bearerAuth": [] }],
        "responses": { "200": { "description": "OK" }, "500": { "description": "Internal Server Error" } }
      }
    },
    "/mcp": {
      "get": { "operationId": "mcpStream", "summary": "MCP 事件流", "responses": { "200": { "description": "SSE" } } }
    }
  },
  "components": {
    "schemas": {
      "ThingApiResponse": {
        "type": "object",
        "properties": {
          "code": { "type": "integer", "format": "int32", "description": "业务码" },
          "message": { "type": "string", "description": "消息" },
          "timestamp": { "type": "string", "format": "date-time", "description": "时间" },
          "data": { "$ref": "#/components/schemas/Thing" }
        }
      },
      "Thing": {
        "type": "object",
        "required": ["id", "status", "priority"],
        "properties": {
          "id": { "type": "string", "format": "uuid", "description": "主键" },
          "status": { "type": "string", "description": "允许 NEEDS-ACTION / IN-PROCESS 等（写错值会被 400 30301 拒绝）" },
          "priority": { "type": "integer", "format": "int32" }
        }
      },
      "UploadForm": {
        "type": "object",
        "properties": {
          "file": { "type": "string", "format": "binary", "description": "文件" },
          "path": { "type": "string", "description": "目标路径" }
        }
      }
    }
  }
}
JSON

# 源码样本：一个用 [FromForm] 的端点，字段未写进契约
mkdir -p "$TMP/src/Endpoints"
cat >"$TMP/src/Endpoints/ThingEndpoints.cs" <<'CS'
public static class ThingEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/things/upload", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            return Results.Ok();
        });

        app.MapPost("/api/v1/things/import", ([FromForm] IFormFile file, [FromForm] string calendarId)
            => Results.Ok());
    }
}
CS

run() { "$PY" "$AUDIT" "$@"; }

echo "== 契约缺口审计器 · 行为测试 =="
echo "审计器: $AUDIT"
echo "解释器: $PY"
echo ""

# ① 干净契约
OUT="$(run --contract "$TMP/clean.json" 2>&1)"; RC=$?
expect_exit "① 干净契约退出 0" 0 $RC
expect_contains "① 干净契约报告零发现" "findings: 0" "$OUT"
expect_not_contains "① 干净契约不出现任何 gap 分类" "[op-meta]" "$OUT"
expect_not_contains "① 干净契约不出现参数缺口" "[query-param]" "$OUT"

# ②~⑩ 全缺口契约
OUT="$(run --contract "$TMP/gappy.json" --source-root "$TMP/src" 2>&1)"; RC=$?
expect_exit "② 有缺口时（非门禁模式）仍退出 0" 0 $RC
expect_contains "② GET /api/v1/things 缺 operationId → op-meta" "GET /api/v1/things — missing: operationId, summary" "$OUT"
expect_contains "② 同一 operation 的多项缺失合并成一条" "missing: operationId, summary" "$OUT"
expect_contains "③ 缺 schema 描述 → Thing 也要报" "Thing — schema 无 description" "$OUT"
expect_contains "③ 属性缺描述 → priority" "priority" "$OUT"
expect_contains "④ query 参数缺 default/enum/description → pageSize" "GET /api/v1/things pageSize — 无 description、无 default" "$OUT"
# review P3-1：CJK 正则此前少了字符类方括号（`一-鿿` 只能匹配字面串），
# 「description 里用中文列出取值」这条豁免从未生效，所有字段都被报。现在两条都要成立。
expect_contains "⑤ priority 是枚举型整数且无说明 → 报 enum-typed" "Thing.priority — 枚举型字段 'priority' 是裸 integer" "$OUT"
expect_not_contains "⑤ status 的中文 description 已列出取值 → 不报" "Thing.status —" "$OUT"
expect_contains "⑥ PUT 缺 requestBody" "PUT /api/v1/things — 写操作没有声明 requestBody" "$OUT"
expect_contains "⑦ multipart 符号出现但契约未声明 multipart → 报出具体端点" "POST /api/v1/things/import — 用 [FromForm]/IFormFile/ReadFormAsync 读表单" "$OUT"
expect_contains "⑧ GET /api/v1/things 的 200 已声明但缺 5xx" "GET /api/v1/things — 客户端会解析成功响应，但没有声明 5xx" "$OUT"
expect_contains "⑨ 契约文本里的业务码 30301 未登记" "30301 — 业务码 30301 出现在契约文本里" "$OUT"
expect_contains "⑩ PascalCase query 参数" "GET /api/v1/things RangeStartUtc — PascalCase query 参数" "$OUT"
expect_contains "contract 范围内每个 operation 都缺 security 时逐条报" "GET /api/v1/things — operation 未声明 security" "$OUT"
expect_contains "DELETE 只声明 204 也要求声明 5xx（服务端 500 时客户端要有类型可解）" "DELETE /api/v1/things/{id} — 客户端会解析成功响应，但没有声明 5xx" "$OUT"
expect_not_contains "协议端点 /mcp 不报 5xx（只声明了 200 的流式面）" "GET /mcp — 客户端会解析成功响应" "$OUT"
expect_not_contains "内置公开白名单里的 /mcp 不报 security" "GET /mcp — operation 未声明 security" "$OUT"

# ⑪ 基线差异
OUT="$(run --contract "$TMP/gappy.json" --baseline "$TMP/clean.json" 2>&1)"
expect_contains "⑪ 基线里有、当前没有 → drift-removed" "POST /api/v1/things — 基线有、当前没有" "$OUT"
expect_not_contains "⑪ 当前有、基线没有（正常新增）→ 不报 drift-added" "drift-added" "$OUT"
expect_not_contains "⑪ 两边都有 → 不报 drift" "[drift-removed] GET /api/v1/things " "$OUT"

# ⑫ 确定性 + JSON
run --contract "$TMP/gappy.json" >"$TMP/a.txt" 2>&1
run --contract "$TMP/gappy.json" >"$TMP/b.txt" 2>&1
if cmp -s "$TMP/a.txt" "$TMP/b.txt"; then pass "⑫ 同输入两次输出逐字节相同"; else fail "⑫ 输出不确定"; fi

run --contract "$TMP/gappy.json" --source-root "$TMP/src" --json >"$TMP/a.json" 2>&1
if "$PY" -c '
import json,sys
d=json.load(open(sys.argv[1],encoding="utf-8"))
assert isinstance(d.get("findings"),list) and d["findings"], "findings 为空"
assert all({"category","label","path","method","detail"} <= set(f) for f in d["findings"]), "finding 字段不全"
assert isinstance(d.get("counts"),dict) and d["counts"], "counts 为空"
' "$TMP/a.json" 2>/dev/null; then pass "⑫ --json 可解析且字段完整"; else
  fail "⑫ --json 不可解析或字段不全"; head -20 "$TMP/a.json"; fi

# ⑬ 门禁模式
run --contract "$TMP/clean.json" --fail-on --strict >/dev/null 2>&1; RC=$?
expect_exit "⑬ 门禁模式：干净契约退出 0" 0 $RC
run --contract "$TMP/gappy.json" --source-root "$TMP/src" --fail-on >/dev/null 2>&1; RC=$?
expect_exit "⑬ 门禁模式：有缺口退出 1" 1 $RC

# ⑭ 读取失败 fail-closed
run --contract "$TMP/does-not-exist.json" >/dev/null 2>&1; RC=$?
expect_exit "⑭ 契约不存在退出 4" 4 $RC
echo '{ not json' >"$TMP/bad.json"
run --contract "$TMP/bad.json" >/dev/null 2>&1; RC=$?
expect_exit "⑭ 非法 JSON 退出 4" 4 $RC
run --contract "$TMP/gappy.json" --baseline "$TMP/does-not-exist.json" >/dev/null 2>&1; RC=$?
expect_exit "⑭ 基线不存在退出 4" 4 $RC

# ⑭b 批准数量下降（review P1-3）：批准是「数量有界」的，实际少于上限时提示下调，
#     而不是因为「这个类别早已批准」继续静默放行。
"$PY" - "$TMP/cfg_stale.json" <<'PY'
import json, sys
cfg = {"publicPaths": [], "excludePaths": [], "operations": [],
       "approvedGaps": {"no-such-category":
                        {"reason": "stale", "owner": "test", "count": 3, "expires": "2999-12-31"}}}
json.dump(cfg, open(sys.argv[1], "w", encoding="utf-8"))
PY
OUT="$(run --contract "$TMP/clean.json" --config "$TMP/cfg_stale.json" 2>&1)"
expect_contains "⑭b 批准上限高于实际 → stale-approval" "no-such-category — 批准上限 3 条，但当前只有 0 条" "$OUT"

# ⑭c 旧式「整类批准」字符串不再生效（review P1-3）：必须报 approval-metadata 并阻断，
#     否则新增实例会被历史批准静默放行。
"$PY" - "$TMP/cfg_legacy.json" <<'PY'
import json, sys
cfg = {"publicPaths": [], "excludePaths": [], "operations": [],
       "approvedGaps": {"no-such-category": "历史遗留的整类批准"}}
json.dump(cfg, open(sys.argv[1], "w", encoding="utf-8"))
PY
OUT="$(run --contract "$TMP/clean.json" --config "$TMP/cfg_legacy.json" --fail-on 2>&1)"; RC=$?
expect_exit "⑭c 旧式字符串批准 → 门禁失败" 1 $RC
expect_contains "⑭c 指出批准元数据不全" "no-such-category — 批准是旧的字符串写法" "$OUT"

# ⑮ 源码侧检查必须真的能报（review N-1）：此前源码路由键与契约键格式不一致，
#    multipart / 手工解析请求体 / 字段数三类检查永不触发，而测试因建议文案里含关键字而误绿。
mkdir -p "$TMP/src2"
cat >"$TMP/src2/M.cs" <<'CS'
public static class M
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        var g = endpoints.MapGroup("/api/v1/up");
        g.MapPost("/a", DoUpload).Produces(200);
        g.MapPost("/b", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            return Results.Ok();
        }).Produces(200);
    }

    private static async Task<IResult> DoUpload(HttpRequest request)
    {
        var form = await request.ReadFormAsync();
        return Results.Ok();
    }
}
CS
cat >"$TMP/withform.json" <<'JSON'
{
  "openapi": "3.0.1",
  "info": { "title": "F", "version": "1" },
  "paths": {
    "/api/v1/up/a": { "post": { "operationId": "a", "summary": "a", "security": [{ "bearerAuth": [] }],
      "requestBody": { "required": true, "content": { "multipart/form-data": { "schema": { "type": "object",
        "properties": { "file": { "type": "string", "format": "binary" } } } } } },
      "responses": { "200": { "description": "OK" }, "500": { "description": "err" } } } },
    "/api/v1/up/b": { "post": { "operationId": "b", "summary": "b", "security": [{ "bearerAuth": [] }],
      "responses": { "200": { "description": "OK" }, "500": { "description": "err" } } } }
  },
  "components": { "securitySchemes": { "bearerAuth": { "type": "http", "scheme": "bearer" } }, "schemas": {} }
}
JSON
OUT="$(run --contract "$TMP/withform.json" --source-root "$TMP/src2" 2>&1)"
expect_not_contains "⑮ 契约已声明 multipart、方法组处理函数读表单 → 不报 multipart" "[multipart] POST /api/v1/up/a" "$OUT"
expect_contains "⑮ 契约未声明 multipart、lambda 读表单 → 报 multipart" "POST /api/v1/up/b — 用 [FromForm]/IFormFile/ReadFormAsync 读表单" "$OUT"

# ⑯ 真实仓契约上源码侧检查非空（证明不再是空转）：当前至少有 1 条 multipart 发现
if [ -d "$ROOT/src" ]; then
  OUT="$(run --contract "$CONTRACT" --source-root "$ROOT/src" 2>&1)"
  expect_contains "⑯ 真实仓：源码侧检出 multipart 缺口" "POST /api/v1/files/items/upload — 用 [FromForm]/IFormFile/ReadFormAsync 读表单" "$OUT"
fi

echo ""
if [ "$FAIL" -ne 0 ]; then
  echo "FAILED：$FAIL 项失败，$PASS 项通过"
  exit 1
fi
echo "PASSED：$PASS 项通过"
