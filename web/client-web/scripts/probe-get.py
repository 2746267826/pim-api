"""只读端点核查：对 GET 端点逐一发起真实请求，输出状态码与响应片段。

用法：python scripts/probe-get.py endpoints.json > probes-get.js
随后在浏览器中执行该 JS。
"""
import json
import re
import sys

DATE = '2026-09-27'
ISO = '2026-09-27T00:00:00Z'
ISO2 = '2026-09-28T00:00:00Z'
UUID = 'bf4091db-7276-4e63-94a9-e3c5c3fed32d'
DEVICE = 'android-a5b98c2e27c8c280'

# 这些 GET 也不要自动打（依赖真实 id，或会触碰外部服务）
SKIP_GET = [
    '/calendar/outlook/local-data',
    '/calendar/outlook/device-code',
    '/files/search',       # 依赖 q
    '/tiles',
]


def skip(path: str) -> bool:
    return any(s in path for s in SKIP_GET)


# 已知的真实 id 映射（来自实测）
REAL = {
    'deviceId': DEVICE,
    'id': UUID,
    'taskId': UUID,
    'blockId': 'eyJ2ZXJzaW9uIjoxLCJzdGFydFV0YyI6IjIwMjYtMDktMjdUMDQ6NDQ6NTMuNTk5MzU2KzAwOjAwIiwiZW5kVXRjIjoiMjAyNi0wOS0yN1QwNDo0NTozMS4xMTk5NzkrMDA6MDAiLCJjYXRlZ29yeUlkIjoiY29tbXVuaWNhdGlvbiIsInBhY2thZ2VOYW1lIjoiY29tLnRlbmNlbnQubW9iaWxlcXEifQ',
    'code': 'S1',
    'objectType': 'calendar_event',
    'objectId': UUID,
    'auditVersionId': '1',
}


def substitute(path: str) -> str:
    # ${expr} → 值
    def repl(m):
        expr = m.group(1)
        for key, val in REAL.items():
            if key in expr:
                return val
        # 简单表达式回退
        if 'encodeURIComponent' in expr:
            inner = re.search(r'encodeURIComponent\(([^)]*)\)', expr)
            name = (inner.group(1) if inner else '').strip().rstrip('!').strip()
            for key, val in REAL.items():
                if key in name:
                    return val
        if 'sortBy' in expr:
            return ''
        return 'x'
    path = re.sub(r'\$\{([^{}]*(?:\{[^{}]*\}[^{}]*)*)\}', repl, path)
    # 裸 {name} 模板
    path = re.sub(r'\{([A-Za-z_][A-Za-z0-9_]*)\}', lambda m: REAL.get(m.group(1), 'x'), path)
    return path


def normalize(path: str) -> str:
    """补齐查询参数到真实可用的形式。"""
    # 去掉残留的 x 占位查询
    if path.endswith('/tasksx'):
        path = path[:-1]
    if '?' in path:
        base, q = path.split('?', 1)
        if q in ('x', '') or q.startswith('x&') or q.startswith('x$'):
            path = base
            q = ''
        # 修复 date=x / limit=x / mode=x / start=2026-09-27&end=... (date-only)
        q = re.sub(r'date=x', f'date={DATE}', q)
        q = re.sub(r'limit=x', 'limit=20', q)
        q = re.sub(r'mode=x', 'mode=pending', q)
        q = re.sub(r'page=x', 'page=1', q)
        q = re.sub(r'pageSize=x', 'pageSize=20', q)
        q = re.sub(r'blockMinutes=x', 'blockMinutes=15', q)
        path = base + ('?' + q if q else '')
    base = path.split('?')[0].rstrip('/')
    qs = path.split('?')[1] if '?' in path else ''

    def add(param: str):
        nonlocal qs
        if param.split('=')[0] not in qs:
            qs = (qs + '&' + param).lstrip('&')

    if any(k in base for k in ('/calendar/events', '/calendar/layers', '/calendar/authenticity')):
        add(f'start={ISO}')
        add(f'end={ISO2}')
    if any(k in base for k in ('/pc/heatmap/', '/pc/aggregation/', '/pc/browser-tt/', '/mobile/analytics/', '/mobile/liveness/', '/mobile/location/analytics/', '/pc/activity-analysis', '/pc/productivity/')):
        add(f'start={DATE}')
        add(f'end={DATE}')
        add(f'date={DATE}')
    if '/pc/browser-tt/daily' in base:
        add(f'from={DATE}')
        add(f'to={DATE}')
    if '/pc' in base and 'date=' not in qs and 'start=' not in qs and base.endswith(('summary', 'detail', 'quality')):
        add(f'date={DATE}')
    if '/calendar/data-center/audit/export' in base:
        add(f'from={DATE}')
        add(f'to={DATE}')
    if '/operations/audit/export' in base:
        add(f'from={DATE}')
        add(f'to={DATE}')
    if '/recycle-bin' in base:
        add(f'page=1')
        add(f'pageSize=20')
    if '/reminders/delivery-log' in base:
        add(f'page=1')
        add(f'pageSize=20')
    if '/today/sections' in base:
        add(f'date={DATE}')
    if '/mobile' in base and '/devices' in base and '/detail' not in base and base.endswith('/devices'):
        pass
    path = base + ('?' + qs if qs else '')
    return path


def main():
    rows = json.load(open(sys.argv[1], encoding='utf-8'))
    probes = []
    for row in rows:
        if row['m'] != 'GET':
            continue
        p = row['p']
        if skip(p):
            continue
        p = substitute(p)
        p = normalize(p)
        if '${' in p or '{' in p:
            continue
        probes.append(p)
    # 去重保序
    seen = set()
    uniq = []
    for p in probes:
        if p in seen:
            continue
        seen.add(p)
        uniq.append(p)
    print(json.dumps(uniq, ensure_ascii=False))


if __name__ == '__main__':
    main()
