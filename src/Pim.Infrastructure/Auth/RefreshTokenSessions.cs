using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Auth;

/// <summary>
/// refresh token 的哈希与「按用户批量撤销」原语。
///
/// 哈希口径既有且不变：<c>Base64(SHA256(明文))</c>，明文只在响应里出现一次。
/// 撤销口径见决策记录 §3.3：
///   - 用户改自己密码 → 除「请求携带的那条当前设备会话」外全部撤销；
///   - 管理员重置密码 → 该用户**全部**未撤销会话失效（重置没有「当前设备」概念）。
/// </summary>
public static class RefreshTokenSessions
{
    /// <summary>明文 refresh token → 落库的 <c>token_hash</c>。</summary>
    public static string HashSecret(string refreshToken)
        => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken)));

    /// <summary>
    /// 把该用户所有「未撤销」会话置为已撤销。
    /// <paramref name="keepTokenHash"/> 非空时保留匹配的那一条（当前设备）。
    /// 只改跟踪状态，不落库；由调用方统一 <c>SaveChangesAsync</c>，保证与密码变更同一次提交。
    ///
    /// <para>
    /// <b>必须 <c>IgnoreQueryFilters</c></b>：<see cref="Data.Entities.RefreshTokenEntity"/> 实现了
    /// <c>IUserOwnedEntity</c>，<c>PimDbContext</c> 会为它挂上
    /// <c>CurrentUserId == null || e.UserId == CurrentUserId</c> 的全局查询过滤器。
    /// 管理员重置密码时「当前用户」是管理员，目标用户的 refresh token 会被过滤器挡掉——
    /// 若不加 <c>IgnoreQueryFilters</c>，撤销会静默变成空操作（AC-6.2 直接失败），
    /// 且不会抛任何异常。此处按显式 <c>UserId</c> 收窄，是本文件唯一允许跨用户读会话的地方。
    /// </para>
    /// </summary>
    public static async Task RevokeForUserAsync(
        PimDbContext db, Guid userId, string? keepTokenHash, DateTimeOffset now, CancellationToken ct)
    {
        var active = await db.RefreshTokens
            .IgnoreQueryFilters()
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ToListAsync(ct);

        foreach (var token in active)
        {
            if (keepTokenHash is not null && token.TokenHash == keepTokenHash)
                continue;

            token.RevokedAt = now;
        }
    }
}
