#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""契约缺口审计器（只读，离线，只用 python3 标准库）。

回答一个具体问题：**只看 contract/openapi.json，别人能不能据此写出一个客户端？**
不能的地方逐条列出来，按「客户端会踩什么坑」分类，并给出建议补法。

它审计的是「契约对客户端是否够用」，不是「契约与代码是否同步」——
后者由 `scripts/ci/*contract*` 的既有新鲜度门禁负责，两者互补。

发现分类（finding.category）：
  op-meta          operation 缺 operationId / summary（客户端无法知道这个端点干什么）
  op-description   operation 缺 description（有 summary 但没写行为语义）
  security         operation 没有声明 security，也没落在明确的公开白名单里
  parametric      路径模板参数没在 operation 的 parameters 里声明
  request-body     POST/PUT/PATCH 既无 requestBody、源码里也没有显式 FromBody/FromForm 参数
  multipart        源码出现 [FromForm]/IFormFile/BinaryFile，但契约里没有 multipart/form-data
  binary-form      契约里有 multipart，但 form schema 没把文件字段标成 format: binary
  query-param      query 参数没有 description、default、enum 中的任何一项
  pascal-query     query 参数是 PascalCase（.NET 模型绑定产物），与契约其余部分的 camelCase 不一致
  path-required    path 参数没标 required: true（OpenAPI 要求恒为 true）
  required-nullable required 的属性同时 nullable（客户端不知道可不可以发 null）
  schema-desc      schema 没有 description
  prop-desc        属性没有 description
  enum-typed       status/kind/priority/… 这类枚举型字段是裸 string/integer，没有 enum 也没有文字枚举说明
  error-semantics  操作会返回非 2xx 但 2xx 之外的响应没有 schema，客户端拿不到业务错误码
  is-api-response  后端实际发 {code,message,data,timestamp} 但响应 schema 不是 ApiResponse 形状
  response-header  源码里显式设置了响应头（X-Truncated / Retry-After …），契约没声明
  no-5xx           客户端会解析 2xx 响应的操作没有声明 5xx（服务端 500 时客户端拿到的是没见过的形状）
  biz-errors       契约文本里出现业务错误码，但没有登记到仓库内的业务错误码表
  undocumented     契约里有、业务错误码表/说明文档里没有提到（需要 --spec，见 --spec）
  drift-added      当前契约有、基线没有的 operation（通常是「问题所在」或未被告知的变更）
  drift-removed    基线有、当前没有的 operation（客户端会 404 —— 最危险的一种）
  drift-schema     同名 schema 的属性集合变了

退出码：
  0  审计完成（非门禁模式总是 0，除非读取失败）
  1  --fail-on 命中且存在非基线类发现（门禁模式）
  2  环境错误
  4  输入非法（文件缺失 / 非 OpenAPI 3.x / 非 JSON）—— fail-closed，绝不当作「没有缺口」

