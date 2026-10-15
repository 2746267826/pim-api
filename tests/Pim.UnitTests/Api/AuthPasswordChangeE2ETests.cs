using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// WO-AUTH-REGISTER-20261014 REQ-5 / REQ-6：
/// 用户修改自己密码 <c>POST /api/v1/auth/password</c>，以及改密码后的会话撤销口径
/// （只踢其它设备，当前设备保持登录）。
///
/// 真实宿主装配 + 真实路由 + 真实鉴权 + 隔离 InMemory 库。
/// </summary>
public sealed class AuthPasswordChangeE2ETests
{
    private const string Password = AuthRegistrationTestHost.DefaultPassword;
    private const string NewPassword = "brand-new-pass";

    private static Task<HttpResponseMessage> ChangePasswordAsync(
        HttpClient client, string currentPassword, string newPassword, string? refreshToken, bool omitRefreshToken = false)
    {
        var payload = new Dictionary<string, object?>
        {
            ["currentPassword"] = currentPassword,
            ["newPassword"] = newPassword
        };
        if (!omitRefreshToken) payload["refreshToken"] = refreshToken;

        return client.PostAsJsonAsync("/api/v1/auth/password", payload);
    }

    private static async Task AssertLoginWorksAsync(
        WebApplicationFactory<Program> factory, string username, string password, bool expected)
    {
        var (status, _) = await AuthRegistrationTestHost.LoginAsync(factory, username, password);
        Assert.Equal(expected ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, status);
    }

    // ===== REQ-5 =====

