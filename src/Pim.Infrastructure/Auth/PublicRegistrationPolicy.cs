using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Auth;

/// <summary>
/// 公开注册策略（WO-AUTH-REGISTER-20261014 REQ-1 / REQ-2）。
///
/// 判定顺序**固定，不可颠倒**（决策记录 §2）：
///   1. <c>users</c> 表为空 → 始终允许注册，首个用户为 <c>admin</c>（全新部署引导，不受开关影响）；
///   2. <c>users</c> 表非空 且 <c>Auth:AllowPublicRegistration = true</c> → 允许注册，新用户为 <c>user</c>；
///   3. <c>users</c> 表非空 且 开关为 <c>false</c>（含未配置）→ 拒绝（403 / 40304），不写库。
///
/// 调用方必须在**参数校验之前**判定，否则关闭状态下非法载荷会先撞上 400 而不是 403。
/// 该开关只影响注册，不影响登录 / 刷新 / 既有账号的任何行为。
/// </summary>
public static class PublicRegistrationPolicy
{
    /// <summary>配置键（已冻结）。</summary>
    public const string AllowPublicRegistrationKey = "Auth:AllowPublicRegistration";

    /// <summary>库非空且未开开关时的错误码（已冻结）。</summary>
    public const int ClosedErrorCode = 40304;

    /// <summary>库非空且未开开关时的提示语（已冻结，逐字）。</summary>
    public const string ClosedMessage = "公开注册已关闭，请联系管理员创建账号";

    /// <summary>开关取值；未配置时为 <c>false</c>（缺省即关闭＝安全默认）。</summary>
    public static bool IsEnabled(IConfiguration configuration)
        => configuration.GetValue(AllowPublicRegistrationKey, false);

    /// <summary>
    /// 是否允许公开注册。库为空时与开关无关（空库引导优先）。
    ///
    /// <para>
    /// <b>「表为空」按物理行判定</b>：<see cref="PimDbContext"/> 给 <c>UserEntity</c> 挂了
    /// <c>DeletedAt == null</c> 的软删除过滤器，若直接用 <c>db.Users.AnyAsync()</c>，
    /// 一个「所有用户都已软删除」的库会被误判成空库，从而在开关为 false 时重新开放匿名注册，
    /// 且新账号还会拿到 admin（与工单「users 表为空时才允许」的条件不符）。
    /// 因此这里用 <c>IgnoreQueryFilters()</c> 数物理行。
    /// </para>
    /// </summary>
    public static async Task<bool> IsAllowedAsync(
        PimDbContext db, IConfiguration configuration, CancellationToken ct)
        => !await db.Users.IgnoreQueryFilters().AnyAsync(ct) || IsEnabled(configuration);
}
