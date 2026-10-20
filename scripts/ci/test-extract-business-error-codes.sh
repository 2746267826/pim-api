#!/usr/bin/env bash
# ---------------------------------------------------------------------------
# 业务错误码提取器 · 行为测试（离线；唯一外部前置是一个可用的 python 3.8+）
#
# 覆盖：
#   ① 从 fixture 采集到 DomainException / ApiResponse.Error 两类写法；
#   ② 按行解析 ResolveDomainStatusCode 的 switch（含 `a or b =>` 多码一支、逗号结尾）；
#   ③ 映射里的码即使源码没直接 throw，也出现在表里；
#   ④ 同码不同消息合并、消息去重；
#   ⑤ 生成物确定（两次运行逐字节相同）；
#   ⑥ --check：与源码一致退出 0；源码新增码后退出 1（文档落后）；
#   ⑦ 在没有源码时不产出半张表（退出 4，fail-closed）。
#
# 用法：bash scripts/ci/test-extract-business-error-codes.sh
# ---------------------------------------------------------------------------
set -uo pipefail
export PYTHONDONTWRITEBYTECODE=1

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "$HERE/../.." && pwd)"
EXTRACT="$ROOT/scripts/ci/extract-business-error-codes.py"

PY=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 \
    && "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then
    PY="$candidate"; break
  fi
done
if [ -z "$PY" ]; then
  echo "需要一个可用的 python 3.8+。" >&2
  exit 2
fi

TMP="$(mktemp -d "${TMPDIR:-/tmp}/pim-bizerr-test.XXXXXX")"
cleanup() { [ "${PIM_BIZERR_TEST_KEEP:-0}" = "1" ] || rm -rf "$TMP"; }
trap cleanup EXIT

PASS=0; FAIL=0
pass() { PASS=$((PASS + 1)); echo "  ok   $1"; }
fail() { FAIL=$((FAIL + 1)); echo "  FAIL $1"; }
has() { grep -qF -- "$2" "$3" && pass "$1" || { fail "$1"; echo "       | 找不到：$2"; help_hint "$3"; }; }
hasnt() { grep -qF -- "$2" "$3" && { fail "$1（不该出现：$2）"; help_hint "$3"; } || pass "$1"; }
help_hint() { sed 's/^/       | /' "$1" | head -30; }

mkdir -p "$TMP/src/Modules"
cat >"$TMP/src/Modules/ThingModule.cs" <<'CS'
public class ThingModule
{
    public static int ResolveDomainStatusCode(int errorCode) => errorCode switch
    {
        40401 or 4004 => StatusCodes.Status404NotFound,
        40101 => StatusCodes.Status401Unauthorized,
        40301 or 40302 or 40303 => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest
    };

    void A() => throw new DomainException(4001, "标题不能为空");
    void B() => throw new DomainException(4001, "标题不能为空");
    void C() => throw new DomainException(4001, "标题过长");
    void D() => _ = ApiResponse<string>.Error(40301, "无权访问");
}
CS

echo "== 业务错误码提取器 · 行为测试 =="

"$PY" "$EXTRACT" --source-root "$TMP/src" --doc "$TMP/doc.md" >/dev/null
has "① 采集到 DomainException 的码" "| 4001 | 400 |" "$TMP/doc.md"
has "① 采集到 ApiResponse.Error 的码" "| 40301 | 403 |" "$TMP/doc.md"
has "② 一行多码映射：4004 → 404" "| 4004 | 404 |" "$TMP/doc.md"
has "② 第二步 40101 → 401" "| 40101 | 401 |" "$TMP/doc.md"
hasnt "② 分支末尾的逗号没有把下一个码带进来（40101 不应是 403）" "| 40101 | 403 |" "$TMP/doc.md"
has "③ 映射里有但源码没 throw 的码也进表（40401）" "| 40401 | 404 |" "$TMP/doc.md"
has "④ 同码两条相同消息只保留一次" "标题不能为空；标题过长" "$TMP/doc.md"
has "④ 同码不同消息合并" "标题过长" "$TMP/doc.md"

"$PY" "$EXTRACT" --source-root "$TMP/src" --doc "$TMP/doc2.md" >/dev/null
cmp -s "$TMP/doc.md" "$TMP/doc2.md" && pass "⑤ 两次生成逐字节相同" || fail "⑤ 生成不确定"

"$PY" "$EXTRACT" --source-root "$TMP/src" --doc "$TMP/doc.md" --check >/dev/null 2>&1
[ $? -eq 0 ] && pass "⑥ --check：与源码一致退出 0" || fail "⑥ --check 一致时应退出 0"

echo '    void E() => throw new DomainException(4999, "新错误");' >>"$TMP/src/Modules/ThingModule.cs"
"$PY" "$EXTRACT" --source-root "$TMP/src" --doc "$TMP/doc.md" --check >/dev/null 2>&1
[ $? -eq 1 ] && pass "⑥ --check：源码新增码后退出 1（文档落后）" || fail "⑥ --check 落后时应退出 1"

"$PY" "$EXTRACT" --source-root "$TMP/no-such-dir" --doc "$TMP/doc3.md" >/dev/null 2>&1
RC=$?
[ $RC -eq 4 ] && pass "⑦ 源码目录不存在退出 4" || fail "⑦ 源码目录不存在应退出 4（实际 $RC）"
[ ! -f "$TMP/doc3.md" ] && pass "⑦ 失败时不产出半张表" || fail "⑦ 失败时不应写文档"

echo ""
if [ "$FAIL" -ne 0 ]; then echo "FAILED：$FAIL 项失败，$PASS 项通过"; exit 1; fi
echo "PASSED：$PASS 项通过"
