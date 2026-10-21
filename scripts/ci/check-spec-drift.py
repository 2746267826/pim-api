#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查《客户端集成规格》里引用的端点是否仍存在于契约里（只读、离线、仅标准库）。

背景：规格文档是手写的，天然会漂移。`pim-web/frontend-rebuild-spec/05-api-reference/`
记录过的 `/api/v1/stats/upload`、文件版本历史等端点，在当前契约里已经不存在；反过来契约新增的
定时器、习惯等端点文档里又没有 —— 最坏情况是开发者照着文档写，运行时 404。

做法（刻意「宁少报、不臆测」）：
  * 只检查反引号内与表格里出现的**显式 METHOD + 路径**（如 `POST /api/v1/calendar/tasks/{id}/plan`）；
    方法枚举（`GET, POST /path`）会**逐个方法**检查（旧实现只看最后一个）；
  * 以及以 `/` 开头的**具体前缀**（`/api/v1/pc/*`、`/api/v1/pc/` 这种星号/斜杠写法按前缀匹配）；
    不以 `/` 或 `*` 结尾的裸路径按**具体端点**精确匹配，不再被更长的路径前缀掩护；
    声明了 `drift-base` 的章节里，反引号内任何以 `/` 开头的相对路径都按该基地址拼接后检查；
  * 指令（`drift-ignore` / `drift-base`）只认**行内代码之外**的写法；反引号里的是引用/示例，
    不生效（文档常把写法当示例抄一遍，不该因此给全文套基地址）；
  * 规格可用 `<!-- drift-ignore: /path -->` 或 `<!-- drift-ignore: METHOD /path -->` 声明
    「这条路径/这个方法不属于本 API」（例如本机守护进程协议）。豁免是**全文按精确路径**生效，
    不依赖所在行；**路径级豁免只对契约里不存在的路径生效** —— 路径若已在契约里，仍会检查，
    避免同一路径上的真实方法被静默放过。方法级写法只豁免那一个方法；
  * 契约路径模板与文档写法都可能省略参数约束（`{id:guid}` / `{id}` / `{}`），比较前统一归一化；
    逐段比较时，契约里**有约束**的参数（`format`/`pattern`/`enum`）不能匹配文档的字面段
    （`{id:guid}` ≠ `upload`），无约束的字符串参数可以（`{type}` = `event`）；
  * 「见 / 同 / 参见 `GET /x`」这类转述只在**同一 (method, path) 已在本文件其它位置正式声明**时跳过，
    否则照常检查（避免「详见 …」成为漏报后门）。

发现分类：
  missing-path   规格里显式写的 METHOD+路径，契约里不存在 → P1（开发者照抄会 404）
  missing-method 路径存在、但契约里没有该方法 → P1（照抄会 405；常见于文档把写操作写成 POST）
  missing-prefix 规格里以 / 开头的具体路径/前缀，契约里没有任何端点落在其下 → P1
  stale-ignore   路径级 drift-ignore 指向了契约里真实存在的路径 → P2（应改成方法级豁免或删除豁免）

