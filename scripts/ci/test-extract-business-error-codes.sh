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
#   ⑦ 在没有源码时不产出半张表（退出 4，fail-closed）；
#   ⑨ `$"..."` 插值消息完整采集（内部嵌套字符串/逗号不截断）；
#   ⑩ 变量消息如实标注「消息为变量或表达式」，不臆造；
#   ⑪ `类名.常量` 形式的码与配对 `const string ...Message`；
#   ⑫ helper 的状态实参、`Results.Conflict(...)`、`Results.Json(..., statusCode:)` 覆盖 HTTP 状态；
#   ⑬ 反向完整性：源码里有码但抽取器没归因 → 拒绝生成（退出 4 / --check 退出 1）；
#   ⑭ 没有既有文档时生成完整模板（约定段与仓内文档一致）。
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

# ⑨–⑫ review P1：真实的业务码写法不止 `(数字, "字面量")`。旧实现按字面量正则采集，
# 会把插值消息截断、把常量码/变量消息整条漏掉，且状态一律写成 400。
mkdir -p "$TMP/forms"
cat >"$TMP/forms/Forms.cs" <<'CS'
public static class ErrorCodes
{
    public const int MissingCode = 5002;
    public const string MissingMessage = "对象不存在";
}
public class Forms
{
    void A(string status) => throw new DomainException(02057,
        $"任务状态 '{status}' 无效；允许值：{string.Join(", ", TaskStatuses.All)}");
    void B(Exception ex) => throw new DomainException(5001, ex.Message);
    void C() => throw new DomainException(ErrorCodes.MissingCode, ErrorCodes.MissingMessage);
    Task D(HttpContext ctx) => WriteDomainLikeErrorAsync(ctx, StatusCodes.Status409Conflict, 5003, "已存在");
    IResult E() => Results.Conflict(ApiResponse<string>.Error(5004, "重复"));
    IResult F() => Results.Json(ApiResponse<string>.Error(5005, "上游失败"), statusCode: 502);
}
CS

"$PY" "$EXTRACT" --source-root "$TMP/forms" --doc "$TMP/forms.md" >/dev/null 2>&1
RC=$?
[ $RC -eq 0 ] && pass "⑨ 新写法可生成（退出 0）" || fail "⑨ 新写法生成失败（实际 $RC）"
has "⑨ 插值消息完整（内部字符串/逗号不截断）" "任务状态 '{status}' 无效；允许值：{string.Join(\", \", TaskStatuses.All)}" "$TMP/forms.md"
has "⑨ 保留源码里的前导零写法" "| 02057 | 400 |" "$TMP/forms.md"
has "⑩ 变量消息如实标注，不臆造" "| 5001 | 400 | Forms | （消息为变量或表达式，见源码）" "$TMP/forms.md"
has "⑪ 类常量作码 + 配对的 Message 常量" "| 5002 | 400 | Forms | 对象不存在" "$TMP/forms.md"
has "⑫ helper 的状态实参决定 HTTP" "| 5003 | 409 | Forms | 已存在" "$TMP/forms.md"
has "⑫ Results.Conflict(...) 覆盖状态" "| 5004 | 409 | Forms | 重复" "$TMP/forms.md"
has "⑫ Results.Json(statusCode:) 覆盖状态" "| 5005 | 502 | Forms | 上游失败" "$TMP/forms.md"

# ⑬ 反向完整性（review P1）：抽取规则本身漏采时，--check 与生成都必须失败，
# 而不是「文档与生成器互相印证」地静默通过。
cat >>"$TMP/forms/Forms.cs" <<'CS'
public class Odd
{
    void G() => throw new DomainException(Convert.ToInt32("5006"), "解析不了的码参数");
}
CS
"$PY" "$EXTRACT" --source-root "$TMP/forms" --doc "$TMP/forms2.md" >"$TMP/forms2.txt" 2>&1
RC=$?
[ $RC -eq 4 ] && pass "⑬ 漏采时拒绝生成（退出 4）" || fail "⑬ 漏采时应退出 4（实际 $RC）"
has "⑬ 指明漏采的码" "5006" "$TMP/forms2.txt"
[ ! -f "$TMP/forms2.md" ] && pass "⑬ 漏采时不写出半张表" || fail "⑬ 漏采时不应写文档"
"$PY" "$EXTRACT" --source-root "$TMP/forms" --doc "$TMP/forms.md" --check >/dev/null 2>&1
RC=$?
[ $RC -eq 1 ] && pass "⑬ --check 对漏采退出 1" || fail "⑬ --check 漏采时应退出 1（实际 $RC）"

