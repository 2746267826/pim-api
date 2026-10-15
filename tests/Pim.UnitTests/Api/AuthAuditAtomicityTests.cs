using System.Net;
using System.Net.Http.Json;
using Pim.Core.Operations;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// WO-AUTH-REGISTER-20261014 REQ-7（review 补强）：审计写入失败时的**原子性**。
///
/// 三条新流程都先 <c>auditLog.Enqueue(...)</c> 只入队、再由业务方**一次** SaveChanges 提交，
/// 所以「审计写失败」必须表现为「业务改动一点都没落库」，而不是
/// 「密码/用户已改，但接口报错、审计缺失」。
///
/// <para>
/// <b>本测试能证明与不能证明的边界（review 要求写明）</b>：
/// 它证明的是「审计侧失败时，业务改动不会留下部分写入」——即控制流上不存在
/// 「先提交业务、再写审计」的窗口。它**不**证明 PostgreSQL 在审计 INSERT 因
/// 死锁/约束/连接中断等**数据库级**错误失败时的回滚行为：测试宿主是 InMemory，
/// 既没有事务也没有唯一约束。数据库级原子性由「单次 SaveChanges」的设计保证，
/// 并由真实 Postgres 上的完整应用实测（`_evidence/full-flow-stage1.md`）覆盖到成功路径；
/// 「审计 INSERT 在数据库层失败 ⇒ 业务回滚」这一支仍属未直接取证的分支。
/// </para>
/// </summary>
public sealed class AuthAuditAtomicityTests
{
    private const string Password = AuthRegistrationTestHost.DefaultPassword;
    private const string NewPassword = "brand-new-pass";

    /// <summary>必定失败的审计实现（故障注入）：入队与直接记录两条路径都失败。</summary>
    private sealed class FailingAuditLogService : IAuditLogService
    {
        public Task<AuditLogDto> RecordAsync(CreateAuditLogRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("fault injection: audit write failed");

        public AuditLogDto Enqueue(CreateAuditLogRequest request)
            => throw new InvalidOperationException("fault injection: audit write failed");
    }

    /// <summary>管理员建号：审计失败 → 接口失败，且 users 表不新增行。</summary>
    [Fact]
    public async Task AdminCreate_AuditFails_NoUserIsCreated()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("atomic-create"),
            auditLog: new FailingAuditLogService());
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "root-admin", role: "admin");
        var before = await AuthRegistrationTestHost.TableCountsAsync(factory);

        var response = await AuthRegistrationTestHost.Authed(factory, admin.AccessToken)
            .PostAsJsonAsync("/api/v1/admin/users", new
            {
                username = "newbie",
                email = "newbie@example.com",
                password = Password,
                role = "user"
            });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var after = await AuthRegistrationTestHost.TableCountsAsync(factory);
        Assert.Equal(before.Users, after.Users);
        Assert.Equal(before.AuditLogs, after.AuditLogs);
        Assert.Null(await AuthRegistrationTestHost.FindUserAsync(factory, "newbie"));
    }

    /// <summary>管理员重置密码：审计失败 → 密码未改、会话未撤销。</summary>
    [Fact]
    public async Task AdminReset_AuditFails_PasswordAndSessionsUntouched()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("atomic-reset"),
            auditLog: new FailingAuditLogService());
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "root-admin", role: "admin");
        var targetId = await AuthRegistrationTestHost.SeedUserAsync(factory, "target");
        var target = await AuthRegistrationTestHost.LoginOkAsync(factory, "target");
        var activeBefore = await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, targetId);

        var response = await AuthRegistrationTestHost.Authed(factory, admin.AccessToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = NewPassword });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // 会话一条都没被撤销。注意顺序：必须先判会话，再做登录断言——
        // 一次成功的登录本身就会新建一条会话。
        Assert.Equal(activeBefore, await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, targetId));
        Assert.Equal(HttpStatusCode.OK,
            (await AuthRegistrationTestHost.RefreshAsync(factory, target.RefreshToken)).StatusCode);

        // 密码未改
        var (oldOk, _) = await AuthRegistrationTestHost.LoginAsync(factory, "target", Password);
        Assert.Equal(HttpStatusCode.OK, oldOk);
        var (newOk, _) = await AuthRegistrationTestHost.LoginAsync(factory, "target", NewPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, newOk);
    }

    /// <summary>用户改自己密码：审计失败 → 密码未改、只踢其它设备的撤销也没发生。</summary>
    [Fact]
    public async Task SelfChangePassword_AuditFails_PasswordAndSessionsUntouched()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("atomic-self"),
            auditLog: new FailingAuditLogService());
        var deviceA = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");
        var deviceB = await AuthRegistrationTestHost.LoginOkAsync(factory, "alice");
        var activeBefore = await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, deviceA.UserId);

        var response = await AuthRegistrationTestHost.Authed(factory, deviceA.AccessToken)
            .PostAsJsonAsync("/api/v1/auth/password", new
            {
                currentPassword = Password,
                newPassword = NewPassword,
                refreshToken = deviceA.RefreshToken
            });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        // 会话一条都没被撤销：两台设备都还活着（先于任何新建会话的登录断言）
        Assert.Equal(activeBefore, await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, deviceA.UserId));
        Assert.Equal(HttpStatusCode.OK,
            (await AuthRegistrationTestHost.RefreshAsync(factory, deviceA.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await AuthRegistrationTestHost.RefreshAsync(factory, deviceB.RefreshToken)).StatusCode);

        // 密码未改
        var (oldOk, _) = await AuthRegistrationTestHost.LoginAsync(factory, "alice", Password);
        Assert.Equal(HttpStatusCode.OK, oldOk);
        var (newOk, _) = await AuthRegistrationTestHost.LoginAsync(factory, "alice", NewPassword);
        Assert.Equal(HttpStatusCode.Unauthorized, newOk);
    }
}
