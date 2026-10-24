"""对照真实 API 逐个核查前端用到的全部端点（只读，不触碰 OneDrive 文件写入与 Outlook 日程写回）。

用法：node scripts/probe-endpoints.mjs <endpoints.json> [--base http://localhost:5173]
在浏览器控制台/Playwright 中运行生成代码，或直接注入页面执行。
"""
import json
import re
import sys

METHODS_SAFE = {'GET', 'POST', 'PUT', 'DELETE', 'PATCH'}

# 危险端点前缀：绝不触发（影响外部服务）
FORBIDDEN = [
    '/calendar/outlook/events/writeback',
    '/calendar/outlook/sync',
    '/calendar/outlook/local-data',
    '/calendar/outlook/disconnect',
    '/calendar/outlook/device-code',
    '/calendar/import-ics',
    '/calendar/export-ics',
    '/files/items/',           # 文件写操作（上传/移动/重命名/删除/分享/文本写）
    '/files/folders',
    '/files/providers/onedrive',
    '/files/providers/',       # 绑定/解绑/同步
    '/files/suggestions/',
    '/files/trash',
    '/files/search',
    '/files/shares',
    '/mobile/devices/',
    '/ai/test',
    '/ai/health-check',
    '/data-reliability/inspection/refresh',
    '/endpoints/',             # 心跳/上报
    '/daemon/',
    '/quick-notes/attachments',
    '/classification/label',
    '/categories/seed',
    '/browser-tt/import',
    '/app-knowledge/apps/',    # 写
    '/app-knowledge/contexts/',
    '/app-knowledge/suggestions/',
    '/pc/app-signatures/',
    '/pc/categories/',
]


def is_forbidden(method: str, path: str) -> str | None:
    """GET 只读放行；写方法命中禁区则拦截。"""
    if method == 'GET':
        # 即使 GET，也不要拉取可能触发外部同步的路径
        for f in ['/calendar/outlook/local-data', '/calendar/outlook/device-code']:
            if f in path:
                return f
        return None
    for f in FORBIDDEN:
        if f in path:
            return f
    return None


UUID = 'bf4091db-7276-4e63-94a9-e3c5c3fed32d'
DEVICE_ID = 'android-a5b98c2e27c8c280'
DATE = '2026-09-27'
# ISO 必须用 Z 形式（后端不接受 +08:00）
ISO = '2026-09-27T00:00:00Z'
ISO2 = '2026-09-28T00:00:00Z'

VALUES = {
    'id': UUID,
    'deviceId': DEVICE_ID,
    'sessionId': 'sess-x',
    'blockId': 'blk-x',
    'segmentId': 'seg-x',
    'providerId': UUID,
    'snapshotId': 'snap-x',
    'versionId': 'ver-x',
    'permissionId': 'perm-x',
    'taskId': UUID,
    'itemId': 'item-x',
    'objectType': 'calendar_event',
    'objectId': UUID,
    'auditVersionId': '1',
    'code': 'S1',
    'type': 'event',
    'z': '1', 'x': '1', 'y': '1',
}


def fill(template: str) -> str:
    def repl(m):
        name = m.group(1)
        return VALUES.get(name, 'x')
    out = re.sub(r'\$\{([A-Za-z_][A-Za-z0-9_]*)(?:\?[^}]*)?\}', repl, template)
    out = re.sub(r'\{([A-Za-z_][A-Za-z0-9_]*)(?:\?[^}]*)?\}', repl, out)
    return out


def to_fetch(method: str, path: str) -> str:
    """把模板里未展开的查询串统一补齐。"""
    if '?' not in path:
        if any(k in path for k in ['/heatmap/', '/aggregation/', '/analytics/', '/liveness/', '/location/analytics/']):
            path += f'?start={DATE}&end={DATE}'
        elif '/calendar/events' in path or '/calendar/layers' in path:
            path += f'?start={ISO}&end={ISO2}'
    return path


def main():
    src = json.load(open(sys.argv[1], encoding='utf-8'))
    lines = []
    skipped = []
    for row in src:
        method, path = row['m'], row['p']
        reason = is_forbidden(method, path)
        if reason:
            skipped.append(f'{method} {path}  <- 跳过（禁区 {reason}）')
            continue
        # 跳过纯占位模板（由调用方拼接、无法静态展开）
        if '${' in path or '{' in path:
            filled = fill(path)
        else:
            filled = path
        if '?' in filled and any(c in filled for c in ['${', '{']):
            pass
        filled = to_fetch(method, filled)
        lines.append((method, filled))
    print('const PROBES = ' + json.dumps(lines, ensure_ascii=False))
    if skipped:
        print('/* 跳过：')
        for s in skipped:
            print('  ' + s)
        print('*/')


if __name__ == '__main__':
    main()
