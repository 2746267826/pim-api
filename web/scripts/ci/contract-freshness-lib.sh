#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# pim 契约新鲜度设施 · 共用函数库（pim-web / pim-android / pim-windows 复用）
#
# 本文件只定义函数，不执行动作、不写任何文件、不发起网络请求。
# 调用方：scripts/ci/check-contract-freshness.sh（只读 check）、
#         scripts/ci/fetch-contract.sh（refresh）、
#         scripts/ci/test-contract-freshness.sh（离线行为测试）。
#
# 环境变量契约（只允许覆盖「来源」，不允许出现「把仓内文件当权威源」的生产旁路）：
#   PIM_CONTRACT_REPO         默认 2746267826/pim-api
#   PIM_CONTRACT_REF          默认 master（分支 / tag / 40 位小写 commit SHA）
#   PIM_CONTRACT_RAW_BASE     默认 https://raw.githubusercontent.com
#   PIM_CONTRACT_REMOTE       默认 https://github.com/$PIM_CONTRACT_REPO.git（仅用于 ref→commit 解析）
#   PIM_CONTRACT_SOURCE_PATH  默认 contract/openapi.json
#
# 测试专用旁路（生产 CI 会拒绝，见 pim_contract_guard_test_bypass）：
#   PIM_CONTRACT_TEST_MODE=1 且 PIM_CONTRACT_TEST_SOURCE_FILE=<本地文件>
#
# 退出码语义（check）：
#   0 一致 / 1 陈旧或不一致 / 3 解析或抓取失败 / 4 配置或 payload 非法
#
# 安全约束：
#   - raw 字节下载绝不带 Authorization，且 --max-redirs 0（不跟随重定向）。
#   - token 只在内存：通过环境变量读取，只发给 api.github.com，不进 argv / stdout / 文件。
#   - 打印任何 URL 前先做 userinfo 脱敏。
# ---------------------------------------------------------------------------

PIM_CONTRACT_DEFAULT_REPO="2746267826/pim-api"
PIM_CONTRACT_DEFAULT_REF="master"
PIM_CONTRACT_DEFAULT_RAW_BASE="https://raw.githubusercontent.com"
PIM_CONTRACT_DEFAULT_SOURCE_PATH="contract/openapi.json"

PIM_CONTRACT_EXIT_OK=0
PIM_CONTRACT_EXIT_STALE=1
PIM_CONTRACT_EXIT_UNKNOWN=2
PIM_CONTRACT_EXIT_FETCH=3
PIM_CONTRACT_EXIT_CONFIG=4

pim_contract_log()  { printf '%s\n' "$*"; }
pim_contract_err()  { printf '::error::%s\n' "$*" >&2; }
pim_contract_warn() { printf '::warning::%s\n' "$*" >&2; }

# --- 配置读取（默认值可被真实覆盖；cross-repo ≠ bypass） --------------------

