#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""从 pim-api 源码提取业务错误码，生成/刷新 docs/business-error-codes.md 的「自动采集」段。

业务错误码是 `ApiResponse.code` 的取值（`0` 表示成功），HTTP 状态由
`ExceptionMiddleware.ResolveDomainStatusCode` 统一决定。客户端要按码分支，就必须有一张
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
CONST_CODE_RE = re.compile(r"const\s+int\s+(\w+)\s*=\s*(\d{4,5})\s*;")
# 消息允许普通字符串或插值字符串（$"..."），模板占位符在表里显示成 {…}
CONST_THROW_RE = re.compile(r'(?:DomainException|ApiResponse<[^>]*>\.Error)\(\s*(\w+)\s*,\s*\$?"((?:[^"\\]|\\.)*)"')

CODE_RE = re.compile(
    r'(?:DomainException|ApiResponse<[^>]*>\.Error|ApiResponse\.Error|ThrowDomain)'
    r'\(\s*(\d{4,5})\s*,\s*"((?:[^"\\]|\\.)*)"'
)
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
        codes = [int(c) for c in re.findall(r"(?<!\d)(\d{4,5})(?!\d)", left)]
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
        for m in _re.finditer(r"\.AllowAnonymous\(\)", text):
            # 往回找最近的 Map* 调用（中间只允许链式调用的换行/空白，遇到其它语句就放弃）
            window = text[max(0, m.start() - 700):m.start()]
            calls = list(_re.finditer(r"(\w+)\s*\.\s*Map(?:Get|Post|Put|Patch|Delete)\(\s*([^,]*)", window))
            others = list(OTHER_ENDPOINT_RE.finditer(window))
            if not calls and not others:
                continue
            # 取位置最靠后、且到 .AllowAnonymous() 为止括号尚未闭合的那个注册
            if calls and (not others or calls[-1].start() > others[-1].start()):
                call = calls[-1]
                prefix_key, expr = call.group(1), call.group(2)
            else:
                call = others[-1]
                prefix_key, expr = "", call.group(1)
            target = _eval_path_expr(expr, consts)
            if target is None:
                continue
            prefix = groups.get(prefix_key, "")
            combined = prefix.rstrip("/") + "/" + target.lstrip("/") if target else prefix
            # 与契约路径同形；SPA fallback（index.html）不是 API 端点，不进白名单。
            if combined.endswith(".html"):
                continue
            found.add(_re.sub(r"\{(\*{1,2})?[^}]*\}", "{}", combined))
    return sorted(found)


def collect(source_root):
    mapping = {}
    entries = {}
    for path in iter_cs(source_root):
        try:
            with open(path, "r", encoding="utf-8-sig") as fh:
                text = fh.read()
        except (OSError, UnicodeDecodeError):
            continue
        if mapping == {} and "ResolveDomainStatusCode" in text:
            found = parse_status_map(text)
            if found:
                mapping = found
        rel = os.path.relpath(path, REPO_ROOT).replace("\\", "/")
        # 保留源码里的原始写法（含前导零）：契约说明文字里常用 `02057` 这种给人读的写法。
        written = {int(value): value for _name, value in CONST_CODE_RE.findall(text)}
        const_codes = {name: int(value) for name, value in CONST_CODE_RE.findall(text)}
        pairs = [(int(c), msg) for c, msg in CODE_RE.findall(text)]
        pairs += [(const_codes[name], msg) for name, msg in CONST_THROW_RE.findall(text) if name in const_codes]
        for code, message in pairs:
            code = int(code)
            entry = entries.setdefault(code, {"messages": [], "files": set(), "written": set()})
            entry.setdefault("written", set()).add(written.get(code, "%d" % code))
            entry["files"].add(rel)
            if message not in entry["messages"]:
                entry["messages"].append(message)
    return mapping, entries


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
        status = mapping.get(code, 400)
        msgs = "；".join(entries[code]["messages"][:3])
        msgs = msgs.replace("|", "\\|")
        advice = ADVICE_BY_STATUS.get(status, "见 HTTP 状态通用处理")
        shown = " / ".join(sorted(entries[code].get("written") or {"%d" % code}))
        lines.append("| %s | %d | %s | %s | %s |" % (shown, status, module_of(entries[code]["files"]), msgs, advice))
    lines += ["", END]
    return "\n".join(lines)


DOC_TEMPLATE = """# 业务错误码表（Business error codes）

> 本文是 `contract/openapi.json` 的**补充契约**：OpenAPI 只能表达 HTTP 状态码，表达不了
> `ApiResponse.code` 的业务语义。客户端（Web / Android / Windows / 外部开发者）按这里的码做分支，
> 前端展示 `message`、按「客户端建议动作」处理。
>
> - 生成方式：`python3 scripts/ci/extract-business-error-codes.py`（从 `pim-api` 源码的
>   `DomainException(<码>, "<消息>")` / `ApiResponse<...>.Error(<码>, "<消息>")` 自动采集）。
> - 校验方式：`python3 scripts/ci/audit-contract-gaps.py` 会要求「契约文本里出现的码」都在本表内；
>   `python3 scripts/ci/extract-business-error-codes.py --check` 判断本表是否落后于源码。
> - 约定：
>   1. `code = 0` 表示成功，其余均为业务失败；HTTP 状态由 `ExceptionMiddleware.ResolveDomainStatusCode` 决定；
>   2. 客户端**不要**按 message 字符串判断错误类型，只按 code；
>   3. 新增业务码必须同时出现在契约相关端点的错误响应说明里，并在 PR 正文声明影响面。
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
    mapping, entries = collect(args.source_root)
    if not entries:
        print("没有从源码采集到任何业务错误码：%s" % args.source_root, file=sys.stderr)
        return 4
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
