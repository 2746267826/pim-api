"""从 src 提取所有 (HTTP 方法, 路径模板)，用于对照真实 API 做端点核查。"""
import re
import os
import json

ROOT = 'src'
PAT = re.compile(r"api(Get|Post|Put|Delete|Patch)?(?:<[^>]*>)?\(\s*[`']([^`']+)[`']", re.S)

rows = []
for dirpath, _, files in os.walk(ROOT):
    if 'test' in dirpath:
        continue
    for f in files:
        if not f.endswith(('.ts', '.tsx')) or '.test.' in f:
            continue
        path_file = os.path.join(dirpath, f)
        with open(path_file, encoding='utf-8', errors='ignore') as fh:
            src = fh.read()
        for m in PAT.finditer(src):
            method = (m.group(1) or 'Get').upper()
            url = m.group(2)
            if not url.startswith('/api/'):
                continue
            rows.append((method, url, path_file.replace(os.sep, '/')))

seen = {}
for method, url, pf in rows:
    seen.setdefault((method, url), set()).add(pf)

out = [{'m': k[0], 'p': k[1], 'files': sorted(v)} for k, v in sorted(seen.items())]
with open('endpoints.json', 'w', encoding='utf-8') as fh:
    json.dump(out, fh, ensure_ascii=False, indent=1)
print('TOTAL', len(out), '-> endpoints.json')