pim_contract_repo()        { printf '%s' "${PIM_CONTRACT_REPO:-$PIM_CONTRACT_DEFAULT_REPO}"; }
pim_contract_ref()         { printf '%s' "${PIM_CONTRACT_REF:-$PIM_CONTRACT_DEFAULT_REF}"; }
pim_contract_raw_base()    { printf '%s' "${PIM_CONTRACT_RAW_BASE:-$PIM_CONTRACT_DEFAULT_RAW_BASE}"; }
pim_contract_source_path() { printf '%s' "${PIM_CONTRACT_SOURCE_PATH:-$PIM_CONTRACT_DEFAULT_SOURCE_PATH}"; }
pim_contract_remote()      { printf '%s' "${PIM_CONTRACT_REMOTE:-"https://github.com/$(pim_contract_repo).git"}"; }

# --- 基础判定 ---------------------------------------------------------------

pim_contract_is_sha40() { printf '%s' "$1" | grep -qE '^[0-9a-f]{40}$'; }
pim_contract_is_sha256() { printf '%s' "$1" | grep -qE '^[0-9a-f]{64}$'; }

pim_contract_in_ci() {
  [ "${CI:-}" = "true" ] || [ "${GITHUB_ACTIONS:-}" = "true" ] || [ "${PIM_CONTRACT_STRICT:-0}" = "1" ]
}

# URL 脱敏：把 userinfo（可能含凭据）替换成 <redacted>，其余原样保留。
pim_contract_redact_url() {
  printf '%s' "$1" | sed -E 's#^([a-zA-Z][a-zA-Z0-9+.-]*://)[^/@]*@#\1<redacted>@#'
}

pim_contract_sha256() { sha256sum "$1" | cut -d' ' -f1; }
pim_contract_bytes()  { wc -c < "$1" | tr -d ' '; }

pim_contract_require_python() {
  if ! command -v python3 >/dev/null 2>&1; then
    pim_contract_err "缺少 python3（用于契约 payload / provenance 的 JSON 校验）"
    return 4
  fi
  return 0
}

# --- 测试旁路守卫 -----------------------------------------------------------
# 生产 CI 路径不得把「本地文件当权威源」或「改写仓内产物路径」当开关用；
# 在 CI 环境下出现任何本地旁路一律按配置错误拒绝，而不是「有就用」。
# 允许的真实覆盖只有「来源」：REPO / REF / RAW_BASE / REMOTE / SOURCE_PATH。
pim_contract_guard_test_bypass() { # rc 0 = 允许 / 4 = 拒绝
  local src="${PIM_CONTRACT_TEST_SOURCE_FILE:-}" mode="${PIM_CONTRACT_TEST_MODE:-0}"
  local paths="${PIM_CONTRACT_SNAPSHOT:-}${PIM_CONTRACT_PROVENANCE:-}${PIM_CONTRACT_GENERATED:-}"
  if pim_contract_in_ci; then
    if [ -n "$src" ] || [ "$mode" = "1" ] || [ -n "$paths" ]; then
      pim_contract_err "检测到本地旁路（PIM_CONTRACT_TEST_MODE / PIM_CONTRACT_TEST_SOURCE_FILE / PIM_CONTRACT_SNAPSHOT / PIM_CONTRACT_PROVENANCE / PIM_CONTRACT_GENERATED）与 CI 环境变量同时存在；CI 必须按配置的 repo/ref 访问真实上游并校验仓内默认路径，拒绝按配置错误退出。"
      return 4
    fi
    return 0
  fi
  if [ -n "$src" ] && [ "$mode" != "1" ]; then
    pim_contract_err "PIM_CONTRACT_TEST_SOURCE_FILE 只在测试替身内可用：必须同时设置 PIM_CONTRACT_TEST_MODE=1。"
    return 4
  fi
  return 0
}

# --- ref -> 不可变 40-hex commit SHA（同一次运行只解析一次） ----------------

pim_contract_resolve_ref_git() { # <remote> <ref> -> stdout 40-hex；失败 rc 3
  local remote="$1" ref="$2" out sha
  # `--` 终止选项解析：ref 不可能被当成 git 选项（例如以 '-' 开头的 ref）。
  out="$(git ls-remote "$remote" -- "$ref" 2>/dev/null || true)"
  [ -n "$out" ] || return 3
  sha="$(printf '%s\n' "$out" | awk -v r="refs/heads/$ref"   '$2==r {print $1; exit}')"
  [ -n "$sha" ] || sha="$(printf '%s\n' "$out" | awk -v r="refs/tags/$ref^{}" '$2==r {print $1; exit}')"
  [ -n "$sha" ] || sha="$(printf '%s\n' "$out" | awk -v r="refs/tags/$ref" '$2==r {print $1; exit}')"
  [ -n "$sha" ] || sha="$(printf '%s\n' "$out" | awk -v r="$ref" '$2==r {print $1; exit}')"
  pim_contract_is_sha40 "$sha" || return 3
  printf '%s' "$sha"
}

pim_contract_resolve_ref_api() { # <repo> <ref> -> stdout 40-hex；失败 rc 3
  local repo="$1" ref="$2" sha
  command -v python3 >/dev/null 2>&1 || return 3
  sha="$(python3 - "$repo" "$ref" <<'PY' || true
import json, os, sys, http.client, urllib.parse
repo, ref = sys.argv[1], sys.argv[2]
token = os.environ.get("GITHUB_TOKEN") or os.environ.get("PIM_CONTRACT_TOKEN") or ""
path = "/repos/%s/commits/%s" % (repo, urllib.parse.quote(ref, safe=""))
headers = {"Accept": "application/vnd.github+json", "User-Agent": "pim-contract-freshness"}
if token:
    headers["Authorization"] = "Bearer " + token
try:
    conn = http.client.HTTPSConnection("api.github.com", 443, timeout=30)
    conn.request("GET", path, headers=headers)   # http.client 不跟随重定向
    resp = conn.getresponse()
    body = resp.read()
except Exception:
    sys.exit(3)
if resp.status != 200:
    sys.exit(3)
try:
    sha = json.loads(body.decode("utf-8")).get("sha", "")
except Exception:
    sys.exit(3)
if len(sha) != 40 or any(c not in "0123456789abcdef" for c in sha):
    sys.exit(3)
sys.stdout.write(sha)
PY
)"
  pim_contract_is_sha40 "$sha" || return 3
  printf '%s' "$sha"
}

