#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Parse a merged PR JSON (from `gh pr view --json ...`) on stdin and render
the release-note section for it on stdout.

Extracts the template headings from .github/pull_request_template.md:
    技术修改 / technical        -> tech
    功能变化 / feature          -> feature
    如何体验 / how to try/test  -> try
    测试 / tests               -> tests
PRs without any template sections fall back to a plain title link.
"""
import json
import re
import sys

try:
    sys.stdin.reconfigure(encoding="utf-8")
    sys.stdout.reconfigure(encoding="utf-8")
except (AttributeError, ValueError):
    pass

GROUP_RULES = [
    ("tech", ("技术修改", "technical")),
    ("feature", ("功能变化", "feature")),
    ("try", ("如何体验", "how to try", "how to test", "体验")),
    ("tests", ("测试", "tests", "verification")),
]

heading_re = re.compile(r"^#{2,6}\s+(.*)$")
comments = re.compile(r"<!--.*?-->", re.S)


def parse_sections(body: str) -> dict:
    sections = {}
    current = None
    buf = []

    def flush():
        if current is None:
            return
        text = comments.sub("", "\n".join(buf)).strip()
        lines = [line.strip() for line in text.splitlines()]
        text = "\n".join(line for line in lines if line).strip()
        if text:
            sections.setdefault(current, []).append(text)

    for line in body.splitlines():
        m = heading_re.match(line)
        if m:
            flush()
            head = m.group(1).lower()
            current = None
            for key, keywords in GROUP_RULES:
                if any(k in head for k in keywords):
                    current = key
                    break
            buf = []
        else:
            buf.append(line)
    flush()
    return sections


def main() -> None:
    pr = json.load(sys.stdin)
    num = pr["number"]
    title = (pr.get("title") or "").strip()
    url = pr.get("url") or ""
    body = pr.get("body") or ""

    print(f"### #{num} — {title}")

    sections = parse_sections(body)
    if sections:
        labels = [
            ("tech", "技术修改 / Technical changes"),
            ("feature", "功能变化 / Feature changes"),
            ("try", "如何体验 / How to try it"),
            ("tests", "测试 / Tests"),
        ]
        for key, label in labels:
            texts = sections.get(key)
            if not texts:
                continue
            print(f"**{label}**")
            for text in texts:
                print(text)
            print("")
        if url:
            print(f"[PR #{num}]({url})")
            print("")
    else:
        if url:
            print(f"[PR #{num}]({url})")
        print("")


if __name__ == "__main__":
    main()