# ⑭ 没有既有文档时用完整模板：约定段必须与仓内 docs/business-error-codes.md 同源，
# 否则重新生成的文档会悄悄退回到「HTTP 状态只由 ResolveDomainStatusCode 决定」的旧说法。
"$PY" "$EXTRACT" --source-root "$TMP/src" --doc "$TMP/fresh.md" >/dev/null 2>&1
has "⑭ 新模板说明调用点显式状态优先" "优先取调用点显式状态" "$TMP/fresh.md"
has "⑭ 新模板说明前导零" "前导零只是源码可读性" "$TMP/fresh.md"
has "⑭ 仓内文档与新模板一致" "优先取调用点显式状态" "$ROOT/docs/business-error-codes.md"

# ⑧ 公开端点识别（review I-1）：.AllowAnonymous() 必须归属到它**自己**的注册调用，
#    不能被上一条语句的 MapGet 借用，也不能受同文件后面的 MapGroup 影响。
mkdir -p "$TMP/pub"
cat >"$TMP/pub/Pub.cs" <<'CS'
public static class Pub
{
    public static void Map(IEndpointRouteBuilder endpoints)
    {
        app.MapGet("/api/client/shell/latest", Handle).AllowAnonymous()
            .Produces(200);
        var g = endpoints.MapGroup("/api/v1/x");
        g.MapGet("/y", H).AllowAnonymous();
        var h = endpoints.MapGroup("/api/v1/z");
        Configure();
        g.MapGet("/v1/k", H).AllowAnonymous();
        endpoints.MapGet("/api/v1/tiles/{z}/{x}/{y}.png", async (
            string z, string x, string y) =>
        {
            if (z == ";") { return Results.StatusCode(502); }
            return Results.File(Array.Empty<byte>(), "image/png");
        }).AllowAnonymous();
    }
}
CS
"$PY" - "$TMP/pub" <<'PY' >"$TMP/pub.txt"
import importlib.util, sys
spec = importlib.util.spec_from_file_location('e', 'scripts/ci/extract-business-error-codes.py')
m = importlib.util.module_from_spec(spec); spec.loader.exec_module(m)
print("\n".join(m.public_paths_from_source(sys.argv[1])))
PY
has() { grep -qxF -- "$2" "$3" && pass "$1" || { fail "$1（找不到：$2）"; sed 's/^/       | /' "$3"; }; }
# 上面的 has() 在这里被改成「整行相等」（用于公开端点的路径清单）；表格断言仍按子串查。
has_part() { grep -qF -- "$2" "$3" && pass "$1" || { fail "$1（找不到：$2）"; sed 's/^/       | /' "$3" | head -30; }; }
has "⑧ 链式 .AllowAnonymous() 归属自身端点" "/api/client/shell/latest" "$TMP/pub.txt"
has "⑧ group 端点拼上前缀" "/api/v1/x/y" "$TMP/pub.txt"
has "⑧ lambda 体内含分号不影响路径" "/api/v1/tiles/{}/{}/{}.png" "$TMP/pub.txt"
hasnt "⑧ 不被同文件后面的 MapGroup 影响" "/api/v1/z/y" "$TMP/pub.txt"
has "⑧ 中间夹了其它语句时仍归属自己的 group" "/api/v1/x/v1/k" "$TMP/pub.txt"
[ "$(wc -l <"$TMP/pub.txt")" -eq 4 ] && pass "⑧ 恰好 4 个公开端点" || fail "⑧ 公开端点数量不对"

