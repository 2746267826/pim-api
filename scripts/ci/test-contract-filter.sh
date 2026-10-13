#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 自检：`changes` job 的路径过滤器必须让契约变更触发 API 构建（工单 §9.1/§9.3）。
#
# 为什么需要它：契约硬校验 `Verify client contract is up to date` 在 `build-api` 里，
# 而 `build-api` 只在 `api` flag 为真时才运行。过滤器的口径一旦被改窄（例如有人
# 清理过滤器时把 `contract/**` 删掉），**不会有任何报错** —— 只会静默地又回到
# 「契约-only 的 PR 全绿但契约从未被校验」的假绿状态。
#
# 本测试直接读 `.github/workflows/ci.yml` 里真实的 `filters: |` 块，用与
# dorny/paths-filter（picomatch）一致的 glob 语义判定：
#   * 正向：`contract/openapi.json` 必须命中 `api` 过滤器的某条模式；
#   * 反向（负对照）：`README.md` 不许命中 —— 否则有人用 `**` 之类"让所有路径都触发"
#     的方式蒙混过关，那会让每个 PR 都跑全量构建；
#   * 回归：原有意图（`src/Pim.Api/**` 命中 API、`Dockerfile` 命中 docker）没被改坏。
#
# 边界如实说明：这是**配置层**测试（证明过滤规则覆盖了该路径），不是 GitHub 侧
# 端到端证明。端到端那次（真的开一个只改契约的 PR、看 build-api 是否运行）需要
# 修复本身已经合进 master 才有可能不被其它改动干扰，见 PR 描述里的说明。
#
# 用法：scripts/ci/test-contract-filter.sh
# ---------------------------------------------------------------------------
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CI_YML="$HERE/../../.github/workflows/ci.yml"

python3 - "$CI_YML" <<'PY'
import re, sys

path = sys.argv[1]
text = open(path, encoding="utf-8").read()

# --- 取出 changes job 里 filters: | 这个块（按缩进切） ----------------------
m = re.search(r"^(\s*)filters: \|\n(.*?)(?=^\1\S|\Z)", text, re.S | re.M)
if not m:
    print("  ❌ 没能在 ci.yml 里找到 `filters: |` 块（结构被改过？）")
    sys.exit(1)
block = m.group(2)

filters = {}
current = None
base_indent = None
for line in block.splitlines():
    if not line.strip():
        continue
    indent = len(line) - len(line.lstrip())
    stripped = line.strip()
    if stripped.startswith("- "):
        if current is None:
            continue
        filters[current].append(stripped[2:].strip().strip("'\""))
    elif stripped.endswith(":"):
        current = stripped[:-1]
        filters[current] = []
        if base_indent is None:
            base_indent = indent

if not filters:
    print("  ❌ 解析出的过滤器是空的")
    sys.exit(1)

# --- picomatch 语义的极简实现（** 跨目录，* 不跨目录，? 单字符）------------
def to_regex(pattern: str) -> str:
    out = []
    i = 0
    while i < len(pattern):
        c = pattern[i]
        if c == "*":
            if pattern[i:i+2] == "**":
                out.append(".*")
                i += 2
                continue
            out.append("[^/]*")
        elif c == "?":
            out.append("[^/]")
        else:
            out.append(re.escape(c))
        i += 1
    return "^" + "".join(out) + "$"

def matches(patterns, candidate: str):
    return [p for p in patterns if re.match(to_regex(p), candidate)]

passed = 0
failed = 0
def check(label, ok, extra=""):
    global passed, failed
    if ok:
        print(f"  ✅ {label}")
        passed += 1
    else:
        print(f"  ❌ {label}{(' — ' + extra) if extra else ''}")
        failed += 1

print("== 变更过滤器（.github/workflows/ci.yml 的 changes job）==")
print("  解析到的过滤器：" + ", ".join(f"{k}({len(v)})" for k, v in filters.items()))

api = filters.get("api", [])
check("存在 api 过滤器", bool(api))

hit = matches(api, "contract/openapi.json")
check("只改 contract/openapi.json 会命中 api 过滤器 → build-api 会运行（契约硬校验就在里面）",
      bool(hit), f"命中的模式：{hit or '（一条都没有）'}")

check("api 过滤器仍覆盖后端源码（src/Pim.Api/**）",
      bool(matches(api, "src/Pim.Api/Program.cs")))
check("api 过滤器仍覆盖 Pim.sln", bool(matches(api, "Pim.sln")))

# 负对照：不许用"让所有路径都命中"的方式蒙混过关
for candidate in ["README.md", "docs/contract-mapping.md", "deploy/x.sh"]:
    check(f"反向对照：{candidate} 不应命中 api 过滤器（否则每个 PR 都会全量构建）",
          not matches(api, candidate))

docker = filters.get("docker", [])
check("docker 过滤器仍覆盖 Dockerfile",
      bool(matches(docker, "src/Pim.Api/Dockerfile")))
check("反向对照：contract/openapi.json 不应命中 docker 过滤器（契约变更不该重建镜像定义）",
      not matches(docker, "contract/openapi.json"))

print("")
print(f"contract-filter: passed={passed} failed={failed}")
sys.exit(1 if failed else 0)
PY
