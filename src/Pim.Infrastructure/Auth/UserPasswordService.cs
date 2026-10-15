using Microsoft.EntityFrameworkCore;
using Pim.Core.Operations;
using Pim.Infrastructure.Data;

namespace Pim.Infrastructure.Auth;

/// <summary>用户修改自己密码的结果。</summary>
public enum ChangeOwnPasswordStatus
{
    Ok,

    /// <summary>令牌指向的用户不存在或已停用 → 401。</summary>
    UserNotFound,

    /// <summary>请求携带的 refreshToken 缺失、无效、不属于本人、已撤销或已过期 → 400 / 40065。</summary>
    InvalidSessionToken,

    /// <summary>当前密码不正确 → 400 / 40063。</summary>
    WrongCurrentPassword,

    /// <summary>新密码与当前密码相同 → 400 / 40064。</summary>
    SameAsCurrentPassword,

    /// <summary>
    /// 新密码不满足长度口径（端点已先行按 40004 / 40007 拦下，这里是纵深防御）。
    /// </summary>
    InvalidNewPassword
}

/// <summary>
/// 改密码相关的错误码与提示语（决策记录 §3.1，**已冻结**：编号、HTTP 状态与中文提示语都不可改，
/// 它们是 AC 的判定依据）。集中在此以便端点与测试引用同一份字面量。
/// </summary>
public static class PasswordChangeErrors
{
    public const int WrongCurrentPasswordCode = 40063;
    public const string WrongCurrentPasswordMessage = "当前密码不正确";

    public const int SameAsCurrentCode = 40064;
    public const string SameAsCurrentMessage = "新密码不能与当前密码相同";

    public const int InvalidSessionCode = 40065;
    public const string InvalidSessionMessage = "会话标识无效，请重新登录后再试";
}

public interface IUserPasswordService
{    /// <summary>
    /// 修改**本人**密码：校验当前设备会话 → 校验当前密码 → 换哈希 →
    /// 撤销该用户除当前设备外的全部会话（决策记录 §3.3）。
    /// </summary>
    Task<ChangeOwnPasswordStatus> ChangeOwnPasswordAsync(
        Guid userId, string? currentPassword, string? newPassword, string? refreshToken, CancellationToken ct);
}

/// <summary>
/// 用户自助改密码（WO-AUTH-REGISTER-20261014 REQ-5 / REQ-6）。
///
/// 判定顺序是有意的：
///   1. 先校验请求携带的 refreshToken —— 它既用来识别「当前设备保留哪条会话」，
///      也必须在**任何写操作之前**失败退出，否则无效标识会把用户所有设备踢下线（AC-5.5 / AC-5.9）；
///   2. 再校验当前密码（AC-5.2）；
///   3. 最后才比对「新旧是否相同」（AC-5.3）—— 顺序反了会在当前密码错误时报错码不匹配。
/// 密码长度这类纯入参校验由端点先行完成（沿用 40004 / 40007）。
/// </summary>
public sealed class UserPasswordService(PimDbContext db, IAuditLogService auditLog) : IUserPasswordService
{
    private const string AuditSource = "user";
    private const string AuditAction = "auth.password.change";

    public async Task<ChangeOwnPasswordStatus> ChangeOwnPasswordAsync(
        Guid userId, string? currentPassword, string? newPassword, string? refreshToken, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return ChangeOwnPasswordStatus.UserNotFound;

        // 纵深防御：端点已按 40004 / 40007 拦下长度不合规的新密码。
        if (UserCredentialsValidator.ValidatePassword(newPassword) is not null)
            return ChangeOwnPasswordStatus.InvalidNewPassword;

        if (string.IsNullOrWhiteSpace(refreshToken))
            return ChangeOwnPasswordStatus.InvalidSessionToken;

        var now = DateTimeOffset.UtcNow;
        var tokenHash = RefreshTokenSessions.HashSecret(refreshToken);

        // 当前设备的会话标识：必须属于本人、未撤销、未过期。
        // 不满足时**不做任何写入**——包括不撤销其它会话。
        // IgnoreQueryFilters：RefreshTokenEntity 是按用户隔离的实体（IUserOwnedEntity），
        // 这里显式按 UserId 收窄，避免依赖「当前用户恰好等于本人」这一隐含前提。
        var currentSession = await db.RefreshTokens
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(
                t => t.TokenHash == tokenHash
                     && t.UserId == userId
                     && t.RevokedAt == null
                     && t.ExpiresAt > now,
                ct);
        if (currentSession is null)
            return ChangeOwnPasswordStatus.InvalidSessionToken;

        if (string.IsNullOrWhiteSpace(currentPassword) ||
            !PasswordHasher.Verify(currentPassword, user.PasswordHash))
            return ChangeOwnPasswordStatus.WrongCurrentPassword;

        if (string.Equals(newPassword, currentPassword, StringComparison.Ordinal))
            return ChangeOwnPasswordStatus.SameAsCurrentPassword;

        // 密码变更 + 会话撤销 + 审计由**同一次** SaveChanges 落库：
        // 审计只入队（auditLog.Enqueue），落库由这里显式完成，因此不会出现
        // 「密码已改、其它设备还在线」却没有审计的中间状态；任一条失败整体回滚。
        //
        // 不用显式事务：生产装配开了 EnableRetryOnFailure，NpgsqlRetryingExecutionStrategy
        // 明确拒绝「用户自己发起的事务」——事务内第一条命令就抛 InvalidOperationException
        //（本仓 DeviceManagementService / MobileAnalyticsMaterializationService 有同款注释）。
        user.PasswordHash = PasswordHasher.Hash(newPassword!);
        user.UpdatedAt = now;

        // 只踢其它设备：保留当前设备那条会话
        await RefreshTokenSessions.RevokeForUserAsync(db, userId, currentSession.TokenHash, now, ct);

        // 审计内容不含口令明文，也不含 password_hash
        auditLog.Enqueue(new CreateAuditLogRequest(
            UserId: userId,
            ActorType: AuditActorType.User,
            Action: AuditAction,
            ResourceType: "user",
            ResourceId: user.Id.ToString(),
            Source: AuditSource,
            Result: AuditResult.Success,
            IpAddress: null,
            UserAgent: null,
            CorrelationId: null,
            Metadata: null,
            ErrorCode: null,
            ErrorMessage: null));

        await db.SaveChangesAsync(ct);

        return ChangeOwnPasswordStatus.Ok;
    }
}