# ⑮–⑳ review P1-1/P1-3：业务码不止「DomainException/Error(4–5 位, 字面量)」这一种形态。
#    3 位码、`new XxxError(...)`、匿名对象 / 手拼 JSON 的错误体、BadRequest 包装都要采集；
#    `new XxxException(200|429, …)` 的第一实参不是业务码，不能混进码表。
mkdir -p "$TMP/newforms"
cat >"$TMP/newforms/New.cs" <<'CS'
public class NewForms
{
    static int ResolveDomainStatusCode(int errorCode) => errorCode switch
    {
        409 => StatusCodes.Status409Conflict,
        41002 => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status400BadRequest
    };

    void A() => throw new DomainException(400, "参数缺失");
    void H() => throw new DomainException(409, "状态冲突");
    IResult B() => Results.BadRequest(new ValidationError(41001, "标题不能为空"));
    IResult C() => Results.Json(new { code = 40010, message = "幂等键过长" }, statusCode: 400);
    IResult D() => Results.Content("{\"code\":40501,\"message\":\"解析失败\"}", "application/json", statusCode: 400);
    IResult E() => Results.BadRequest(ApiResponse<string>.Error(41002, "显式状态优先"));
    void F() => _ = new GoogleDriveException(200, "上游状态，不是业务码");
    void G() => _ = new ProviderTransientException(429, "上游状态，不是业务码");
    public const int StatusCode = 41005;
}
CS

"$PY" "$EXTRACT" --source-root "$TMP/newforms" --doc "$TMP/newforms.md" >"$TMP/newforms.txt" 2>&1
RC=$?
[ $RC -eq 0 ] && pass "⑮ 新形态可生成（退出 0）" || fail "⑮ 新形态生成失败（实际 $RC）"
has_part "⑮ 3 位业务码（400）被采集" "| 400 | 400 |" "$TMP/newforms.md"
has_part "⑮ 3 位业务码（409）被采集" "| 409 | 409 |" "$TMP/newforms.md"
has_part "⑯ new XxxError(...) 携带的码被采集" "| 41001 | 400 |" "$TMP/newforms.md"
has_part "⑰ 匿名对象 { code = … } 被采集" "| 40010 | 400 |" "$TMP/newforms.md"
has_part "⑰ 手拼 JSON \"code\":… 被采集" "| 40501 | 400 |" "$TMP/newforms.md"
has_part "⑱ 调用点 Results.BadRequest 覆盖映射里的 409" "| 41002 | 400 |" "$TMP/newforms.md"
hasnt "⑱ 不会写成映射里的 409" "| 41002 | 409 |" "$TMP/newforms.md"
hasnt "⑲ new GoogleDriveException(200, …) 不当作业务码" "| 200 |" "$TMP/newforms.md"
hasnt "⑲ new ProviderTransientException(429, …) 不当作业务码" "| 429 |" "$TMP/newforms.md"
hasnt "⑳ 名字含 Status 的常量不被猜成业务码" "| 41005 |" "$TMP/newforms.md"

# ㉑ helper 转发：码由调用点传入、错误调用在 helper 体内（review P1-1）。
# 语句内匹配只认错误调用本身，安全网也只认错误调用，这类码会两边同时漏掉。
mkdir -p "$TMP/forward"
cat >"$TMP/forward/Forward.cs" <<'CS'
public class Forward
{
    static void ValidateJson(string json, int errorCode, string message)
    {
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException) { throw new DomainException(errorCode, message); }
    }

    void A() => ValidateJson("{}", 41006, "Payload JSON is invalid");
    void B() => ValidateJson("{}", 41007, "Preview JSON is invalid");
}
CS
"$PY" "$EXTRACT" --source-root "$TMP/forward" --doc "$TMP/forward.md" >"$TMP/forward.txt" 2>&1
RC=$?
[ $RC -eq 0 ] && pass "㉑ helper 转发形态可生成（退出 0）" || fail "㉑ helper 转发生成失败（实际 $RC）"
has_part "㉑ helper 转发的码被采集" "| 41006 | 400 |" "$TMP/forward.md"
has_part "㉑ helper 转发的消息取自调用点" "Payload JSON is invalid" "$TMP/forward.md"
hasnt "㉑ helper 的参数名不被当成码" "| errorCode |" "$TMP/forward.md"

echo ""
if [ "$FAIL" -ne 0 ]; then echo "FAILED：$FAIL 项失败，$PASS 项通过"; exit 1; fi
echo "PASSED：$PASS 项通过"
