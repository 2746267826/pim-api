#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 pim-api 源码提取业务错误码，生成/刷新 docs/business-error-codes.md 的「自动采集」段。

业务错误码是 `ApiResponse.code` 的取值（`0` 表示成功）。HTTP 状态优先由**调用点显式状态**决定
（`Results.Conflict/NotFound`、`Results.Json(..., statusCode:)`、`Response.StatusCode`、
`WriteDomainLikeErrorAsync` 的状态实参），否则回落到 `ExceptionMiddleware.ResolveDomainStatusCode`
的映射（默认 400）。客户端要按码分支，就必须有一张
「码 → HTTP 状态 → 含义 → 客户端该做什么」的表 —— OpenAPI 本身表达不了这层语义，
所以它作为契约的补充文档随仓维护，并由 `audit-contract-gaps.py --biz-errors` 交叉校验。

做法：
  1. 扫描 *.cs，抓 `DomainException(<码>, "<消息>")`、`ApiResponse<...>.Error(<码>, "<消息>")`、
     `ApiResponse.Error(<码>, "<消息>")` 三类写法；
  2. 从 `ResolveDomainStatusCode` 的 switch 表达式读出「码 → HTTP 状态」映射（默认 400）；
  3. 按码归并：HTTP 状态、触发文件、消息样例（去重、最多 3 条）；
  4. 只重写 `<!-- BEGIN GENERATED -->` 与 `<!-- END GENERATED -->` 之间的内容，
     表头、人工维护的「客户端建议动作」列与「约定」段落保持一致。

本脚本**不联网**，只用 python3 标准库；它写仓，所以不放进 CI 门禁（门禁只跑只读的审计器）。
人工核对后用 `--check` 判断文档是否落后于源码。

用法：
  python3 scripts/ci/extract-business-error-codes.py --audit-config docs/contract-baseline.json   # 同时刷新审计基线
  python3 scripts/ci/extract-business-error-codes.py --check    # 只读：落后则退出 1