用法见 `--help`。
"""

import argparse
import json
import os
import re
import sys

EXIT_OK = 0
EXIT_FINDINGS = 1
EXIT_ENV = 2

MODE_ORDER = {"P0": 0, "P1": 1, "P2": 2, "P3": 3}
EXIT_INPUT = 4

HTTP_METHODS = ("get", "post", "put", "patch", "delete", "head", "options", "trace")

# 默认参数缺省值：这些字段名有行业共识的缺省行为，不强制在契约里写 default。
CONVENTIONAL_QUERY_DEFAULTS = {
    "force": "false",
    "page": "1",
    "pageSize": "20（各端点不同，见说明）",
    "cursor": None,
    "search": None,
    "q": None,
}

# 「枚举型字段」：字段名本身就在告诉读者它是一组有限取值。
ENUM_TYPED_NAMES = {
    "status", "kind", "type", "priority", "severity", "role", "state",
    "source", "classificationSource", "scope", "action", "mode", "granularity",
    "level", "direction", "format", "provider", "category",
}

# 内置的公开端点（认证入口与协议面）。仓库内 docs/contract-baseline.json 的 publicPaths 会并入这里。
BUILTIN_PUBLIC_PATHS = {
    "/api/v1/auth/login",
    "/api/v1/auth/refresh",
    "/api/v1/auth/register",
    "/api/v1/mcp/verify",
    "/mcp",
    "/health",
    "/health/live",
    "/health/ready",
}

# 协议面 / 别名：不参与 5xx、request-body 之类「业务端点规约」的检查。
PROTOCOL_PATHS = {"/mcp", "/metrics", "/hangfire"}

# 不做格式规约检查的路径（框架兜底路由：路径与形状都不是契约的一部分）。
INFRA_PATHS = {"/api/{path}"}

CJK = re.compile("\u4e00-\u9fff")

# 「关键」头（第三项 True）：不声明客户端就没法正确实现（认证/限流/截断）。
# 「提示」头（False）：文件下载天然带 ETag / Content-Disposition / Range，契约不声明也能工作，只记 P2。
HEADER_PATTERNS = (
    (r"Retry-After|RetryAfter", "Retry-After", True),
    (r"X-Truncated|Results\.StatusCode\(206\)", "X-Truncated / 206", True),
    (r"ConditionalRequest|IfNoneMatchEvaluated|Results\.File\(|EnableRangeProcessing|SendFileAsync",
     "ETag / 304 / Range（文件下载/条件请求）", False),
    (r"Content-Range|Content-Disposition|Attachment\(", "Content-Range / Content-Disposition", False),
)

API_PREFIX = "/api/"

BIN = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
REPO_ROOT = os.path.dirname(BIN)
DEFAULT_BIZ_ERRORS = os.path.join(REPO_ROOT, "docs", "business-error-codes.md")
DEFAULT_BASELINE = os.path.join(REPO_ROOT, "docs", "contract-baseline.json")


def load_baseline_config(path):
    if not path or not os.path.isfile(path):
        return {"publicPaths": [], "excludePaths": [], "approvedGaps": {}, "operations": []}
    cfg = load_json(path, "审计基线")
    if not isinstance(cfg, dict):
        die_input("审计基线必须是 JSON 对象：%s" % path)
    return {
        "publicPaths": [str(p) for p in cfg.get("publicPaths") or []],
        "excludePaths": [str(p) for p in cfg.get("excludePaths") or []],
        "approvedGaps": {str(k): str(v) for k, v in (cfg.get("approvedGaps") or {}).items()},
        "operations": [str(o) for o in cfg.get("operations") or []],
    }

# 源码里「显式读到请求体」的符号。审计**只**用它们去解释/核对契约，绝不用它们去「豁免」缺口：
# 契约是对客户端的承诺，客户端只看契约，不看源码。命中越多，说明这个端点越需要人工补契约。
SOURCE_BODY_READERS = (
    "ReadFromJsonAsync",             # Microsoft.AspNetCore.Http.HttpRequestJsonExtensions / System.Net.Http.Json
    "JsonSerializer.DeserializeAsync",
    "JsonSerializer.Deserialize",
    "JsonDocument.ParseAsync",
    "JObject.Load",
    "JObject.Parse",
    "ReadFormAsync",                 # multipart / urlencoded
)
# 「源码已经自己把请求体解析成具名 DTO」的符号：OpenAPI 导出器看不见，是 request-body 缺口的典型根因。
HAND_BOUND_READERS = (
    "Results.BindAsync",
    ".BindAsync(",
    "TryReadJsonAsync",
    "ReadUploadAsync",
    "ReadBodyAsync",
)
APPROVED_HAND_WRITTEN = "@x-approved-unexported-body"  # 源码行上的显式豁免标记（必须带理由）
FIELD_METHODS = {"get", "set", "init", "add", "remove", "equals", "gethashcode", "tostring",
                 "compareto", "deconstruct", "printmembers", "clone"}
# 在 record 主构造参数里出现这些类型，说明它绑的是 HTTP 上下文而不是请求体字段。
NON_BODY_PARAMS = re.compile(r"^(HttpRequest|HttpResponse|HttpContext|CancellationToken|IFormFile|IFormFileCollection|"
                             r"IFormCollection|Stream|ClaimsPrincipal|IServiceProvider)\b")


def normalize_route(path):
    """{.NET 约束语法} → {}，与 OpenAPI 路径模板同形；{*catchall} 与 {**catchall} 同理。"""
    return re.sub(r"\{(\*{1,2})?[^}]*\}", "{}", path)


# ---------------------------------------------------------------------------
# 输入
# ---------------------------------------------------------------------------
def die_input(msg):
    print("audit-contract-gaps: " + msg, file=sys.stderr)
    sys.exit(EXIT_INPUT)


def load_json(path, what):
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            return json.load(fh)
    except FileNotFoundError:
        die_input("%s 不存在：%s" % (what, path))
    except (OSError, UnicodeDecodeError) as exc:
        die_input("%s 无法读取：%s（%s）" % (what, path, exc))
    except json.JSONDecodeError as exc:
        die_input("%s 不是合法 JSON：%s（%s）" % (what, path, exc))


def load_contract(path):
    doc = load_json(path, "契约")
    version = str(doc.get("openapi", ""))
    if not version.startswith("3."):
        die_input("契约不是 OpenAPI 3.x（openapi=%r）：%s" % (version, path))
    if not isinstance(doc.get("paths"), dict):
        die_input("契约没有 paths 对象：%s" % path)
    return doc


def iter_operations(doc):
    """按路径字典序、方法固定顺序产出 (method, path, operation)。输出顺序因此是确定的。"""
    for path in sorted(doc["paths"]):
        item = doc["paths"][path]
        if not isinstance(item, dict):
            continue
        for method in HTTP_METHODS:
            op = item.get(method)
            if isinstance(op, dict):
                yield method.upper(), path, op


# ---------------------------------------------------------------------------
# $ref 与 schema 遍历
# ---------------------------------------------------------------------------
def resolve_ref(doc, ref):
    if not ref.startswith("#/"):
        return None
    node = doc
    for part in ref[2:].split("/"):
        part = part.replace("~1", "/").replace("~0", "~")
        if not isinstance(node, dict) or part not in node:
            return None
        node = node[part]
    return node


def deref(doc, schema):
    """解一层 $ref（循环引用不会死循环，因为只解一层）。"""
    if isinstance(schema, dict) and isinstance(schema.get("$ref"), str):
        return resolve_ref(doc, schema["$ref"]) or {}
    return schema if isinstance(schema, dict) else {}


def schema_ref_name(schema):
    if isinstance(schema, dict) and isinstance(schema.get("$ref"), str):
        return schema["$ref"].rsplit("/", 1)[-1]
    return None


def schema_shape(doc, schema):
    """返回 (顶层 ref 名或 None, 解 $ref 后的对象)。"""
    return schema_ref_name(schema), deref(doc, schema)


def looks_like_api_response(doc, schema):
    name, shape = schema_shape(doc, schema)
    if name and name.endswith("ApiResponse"):
        return True
    props = shape.get("properties") or {}
    return {"code", "message", "timestamp"} <= set(props) and "data" in props


def walk_properties(doc, schema, seen=None):
    """遍历 schema 闭包内的每个属性，产出 (owner_schema_name, prop_name, prop_schema, required)。"""
    if seen is None:
        seen = set()
    name = schema_ref_name(schema)
    shape = deref(doc, schema)
    # 循环引用靠「对象身份」剪枝：用名称会让同名但形状不同的定义（例如不同契约里的同名 schema）被误剪。
    key = id(shape)
    if key in seen:
        return
    seen.add(key)
    owner = name or "<inline>"
    required = set(shape.get("required") or [])
    for prop_name in sorted((shape.get("properties") or {})):
        prop = shape["properties"][prop_name] or {}
        yield owner, prop_name, prop, prop_name in required
        yield from walk_properties(doc, prop, seen)  # 数组 items / 嵌套 $ref 由 deref 处理
        for sub in ("items", "additionalProperties"):
            if isinstance(prop.get(sub), dict):
                yield from walk_properties(doc, prop[sub], seen)
    for combinator in ("allOf", "oneOf", "anyOf"):
        for sub in shape.get(combinator) or []:
            yield from walk_properties(doc, sub, seen)


def operation_parameters(op, path_item):
    params = list(path_item.get("parameters") or []) + list(op.get("parameters") or [])
    # 同名同位置以 operation 级覆盖 path 级
    merged = {}
    for p in params:
        if isinstance(p, dict):
            merged[(p.get("in"), p.get("name"))] = p
    return [merged[k] for k in sorted(merged, key=lambda k: (str(k[0]), str(k[1])))]


def path_template_params(path):
    return re.findall(r"\{([^}]+)\}", path)


def response_schemas(op):
    """产出 (status, schema 或 None)。"""
    for status in sorted(op.get("responses") or {}, key=lambda s: (s.isdigit() is False, s)):
        resp = op["responses"][status]
        if not isinstance(resp, dict):
            yield status, None
            continue
        content = resp.get("content") or {}
        schema = None
        for media in ("application/json", "application/problem+json"):
            if isinstance(content.get(media), dict):
                schema = content[media].get("schema")
                break
        if schema is None:
            for media in sorted(content):
                if isinstance(content[media], dict) and content[media].get("schema"):
                    schema = content[media]["schema"]
                    break
        yield status, schema


# ---------------------------------------------------------------------------
# 发现
# ---------------------------------------------------------------------------
class Findings(object):
    def __init__(self):
        self.items = []

    def add(self, category, label, detail, path="", method="", suggestion="", severity="P2"):
        self.items.append({
            "category": category,
            "label": label,
            "method": method,
            "path": path,
            "detail": detail,
            "suggestion": suggestion,
            "severity": severity,
        })


def describe_param_gap(doc, param, name):
    schema = deref(doc, param.get("schema") or {})
    gaps = []
    if not (param.get("description") or schema.get("description")):
        gaps.append("无 description")
    if "default" not in schema and "default" not in param:
        gaps.append("无 default")
    ptype = schema.get("type")
    # string 参数大多有有限取值；integer 只有「枚举型名字」（priority/level…）才要求 enum，
    # page/pageSize/limit 这类计数参数不要求。
    if "enum" not in schema and (ptype == "string" or (ptype == "integer" and name in ENUM_TYPED_NAMES)):
        gaps.append("无 enum")
    return gaps


def _shape(path):
    """路径形态：模板参数统一成 `{}`（`/tiles/{z}/{x}/{y}.png` 与 `/tiles/{}/{}/{}.png` 视为同一路径）。"""
    return re.sub(r"\{[^}]*\}", "{}", path)


def audit_operations(doc, findings, audit_cfg=None):
    audit_cfg = audit_cfg or {}
    public_paths = set(BUILTIN_PUBLIC_PATHS) | set(audit_cfg.get("publicPaths") or [])
    for method, path, op in iter_operations(doc):
        if path in INFRA_PATHS:
            continue
        path_item = doc["paths"][path]
        if not isinstance(path_item, dict):
            path_item = {}

        # op-meta
        missing = [k for k in ("operationId", "summary") if not op.get(k)]
        if missing:
            findings.add("op-meta", "%s %s" % (method, path),
                         "missing: " + ", ".join(missing),
                         path, method,
                         "补 operationId（稳定、驼峰、全局唯一）与 summary（一句话说清客户端什么时候调用它）")

        # security（公开端点要么写进白名单，要么在 operation 上声明）
        if _shape(path) not in {_shape(p) for p in public_paths} and not op.get("security"):
            findings.add("security", "%s %s" % (method, path),
                         "operation 未声明 security，也未列入公开白名单",
                         path, method,
                         "显式写 security: [{bearerAuth: []}] / [{opsKey: []}]，让客户端知道必须带哪种凭证；"
                         "确实公开的写进 docs/contract-baseline.json 的 publicPaths")

        # query 参数
        for param in operation_parameters(op, path_item):
            where, name = param.get("in"), param.get("name")
            if where == "path" and param.get("required") is not True:
                findings.add("path-required", "%s %s %s" % (method, path, name),
                             "path 参数未标 required: true", path, method,
                             "补 required: true（OpenAPI 规定 path 参数恒为必填）")
            if where != "query":
                continue
            gaps = describe_param_gap(doc, param, name)
            if gaps:
                conventional = name in CONVENTIONAL_QUERY_DEFAULTS
                findings.add(
                    "query-param",
                    "%s %s %s" % (method, path, name),
                    "、".join(gaps) + ("（该名字有惯例缺省，但契约仍应写明）" if conventional else ""),
                    path, method,
                    "在 schema 里补 default/minimum/maximum 或在 parameter 上写 description；有限取值的写 enum",
                )
            if name[:1].isupper():
                findings.add(
                    "pascal-query",
                    "%s %s %s" % (method, path, name),
                    "PascalCase query 参数（与契约其它参数的 camelCase 不一致，客户端容易写错大小写导致参数被静默忽略）",
                    path, method,
                    "改为 camelCase 显式参数，或 [FromQuery(Name=\"...\")] 固定 wire 名后在契约 description 写明",
                )

        is_api = path.startswith(API_PREFIX) and path not in PROTOCOL_PATHS
        is_write = method in ("POST", "PUT", "PATCH")
        has_body = isinstance(op.get("requestBody"), dict) and bool(op["requestBody"])

        # request-body：写操作没有声明请求体。契约层面无法区分「真的没有」与「导出时丢了」，
        # 所以前者需要在描述里写明（源码侧由 audit_sources 给出「自己解析请求体」的直接证据）。
        if is_api and is_write and not has_body:
            findings.add("request-body", "%s %s" % (method, path),
                         "写操作没有声明 requestBody（无论 JSON 还是 multipart），也没有写明「无请求体」",
                         path, method,
                         "补 requestBody（schema 复用 DTO 名）；确实不要请求体的在 description 写明「无请求体」",
                         "P1")

        # 响应
        statuses = [s for s, _ in response_schemas(op)]
        has_2xx = any(s.startswith("2") for s in statuses)
        if is_api and has_2xx and not any(s.startswith("5") for s in statuses):
            findings.add("no-5xx", "%s %s" % (method, path),
                         "客户端会解析成功响应，但没有声明 5xx",
                         path, method,
                         "声明 500（以及服务端会返回的 502/503）并给出 schema，否则客户端对异常没有类型可解",
                         "P1")

        for status, schema in response_schemas(op):
            if status.startswith("2") or status == "default":
                continue
            content = (op["responses"][status] or {}).get("content") or {}
            if content and schema is None:
                findings.add("error-semantics", "%s %s" % (method, path),
                             "%s 响应声明了 content 但没有 schema" % status, path, method,
                             "补错误响应 schema（统一 ErrorResponse 或 ApiResponse<T>）")
            if status in ("400", "401", "403", "409", "422", "429") and not content:
                findings.add("error-semantics", "%s %s" % (method, path),
                             "%s 响应是空的：客户端拿不到业务错误码/可读原因" % status, path, method,
                             "声明错误体 schema，并在 schema 的 description 里列出该状态可能出现的关键业务码（或引用业务错误码表）")
                break  # 每个 operation 只报一次错误语义，避免噪音

        # requestBody 中的 form 形态
        for media_key, media in sorted(((op.get("requestBody") or {}).get("content") or {}).items()):
            if "form" not in media_key:
                continue
            schema = (media or {}).get("schema")
            name, shape = schema_shape(doc, schema or {})
            props = shape.get("properties") or {}
            has_file_field = any((props[p] or {}).get("format") in ("binary", "byte") for p in props)
            if not has_file_field:
                findings.add("binary-form", "%s %s" % (method, path),
                             "%s 声明了 multipart，但没有一个字段是 format: binary（文件字段无法识别）" % (name or "<inline>"),
                             path, method,
                             "文件字段写 type: string, format: binary；其它字段写正常类型与 description")


def audit_schemas(doc, findings):
    schemas = ((doc.get("components") or {}).get("schemas") or {})
    for name in sorted(schemas):
        schema = schemas[name] or {}
        if not schema.get("description"):
            findings.add("schema-desc", name, "schema 无 description", "",
                         suggestion="用 <summary> 注释导出：这个结构代表什么、用在哪个端点")

    seen = set()
    for name in sorted(schemas):
        # 每个根 schema 单独去重：共享 seen 会让闭包里的同名类型只按第一个根展开
        for owner, prop_name, prop, required in walk_properties(doc, {"$ref": "#/components/schemas/" + name}, set()):
            key = (owner, prop_name)
            if key in seen:
                continue
            seen.add(key)
            resolved = deref(doc, prop)
            if not (prop.get("description") or resolved.get("description")):
                findings.add("prop-desc", "%s.%s" % (owner, prop_name), "属性无 description")
            if required and prop.get("nullable") is True:
                findings.add("required-nullable", "%s.%s" % (owner, prop_name),
                             "required 且 nullable：客户端无法判断能不能发 null",
                             suggestion="二者取一：要么去掉 required，要么去掉 nullable")
            ptype = prop.get("type") or resolved.get("type")
            if prop_name in ENUM_TYPED_NAMES and "$ref" not in prop and ptype in ("string", "integer"):
                text = (prop.get("description") or resolved.get("description") or "")
                if "enum" not in prop and "enum" not in resolved and not CJK.search(text):
                    findings.add("enum-typed", "%s.%s" % (owner, prop_name),
                                 "枚举型字段 %r 是裸 %s：既无 enum，也没有文字枚举说明" % (prop_name, ptype),
                                 suggestion="能导出成 enum 的写 enum（含响应方向）；不能的至少在 description 里列出全部取值与含义",
                                 severity="P1")


def audit_biz_codes(doc, findings, biz_table_path):
    """统一错误响应里引用的码必须是「服务端真会返回」的码。

    业务码有两种写法：
      - 源码字面量 `Error(02057, ...)` → 编译后是整数 2057；
      - 说明文字里写 `02057`（带前导零，给人读的写法）。
    所以比较时**归一成整数**，并把带前导零的写法也收录进码表，避免「码表有 2057、契约写 02057」的假阴性。
    """
    text = json.dumps(doc, ensure_ascii=False)
    codes = set()
    for raw in re.findall(r"(?<![\d.])(\d{5})(?![\d.])", text):
        codes.add(int(raw))
    if not codes:
        return 0
    documented = set()
    if biz_table_path and os.path.isfile(biz_table_path):
        with open(biz_table_path, "r", encoding="utf-8-sig") as fh:
            for raw in re.findall(r"(?<!\d)(\d{5})(?!\d)", fh.read()):
                documented.add(int(raw))
    for code in sorted(codes):
        if code not in documented:
            findings.add("biz-errors", "%d" % code,
                         "业务码 %d 出现在契约文本里，但不在业务错误码表 %s 中"
                         % (code, os.path.relpath(biz_table_path, REPO_ROOT) if biz_table_path else "<未提供>"),
                         suggestion="运行 extract-business-error-codes.py 刷新码表；若该码只出现在文字说明里、"
                                    "服务端并不会真返回，请在契约里改写说明")
    return len(codes)


def audit_removed_vs_baseline_set(doc, recorded_operations, findings):
    """review I-2：门禁必须能发现「端点被悄悄删除」。

    基线 JSON 里的 `operations`（`"METHOD /path"` 列表）是**已发布给客户端**的操作集；
    当前契约里少了任何一条 → P0。与 `--baseline` 的区别：这里不需要另一份契约快照，
    所以 CI 可以直接、始终执行它。
    """
    if not recorded_operations:
        return
    current = {"%s %s" % (m, p) for m, p, _ in iter_operations(doc)}
    for op in sorted(set(recorded_operations) - current):
        method, _, path = op.partition(" ")
        findings.add("drift-removed", op, "基线（已发布的客户端契约）里有、当前契约没有：存量客户端会 404",
                     path, method, "恢复端点；若确要下线，先从基线移除并在 PR 里声明替代端点", "P0")


def audit_drift(doc, baseline, findings):
    """只报「基线有、当前没有」——这是存量客户端会 404 的 P0。新增端点属于正常演进，不是缺口。"""
    cur = {(m, p) for m, p, _ in iter_operations(doc)}
    base = {(m, p) for m, p, _ in iter_operations(baseline)}
    for m, p in sorted(base - cur):
        findings.add("drift-removed", "%s %s" % (m, p), "基线有、当前没有：按基线开发的客户端会 404",
                     p, m, "若是误删请恢复；若是有意下线，需在变更说明中告知并给出替代端点", "P0")

# ---------------------------------------------------------------------------
# 源码扫描（只作「解释器」：定位端点怎么读请求体、显式设置了哪些响应头）
# ---------------------------------------------------------------------------
def read_sources(source_root):
    files = []
    for dirpath, dirnames, filenames in os.walk(source_root):
        dirnames[:] = sorted(d for d in dirnames if d not in ("bin", "obj", "node_modules", ".git"))
        for fn in sorted(filenames):
            if fn.endswith(".cs"):
                files.append(os.path.join(dirpath, fn))
    return files


def count_params(text):
    """数顶层逗号 + 1；忽略尖括号/方括号里的逗号（List<T>, [FromServices]）。"""
    depth = 0
    count = 0
    has_content = False
    for ch in text:
        if ch in "(<[":
            depth += 1
        elif ch in ")>]":
            depth -= 1
        elif ch == "," and depth == 0:
            count += 1
        elif not ch.isspace():
            has_content = True
    return count + 1 if has_content else 0


def looks_like_record_param_list(text):
    """record 主构造参数列表里出现 HTTP 上下文类型，或既没有类型名也没有编译期常量 → 不是请求体 DTO。"""
    parts = [p.strip() for p in re.split(r",(?![^<]*>)", text) if p.strip()]
    for part in parts:
        first = part.split()[0]
        if NON_BODY_PARAMS.match(first):
            return False
        has_type = re.search(r"^\[[^\]]*\]\s*[\w.<>\[\]?]+\s+\w+", part)
        if not has_type and not re.match(r"^[\w.]+\s*=", part) and not re.match(r"^[\w.]+\s*$", part):
            return False
    return True


def eval_path_expr(expr, consts):
    """路径表达式求值：字面量 / $"{Const}/x" / Class.Const。求不出来返回 None。"""
    expr = expr.strip()
    if expr.startswith('$"') or expr.startswith('"'):
        parts = []
        pos = 0
        for m in re.finditer(r"\{(\w+)\}", expr):
            parts.append(expr[pos:m.start()])
            value = eval_path_expr(m.group(1), consts)
            if value is None:
                return None
            parts.append(value)
            pos = m.end()
        parts.append(expr[pos:])
        return "".join(parts).strip('"$')
    short = expr.split(".")[-1]
    if short in consts:
        return eval_path_expr(consts[short], consts)
    return None


def _method_body(text, name):
    """取方法 `name` 的实现体（跨行签名 + 配平的花括号），取不到返回空串。"""
    decl = re.search(r"(\b(?:private|public|internal|protected|static|async|sealed)\b[\w\s<>,\[\]\.\?]*\b"
                     + re.escape(name) + r"\s*\()", text)
    if not decl:
        return ""
    open_paren = decl.end() - 1
    depth, i = 0, open_paren
    while i < len(text):
        if text[i] == "(":
            depth += 1
        elif text[i] == ")":
            depth -= 1
            if depth == 0:
                break
        i += 1
    if i >= len(text):
        return ""
    start = decl.start()
    rest = i + 1
    brace = text.find("{", rest)
    arrow = text.find("=>", rest)
    if arrow != -1 and (brace == -1 or arrow < brace):
        end = text.find(";", arrow)
        return text[start:end + 1] if end != -1 else ""
    if brace == -1:
        return ""
    depth = 0
    for j in range(brace, min(len(text), brace + 8000)):
        if text[j] == "{":
            depth += 1
        elif text[j] == "}":
            depth -= 1
            if depth == 0:
                return text[start:j + 1]
    return ""


def _handler_route(text, handler_name, consts, groups):
    """方法组注册的路径：在同文件里找同名方法定义，返回它内部唯一的 `MapXxx("...", ...)` 路径。

    方法组（`group.MapPost("/items/upload", UploadItemAsync)`）的路径就在注册行里，这里已经拿到了；
    这个函数只处理**另一类写法**：注册行没有字面量、却在方法体里再挂一次路由的罕见情况。
    找不到就返回 None（由调用方跳过，不猜）。
    """
    m = re.search(r"%s\s*\([^)]*\)\s*(?:=>|\{)" % re.escape(handler_name), text)
    if not m:
        return None
    tail = text[m.start(): m.start() + 4000]
    inner = re.findall(r'\.Map(?:Get|Post|Put|Patch|Delete)\(\s*"([^"]+)"', tail)
    return inner[0] if len(set(inner)) == 1 else None


def first_call_argument(text, open_paren_index):
    """取以 text[open_paren_index] == '(' 开始的那次调用的第一个实参（考虑嵌套括号与字符串）。"""
    depth = 0
    in_str = False
    i = open_paren_index
    start = i + 1
    while i < len(text):
        ch = text[i]
        if in_str:
            if ch == "\\":
                i += 2
                continue
            if ch == '"':
                in_str = False
        elif ch == '"':
            in_str = True
        elif ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
            if depth == 0:
                return text[start:i].split(",")[0].strip()
        elif ch == "," and depth == 1:
            return text[start:i].strip()
        i += 1
    return ""


def parse_source(files):
    """返回 (routes, global_readers, record_fields, header_settings)。

    routes: (path, http) -> {forms, readers, hand_bound, approved, source}
    """
    routes = {}
    global_readers = set()
    record_fields = {}
    header_settings = set()

    file_texts = {}
    for fpath in files:
        try:
            with open(fpath, "r", encoding="utf-8-sig") as fh:
                file_texts[fpath] = fh.read()
        except (OSError, UnicodeDecodeError):
            continue

    for fpath, text in file_texts.items():
        rel = os.path.relpath(fpath, REPO_ROOT).replace("\\", "/")
        if any(r in text for r in SOURCE_BODY_READERS + HAND_BOUND_READERS) or "BodyReader.Read" in text:
            global_readers.add(rel)
        # 显式响应头 / 状态语义 → 客户端要处理，但契约没声明。
        # 只看「会向 HTTP 客户端写响应」的文件（端点 / 模块 / 中间件），避免把出站 HTTP 客户端和
        # 正则字面量（例如 ETag 出现在脱敏正则里）当成响应头。
        is_outbound = (".Map(" in text or "MapGroup(" in text or "Middleware" in rel or "/Endpoints/" in rel)
        if not is_outbound:
            continue
        # 响应头按「端点」定位：先切出每个注册的语句块（到下一个注册或 60 行为止），
        # 再看块内有没有直接设置响应头/状态。文件级命中会把「同文件里的 304 帮助方法」
        # 误报成「该文件所有端点都返回 304」。
        consts = dict(re.findall(r'const\s+string\s+(\w+)\s*=\s*(.+?);', text))
        groups = {}
        for name, expr in re.findall(r"(\w+)\s*=\s*[\w.]+\.MapGroup\(\s*([^)]+?)\s*\)", text):
            value = eval_path_expr(expr, consts)
            if value:
                groups[name] = value
        regs = list(re.finditer(
            r'(\w+)\s*\.\s*Map(Get|Post|Put|Patch|Delete|HealthChecks|Methods)\(', text))
        for i, reg in enumerate(regs):
            stop = regs[i + 1].start() if i + 1 < len(regs) else len(text)
            block = text[reg.start(): min(stop, reg.end() + 3000)]
            receiver, method = reg.group(1), reg.group(2).upper()
            # 路径实参可能是字面量、$"..." 模板或常量；先求值，再与 group 前缀合并。
            arg = first_call_argument(text, reg.end() - 1) or ""
            if not arg:
                continue
            value = eval_path_expr(arg, consts)
            handler_code = None
            if not value:
                # 方法组注册：MapPost("/items/upload", UploadItemAsync)。`first_call_argument` 在遇到
                # 方法组时会把路径与处理函数名一起取出（逗号被括号深度吞掉），这里取首个形如 "..." 的字面量。
                literal = re.search(r'"([^"]*)"', arg)
                if literal and re.fullmatch(r'"[^"]*"\s*,\s*\w+', arg.strip()):
                    value = eval_path_expr(literal.group(1), consts)
            if not value:
                continue  # 动态路径：不在这里猜
            prefix = groups.get(receiver, "")
            # group 上的相对路径在 C# 里也写成 "/users"（以 / 开头但仍是相对该 group 的）。
            # 判定依据是「接收者是不是 group」，而不是字符串是否以 / 开头。
            if prefix and not value.startswith(prefix.rstrip("/") + "/"):
                path = prefix.rstrip("/") + "/" + value.lstrip("/")
            elif value.startswith("/"):
                path = value
            else:
                path = "/" + value
            path = normalize_route(path)
            for pattern, what, important in HEADER_PATTERNS:
                if re.search(pattern, block):
                    header_settings.add((method, path, what, rel, important))
            # 路由登记（review N-1 附带修复）：此前用另一套行级正则收集路由且**不拼 MapGroup 前缀**，
            # 308 个源码路由里只有几个能与契约路径对上，源码侧检查基本空转。这里统一用已求值的路径。
            # 方法组注册（MapPost("/x", Handler)）：把处理函数的实现体并进块，才能看到它怎么读请求体。
            handler_name = re.search(r'"[^"]*"\s*,\s*(\w+)\s*\)', text[reg.end(): reg.end() + 200])
            if handler_name:
                body = _method_body(text, handler_name.group(1))
                if body:
                    block = block + "\n" + body
            entry = routes.setdefault((path, method), {"forms": False, "readers": [], "hand_bound": [],
                                                        "approved": [], "source": rel})
            if "[FromForm]" in block or "IFormFile" in block or "BinaryFile" in block or "ReadFormAsync" in block:
                entry["forms"] = True
            for reader in SOURCE_BODY_READERS:
                if reader in block:
                    entry["readers"].append(reader)
            for reader in HAND_BOUND_READERS:
                if reader in block:
                    entry["hand_bound"].append(reader)
            if APPROVED_HAND_WRITTEN in block:
                entry["approved"].append(APPROVED_HAND_WRITTEN)

    for fpath, text in file_texts.items():
        rel = os.path.relpath(fpath, REPO_ROOT).replace("\\", "/")
        lines = text.splitlines()

        # record 主构造参数 → 字段数（排除 record class / record struct 的非法简写与普通声明）
        for m in re.finditer(r"\brecord\s+(?:class\s+|struct\s+)?(\w+)\s*\(([^;{}]*)\)\s*[;{]", text, re.S):
            name, params = m.group(1), m.group(2)
            if not params.strip() or not looks_like_record_param_list(params):
                continue
            record_fields[name] = count_params(params)

    return routes, global_readers, record_fields, header_settings


def audit_sources(doc, findings, source_root):
    files = read_sources(source_root)
    routes, global_readers, record_fields, header_settings = parse_source(files)
    # 路由键统一是 (path, METHOD 大写)，与 parse_source 产出的键一致。
    # review N-1：曾经把契约侧的小写 method 与源码侧的大写混用，导致 308 个源码路由只匹配上 4 个，
    # 下面三类源码侧检查（multipart / 手工解析请求体 / 字段数）实际上永远不会触发。
    contract_ops = {(_shape(p), m.upper()) for m, p, _ in iter_operations(doc)}
    del global_readers  # 目前只做记录；有逐端点证据时不用它

    for method, path, what, rel, important in sorted(header_settings):
        op = (doc["paths"].get(path) or {}).get(method.lower())
        if op is None:
            continue  # 契约里没有这个端点：由 drift 类发现负责，不在响应头里重复报
        declared = any((resp or {}).get("headers") for resp in (op.get("responses") or {}).values())
        if declared:
            continue
        findings.add("response-header", "%s %s" % (method, path),
                     "源码设置/依赖了 %s（%s），契约没有在该端点的响应上声明 headers" % (what, rel),
                     path, method,
                     "在对应响应的 headers 里声明（例：X-Truncated: boolean、Retry-After: integer、ETag: string）",
                     "P1" if important else "P2")

    for (path, http) in sorted(routes):
        entry = routes[(path, http)]
        # 只审「契约里存在」的端点：源码有、契约没有的，已在 audit_operations 侧统一报 drift-removed，
        # 这里再报一遍只会让同一个根因出现两次。
        if (_shape(path), http) not in contract_ops:
            continue

        op = (doc["paths"].get(path) or {}).get(http.lower()) if path in doc["paths"] else None
        if op is None:
            continue
        form_content = ((op.get("requestBody") or {}).get("content") or {})
        has_contract_form = any("form" in media for media in form_content)

        if entry["forms"] and not has_contract_form:
            findings.add("multipart", "%s %s" % (http, path),
                         "用 [FromForm]/IFormFile/ReadFormAsync 读表单，契约里没有 multipart/form-data（来源：%s）" % os.path.basename(entry["source"]),
                         path, http,
                         "补 requestBody: multipart/form-data + form schema（文件字段 format: binary）", "P1")

        if http in ("POST", "PUT", "PATCH") and not form_content and entry["hand_bound"] and not entry["approved"]:
            raw = [r for r in entry["readers"] if r != "ReadFormAsync"]
            findings.add("request-body", "%s %s" % (http, path),
                         "源码用 %s 自己把请求体解析成 DTO（读法：%s），OpenAPI 导出器看不见；客户端只能猜请求体"
                         % (", ".join(sorted(set(entry["hand_bound"]))), ", ".join(sorted(set(raw))) or "外部 Reader 类"),
                         path, http,
                         "改成 [FromBody] 参数（自动导出），或在契约里手写 requestBody；确属无法导出的，在注册处写 "
                         + APPROVED_HAND_WRITTEN + " 并附理由", "P1")

        # 契约声明了 requestBody，且源码 record 字段数与契约属性数不一致 → 可能漏导出字段
        for media in sorted(form_content):
            ref_name = schema_ref_name((form_content[media] or {}).get("schema") or {})
            if ref_name and ref_name in record_fields:
                props = len(((doc["components"]["schemas"].get(ref_name) or {}).get("properties") or {}))
                if props != record_fields[ref_name]:
                    findings.add("request-body", "%s %s" % (http, path),
                                 "契约 schema %s 有 %d 个属性，源码 record 有 %d 个字段（可能漏导出字段）"
                                 % (ref_name, props, record_fields[ref_name]),
                                 path, http, "核对源 DTO 与契约 schema 的字段集合", "P1")


# ---------------------------------------------------------------------------
# 响应封装一致性（不是 API 封装的裸对象；文件流 / 协议面除外）
# ---------------------------------------------------------------------------
NON_JSON_MEDIA = ("application/octet-stream", "text/calendar", "image/png", "text/event-stream",
                  "text/html", "text/plain", "application/zip", "application/json-seq")


def audit_is_api_response(doc, findings):
    for method, path, op in iter_operations(doc):
        if path in PROTOCOL_PATHS or path in INFRA_PATHS:
            continue
        for status, schema in response_schemas(op):
            if not status.startswith("2") or schema is None:
                continue
            content = (op["responses"][status] or {}).get("content") or {}
            if any(media in content for media in NON_JSON_MEDIA):
                continue
            name, shape = schema_shape(doc, schema)
            if "properties" not in shape or shape.get("type") not in (None, "object"):
                continue  # 原始值 / 数组 / 文件
            if looks_like_api_response(doc, schema):
                continue
            findings.add("is-api-response", "%s %s" % (method, path),
                         "%s 响应是裸对象 %s，其它端点都是 {code,message,data,timestamp} 封装" % (status, name or "<inline>"),
                         path, method,
                         "统一封装（或在 description 写明该端点刻意不封装，并说明原因）")


# ---------------------------------------------------------------------------
# 输出
# ---------------------------------------------------------------------------
CATEGORY_TITLES = [
    # 每个 category 只归属一个分组（集合顺序不参与渲染顺序，避免「同输入不同输出」）。
    ("P0", "P0 · 会让存量客户端直接坏掉", ("drift-removed",)),
    ("P1", "P1 · 客户端会猜错或写不出来",
     ("op-meta", "security", "request-body", "multipart", "binary-form", "is-api-response",
      "error-semantics", "no-5xx")),
    ("P2", "P2 · 会让客户端开发者反复问人",
     ("query-param", "pascal-query", "enum-typed", "required-nullable", "path-required",
      "response-header", "biz-errors")),
    ("P2b", "P2 · 过期批准（应删除的 approvedGaps）", ("stale-approval",)),
    ("P3", "P3 · 文档质量", ("schema-desc", "prop-desc")),
]

SUGGESTIONS = {
    "op-meta": "在 endpoint 上写 .WithName(...) 与 .WithSummary(...)（或 XML 注释），导出时带进 operationId/summary",
    "security": "显式 .RequireAuthorization() 并让导出器输出 security；公开端点登记到 docs/contract-baseline.json publicPaths",
    "query-param": "给查询参数绑定模型加默认值与 [Description]，或用 [AsParameters] 前先拆成 camelCase 显式参数",
    "pascal-query": "改用 camelCase 显式参数，或 [FromQuery(Name=\"rangeStartUtc\")] 固定 wire 名",
    "enum-typed": "给字段加 enum（能导出成 enum 的优先），否则在 description 里列出取值",
    "request-body": "补 requestBody（或写 description 说明没有请求体）",
    "multipart": "补 multipart/form-data 的 requestBody 与 form schema",
    "binary-form": "文件字段写 format: binary",
    "error-semantics": "为 4xx/5xx 声明错误体 schema，并在 description 里列出业务码",
    "no-5xx": "声明 500/502/503 及 schema",
    "is-api-response": "统一用 ApiResponse<T> 封装",
    "response-header": "在响应上声明 headers",
    "biz-errors": "登记到 docs/business-error-codes.md",
    "required-nullable": "required 与 nullable 只保留一个",
    "schema-desc": "给类型加 <summary>",
    "prop-desc": "给属性加 <summary>",
    "drift-removed": "恢复端点，或在变更说明里宣告下线与替代端点",
    "drift-added": "在变更说明里宣告新端点",
    "drift-schema": "在变更说明里宣告字段增删",
    "stale-approval": "删除对应的 approvedGaps 条目",
}


def render_text(result, max_per_category=0):
    out = []
    out.append("== 契约缺口审计 ==")
    out.append("契约    : %s" % result["contract"])
    if result.get("baseline"):
        out.append("基线    : %s" % result["baseline"])
    out.append("operation: %d    schema: %d" % (result["totals"]["operations"], result["totals"]["schemas"]))
    out.append("findings: %d" % len(result["findings"]))
    out.append("")
    by_cat = {}
    for f in result["findings"]:
        by_cat.setdefault(f["category"], []).append(f)
    rendered = set()
    for _, title, cats in CATEGORY_TITLES:
        present = [c for c in cats if by_cat.get(c)]
        if not present:
            continue
        out.append("## " + title)
        for cat in present:
            items = by_cat[cat]
            out.append("")
            approved_n = sum(1 for f in items if f.get("approved"))
            suffix = "，已批准 %d" % approved_n if approved_n else ""
            out.append("### [%s] %s（%d 条%s）" % (cat, SUGGESTIONS.get(cat, ""), len(items), suffix))
            shown = items[:max_per_category] if max_per_category else items
            for f in shown:
                rendered.add(cat)
                mark = " [已批准]" if f.get("approved") else ""
                out.append("  - %s — %s%s" % (f["label"], f["detail"], mark))
            if max_per_category and len(items) > max_per_category:
                out.append("  - …… 其余 %d 条见 JSON（--json）或 --max-per-category 0" % (len(items) - max_per_category))
        out.append("")
    leftover = [c for c in sorted(by_cat) if c not in rendered]
    if leftover:
        out.append("## 其它")
        for cat in leftover:
            for f in by_cat[cat]:
                out.append("  - [%s] %s — %s" % (cat, f["label"], f["detail"]))
        out.append("")
    out.append("## 计数")
    for cat in sorted(by_cat):
        out.append("  %-16s %5d" % (cat, len(by_cat[cat])))
    out.append("")
    return "\n".join(out)


def main(argv):
    ap = argparse.ArgumentParser(description="审计 OpenAPI 契约对客户端开发者的信息缺口（只读、离线）")
    ap.add_argument("--contract", default=os.path.join(REPO_ROOT, "contract", "openapi.json"))
    ap.add_argument("--baseline", help="基线契约；给出时做 operation/schema 差异")
    ap.add_argument("--source-root", help="后端源码根目录；给出时核对 FromForm/BindAsync/响应头")
    ap.add_argument("--biz-errors", default=DEFAULT_BIZ_ERRORS, help="业务错误码表（Markdown）")
    ap.add_argument("--config", default=None,
                    help="审计基线（publicPaths / excludePaths / approvedGaps / operations）；缺省用仓内 docs/contract-baseline.json")
    ap.add_argument("--json", action="store_true", help="输出 JSON 而不是人读报告")
    ap.add_argument("--record-operations", action="store_true",
                    help="把当前契约的操作集写进基线的 operations（人工确认后用于发布新的契约基线）")
    ap.add_argument("--fail-on", action="store_true", help="门禁模式：存在发现则退出 1")
    ap.add_argument("--max-per-category", type=int, default=0,
                    help="每个类别人读报告最多列几条（0=全部；JSON 输出不受影响）")
    ap.add_argument("--strict", action="store_true", help="配合 --fail-on：P0/P1/P2/P3 全部算失败（默认只算 P0/P1）")
    args = ap.parse_args(argv)

    doc = load_contract(args.contract)
    if args.record_operations:
        ops = sorted("%s %s" % (m, p) for m, p, _ in iter_operations(doc))
        record_path = args.config or DEFAULT_BASELINE
        if not record_path:
            die_input("--record-operations 需要 --config（或仓内 docs/contract-baseline.json）")
        raw = load_json(record_path, "审计基线") if os.path.isfile(record_path) else {}
        raw["operations"] = ops
        with open(args.config, "w", encoding="utf-8", newline="\n") as fh:
            json.dump(raw, fh, ensure_ascii=False, indent=2)
            fh.write("\n")
        print("已记录 %d 个 operation 到 %s" % (len(ops), record_path))
        return EXIT_OK
    # 基线只在**显式**给出（或给了 --source-root 时用仓库默认基线）时生效：
    #   * 门禁脚本与 CI 始终显式传 --config，所以已发布操作集一定会参与删除检测（review I-2）；
    #   * 对任意第三方契约做一次性审计（例如 fixture）时不会被仓库基线误伤。
    config_path = args.config
    if config_path is None and args.source_root and os.path.isfile(DEFAULT_BASELINE):
        config_path = DEFAULT_BASELINE
    cfg = load_baseline_config(config_path) if config_path else {"publicPaths": [], "excludePaths": [],
                                                                 "approvedGaps": {}, "operations": []}
    excluded = set(cfg["excludePaths"])
    findings = Findings()

    audit_operations(doc, findings, cfg)
    audit_schemas(doc, findings)
    audit_biz_codes(doc, findings, args.biz_errors)
    audit_is_api_response(doc, findings)
    audit_removed_vs_baseline_set(doc, cfg["operations"], findings)
    if args.baseline:
        audit_drift(doc, load_contract(args.baseline), findings)
    if args.source_root:
        if not os.path.isdir(args.source_root):
            die_input("源码根目录不存在：%s" % args.source_root)
        audit_sources(doc, findings, args.source_root)

    if excluded:
        findings.items = [f for f in findings.items if f["path"] not in excluded]
    approved = set(cfg["approvedGaps"])
    for f in findings.items:
        f["approved"] = f["category"] in approved
    # 过期批准（review r3）：已登记为「已批准」但已经没有任何发现的类别，必须从 approvedGaps 删掉。
    # 否则以后这类问题再次出现时会继续被静默放行。作为 P2 发现输出（不阻断，但可见）。
    present = {f["category"] for f in findings.items}
    # 只有**真正跑过**的检查才能判定「批准过期」：源码侧类别在没给 --source-root 时不会产生发现，
    # 不能据此说批准过期（否则单独审计契约时会误报）。
    source_side = {"multipart", "response-header", "drift-removed"}
    if not args.source_root:
        present |= source_side
    stale = sorted(approved - present)
    for category in stale:
        findings.items.append({
            "category": "stale-approval",
            "label": category,
            "method": "",
            "path": "",
            "detail": "approvedGaps 里登记了 %s，但当前没有任何该类发现：请删除这条批准（否则该类问题以后会被静默放行）" % category,
            "suggestion": "从 docs/contract-baseline.json 的 approvedGaps 删除该条目",
            "severity": "P2",
            "approved": False,
        })

    # 排序键必须覆盖渲染用到的全部字段，否则同键项的先后随 set/dict 迭代顺序浮动（会出现「同输入不同输出」）
    findings.items.sort(key=lambda f: (f["category"], MODE_ORDER.get(f["severity"], 9), f["path"],
                                       f["method"], f["label"], f["detail"], f["suggestion"]))
    counts = {}
    for f in findings.items:
        counts[f["category"]] = counts.get(f["category"], 0) + 1
    result = {
        "contract": _rel(args.contract),
        "baseline": _rel(args.baseline) if args.baseline else None,
        "totals": {
            "operations": sum(1 for _ in iter_operations(doc)),
            "schemas": len(((doc.get("components") or {}).get("schemas") or {})),
        },
        "counts": counts,
        "findings": findings.items,
    }

    if args.json:
        print(json.dumps(result, ensure_ascii=False, indent=2))
    else:
        print(render_text(result, args.max_per_category))

    if args.fail_on:
        blocking = {"P0", "P1", "P2", "P3"} if args.strict else {"P0", "P1"}
        bad = [f for f in findings.items if f["severity"] in blocking and not f["approved"]]
        if bad:
            print("", file=sys.stderr)
            print("门禁：%d 条未批准的 %s 发现（已批准 %d 类，见 docs/contract-baseline.json approvedGaps）"
                  % (len(bad), "/".join(sorted(blocking)), len(approved)), file=sys.stderr)
            return EXIT_FINDINGS
    return EXIT_OK


def _rel(path):
    try:
        return os.path.relpath(path, REPO_ROOT).replace("\\", "/")
    except ValueError:
        return path


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
    if sys.version_info < (3, 8):
        print("需要 python 3.8+", file=sys.stderr)
        sys.exit(EXIT_ENV)
    sys.exit(main(sys.argv[1:]))