# --- raw 字节抓取（不带 Auth、不跟随重定向） --------------------------------

pim_contract_fetch_raw() { # <raw_base> <repo> <sha> <source_path> <out_file> -> rc 0/3
  local base="$1" repo="$2" sha="$3" spath="$4" out="$5" url
  url="$base/$repo/$sha/$spath"
  if ! curl -fsS --max-redirs 0 --connect-timeout 20 --max-time 180 "$url" -o "$out"; then
    pim_contract_err "无法从权威源获取字节：$(pim_contract_redact_url "$url")"
    return 3
  fi
  return 0
}

# --- payload 健全性（只判形状，不锁 info.title） ----------------------------
# 仅允许：JSON 对象 / openapi 3.x / paths 非空 / 非 HTML·404·空体。
pim_contract_validate_payload() { # <file> -> rc 0/4，失败原因打到 stderr
  local f="$1"
  python3 - "$f" <<'PY'
import json, sys
path = sys.argv[1]
try:
    with open(path, "rb") as fh:
        raw = fh.read()
except OSError as exc:
    print("payload 不可读：%s" % exc, file=sys.stderr)
    sys.exit(4)
if not raw.strip():
    print("payload 是空体（0 字节或纯空白），不能当作契约比对", file=sys.stderr)
    sys.exit(4)
try:
    doc = json.loads(raw.decode("utf-8"))
except Exception as exc:
    head = raw[:80].decode("utf-8", "replace").replace("\n", " ")
    print("payload 不是合法 JSON（%s）；前 80 字节：%r" % (exc, head), file=sys.stderr)
    sys.exit(4)
if not isinstance(doc, dict):
    print("payload 顶层不是 JSON 对象（%s）" % type(doc).__name__, file=sys.stderr)
    sys.exit(4)
version = doc.get("openapi")
if not isinstance(version, str) or not version.startswith("3."):
    print("payload 不是 OpenAPI 3.x 文档（openapi=%r）" % (version,), file=sys.stderr)
    sys.exit(4)
paths = doc.get("paths")
if not isinstance(paths, dict) or not paths:
    print("payload 的 paths 缺失或为空", file=sys.stderr)
    sys.exit(4)
sys.exit(0)
PY
}

pim_contract_payload_summary() { # <file> -> 一行人类可读摘要（不决定退出码）
  python3 - "$1" <<'PY' 2>/dev/null || true
import json, sys
doc = json.load(open(sys.argv[1], encoding="utf-8"))
methods = {"get", "post", "put", "delete", "patch", "head", "options", "trace"}
ops = sum(1 for item in doc.get("paths", {}).values() for m in item if m in methods)
schemas = len(((doc.get("components") or {}).get("schemas")) or {})
print("operations=%d componentSchemas=%d" % (ops, schemas))
PY
}

# --- 生成物：banner 只由快照内容决定，不含 ref / 不含可移动 SHA --------------

pim_contract_banner() { # <contract_file> -> stdout banner
  local f="$1" sha bytes
  sha="$(pim_contract_sha256 "$f")"
  bytes="$(pim_contract_bytes "$f")"
  cat <<EOF
// 本文件由 scripts/ci/fetch-contract.sh 自动生成，请勿手工编辑。
// 契约快照：contract/openapi.json（sha256 $sha，$bytes 字节）
// 更新方式：在 pim-web 下执行 scripts/ci/fetch-contract.sh 并提交结果。
EOF
}

pim_contract_contract_cli() { # <root> -> 仓内锁定 CLI 的绝对路径（不用 npx）
  printf '%s' "$1/client-web/node_modules/.bin/openapi-typescript"
}

pim_contract_generate_types() { # <cli> <contract> <out> -> rc 0/4
  local cli="$1" contract="$2" out="$3"
  if [ ! -x "$cli" ]; then
    pim_contract_err "未找到仓内锁定的生成器 $cli（请先在 client-web 下执行 npm ci）"
    return 4
  fi
  {
    pim_contract_banner "$contract"
    "$cli" "$contract"
  } > "$out" || { pim_contract_err "openapi-typescript 生成失败（契约：$contract）"; return 4; }
  [ -s "$out" ] || { pim_contract_err "openapi-typescript 产物为空"; return 4; }
  return 0
}