退出码：0 一致/已写入；1 --check 发现落后；2 环境错误；4 输入非法。
"""

import argparse
import json
import os
import re
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(os.path.dirname(HERE))
DOC = os.path.join(REPO_ROOT, "docs", "business-error-codes.md")
BEGIN = "<!-- BEGIN GENERATED: 由 scripts/ci/extract-business-error-codes.py 生成，请勿手工编辑 -->"
END = "<!-- END GENERATED -->"

# 客户端建议动作：按码段归类（人工维护的约定，写进文档说明段）。
ADVICE_BY_STATUS = {
    400: "展示 message；多数是请求形状/参数问题，检查客户端组装",
    401: "刷新令牌后重试一次；刷新失败则回登录页",
    403: "无权限：不要重试；提示用户或联系管理员",
    404: "目标不存在或已被删除：刷新列表，不要重试同一请求",
    409: "状态冲突（重复/并发/版本过期）：重新拉取最新数据后再操作",
    429: "限流：按 Retry-After 退避后重试",
    502: "上游（云盘/微软）失败：稍后重试，可展示为三方服务异常",
    503: "服务不可用/功能被关闭：不要重试，展示配置提示",
    500: "服务端未预期错误：可重试一次，仍失败则上报",
}

# `const int Foo = 码;` + `throw new DomainException(Foo, "...")`：码走常量时也要能采集。
# 位数 3–5：本仓同时有 3 位（400 / 503）、4 位（2057，源码常写 `02057`）与 5 位（40902 / 40010）码。
CONST_CODE_RE = re.compile(r"const\s+int\s+(\w+)\s*=\s*(\d{3,5})\s*;")
# 类名（用于把 `PublicRegistrationPolicy.ClosedErrorCode` 解析成码）。
CLASS_RE = re.compile(r"\b(?:class|record|struct)\s+(\w+)")
# 所有把码交给客户端的调用点。消息形态不参与匹配 —— 消息可能是字面量、插值、变量，
# 甚至第一个参数就是常量（review P1：只匹配 `(<数字>, "<字面量>")` 会漏掉 20 个码）。
#
# 第四种形态是「把码装进错误对象再返回」：`return Results.BadRequest(new CredentialValidationError(40007, "…"))`。
# 这类码既不经过 `ApiResponse.Error`，也不是抛出的 `DomainException`，此前只能靠「常量名带 Code」的
# 启发式兜到一部分（review P1-1）。
#
# 只收 `…Error`，**不收** `…Exception`：本仓的 `new XxxException(...)` 第一实参是消息、HTTP 状态
# （`GoogleDriveException(200, …)`）或上游错误码（`ProviderTransientException(429, …)`），
# 都不是业务码；唯一携带业务码的异常是 `DomainException`，已单独列出。
CALL_RE = re.compile(
    r"(?P<call>DomainException|ApiResponse(?:<[^>]*>)?\.Error|WriteDomainLikeErrorAsync"
    r"|new\s+\w*Error)\s*\("
)
# 匿名声明的错误体：`Results.Json(new { code = 40001, message = "…" }, statusCode: 400)`。
# 过程间转发（review P1-1）：helper 把调用点传入的 int 参数原样交给错误调用，例如
# `ValidateJson(json, 3006, "…")` → helper 内 `throw new DomainException(errorCode, message)`。
# 语句内匹配只看「错误调用本身」，安全网也只认错误调用，于是这类码两边同时漏掉。
HELPER_HEAD_RE = re.compile(
    r"(?:(?:public|private|internal|protected)\s+)?"
    r"(?:static\s+|async\s+|sealed\s+|override\s+|partial\s+)*"
    r"[\w<>\[\],\.\?]+\s+(\w+)\s*\(([^)]*)\)\s*(?:where\s+[^{]+)?\{")
FORWARDED_CALL_RE = re.compile(
    r"(?:DomainException|ApiResponse(?:<[^>]*>)?\.Error|WriteDomainLikeErrorAsync)"
    r"\s*\(\s*(\w+)\b")
INT_PARAM_RE = re.compile(r"\bint\??\s+(\w+)")
ANON_CODE_RE = re.compile(r"new\s*\{[^{}]*?\bcode\s*=\s*([^,}]+)")
# 直接拼 JSON 的错误体：`"{\"code\":40501,…}"` / `@"{ ""code"": 40501 }"`。
# 引号在源码里可能是 `\"`、`""` 或裸 `"`，所以两侧都允许 0–2 个引号/转义。
JSON_CODE_RE = re.compile(r'\\?"{1,2}code\\?"{1,2}\s*:\s*(\d{3,5})')
STRING_CONST_RE = re.compile(r'const\s+string\s+(\w+)\s*=\s*"((?:[^"\\]|\\.)*)"\s*;')
# 调用点的 HTTP 状态：显式 `Results.NotFound(...)` / `Results.Conflict(...)`、
# `Results.Json(..., statusCode: 503)`、以及 `Response.StatusCode = 500`。
RESULTS_STATUS = (
    # 顺序即优先级：同一语句里出现多个时取最先匹配的那个（`BadRequest` 必须在内，
    # 它是本仓最常见的 4xx 包装，漏掉会让所有参数类错误码被记成 400 以外的状态，review P1-3）。
    (re.compile(r"Results\s*\.\s*Conflict\s*\("), 409),
    (re.compile(r"Results\s*\.\s*NotFound\s*\("), 404),
    (re.compile(r"Results\s*\.\s*BadRequest\s*\("), 400),
    (re.compile(r"Results\s*\.\s*Unauthorized\s*\("), 401),
    (re.compile(r"Results\s*\.\s*Forbid\s*\("), 403),
)
RESPONSE_STATUS_RE = re.compile(r"Response\s*\.\s*StatusCode\s*=\s*(?:StatusCodes\s*\.\s*)?Status(\d{3})\w*")
JSON_STATUS_RE = re.compile(r"statusCode\s*:\s*(?:StatusCodes\s*\.\s*)?Status(\d{3})\w*")
JSON_STATUS_NUM_RE = re.compile(r"statusCode\s*:\s*(\d{3})\b")
FIRST_ARG_LIMIT = 200
MAP_RE = re.compile(r"ResolveDomainStatusCode[\s\S]{0,400}?=>\s*errorCode\s*switch\s*\{([\s\S]*?)\};")
STATUS_NAMES = {
    "Status404NotFound": 404, "Status401Unauthorized": 401, "Status403Forbidden": 403,
    "Status429TooManyRequests": 429, "Status503ServiceUnavailable": 503,
    "Status409Conflict": 409, "Status502BadGateway": 502, "Status500InternalServerError": 500,
}


def iter_cs(root):
    for dirpath, dirnames, filenames in os.walk(root):
        dirnames[:] = sorted(d for d in dirnames if d not in ("bin", "obj", "node_modules", ".git"))
        for fn in sorted(filenames):
            if fn.endswith(".cs"):
                yield os.path.join(dirpath, fn)


def parse_status_map(text):
    """解析 `errorCode switch { 40401 or 4004 => StatusCodes.Status404NotFound, ... _ => StatusCodes.Status400BadRequest }`。

    按「分支行」解析而不是按 `=>` 切分：分支以逗号结尾，切分会把下一行的码吃进上一分支。
    """
    m = MAP_RE.search(text)
    if not m:
        return None
    mapping = {}
    for raw in m.group(1).splitlines():
        line = raw.strip().rstrip(",")
        if "=>" not in line:
            continue
        left, right = line.split("=>", 1)
        codes = [int(c) for c in re.findall(r"(?<!\d)(\d{3,5})(?!\d)", left)]
        nums = re.findall(r"Status(?:_?)(\d{3})", right)
        named = [v for k, v in STATUS_NAMES.items() if k in right]
        status = int(nums[0]) if nums else (named[0] if named else None)
        if status is None:
            continue
        for code in codes:
            mapping[code] = status
    return mapping



DEFERRED_ANON_RE = re.compile(r"app\.Map(?:Get|Post|Put|Patch|Delete)\(\s*\"([^\"]*)\"[\s\S]{0,600}?\.AllowAnonymous\(\)")
GROUP_DEF_RE = re.compile(r"(\w+)\s*=\s*\w+\.MapGroup\(\s*\"([^\"]*)\"")


CONST_RE = re.compile(r'const\s+string\s+(\w+)\s*=\s*(.+?);')
IDENT_RE = re.compile(r"[A-Za-z_][\w.]*")
OTHER_ENDPOINT_RE = re.compile(
    r"(?:\w+\s*\.\s*)?Map(?:HealthChecks|Methods|FallbackToFile|Fallback)\(\s*(\"[^\"]*\"|\$\"[^\"]*\"|[A-Za-z_][\w.]*)")


RECEIVER_RE = re.compile(r"(\w+)\s*\n?\s*\.?\s*Map(?:Get|Post|Put|Patch|Delete)\(\s*$")


def _eval_path_expr(expr, consts):
    """把 `FileEndpointPaths.GoogleCallback` / `"/api/client/shell/latest"` 求值成全路径；求不出来返回 None。"""
    expr = expr.strip()
    if expr.startswith('"'):
        lit = re.match(r'"([^"]*)"', expr)
        return lit.group(1) if lit else None
    if expr.startswith("$"):
        out, pos = [], 0
        for m in re.finditer(r"\{(\w+)\}", expr):
            out.append(expr[pos:m.start()])
            value = _eval_ident(m.group(1), consts)
            if value is None:
                return None
            out.append(value)
            pos = m.end()
        out.append(expr[pos:])
        return "".join(out).strip('"')
    return _eval_ident(expr, consts)


def _eval_ident(name, consts):
    if name in consts:
        return _eval_path_expr(consts[name], consts)
    short = name.split(".")[-1]
    return _eval_path_expr(consts[short], consts) if short in consts else None


def public_paths_from_source(source_root):
    """源码里**显式** `.AllowAnonymous()` 的端点 → 审计器的公开白名单。

    - 路径表达式按文件内 `const string` 求值（支持 `$"{Root}/x"` 与 `FileEndpointPaths.Foo`）；
    - `xxx.MapGroup("/prefix")` 定义的 group，其 `.AllowAnonymous()` 端点拼回前缀；
    - 只认显式标注：既没 RequireAuthorization 也没 AllowAnonymous 的端点属于「没声明」，**不算公开**。
    """
    import re as _re  # 局部别名，避免与上面的模块级常量混淆
    found = set()
    for path in iter_cs(source_root):
        try:
            text = open(path, "r", encoding="utf-8-sig").read()
        except (OSError, UnicodeDecodeError):
            continue
        consts = {m.group(1): m.group(2) for m in CONST_RE.finditer(text)}
        groups = dict(GROUP_DEF_RE.findall(text))
        # 公开端点 = 显式 .AllowAnonymous() 的端点。
        #
        # 判定方式（review I-1 后重写）：按**源码顺序**把每个「注册调用」与它之后的链式调用
        # `.AllowAnonymous()` 配对 —— 一个注册调用的「作用范围」到下一个注册调用开始为止。
        # 不按「往回 N 字符」也不按「往上找语句」，因此：
        #   * 上一条语句的 MapGet 不会被下一条 .AllowAnonymous() 借用；
        #   * lambda 体里的 `.AllowAnonymous()`（极少见）也会归到它自己的注册调用；
        #   * 同文件后面的`MapGroup` 不会影响前面端点的前缀。
        registrations = []
        for reg in _re.finditer(
                r"(\w+)\s*\.\s*Map(Get|Post|Put|Patch|Delete|HealthChecks|Methods|FallbackToFile|Fallback)\(\s*([^,)]*)",
                text):
            registrations.append((reg.start(), reg.group(1), reg.group(2), reg.group(3)))
        if not registrations:
            continue
        for m in _re.finditer(r"\.AllowAnonymous\(\)", text):
            owner = None
            for reg in registrations:
                if reg[0] < m.start():
                    owner = reg
                else:
                    break
            if owner is None:
                continue
            _, receiver, _, expr = owner
            prefix = groups.get(receiver, "")
            target = _eval_path_expr(expr, consts)
            if target is None:
                continue
            # 与 audit-contract-gaps 同一规则（review r3 1.2）：只有接收者确实是 group、且路径还没有
            # 带上该前缀时才拼接。中间夹了别的语句也不会把下一条注册算到旧 group 头上。
            if prefix and target.startswith(prefix.rstrip("/") + "/"):
                combined = target
            elif prefix:
                combined = prefix.rstrip("/") + "/" + target.lstrip("/")
            else:
                combined = target
            if combined.endswith(".html"):
                continue  # SPA fallback 不是 API 端点
            normalized = _re.sub(r"\{(\*{1,2})?[^}]*\}", "{}", combined)
            # 形态化之后的兜底路由（例如 `/api/{path}` → `/api/{}`）与没有前缀的散段不是真实端点，
            # 不进白名单（review M-3）。
            if normalized.startswith("/api/{}") or not normalized.startswith("/"):
                continue
            found.add(normalized)

    return sorted(found)


def read_source(path):
    """读取源码。极少数 `.Designer.cs` 不是 UTF-8；静默跳过会让这些文件里的码永久漏采，
    所以退回 latin-1（ASCII 部分不变，足够做码/调用点扫描）。"""
    try:
        with open(path, "r", encoding="utf-8-sig") as fh:
            return fh.read()
    except UnicodeDecodeError:
        with open(path, "r", encoding="latin-1") as fh:
            return fh.read()


def scan_const_codes(text):
    """收集 `const int Name = 码;`，同时给出「类名.Name」与无歧义的裸名两种解析表。"""
    classes = [(m.start(), m.group(1)) for m in CLASS_RE.finditer(text)]
    qualified, bare = {}, {}
    for m in CONST_CODE_RE.finditer(text):
        name, value = m.group(1), int(m.group(2))
        owner = None
        for pos, class_name in classes:
            if pos < m.start():
                owner = class_name
            else:
                break
        if owner:
            qualified["%s.%s" % (owner, name)] = value
        bare.setdefault(name, set()).add(value)
    return qualified, bare


def scan_string_consts(text):
    classes = [(m.start(), m.group(1)) for m in CLASS_RE.finditer(text)]
    qualified, bare = {}, {}
    for m in STRING_CONST_RE.finditer(text):
        name, value = m.group(1), m.group(2)
        owner = None
        for pos, class_name in classes:
            if pos < m.start():
                owner = class_name
            else:
                break
        if owner:
            qualified["%s.%s" % (owner, name)] = value
        bare.setdefault(name, set()).add(value)
    return qualified, bare


def skip_cs_string(text, i, limit):
    """`i` 指向 `$`/`@`/`"` 时返回字符串字面量之后的索引。

    插值字符串里的 `{...}` 是表达式，表达式内部的字符串（`{string.Join(", ", xs)}`）
    会被整体跳过，因此内部引号/逗号不会提前结束外层字面量。
    """
    j = i
    if j < limit and text[j] == "$":
        j += 1
    verbatim = j < limit and text[j] == "@"
    if verbatim:
        j += 1
    if j >= limit or text[j] != '"':
        return i + 1
    j += 1
    depth = 0
    while j < limit:
        ch = text[j]
        if not verbatim and ch == "\\":
            j += 2
            continue
        if ch == '"':
            if depth == 0:
                return j + 1
            j = skip_cs_string(text, j, limit)  # 表达式里的嵌套字符串
            continue
        if ch == "{":
            depth += 1
        elif ch == "}" and depth > 0:
            depth -= 1
        j += 1
    return limit


def literal_message(expr):
    m = re.match(r'\$?@?"', expr)
    if not m:
        return None
    quote = m.end() - 1
    end = skip_cs_string(expr, quote, len(expr))
    if end <= quote + 1 or expr[end - 1] != '"':
        return expr[quote + 1:]
    return expr[quote + 1:end - 1]


def resolve_code(first_arg, qualified, bare):
    token = first_arg.strip().strip("()").strip()
    if re.fullmatch(r"\d{3,5}", token):
        return int(token)
    if re.fullmatch(r"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*", token):
        if token in qualified:
            return qualified[token]
        short = token.rsplit(".", 1)[-1]
        if short in bare:
            value = bare[short]
            if isinstance(value, set):
                return next(iter(value)) if len(value) == 1 else None
            return value
    return None


def first_argument(text, start):
    """从 `(` 之后取出**第一个实参**（按括号/字符串深度切分），最多看 200 字符。"""
    depth, i, limit = 0, start, min(len(text), start + FIRST_ARG_LIMIT)
    while i < limit:
        ch = text[i]
        if ch == '"' or (ch in "$@" and text[i + 1:i + 2] == '"'):
            i = skip_cs_string(text, i, limit)
            # skip_cs_string 已经返回闭引号之后的位置；再走一次 `i += 1` 会跳过紧跟的逗号，
            # 实参以字符串开头时（`ValidateJson("{}", 3006, …)`）整个切分会错位。
            continue
        elif ch in "([{":
            depth += 1
        elif ch in ")]}":
            if depth == 0:
                return text[start:i]
            depth -= 1
        elif ch == "," and depth == 0:
            return text[start:i]
        i += 1
    return text[start:limit]


def nth_argument(text, start, index):
    pos = start
    for _ in range(index):
        arg = first_argument(text, pos)
        pos += len(arg)
        while pos < len(text) and text[pos] in " \t\r\n":
            pos += 1
        if pos < len(text) and text[pos] == ",":
            pos += 1
        else:
            return ""
    return first_argument(text, pos)


def split_parameters(text):
    """按顶层逗号切参数表（跳过字符串与嵌套括号）。"""
    params, depth, start, i = [], 0, 0, 0
    while i < len(text):
        ch = text[i]
        if ch == '"':
            i = skip_cs_string(text, i, len(text))
            continue
        if ch in "([{":
            depth += 1
        elif ch in ")]}":
            depth -= 1
        elif ch == "," and depth == 0:
            params.append(text[start:i])
            start = i + 1
        i += 1
    if text[start:].strip():
        params.append(text[start:])
    return params


def block_after(text, brace):
    """`brace` 指向 `{`；返回配对 `}` 之前的文本（跳过字符串）。"""
    depth, i = 0, brace
    while i < len(text):
        ch = text[i]
        if ch == '"':
            i = skip_cs_string(text, i, len(text))
            continue
        if ch == "{":
            depth += 1
        elif ch == "}":
            depth -= 1
            if depth == 0:
                return text[brace + 1:i]
        i += 1
    return text[brace + 1:]


def forwarding_helpers(text):
    """{helper 名: 码参数下标}：方法把某个 int 参数原样传给错误调用（review P1-1）。

    `WriteDomainLikeErrorAsync` 自带 HTTP 状态参数，它的调用点会由 CALL_RE 支路按
    第二个实参取状态；再按「缺省 400」走一遍会把 502 之类的码记成 400，所以排除。
    """
    helpers = {}
    for match in HELPER_HEAD_RE.finditer(text):
        if match.group(1) == "WriteDomainLikeErrorAsync":
            continue
        body = block_after(text, match.end() - 1)
        forwarded = {m.group(1) for m in FORWARDED_CALL_RE.finditer(body)}
        if not forwarded:
            continue
        for index, param in enumerate(split_parameters(match.group(2))):
            int_param = INT_PARAM_RE.search(param)
            if int_param and int_param.group(1) in forwarded:
                helpers.setdefault(match.group(1), index)
                break
    return helpers


def code_argument_index(call_shape):
    """`WriteDomainLikeErrorAsync(context, status, code, message)` 的码在第三个实参；
    其余构造器/helper 的码都是第一个实参。"""
    return 2 if call_shape == "WriteDomainLikeErrorAsync" else 0


def message_after(text, call_end, code_index, string_consts):
    """码后面的实参若是字符串字面量/插值字符串/字符串常量就取其内容，否则如实说明是变量。"""
    second = nth_argument(text, call_end, code_index + 1).strip()
    literal = literal_message(second)
    if literal is not None:
        return literal
    qualified, bare = string_consts
    token = second.strip("()")
    if re.fullmatch(r"[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*", token):
        if token in qualified:
            return qualified[token]
        short = token.rsplit(".", 1)[-1]
        if short in bare and len(bare[short]) == 1:
            return next(iter(bare[short]))
    if second:
        return "（消息为变量或表达式，见源码）"
    return ""


def helper_status(text, call_end):
    """`WriteDomainLikeErrorAsync(context, status, code, message)` 的第二个实参就是状态。"""
    arg = nth_argument(text, call_end, 1)
    m = re.search(r"Status(?:Codes\s*\.\s*)?Status(\d{3})", arg) or re.search(r"^\s*(\d{3})\s*$", arg)
    return int(m.group(1)) if m else None


def statement_tail(text, call_start, window=400):
    """调用点所在的**单个语句/分支**：从最近的 `;`、`=>`、`{`、`}` 之后开始。

    只看 `;` 会把整个 switch 表达式当成一条语句：`x => Results.NotFound(…)` 与
    `y => Results.Conflict(…)` 在同一个 `return` 里，先匹配到的 `Conflict` 会把 NotFound 分支
    也记成 409（review P1-3）。按最近的 `=>`/花括号切，每个分支各自推断。
    """
    before = text[max(0, call_start - window):call_start]
    cut = 0
    for sep in (";", "=>", "{", "}"):
        idx = before.rfind(sep)
        if idx != -1:
            cut = max(cut, idx + len(sep))
    return before[cut:]


def status_for_call(text, call_start, mapping, code):
    """调用点上下文决定 HTTP 状态：显式 `Response.StatusCode = 500` 优先，
    其次是 `Results.Conflict/NotFound/BadRequest/Unauthorized/Forbid` 与 `Results.Json(..., statusCode:)`，
    都没有才回落到 `ResolveDomainStatusCode` 的映射（默认 400）。"""
    before = text[max(0, call_start - 400):call_start]
    explicit = None
    for m in RESPONSE_STATUS_RE.finditer(before):
        explicit = int(m.group(1))  # 取最近一次赋值
    if explicit is not None:
        return explicit

    statement = statement_tail(text, call_start)
    for pattern, status in RESULTS_STATUS:
        if pattern.search(statement):
            return status
    if re.search(r"Results\s*\.\s*Json\s*\(", statement):
        after = text[call_start:call_start + 400].split(";", 1)[0]
        m = JSON_STATUS_RE.search(after) or JSON_STATUS_NUM_RE.search(after)
        if m:
            return int(m.group(1))
    return mapping.get(code, 400)


def collect(source_root):
    mapping = {}
    entries = {}
    sources = []
    qualified, bare_sets = {}, {}
    string_qualified, string_bare_sets = {}, {}

    for path in iter_cs(source_root):
        try:
            text = read_source(path)
        except OSError:
            continue
        sources.append((path, text))
        if mapping == {} and "ResolveDomainStatusCode" in text:
            found = parse_status_map(text)
            if found:
                mapping = found
        file_qualified, file_bare = scan_const_codes(text)
        qualified.update(file_qualified)
        for name, values in file_bare.items():
            bare_sets.setdefault(name, set()).update(values)
        file_str_qualified, file_str_bare = scan_string_consts(text)
        string_qualified.update(file_str_qualified)
        for name, values in file_str_bare.items():
            string_bare_sets.setdefault(name, set()).update(values)
    # 只在全仓唯一时才按裸名解析：同名常量指向不同码时宁可漏采，也不猜。
    bare = {name: next(iter(values)) for name, values in bare_sets.items() if len(values) == 1}
    string_bare = {name: next(iter(values)) for name, values in string_bare_sets.items() if len(values) == 1}
    string_consts = (string_qualified, string_bare)
    # helper 定义与调用点通常不在同一个文件：先全仓收集，再逐个文件找调用点。
    forwarding = {}
    for _path, source in sources:
        for name, index in forwarding_helpers(source).items():
            forwarding.setdefault(name, index)

    for path, text in sources:
        rel = os.path.relpath(path, REPO_ROOT).replace("\\", "/")
        # 保留源码里的原始写法（含前导零）：契约说明文字里常用 `02057` 这种给人读的写法。
        written = {int(value): value for _name, value in CONST_CODE_RE.findall(text)}
        for match in CALL_RE.finditer(text):
            index = code_argument_index(match.group("call"))
            code_arg = nth_argument(text, match.end(), index)
            code = resolve_code(code_arg, qualified, bare)
            if code is None:
                continue
            entry = entries.setdefault(
                code, {"messages": [], "files": set(), "written": set(), "statuses": set()})
            # 数字字面量按源码原样保留（`02057` 与 `2057` 是同一个码，但文档里保持可搜索的写法）；
            # 常量形式的码用它声明处的写法。
            literal_token = code_arg.strip()
            entry["written"].add(
                literal_token if re.fullmatch(r"\d{3,5}", literal_token) else written.get(code, "%d" % code))
            entry["files"].add(rel)
            if match.group("call") == "WriteDomainLikeErrorAsync":
                helper = helper_status(text, match.end())
                if helper is not None:
                    entry["statuses"].add(helper)
                # 解析不出状态（变量/三元表达式）就留空：由其它调用点或下面的默认值补，
                # 不要把「不知道」写成 400。
            else:
                entry["statuses"].add(status_for_call(text, match.start(), mapping, code))
            message = message_after(text, match.end(), index, string_consts)
            if message and message not in entry["messages"]:
                entry["messages"].append(message)

        # 转发 helper 的调用点：码在调用实参里，错误调用出现在 helper 体内（review P1-1）。
        for helper_name, helper_index in forwarding.items():
            for match in re.finditer(r"\b%s\s*\(" % re.escape(helper_name), text):
                code_arg = nth_argument(text, match.end(), helper_index)
                code = resolve_code(code_arg, qualified, bare)
                if code is None:
                    continue
                entry = entries.setdefault(
                    code, {"messages": [], "files": set(), "written": set(), "statuses": set()})
                literal_token = code_arg.strip()
                entry["written"].add(
                    literal_token if re.fullmatch(r"\d{3,5}", literal_token)
                    else written.get(code, "%d" % code))
                entry["files"].add(rel)
                entry["statuses"].add(status_for_call(text, match.start(), mapping, code))
                message = message_after(text, match.end(), helper_index, string_consts)
                if message and message not in entry["messages"]:
                    entry["messages"].append(message)

        # 匿名错误体 `new { code = 40001, … }` 与直接拼的 JSON `"{\"code\":40501,…}"`：
        # 码不经过任何构造函数，CALL_RE 看不见（review P1-1）。`code = 0` 是成功语义，跳过。
        for match in ANON_CODE_RE.finditer(text):
            code = resolve_code(match.group(1), qualified, bare)
            if code is None or code == 0:
                continue
            entry = entries.setdefault(
                code, {"messages": [], "files": set(), "written": set(), "statuses": set()})
            entry["files"].add(rel)
            entry["written"].add("%d" % code)
            entry["statuses"].add(status_for_call(text, match.start(), mapping, code))
        for match in JSON_CODE_RE.finditer(text):
            code = int(match.group(1))
            if code == 0:
                continue
            entry = entries.setdefault(
                code, {"messages": [], "files": set(), "written": set(), "statuses": set()})
            entry["files"].add(rel)
            entry["written"].add(match.group(1))
            entry["statuses"].add(status_for_call(text, match.start(), mapping, code))

        # 只经由常量/DTO 返回、没有被直接传给 Error(...) 的码（例如
        # `const int PasswordTooLongCode = 40007` → `new CredentialValidationError(...)`，
        # 端点再用 `error.Code` 返回）。常量名带 `Code` 才收录，避免把普通数字常量当成业务码。
        for const_match in CONST_CODE_RE.finditer(text):
            name = const_match.group(1)
            # `HttpStatusCode = 404` 这类是 HTTP 状态常量，不是业务码：只按名字收 `…Code`，
            # 并排除名字里带 `Status` 的（3 位数的误收会往码表里塞进一堆状态码）。
            if "Code" not in name or "Status" in name:
                continue
            code = int(const_match.group(2))
            entry = entries.setdefault(
                code, {"messages": [], "files": set(), "written": set(), "statuses": set()})
            entry["written"].add(const_match.group(2))
            entry["files"].add(rel)
            if not entry["statuses"]:
                entry["statuses"].add(mapping.get(code, 400))
            message = entry["messages"][:]  # 已由调用点采集时不再补占位消息
            if not message:
                paired = name.replace("ErrorCode", "Message").replace("Code", "Message")
                text_const = string_qualified.get(paired) or (
                    string_bare.get(paired) if len(string_bare_sets.get(paired, ())) == 1 else None)
                placeholder = text_const or "（经常量/DTO 返回，见源码）"
                if placeholder not in entry["messages"]:
                    entry["messages"].append(placeholder)
    return mapping, entries, (qualified, bare)


# 独立安全网用的**另一套**正则：安全网如果和被测代码共用同一套模式，
# 它的盲区就是被测代码的盲区（循环论证，review P1-1）。这里只认「错误调用 / 错误对象 /
# code 字段」之后、到下一个逗号/右括号之前的第一个 3–5 位数字。
INDEPENDENT_HINT_RE = re.compile(
    r"(?:DomainException|\.Error\s*\(|WriteDomainLikeErrorAsync"
    r"|new\s+\w*Error"
    # `code =` / `code:` 才算码字段；`code == 408`、`code >= 500` 是比较，不是赋值。
    r"|\bcode\b[\\\"']{0,3}\s*(?::|(?<![=!<>])=(?!=)))\s*\(?")
INDEPENDENT_NUM_RE = re.compile(r"(?<![\w.])(\d{3,5})(?![\w.])")


def find_unattributed(source_root, entries):
    """独立的安全网：找出抽取器没有归因到 entries 的码。

    `--check` 会因此失败，避免抽取器规则再次悄悄漏采（文档与生成器互相印证是循环论证）。
    失败是**安全**方向：宁可让人看一眼「这个数字是不是业务码」，也不要静默漏掉一个码。
    """
    missing = {}
    for path in iter_cs(source_root):
        try:
            text = read_source(path)
        except OSError:
            continue
        rel = os.path.relpath(path, REPO_ROOT).replace("\\", "/")
        for hint in INDEPENDENT_HINT_RE.finditer(text):
            tail = text[hint.end(): hint.end() + 80].lstrip()
            # 第一个实参是消息字面量（`new ArgumentException("…240…")`）时里面的数字不是码。
            if tail[:1] in ('"', "@", "$"):
                continue
            arg = re.split(r"[,)\n]", tail, maxsplit=1)[0]
            m = INDEPENDENT_NUM_RE.search(arg)
            if not m:
                continue
            code = int(m.group(1))
            if code == 0 or code in entries:
                continue
            missing.setdefault(code, set()).add(rel)
    return missing


def module_of(files):
    modules = set()
    for f in files:
        m = re.search(r"Pim\.(Module|Api)\.([A-Za-z]+)", f)
        modules.add(m.group(2) if m else os.path.splitext(os.path.basename(f))[0])
    return ", ".join(sorted(modules))


def render(mapping, entries):
    lines = [BEGIN, "", "| 码 | HTTP | 模块 | 含义（源码消息样例，最多 3 条） | 客户端建议动作 |", "| --- | --- | --- | --- | --- |"]
    # 映射里出现过的码即使源码没有直接 throw（由下层服务抛出），也必须有表项：
    # 客户端只看得到「哪个码会到达它」，看不到「谁抛的」。
    for code in sorted(set(entries) | set(mapping)):
        entries.setdefault(code, {"messages": [], "files": set(), "written": set()})
        statuses = sorted(entries[code].get("statuses") or {mapping.get(code, 400)})
        msgs = "；".join(entries[code]["messages"][:3])
        msgs = msgs.replace("|", "\\|")
        if len(statuses) == 1:
            status_cell = "%d" % statuses[0]
            advice = ADVICE_BY_STATUS.get(statuses[0], "见 HTTP 状态通用处理")
        else:
            # 同一个码在不同路径上返回不同状态（例如 02009：校验失败 400、Outlook 需重连 409）。
            status_cell = " / ".join("%d" % s for s in statuses)
            advice = "按具体状态处理：" + "；".join(
                "%d %s" % (s, ADVICE_BY_STATUS.get(s, "见 HTTP 状态通用处理")) for s in statuses)
        shown = " / ".join(sorted(entries[code].get("written") or {"%d" % code}))
        lines.append("| %s | %s | %s | %s | %s |" % (shown, status_cell, module_of(entries[code]["files"]), msgs, advice))
    lines += ["", END]
    return "\n".join(lines)


DOC_TEMPLATE = """# 业务错误码表（Business error codes）