退出码：0 无漂移；1 有缺失；4 输入非法（文件不存在 / 契约不是合法 OpenAPI 3.x /
规格里识别不到**未被豁免**的引用 —— 全被 ignore 的规格不能静默放行）。
用法：python3 scripts/ci/check-spec-drift.py [--spec FILE] [--contract FILE] [--json]
"""

import argparse
import json
import os
import posixpath
import re
import sys

METHODS = ("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS")
_METHOD = "(?:" + "|".join(METHODS) + ")"
# 方法枚举：`GET, POST /path` —— 整个列表都要检查。
EXPLICIT_RE = re.compile(r"\b((?:" + _METHOD + r"\s*,\s*)*" + _METHOD + r")\s+(/[A-Za-z0-9_\-{}/.:*]*)")
BARE_PATH_RE = re.compile(r"`(/(?:api|health|mcp|metrics|version)[A-Za-z0-9_\-{}/.:*]*)`")
# 声明了 drift-base 的章节里，相对路径也可以写成任意 `/xxx`（如 `/calendar/tasks`、`/files/items/upload`）。
RELATIVE_BARE_RE = re.compile(r"`(/[A-Za-z0-9_\-{}/.:*]+)`")
# `<!-- drift-ignore: /path -->` 或 `<!-- drift-ignore: POST /path -->`，全文按精确路径生效。
IGNORE_RE = re.compile(r"<!--\s*drift-ignore:\s*(?:(?P<method>" + _METHOD + r")\s+)?(?P<path>/\S+)\s*-->")
# 路径**直接接在** URL 后面（中间没有空格/引号/反引号/中文标点）才算「外部地址的一部分」。
# 不能用 `\S*$`：中文句子没有空格，会把同一行后面真正的端点声明一起吞掉。
URL_UP_TO_MATCH_RE = re.compile(r"https?://[A-Za-z0-9._~:/?#\[\]@!$&'()*+,;=%-]*$")
# 本章基地址：`<!-- drift-base: /api/v1/calendar -->`。之后的相对路径（如 `GET /calendar/tasks`）
# 若不以 `/api` 开头，则按该基地址拼接 —— 域文档常用「本域路由组整体前缀是 X」的写法，不必每行写全路径。
# 语义：从该行起生效；位于第一个 drift-base 之前的行使用**第一个**基地址（文档顺序不该影响判定）。
BASE_RE = re.compile(r"<!--\s*drift-base:\s*(\S+?)\s*-->")
# 行内代码（反引号）里的内容不算指令：文档常把写法当示例引用，
# 例如「每份文档顶部加一行 `<!-- drift-base: /api/v1 -->`」——
# 若把它当成真指令，一句示例会给全文套上基地址，制造大量假引用。
_CODE_SPAN_RE = re.compile(r"`[^`\n]*`")


def mask_code_spans(line):
    """把行内代码替换成等长空格，供只扫描指令使用（引用仍照常解析）。"""
    return _CODE_SPAN_RE.sub(lambda match: " " * len(match.group(0)), line)


def normalize(path):
    path = path.strip().rstrip(".,;:、）)")
    path = path.replace("/*", "/")
    path = re.sub(r"\{[^}]*\}", "{}", path)
    path = path.rstrip("*")  # `/api/v1/ops*` 这种前缀写法按 `/api/v1/ops` 处理
    return path.rstrip("/") or "/"


def same_template(a, b):
    """两个路径模板「同形」= 归一化后完全相等（用于裸路径/前缀判定）。

    `normalize` 已把 `{id:guid}` / `{taskId}` / `{id}` 统一成 `{}`，所以具名参数的写法差异
    自动消失；而**字面段必须逐字相等** —— 不能拿 `{}` 去通配字面段，否则
    `DELETE /api/v1/files/items/upload` 会被 `DELETE /api/v1/files/items/{id}` 放行
    （运行时 `{id:guid}` 匹配不了 `upload`，实际是 404/405）。
    逐段判定、能区分「有约束参数」与「自由文本参数」的版本见 `template_matches`。
    """
    return a == b


def _response_statuses(item):
    statuses = set()
    for method, op in item.items():
        if method.upper() not in METHODS or not isinstance(op, dict):
            continue
        for status in (op.get("responses") or {}):
            statuses.add(str(status))
    return statuses


def is_catch_all(path, item):
    """契约里的 404 兜底不算真实端点。

    两种真实形态：
      * `/api/{*path}`（ASP.NET 的 catch-all 写法）；
      * `/api/{path}`（OpenAPI 生成器把 catch-all 参数渲染成普通参数名）——
        只在它**只有兜底语义**时认定：最后一段是 `{path}`/`{*path}`，
        且所有响应状态都在 `{404, 500}` 内。否则可能误伤真实的 `/api/{path}` 端点。
    """
    segments = [s for s in path.split("/") if s]
    if any(segment.startswith("{*") for segment in segments):
        return True
    return (
        len(segments) == 2
        and segments[0] == "api"
        and segments[1] in ("{path}", "{*path}")
        and _response_statuses(item) <= {"404", "500"}
    )


def _path_param_schemas(path_item, op):
    """收集该操作的路径参数 schema（路径级参数在前，操作级覆盖）。"""
    schemas = {}
    for source in (path_item.get("parameters") or [], op.get("parameters") or []):
        if not isinstance(source, list):
            continue
        for prm in source:
            if isinstance(prm, dict) and prm.get("in") == "path" and prm.get("name"):
                schemas[prm["name"]] = prm.get("schema")
    return schemas


def contract_paths(doc):
    """返回 (ops, paths)。ops 为 {(METHOD, 原始模板): 路径参数 schema}。"""
    ops, paths = {}, set()
    for path, item in (doc.get("paths") or {}).items():
        if not isinstance(item, dict):
            continue
        if is_catch_all(path, item):
            continue  # 兜底路由：语义由中间件保证，不是可引用的端点
        paths.add(normalize(path))
        for method, op in item.items():
            if method.upper() in METHODS and isinstance(op, dict):
                ops[(method.upper(), path)] = _path_param_schemas(item, op)
    return ops, paths


def _segment_parts(path):
    return [s for s in path.strip("/").split("/") if s]


def _is_param(segment):
    return segment.startswith("{") and segment.endswith("}")


def _shape(segment):
    """把段内的参数占位统一成 `{}`：`{y}.png` → `{}.png`。"""
    return re.sub(r"\{[^}]*\}", "{}", segment)


def _free_form_parameter(segment, schemas):
    """契约里的参数段能否匹配文档里的**字面段**（如 `event` 之于 `{type}`）。

    只有无约束的纯字符串参数才可以：`format: uuid`（`{id:guid}`）、`pattern`、`enum`
    都限定了取值，拿它们去匹配字面段正是 review 指出的漏报
    （`DELETE /files/items/upload` 会被 `/files/items/{id}` 放行，运行时是 404/405）。
    取不到 schema 时按「有约束」处理（fail-closed）。
    """
    schema = schemas.get(segment[1:-1])
    if not isinstance(schema, dict) or not schema:
        return False
    return not any(key in schema for key in ("format", "pattern", "enum"))


def template_matches(contract_path, doc_path, schemas):
    """逐段比较契约模板与规格里的路径，参数名与 `{}` 写法差异不算漂移。

    - 参数 vs 参数：视为同一端点；
    - 契约参数 vs 文档字面段：仅当该参数无取值约束（`{type}` 可取 `event`）；
    - 文档参数 vs 契约字面段：不是同一端点（文档不能拿 `{id}` 去代表一个固定路径）。
    """
    c, d = _segment_parts(contract_path), _segment_parts(doc_path)
    if len(c) != len(d):
        return False
    for cs, ds in zip(c, d):
        if _shape(cs) == _shape(ds):
            continue  # 同形（含 `{y}.png` 这种带后缀的参数段），参数名差异不算漂移
        if _is_param(cs) and "{}" not in _shape(ds) and _free_form_parameter(cs, schemas):
            continue  # 无约束参数段取文档里的字面值（`{type}` = `event`）
        return False
    return True


TOP_LEVEL_PREFIXES = ("/api", "/health", "/mcp", "/metrics", "/version")


def resolve(path, base):
    """相对路径按基地址拼接；已是顶层前缀的绝对路径原样返回。"""
    if not base or path.startswith(TOP_LEVEL_PREFIXES):
        return path
    return posixpath.join(base.rstrip("/"), path.lstrip("/"))


def path_authorship(preceding):
    """判断 `METHOD /path` 是否「本文档作者在正式声明真实端点」。

    保守规则（只认明显是**转述 / 外部地址**的写法，其余一律当作声明 —— 宁可多报，不漏报）：
      * 路径本身是 URL 的一部分（`https://example.com/api/v1/x`）→ 外部地址；
        注意「同一句里提到 URL、随后又声明端点」不算 —— 只有 URL 一直连到该路径才跳过；
      * 紧跟前缀是「同 / 见 / 参见 / 参考 / 例如 / 等价于」→ 视为转述。
        转述**只在同一 (method, path) 已在别处正式声明**时跳过（见 `spec_references` 的两遍扫描），
        否则仍会检查 —— 「详见 `GET /api/v1/new-endpoint`」不能成为漏报后门。
    """
    head = preceding
    if URL_UP_TO_MATCH_RE.search(head):
        return False
    sentence = re.split(r"[。；;！!？?\n]", head)[-1].strip()
    if re.search(r"(同|见|参见|参考|例如|比如|等价于)\s*$", sentence):
        return False
    return True


def spec_references(text):
    """解析规格里的引用。

    返回 (explicit, bare, ignores)：
      explicit: {(METHOD, 归一化路径)}
      bare:     {归一化路径: 是否前缀写法}
      ignores:  {(METHOD 或 None, 归一化路径)}，None 表示路径级
    """
    # 指令只认代码之外的写法；行内代码里的是「引用/示例」，不是声明。
    directive_text = "\n".join(mask_code_spans(line) for line in text.splitlines())

    ignores = set()
    for match in IGNORE_RE.finditer(directive_text):
        method = (match.group("method") or "").upper() or None
        ignores.add((method, normalize(match.group("path"))))

    bases = BASE_RE.findall(directive_text)
    # 第一个基地址也用于它**之前**的行（文档顺序不该影响判定）；之后按行切换，一份文档可以有多
    # 个域（calendar / files / pc …），各自声明自己的前缀。
    base = bases[0] if bases else ""
    explicit_raw = []  # (method, path, quoted)
    bare_raw = []      # (path, is_prefix, quoted)
    explicit_paths = set()

    for line in text.splitlines():
        line_base = BASE_RE.search(mask_code_spans(line))
        if line_base:
            base = line_base.group(1)
        # `drift-ignore` 只豁免它点名的那条路径/方法，不豁免同一行的其它引用 ——
        # 否则在一行里再补一个端点会被整行静默放过。
        for match in EXPLICIT_RE.finditer(line):
            methods_blob, path = match.group(1), match.group(2)
            if len(path) <= 1 or normalize(path) in ("/api", "/api/v1"):
                continue  # `GET /api` 这类只是提前缀，不是端点
            quoted = not path_authorship(line[:match.start()])
            target = resolve(path, base)
            normalized = normalize(target)
            explicit_paths.add(normalized)
            for method in (part.strip().upper() for part in methods_blob.split(",")):
                explicit_raw.append((method, normalized, quoted))
        # 声明了基地址的章节里，任何 `/xxx` 反引号路径都算引用；没有基地址时只认已知的顶层前缀
        # （避免把 SPA 路由 `/calendar` 之类当成 API 路径）。
        for match in (RELATIVE_BARE_RE if base else BARE_PATH_RE).finditer(line):
            raw = match.group(1)
            quoted = not path_authorship(line[:match.start()])
            target = normalize(resolve(raw, base))
            if target in explicit_paths:
                continue  # 同一路径已作为「METHOD+路径」声明过，不重复判定
            is_prefix = raw.endswith("*") or raw.endswith("/")
            bare_raw.append((target, is_prefix, quoted))

    # 第二遍：转述只在同一 (method, path) 已正式声明时跳过。
    declared = {(method, path) for method, path, quoted in explicit_raw if not quoted}
    explicit = {(method, path) for method, path, quoted in explicit_raw
                if not quoted or (method, path) not in declared}

    declared_bare = {path for path, _, quoted in bare_raw if not quoted}
    bare = {}
    for path, is_prefix, quoted in bare_raw:
        if quoted and path in declared_bare:
            continue
        bare[path] = bare.get(path, False) or is_prefix

    return explicit, bare, ignores


def main(argv):
    here = os.path.dirname(os.path.abspath(__file__))
    root = os.path.dirname(os.path.dirname(here))
    ap = argparse.ArgumentParser(description="检查规格文档引用的端点是否仍存在于契约")
    ap.add_argument("--spec", default=os.path.join(root, "docs", "client-integration-spec.md"))
    ap.add_argument("--contract", default=os.path.join(root, "contract", "openapi.json"))
    ap.add_argument("--json", action="store_true")
    args = ap.parse_args(argv)

    for label, path in (("规格", args.spec), ("契约", args.contract)):
        if not os.path.isfile(path):
            print("%s不存在：%s" % (label, path), file=sys.stderr)
            return 4
    try:
        doc = json.load(open(args.contract, encoding="utf-8-sig"))
    except json.JSONDecodeError as exc:
        print("契约不是合法 JSON：%s" % exc, file=sys.stderr)
        return 4
    if not str(doc.get("openapi", "")).startswith("3."):
        print("契约不是 OpenAPI 3.x", file=sys.stderr)
        return 4

    text = open(args.spec, encoding="utf-8-sig").read()
    ops, paths = contract_paths(doc)
    explicit, bare, ignores = spec_references(text)

    def contract_has_path(path):
        return any(template_matches(p, path, schemas) for (_, p), schemas in ops.items())

    def ignored(method, path):
        if (method, path) in ignores:
            return True
        # 路径级豁免只对契约里不存在的路径生效：契约里已有该路径时，同一路径上的
        # 真实方法被删除也必须报红，不能被「整路径 ignore」掩护（review P1-1）。
        return (None, path) in ignores and not contract_has_path(path)

    findings = []

    # 路径级 ignore 指向契约里真实存在的路径：提示改成方法级/删除，避免以后静默漏报。
    for method, path in sorted(ignores, key=lambda x: (x[1], x[0] or "")):
        if method is None and contract_has_path(path):
            findings.append({"category": "stale-ignore", "label": path,
                             "detail": "路径级 drift-ignore 指向契约里存在的路径；请改成 METHOD /path 或删除豁免",
                             "severity": "P2"})

    effective_explicit = sorted(x for x in explicit if not ignored(*x))
    effective_bare = {p: prefix for p, prefix in bare.items()
                      if not ((None, p) in ignores and not contract_has_path(p))}

    # 空文件 / 只剩标题 / 全部被 ignore 的规格会「0 findings」——门禁形同虚设。
    # 这里按**应用豁免之后**的有效引用判定（review P1-2）。
    if not effective_explicit and not effective_bare:
        print("规格里没有识别到任何未被豁免的端点引用（文件可能被截断，或全部加了 drift-ignore）：%s"
              % args.spec, file=sys.stderr)
        return 4

    for method, path in effective_explicit:
        if any(m == method and template_matches(p, path, schemas) for (m, p), schemas in ops.items()):
            continue
        # 路径存在但方法不对（常见：文档把 GET 写成 POST）→ 单独报，避免开发者按文档调用收到 405。
        same_path = sorted({m for (m, p), schemas in ops.items() if template_matches(p, path, schemas)})
        if same_path:
            findings.append({"category": "missing-method", "label": "%s %s" % (method, path),
                             "detail": "路径存在，但契约里只有 %s" % "/".join(same_path), "severity": "P1"})
        else:
            findings.append({"category": "missing-path", "label": "%s %s" % (method, path),
                             "detail": "规格里写了该端点，契约里没有这个 METHOD+路径", "severity": "P1"})

    for path in sorted(effective_bare):
        if path.count("/") < 2:
            continue  # `/api`、`/mcp` 这类太短，容易是误报
        if effective_bare[path]:
            prefix = path.rstrip("/")
            ok = any(p == prefix or p.startswith(prefix + "/") for p in paths)
        else:
            # 具体端点：只做精确/模板匹配。旧实现会用「更长路径前缀」掩护，
            # 删掉集合端点（`/api/v1/pc/summary`）时被 `/api/v1/pc/summary/detail` 放行（review P2-1）。
            ok = contract_has_path(path)
        if not ok:
            findings.append({"category": "missing-prefix", "label": path,
                             "detail": "规格里提到的路径（前缀），契约里没有任何端点落在其下", "severity": "P1"})

    counts = {}
    for finding in findings:
        counts[finding["category"]] = counts.get(finding["category"], 0) + 1
    if args.json:
        print(json.dumps({"spec": args.spec, "contract": args.contract, "counts": counts,
                          "findings": findings}, ensure_ascii=False, indent=2))
    else:
        print("== 规格漂移检查 ==")
        print("规格    : %s" % args.spec)
        print("契约    : %s" % args.contract)
        print("引用    : 显式端点 %d 个，裸路径 %d 个，声明豁免 %d 条"
              % (len(explicit), len(bare), len(ignores)))
        print("findings: %d" % len(findings))
        for finding in findings:
            print("  - [%s] %s — %s" % (finding["category"], finding["label"], finding["detail"]))
        if not findings:
            print("  规格引用的端点全部存在于契约中")
    return 1 if findings else 0


if __name__ == "__main__":
    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8")
        except (AttributeError, ValueError):
            pass
    sys.exit(main(sys.argv[1:]))