# --- provenance（确定性：无时间戳；只记录来源，不参与「等于今天上游 SHA」判定）

pim_contract_write_provenance() {
  # <out> <repo> <ref> <resolvedRef> <resolvedRefSource> <sourcePath> <sourceUrl> <bytes> <sha256>
  python3 - "$@" <<'PY'
import json, sys
out, repo, ref, sha, rsrc, spath, surl, nbytes, fsha = sys.argv[1:10]
doc = {
    "schemaVersion": 1,
    "repo": repo,
    "ref": ref,
    "resolvedRef": sha,
    "resolvedRefSource": rsrc,
    "sourcePath": spath,
    "sourceUrl": surl,
    "bytes": int(nbytes),
    "sha256": fsha,
}
with open(out, "w", encoding="utf-8") as fh:
    json.dump(doc, fh, ensure_ascii=False, indent=2)
    fh.write("\n")
PY
}

# 校验 provenance 的存在性与字段形状，并核对它与「仓内快照」一致。
# 明确不做的事：不要求 resolvedRef 等于今天的上游 HEAD SHA（上游非契约提交不应导致红）。
# stdout = 人类可读结论；rc 0 = 一致 / 1 = 缺失或不一致。
pim_contract_validate_provenance() {
  # <provenance> <snapshot> <expected_repo> <expected_source_path>
  python3 - "$@" <<'PY'
import hashlib, json, os, re, sys
prov_path, snap_path, exp_repo, exp_spath = sys.argv[1:5]
HEX40 = re.compile(r"^[0-9a-f]{40}$")
HEX64 = re.compile(r"^[0-9a-f]{64}$")

if not os.path.isfile(prov_path):
    print("contract/provenance.json 不存在：刷新来源不可复核，请运行 scripts/ci/fetch-contract.sh 并提交结果")
    sys.exit(1)
try:
    with open(prov_path, encoding="utf-8") as fh:
        doc = json.load(fh)
except Exception as exc:
    print("contract/provenance.json 不是合法 JSON：%s" % exc)
    sys.exit(1)
if not isinstance(doc, dict):
    print("contract/provenance.json 顶层不是 JSON 对象")
    sys.exit(1)

problems = []
def need_str(key, pattern=None):
    val = doc.get(key)
    if not isinstance(val, str) or not val:
        problems.append("%s 缺失或不是非空字符串" % key)
        return None
    if pattern is not None and not pattern.match(val):
        problems.append("%s 形状非法（%r）" % (key, val))
    return val

if doc.get("schemaVersion") != 1:
    problems.append("schemaVersion 必须是 1（实际 %r）" % (doc.get("schemaVersion"),))
repo = need_str("repo")
ref = need_str("ref")
sha = need_str("resolvedRef", HEX40)
need_str("resolvedRefSource")
spath = need_str("sourcePath")
fsha = need_str("sha256", HEX64)
nbytes = doc.get("bytes")
if not isinstance(nbytes, int) or isinstance(nbytes, bool) or nbytes <= 0:
    problems.append("bytes 必须是正整数（实际 %r）" % (nbytes,))
if problems:
    print("contract/provenance.json 字段形状非法：" + "；".join(problems))
    sys.exit(1)

if not os.path.isfile(snap_path):
    print("仓内快照 %s 不存在，无法与 provenance 交叉核对" % snap_path)
    sys.exit(1)
with open(snap_path, "rb") as fh:
    snap = fh.read()
snap_sha = hashlib.sha256(snap).hexdigest()
if doc["sha256"] != snap_sha:
    print("provenance 与仓内快照不一致：provenance.sha256=%s 仓内快照 sha256=%s" % (doc["sha256"], snap_sha))
    sys.exit(1)
if doc["bytes"] != len(snap):
    print("provenance 与仓内快照不一致：provenance.bytes=%d 仓内快照 %d 字节" % (doc["bytes"], len(snap)))
    sys.exit(1)
if repo != exp_repo:
    print("provenance 记录的是另一个来源仓：provenance.repo=%s 本次校验仓=%s" % (repo, exp_repo))
    sys.exit(1)
if spath != exp_spath:
    print("provenance 记录的是另一个契约路径：provenance.sourcePath=%s 本次校验路径=%s" % (spath, exp_spath))
    sys.exit(1)

print("provenance 一致：repo=%s ref=%s resolvedRef=%s bytes=%d sha256=%s（resolvedRef 允许滞后于上游 HEAD，不参与新鲜度判定）"
      % (repo, ref, sha, doc["bytes"], doc["sha256"][:12]))
sys.exit(0)
PY
}