> 本文是 `contract/openapi.json` 的**补充契约**：OpenAPI 只能表达 HTTP 状态码，表达不了
> `ApiResponse.code` 的业务语义。客户端（Web / Android / Windows / 外部开发者）按这里的码做分支，
> 前端展示 `message`、按「客户端建议动作」处理。
>
> - 生成方式：`python3 scripts/ci/extract-business-error-codes.py`。采集范围覆盖
>   `DomainException(...)`、`ApiResponse<...>.Error(...)`、`WriteDomainLikeErrorAsync(context, status, code, ...)`
>   与名字带 `Code` 的 `const int`（含 `$"..."` 插值、变量消息、`类名.常量` 写法）；消息来自第二个实参或配对的
>   `const string ...Message`，取不到时如实写「消息为变量或表达式，见源码」。
> - 校验方式：`python3 scripts/ci/audit-contract-gaps.py` 会要求「契约文本里出现的码」都在本表内；
>   `python3 scripts/ci/extract-business-error-codes.py --check` 判断本表是否落后于源码（并做反向完整性核对：
>   源码里出现、抽取器没归因的码会让它失败，而不是被静默漏掉）。
> - 约定：
>   1. `code = 0` 表示成功，其余均为业务失败；HTTP 状态**优先取调用点显式状态**（`Results.Conflict/NotFound`、
>      `Results.Json(..., statusCode:)`、`Response.StatusCode`、helper 的状态实参），否则用
>      `ExceptionMiddleware.ResolveDomainStatusCode` 的映射（默认 400）。同一个码在不同路径上状态不同时，
>      HTTP 列会写成 `400 / 409` 并给出按状态处理的建议；
>   2. 客户端**不要**按 message 字符串判断错误类型，只按 code；**HTTP 状态也不要用来推断业务码**；
>   3. 表中「码」列保留源码写法（如 `02057`），客户端收到的是整数 `2057`，前导零只是源码可读性；
>   4. 新增业务码必须同时出现在契约相关端点的错误响应说明里，并在 PR 正文声明影响面。
>
{generated}
"""


def main(argv):
    ap = argparse.ArgumentParser(description="提取业务错误码并刷新 docs/business-error-codes.md")
    ap.add_argument("--source-root", default=os.path.join(REPO_ROOT, "src"))
    ap.add_argument("--doc", default=DOC)
    ap.add_argument("--check", action="store_true", help="只读：文档与源码不一致则退出 1")
    ap.add_argument("--audit-config", default=None,
                    help="同时刷新审计基线 JSON 的 publicPaths（保留人工维护的 excludePaths/approvedGaps）")
    args = ap.parse_args(argv)

    if not os.path.isdir(args.source_root):
        print("源码根目录不存在：%s" % args.source_root, file=sys.stderr)
        return 4
    mapping, entries, const_maps = collect(args.source_root)
    if not entries:
        print("没有从源码采集到任何业务错误码：%s" % args.source_root, file=sys.stderr)
        return 4
    unattributed = find_unattributed(args.source_root, entries)
    if unattributed:
        detail = ", ".join("%d（%s）" % (code, ", ".join(sorted(files)[:2]))
                           for code, files in sorted(unattributed.items()))
        print("抽取器漏采了源码里的业务码，拒绝生成/通过：%s" % detail, file=sys.stderr)
        return 1 if args.check else 4
    generated = render(mapping, entries)

    if args.audit_config:
        cfg = {}
        if os.path.isfile(args.audit_config):
            cfg = json.load(open(args.audit_config, encoding="utf-8"))
        cfg["publicPaths"] = public_paths_from_source(args.source_root)
        cfg.setdefault("excludePaths", [])
        cfg.setdefault("approvedGaps", {})
        cfg["$comment"] = ("由 scripts/ci/extract-business-error-codes.py --audit-config 维护 publicPaths；"
                           "excludePaths 与 approvedGaps 由人工维护。审计器：scripts/ci/audit-contract-gaps.py")
        payload = json.dumps(cfg, ensure_ascii=False, indent=2) + "\n"
        if args.check:
            if not os.path.isfile(args.audit_config) or open(args.audit_config, encoding="utf-8").read() != payload:
                print("审计基线落后于源码：请重新运行并提交 %s" % os.path.relpath(args.audit_config, REPO_ROOT), file=sys.stderr)
                return 1
        else:
            with open(args.audit_config, "w", encoding="utf-8", newline="\n") as fh:
                fh.write(payload)
            print("已写入审计基线 %s（publicPaths=%d）"
                  % (os.path.relpath(args.audit_config, REPO_ROOT), len(cfg["publicPaths"])))

    if os.path.isfile(args.doc):
        current = open(args.doc, encoding="utf-8").read()
        if BEGIN in current and END in current:
            head = current[:current.index(BEGIN)]
            tail = current[current.index(END) + len(END):]
        else:
            head, tail = DOC_TEMPLATE.format(generated="").rstrip("\n") + "\n\n", ""
    else:
        head, tail = DOC_TEMPLATE.format(generated=""), "\n"
    new_doc = head + generated + tail

    if args.check:
        if os.path.isfile(args.doc) and open(args.doc, encoding="utf-8").read() == new_doc:
            print("业务错误码表与源码一致（%d 个码）。" % len(entries))
            return 0
        print("业务错误码表落后于源码：请运行 python3 scripts/ci/extract-business-error-codes.py", file=sys.stderr)
        return 1

    with open(args.doc, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(new_doc)
    print("已写入 %s（%d 个业务码）" % (os.path.relpath(args.doc, REPO_ROOT), len(entries)))
    return 0


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
    sys.exit(main(sys.argv[1:]))