    /// <summary>AC-5.1：登录用户改密码成功 → 新密码可登录、旧密码 401。</summary>
    [Fact]
    public async Task AC_5_1_ChangeOwnPassword_Succeeds()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-change"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");

        var response = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, session.AccessToken), Password, NewPassword, session.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await AssertLoginWorksAsync(factory, "alice", NewPassword, expected: true);
        await AssertLoginWorksAsync(factory, "alice", Password, expected: false);
    }

    /// <summary>AC-5.2：当前密码不正确 → 400 / 40063，且原密码仍可登录。</summary>
    [Fact]
    public async Task AC_5_2_WrongCurrentPassword_Returns40063()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-wrong"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");

        var response = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, session.AccessToken), "not-the-password", NewPassword, session.RefreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (code, message) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40063, code);
        Assert.Equal("当前密码不正确", message);
        await AssertLoginWorksAsync(factory, "alice", Password, expected: true);
    }

    /// <summary>AC-5.3：新密码与当前密码相同 → 400 / 40064，且密码未改变。</summary>
    [Fact]
    public async Task AC_5_3_NewPasswordSameAsCurrent_Returns40064()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-same"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");

        var response = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, session.AccessToken), Password, Password, session.RefreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (code, message) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40064, code);
        Assert.Equal("新密码不能与当前密码相同", message);
        await AssertLoginWorksAsync(factory, "alice", Password, expected: true);
    }

    /// <summary>AC-5.4（反面）：未携带登录态 → 401，且不改变任何账号密码。</summary>
    [Fact]
    public async Task AC_5_4_Anonymous_Returns401_PasswordUnchanged()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-anon"));
        await AuthRegistrationTestHost.SeedUserAsync(factory, "alice");

        var response = await ChangePasswordAsync(factory.CreateClient(), Password, NewPassword, refreshToken: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        await AssertLoginWorksAsync(factory, "alice", Password, expected: true);
    }

    /// <summary>AC-5.5（反面）：refreshToken 缺失或为空 → 400 / 40065，且不撤销任何会话。</summary>
    [Fact]
    public async Task AC_5_5_MissingRefreshToken_Returns40065_NoRevocation()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-no-token"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");
        var before = await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, session.UserId);

        var missing = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, session.AccessToken), Password, NewPassword,
            refreshToken: null, omitRefreshToken: true);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var (missingCode, missingMessage) = await AuthRegistrationTestHost.ReadErrorAsync(missing);
        Assert.Equal(40065, missingCode);
        Assert.Equal("会话标识无效，请重新登录后再试", missingMessage);

        var empty = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, session.AccessToken), Password, NewPassword, refreshToken: "");
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(40065, (await AuthRegistrationTestHost.ReadErrorAsync(empty)).Code);

        Assert.Equal(before, await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, session.UserId));
        Assert.Equal(HttpStatusCode.OK, (await AuthRegistrationTestHost.RefreshAsync(factory, session.RefreshToken)).StatusCode);
    }

    /// <summary>
    /// AC-5.6（反面）：refreshToken 属于其他用户 → 400 / 40065，两边的会话状态都不变。
    /// review 补强：必须同时证明**请求者自己的会话没有被牵连撤销**（否则实现误撤销也检不出来）。
    /// </summary>
    [Fact]
    public async Task AC_5_6_OtherUsersRefreshToken_Returns40065_NothingChanges()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-other-token"));
        var alice = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");
        var bob = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "bob");
        var aliceTokensBefore = await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, alice.UserId);
        var bobTokensBefore = await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, bob.UserId);

        var response = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, alice.AccessToken), Password, NewPassword, bob.RefreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(40065, (await AuthRegistrationTestHost.ReadErrorAsync(response)).Code);

        // 会话状态未被改变。注意顺序：必须先做会话计数与刷新断言，
        // 再跑登录断言——一次成功的登录本身就会新建一条会话。
        Assert.Equal(aliceTokensBefore, await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, alice.UserId));
        Assert.Equal(bobTokensBefore, await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, bob.UserId));
        Assert.Equal(HttpStatusCode.OK, (await AuthRegistrationTestHost.RefreshAsync(factory, bob.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await AuthRegistrationTestHost.RefreshAsync(factory, alice.RefreshToken)).StatusCode);

        // 双方密码都未改变
        await AssertLoginWorksAsync(factory, "alice", Password, expected: true);
        await AssertLoginWorksAsync(factory, "bob", Password, expected: true);
    }

    /// <summary>AC-5.7（反面）：新密码 7 位 / 101 位 → 既有长度校验码，且密码未改变。</summary>
    [Fact]
    public async Task AC_5_7_NewPasswordLengthValidated()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-length"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");
        var client = AuthRegistrationTestHost.Authed(factory, session.AccessToken);

        var tooShort = await ChangePasswordAsync(client, Password, "seven77", session.RefreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(40004, (await AuthRegistrationTestHost.ReadErrorAsync(tooShort)).Code);

        var tooLong = await ChangePasswordAsync(client, Password, new string('p', 101), session.RefreshToken);
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Equal(40007, (await AuthRegistrationTestHost.ReadErrorAsync(tooLong)).Code);

        await AssertLoginWorksAsync(factory, "alice", Password, expected: true);
    }

    /// <summary>
    /// AC-5.9（反面）：refreshToken 属于本人但已被撤销（前端缓存旧值时的典型情形）
    /// → 400 / 40065；密码未改变，且**不撤销**该用户其它会话。
    /// </summary>
    [Fact]
    public async Task AC_5_9_RevokedOwnRefreshToken_Returns40065_NoSideEffects()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-stale"));
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");

        // 制造「旧值」：走一次刷新，原 refresh token 在服务端被轮换撤销
        var refreshed = await AuthRegistrationTestHost.RefreshAsync(factory, session.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        var currentRefreshToken = (await AuthRegistrationTestHost.ReadJsonAsync(refreshed))
            .GetProperty("data").GetProperty("refreshToken").GetString()!;

        var response = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, session.AccessToken), Password, NewPassword, session.RefreshToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (code, _) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40065, code);

        // 新密码未生效、旧密码仍可用
        await AssertLoginWorksAsync(factory, "alice", Password, expected: true);
        // 其它会话没有被牵连撤销：轮换出来的当前令牌仍可用
        Assert.Equal(HttpStatusCode.OK,
            (await AuthRegistrationTestHost.RefreshAsync(factory, currentRefreshToken)).StatusCode);
    }

    /// <summary>
    /// AC-5.9（反面）之二（review 补强）：refreshToken 属于本人但**已过期且未被撤销**
    /// → 400 / 40065；密码未改变，且其它会话不被牵连。
    /// AC-9.4 明确要求覆盖「已撤销**或**已过期」两种分支，这里补上过期分支。
    /// </summary>
    [Fact]
    public async Task AC_5_9b_ExpiredOwnRefreshToken_Returns40065_NoSideEffects()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-expired"));
        var alice = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");

        const string expiredToken = "expired-but-not-revoked-token";
        await AuthRegistrationTestHost.SeedRefreshTokenAsync(
            factory, alice.UserId, expiredToken, DateTimeOffset.UtcNow.AddMinutes(-1));

        var activeBefore = await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, alice.UserId);

        var response = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, alice.AccessToken), Password, NewPassword, expiredToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(40065, (await AuthRegistrationTestHost.ReadErrorAsync(response)).Code);

        // 一条会话都没被撤销（先于任何会新建会话的登录断言）
        Assert.Equal(activeBefore, await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, alice.UserId));
        Assert.Equal(HttpStatusCode.OK,
            (await AuthRegistrationTestHost.RefreshAsync(factory, alice.RefreshToken)).StatusCode);

        // 密码未改变
        await AssertLoginWorksAsync(factory, "alice", Password, expected: true);
        await AssertLoginWorksAsync(factory, "alice", NewPassword, expected: false);
    }

    // ===== REQ-6 =====

    /// <summary>AC-6.1：两台设备场景——改密码的当前设备仍能刷新，另一台被踢。</summary>
    [Fact]
    public async Task AC_6_1_OnlyOtherDevicesRevoked()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-two-devices"));
        var deviceA = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");
        var deviceB = await AuthRegistrationTestHost.LoginOkAsync(factory, "alice");
        Assert.NotEqual(deviceA.RefreshToken, deviceB.RefreshToken);

        var response = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, deviceA.AccessToken), Password, NewPassword, deviceA.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // 当前设备（T_A）继续有效
        var refreshA = await AuthRegistrationTestHost.RefreshAsync(factory, deviceA.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshA.StatusCode);

        // 另一台（T_B）被踢
        var refreshB = await AuthRegistrationTestHost.RefreshAsync(factory, deviceB.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, refreshB.StatusCode);
    }

    /// <summary>AC-6.2：管理员重置密码后，该用户**全部**未撤销 refresh token 均不可用。</summary>
    [Fact]
    public async Task AC_6_2_AdminReset_RevokesAllSessions()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-reset-all"));
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "root-admin", role: "admin");
        var targetId = await AuthRegistrationTestHost.SeedUserAsync(factory, "target");
        var deviceA = await AuthRegistrationTestHost.LoginOkAsync(factory, "target");
        var deviceB = await AuthRegistrationTestHost.LoginOkAsync(factory, "target");

        var reset = await AuthRegistrationTestHost.Authed(factory, admin.AccessToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized,
            (await AuthRegistrationTestHost.RefreshAsync(factory, deviceA.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await AuthRegistrationTestHost.RefreshAsync(factory, deviceB.RefreshToken)).StatusCode);
        Assert.Equal(0, await AuthRegistrationTestHost.ActiveRefreshTokenCountAsync(factory, targetId));
    }

    /// <summary>AC-6.3（反面）：撤销只作用于目标用户——其他用户的会话不受影响。</summary>
    [Fact]
    public async Task AC_6_3_OtherUsersUnaffected()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-scope"));
        var alice = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");
        var carol = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "carol");
        Assert.NotEqual(alice.UserId, carol.UserId);

        var response = await ChangePasswordAsync(
            AuthRegistrationTestHost.Authed(factory, alice.AccessToken), Password, NewPassword, alice.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal(HttpStatusCode.OK,
            (await AuthRegistrationTestHost.RefreshAsync(factory, carol.RefreshToken)).StatusCode);
    }

    /// <summary>
    /// AC-6.3（反面）之二（review 补强）：**管理员重置**这条路径同样只作用于目标用户——
    /// 目标用户全部会话失效，但操作者（管理员）与无关第三方的会话都仍可用。
    /// </summary>
    [Fact]
    public async Task AC_6_3b_AdminReset_DoesNotAffectOtherUsers()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("pwd-scope-reset"));
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "root-admin", role: "admin");
        var targetId = await AuthRegistrationTestHost.SeedUserAsync(factory, "target");
        var target = await AuthRegistrationTestHost.LoginOkAsync(factory, "target");
        var carol = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "carol");

        var reset = await AuthRegistrationTestHost.Authed(factory, admin.AccessToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = NewPassword });
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);

        // 目标用户被踢
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await AuthRegistrationTestHost.RefreshAsync(factory, target.RefreshToken)).StatusCode);
        // 操作者与第三方不受影响
        Assert.Equal(HttpStatusCode.OK,
            (await AuthRegistrationTestHost.RefreshAsync(factory, admin.RefreshToken)).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await AuthRegistrationTestHost.RefreshAsync(factory, carol.RefreshToken)).StatusCode);
    }
}
