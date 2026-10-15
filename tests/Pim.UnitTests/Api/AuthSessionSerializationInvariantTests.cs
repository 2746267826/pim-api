using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 会话串行化的**形状护栏**（不是行为证明）。
///
/// <para>
/// 背景：注册流程曾经分两次提交——先写用户行，再写 refresh token。中间那个窗口里，
/// 管理员可以对刚出现的用户执行重置（撤销"当时全部会话"，实际为空），随后注册流程
/// 再插入一条**从未被撤销**的令牌，与 refresh 逃逸是同一类缺口（独立 review 指出）。
/// </para>
/// <para>
/// 修法不是加锁，而是让两条 INSERT 走**同一个 <c>SaveChangesAsync</c>**（EF 会把多条
/// 改动包进一个事务）。窗口因此从"极小"变成"不存在"，这是结构性保证，
/// 所以这里不能用行为用例去证伪"窗口不存在"——改成盯住这个结构本身：
/// 一旦有人把注册的写入重新拆成两次，本用例立刻转红。
/// </para>
/// <para>
/// 诚实标注：源码结构断言只能证明**形状**，不能替代行为验证。它在这里的作用是
/// 防止回归，而不是证明并发正确性（并发正确性由 <see cref="AuthSessionRaceRealDbTests"/>
/// 在真实 PostgreSQL 上验证）。
/// </para>
/// </summary>
public sealed class AuthSessionSerializationInvariantTests
{
    [Fact]
    public void Register_MustWriteUserAndSessionInTheSameCommit()
    {
        var source = File.ReadAllText(FindRepoFile("src/Pim.Api/Endpoints/AuthEndpoints.cs"));

        var start = source.IndexOf("MapPost(\"/register\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "在 AuthEndpoints.cs 中找不到 /register handler");

        var end = source.IndexOf("group.MapPost(\"/login\"", start, StringComparison.Ordinal);
        Assert.True(end > start, "找不到 /register handler 的结束位置");

        var handler = source[start..end];
        var tokenAdd = handler.IndexOf("db.RefreshTokens.Add", StringComparison.Ordinal);
        var firstSave = handler.IndexOf("SaveChangesAsync", StringComparison.Ordinal);

        Assert.True(tokenAdd >= 0, "注册流程不再发放会话令牌？请确认本护栏是否仍然适用");
        Assert.True(firstSave >= 0, "注册流程里找不到 SaveChangesAsync");
        Assert.True(
            tokenAdd < firstSave,
            "注册的用户行与会话令牌必须在**同一次** SaveChangesAsync 里提交。"
            + "拆成两次写会重新打开「管理员在两次写之间重置该用户 → 注册发出的令牌逃过撤销」的窗口"
            + "（与 refresh 逃逸同类，见 PR #434）。");
    }

    /// <summary>从测试输出目录向上找到仓库根（含 Pim.sln）。</summary>
    private static string FindRepoFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Pim.sln")))
                return Path.Combine(directory.FullName, relativePath);
            directory = directory.Parent;
        }

        throw new InvalidOperationException($"未能从 {AppContext.BaseDirectory} 向上找到含 Pim.sln 的仓库根");
    }
}
