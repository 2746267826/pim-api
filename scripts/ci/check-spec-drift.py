#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""检查《客户端集成规格》里引用的端点是否仍存在于契约里（只读、离线、仅标准库）。

背景：规格文档是手写的，天然会漂移。`pim-web/frontend-rebuild-spec/05-api-reference/`
记录过的 `/api/v1/stats/upload`、文件版本历史等端点，在当前契约里已经不存在；反过来契约新增的
定时器、习惯等端点文档里又没有 —— 最坏情况是开发者照着文档写，运行时 404。

做法（刻意「宁少报、不臆测」）：
  * 只检查反引号内与表格里出现的**显式 METHOD + 路径**（如 `POST /api/v1/calendar/tasks/{id}/plan`）；
  * 以及以 `/` 开头的**具体前缀**（`/api/v1/pc/*` 这种星号写法按 `/api/v1/pc/` 前缀匹配）；
    声明了 `drift-base` 的章节里，反引号内任何以 `/` 开头的相对路径都按该基地址拼接后检查；
  * 规格可用 `<!-- drift-ignore: /path -->` 声明「这条路径不属于本 API」（例如本机守护进程协议），
    **必须写在被引用的那一行**，便于逐条审阅；
  * 契约路径模板与文档写法都可能省略参数约束（`{id:guid}` / `{id}` / `{}`），比较前统一归一化；
    逐段比较时，契约里**有约束**的参数（`format`/`pattern`/`enum`）不能匹配文档的字面段
    （`{id:guid}` ≠ `upload`），无约束的字符串参数可以（`{type}` = `event`）。

发现分类：
  missing-path   规格里显式写的 METHOD+路径，契约里不存在 → P1（开发者照抄会 404）
  missing-method 路径存在、但契约里没有该方法 → P1（照抄会 405；常见于文档把写操作写成 POST）
  missing-prefix 规格里以 / 开头的具体路径/前缀，契约里没有任何端点落在其下 → P1

退出码：0 无漂移；1 有缺失；4 输入非法（文件不存在 / 契约不是合法 OpenAPI 3.x / 规格里识别不到任何引用）。
用法：python3 scripts/ci/check-spec-drift.py [--spec FILE] [--contract FILE] [--json]
"""

import argparse
import json
import os
import posixpath
import re
import sys

METHODS = ("GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS")
EXPLICIT_RE = re.compile(r"\b(" + "|".join(METHODS) + r")\s+(/[A-Za-z0-9_\-{}/.:*]*)")
BARE_PATH_RE = re.compile(r"`(/(?:api|health|mcp|metrics|version)[A-Za-z0-9_\-{}/.:*]*)`")
# 声明了 drift-base 的章节里，相对路径也可以写成任意 `/xxx`（如 `/calendar/tasks`、`/files/items/upload`）。
RELATIVE_BARE_RE = re.compile(r"`(/[A-Za-z0-9_\-{}/.:*]+)`")
IGNORE_RE = re.compile(r"<!--\s*drift-ignore:\s*(/\S+)\s*-->")
# 路径**直接接在** URL 后面（中间没有空格/引号/反引号/中文标点）才算「外部地址的一部分」。
# 不能用 `\S*$`：中文句子没有空格，会把同一行后面真正的端点声明一起吞掉。
URL_UP_TO_MATCH_RE = re.compile(r"https?://[A-Za-z0-9._~:/?#\[\]@!$&'()*+,;=%-]*$")
# 本章基地址：`<!-- drift-base: /api/v1/calendar -->`。之后的相对路径（如 `GET /calendar/tasks`）
# 若不以 `/api` 开头，则按该基地址拼接 —— 域文档常用「本域路由组整体前缀是 X」的写法，不必每行写全路径。
# 语义：从该行起生效；位于第一个 drift-base 之前的行使用**第一个**基地址（文档顺序不该影响判定）。
BASE_RE = re.compile(r"<!--\s*drift-base:\s*(\S+?)\s*-->")


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


def is_catch_all(path):
    """契约里的 404 兜底（`/api/{*path}`）：它匹配任意单段路径，不能算真实端点。"""
    return any(segment.startswith("{*") for segment in path.split("/"))


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
        if is_catch_all(path):
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
    """判断 `METHOD /path` 是否「本文档作者在声明真实端点」。

    保守规则（只认明显是**转述 / 外部地址**的写法，其余一律当作声明 —— 宁可多报，不漏报）：
      * 路径本身是 URL 的一部分（`https://example.com/api/v1/x`）→ 外部地址；
        注意「同一句里提到 URL、随后又声明端点」不算 —— 只有 URL 一直连到该路径才跳过；
      * 紧跟前缀是「同 / 见 / 参见 / 参考 / 例如 / 等价于」→ 指的是另一个已声明端点。
      review 指出：此前还会跳过「带文件扩展名」（会吞掉真实存在的 `/tiles/{z}/{x}/{y}.png`）
      与「`GET, POST /path` 这种方法枚举」（整条不检查）；两者都已删除，枚举写法至少会检查被匹配到的那个方法。
    """
    head = preceding
    if URL_UP_TO_MATCH_RE.search(head):
        return False
    sentence = re.split(r"[。；;！!？?\n]", head)[-1].strip()
    if re.search(r"(同|见|参见|参考|例如|比如|等价于)\s*$", sentence):
        return False
    return True


def spec_references(text):
    ignored = set(normalize(p) for p in IGNORE_RE.findall(text))
    bases = BASE_RE.findall(text)
    # 第一个基地址也用于它**之前**的行（文档顺序不该影响判定）；之后按行切换，一份文档可以有多
    # 个域（calendar / files / pc …），各自声明自己的前缀。
    base = bases[0] if bases else ""
    explicit = set()
    explicit_paths = set()
    bare = set()
    for line in text.splitlines():
        line_base = BASE_RE.search(line)
        if line_base:
            base = line_base.group(1)
        # `drift-ignore` 只豁免它点名的那条路径，不豁免同一行的其它引用 ——
        # 否则在一行里再补一个端点会被整行静默放过。被豁免的路径在汇总阶段按精确值剔除。
        for match in EXPLICIT_RE.finditer(line):
            method, path = match.group(1), match.group(2)
            if len(path) <= 1 or normalize(path) in ("/api", "/api/v1"):
                continue  # `GET /api` 这类只是提前缀，不是端点
            if not path_authorship(line[:match.start()]):
                continue
            # 相对写法（`GET /calendar/tasks`）按本章基地址拼接；绝对写法原样使用。
            target = resolve(path, base)
            explicit.add((method, normalize(target)))
            explicit_paths.add(normalize(target))
        # 声明了基地址的章节里，任何 `/xxx` 反引号路径都算引用；没有基地址时只认已知的顶层前缀
        # （避免把 SPA 路由 `/calendar` 之类当成 API 路径）。
        for match in (RELATIVE_BARE_RE if base else BARE_PATH_RE).finditer(line):
            raw = match.group(1)
            if not path_authorship(line[:match.start()]):
                continue
            target = normalize(resolve(raw, base))
            if target in explicit_paths:
                continue  # 同一路径已作为「METHOD+路径」声明过，不重复判定
            bare.add(target)
    return explicit, bare, ignored


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
    explicit, bare, ignored = spec_references(text)
    explicit_paths = {p for _, p in explicit}

    # 空文件 / 只剩标题的规格会「0 findings」——门禁形同虚设。这里显式判非法（与文件不存在同级）。
    if not explicit and not bare:
        print("规格里没有识别到任何端点引用（文件可能被截断或格式异常）：%s" % args.spec, file=sys.stderr)
        return 4

    findings = []
    for method, path in sorted(explicit):
        if path in ignored:
            continue
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
    for path in sorted(bare - explicit_paths - ignored):
        if path.count("/") < 2:
            continue  # `/api`、`/mcp` 这类太短，容易是误报
        if not any(p == path or same_template(p, path) or p.startswith(path.rstrip("/") + "/") for p in paths):
            findings.append({"category": "missing-prefix", "label": path,
                             "detail": "规格里提到的路径（前缀），契约里没有任何端点落在其下", "severity": "P1"})

    counts = {}
    for f in findings:
        counts[f["category"]] = counts.get(f["category"], 0) + 1
    if args.json:
        print(json.dumps({"spec": args.spec, "contract": args.contract, "counts": counts,
                          "findings": findings}, ensure_ascii=False, indent=2))
    else:
        print("== 规格漂移检查 ==")
        print("规格    : %s" % args.spec)
        print("契约    : %s" % args.contract)
        print("引用    : 显式端点 %d 个，裸路径 %d 个，声明不属于 API 的路径 %d 个"
              % (len(explicit), len(bare), len(ignored)))
        print("findings: %d" % len(findings))
        for f in findings:
            print("  - [%s] %s — %s" % (f["category"], f["label"], f["detail"]))
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
