using System.Net;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// WO-AUTH-REGISTER-20261014 REQ-1 / REQ-2：
/// 公开注册收敛为「仅空库引导」+ 由 <c>Auth:AllowPublicRegistration</c> 控制库非空时的行为。
///
/// 真实宿主装配 + 真实路由 + 隔离 InMemory 库；判定顺序（关闭先于参数校验）是本文件的重点。
/// </summary>
public sealed class PublicRegistrationGateE2ETests
{
    private const string Password = AuthRegistrationTestHost.DefaultPassword;

    // ===== REQ-1 =====

    /// <summary>AC-1.1：空库匿名注册合法载荷 → 201 且角色为 admin（全新部署引导）。</summary>
    [Fact]
    public async Task AC_1_1_EmptyDatabase_RegisterSucceeds_AsAdmin()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-empty"), allowPublicRegistration: false);

        var response = await AuthRegistrationTestHost.RegisterAsync(factory.CreateClient(), "first-admin");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal("admin", root.GetProperty("data").GetProperty("user").GetProperty("role").GetString());
    }

    /// <summary>AC-1.2：库非空 + 未开开关 → 403 / 40304，响应体是完整 ApiResponse 信封。</summary>
    [Fact]
    public async Task AC_1_2_NonEmptyDatabase_RegistrationClosed_Returns403With40304()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-closed"), allowPublicRegistration: false);
        await AuthRegistrationTestHost.SeedUserAsync(factory, "existing");

        var response = await AuthRegistrationTestHost.RegisterAsync(factory.CreateClient(), "newcomer");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);

        // 信封四字段齐备（前端按 ApiResponse 解包，缺字段会炸）
        Assert.True(root.TryGetProperty("code", out var code), "响应缺少 code 字段");
        Assert.True(root.TryGetProperty("message", out var message), "响应缺少 message 字段");
        Assert.True(root.TryGetProperty("data", out _), "响应缺少 data 字段");
        Assert.True(root.TryGetProperty("timestamp", out _), "响应缺少 timestamp 字段");

        Assert.Equal(40304, code.GetInt32());
        Assert.Equal("公开注册已关闭，请联系管理员创建账号", message.GetString());
    }

    /// <summary>AC-1.3（反面）：被拒的注册不产生任何写入——users 与 refresh_tokens 行数不变。</summary>
    [Fact]
    public async Task AC_1_3_RejectedRegistration_WritesNoRows()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-nowrite"), allowPublicRegistration: false);
        await AuthRegistrationTestHost.SeedUserAsync(factory, "existing");

        var before = await AuthRegistrationTestHost.TableCountsAsync(factory);
        var response = await AuthRegistrationTestHost.RegisterAsync(factory.CreateClient(), "newcomer");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await AuthRegistrationTestHost.TableCountsAsync(factory);

        Assert.Equal(before.Users, after.Users);
        Assert.Equal(before.RefreshTokens, after.RefreshTokens);
        Assert.Null(await AuthRegistrationTestHost.FindUserAsync(factory, "newcomer"));
    }

    /// <summary>
    /// AC-1.4（反面）：关闭状态下**非法载荷也返回 403 / 40304**（关闭判定先于参数校验），不得 500。
    /// 日志不含口令明文一条在完整应用实测中取证（见 PR「测试」章节），此处覆盖 HTTP 行为。
    /// </summary>
    [Theory]
    [InlineData("", "someone@example.com", Password)]          // 缺用户名
    [InlineData("someone", "not-an-email", Password)]          // 邮箱格式错误
    [InlineData("someone", "someone@example.com", "seven77")]  // 密码 7 位
    public async Task AC_1_4_ClosedGate_WinsOverPayloadValidation(string username, string email, string password)
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-order"), allowPublicRegistration: false);
        await AuthRegistrationTestHost.SeedUserAsync(factory, "existing");

        var response = await AuthRegistrationTestHost.RegisterAsync(
            factory.CreateClient(), username, email, password);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var (code, message) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40304, code);
        Assert.Equal("公开注册已关闭，请联系管理员创建账号", message);
    }

    /// <summary>AC-1.5（反面）：空库 + 非法载荷 → 仍是 400 与既有校验码，且不落库。</summary>
    [Theory]
    [InlineData("", "someone@example.com", Password, 40001)]
    [InlineData("someone", "not-an-email", Password, 40003)]
    [InlineData("someone", "someone@example.com", "seven77", 40004)]
    public async Task AC_1_5_EmptyDatabase_InvalidPayload_ReturnsValidationCodes(
        string username, string email, string password, int expectedCode)
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-empty-invalid"), allowPublicRegistration: false);
        var before = await AuthRegistrationTestHost.TableCountsAsync(factory);

        var response = await AuthRegistrationTestHost.RegisterAsync(
            factory.CreateClient(), username, email, password);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (code, _) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(expectedCode, code);

        var after = await AuthRegistrationTestHost.TableCountsAsync(factory);
        Assert.Equal(before.Users, after.Users);
    }

    // ===== REQ-2 =====

    /// <summary>AC-2.1：开关为 true 且库非空 → 201 且新用户角色为 user。</summary>
    [Fact]
    public async Task AC_2_1_SwitchOn_NonEmptyDatabase_RegisterSucceeds_AsUser()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-on"), allowPublicRegistration: true);
        await AuthRegistrationTestHost.SeedUserAsync(factory, "existing", role: "admin");

        var response = await AuthRegistrationTestHost.RegisterAsync(factory.CreateClient(), "newcomer");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal("user", root.GetProperty("data").GetProperty("user").GetProperty("role").GetString());
    }

    /// <summary>AC-2.2：完全不配置该键 == 显式 false（库非空 → 403 / 40304）。</summary>
    [Fact]
    public async Task AC_2_2_SwitchAbsent_BehavesLikeExplicitFalse()
    {
        using var absent = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-absent"), allowPublicRegistration: null);
        await AuthRegistrationTestHost.SeedUserAsync(absent, "existing");
        var absentResponse = await AuthRegistrationTestHost.RegisterAsync(absent.CreateClient(), "newcomer");

        using var explicitFalse = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-false"), allowPublicRegistration: false);
        await AuthRegistrationTestHost.SeedUserAsync(explicitFalse, "existing");
        var explicitResponse = await AuthRegistrationTestHost.RegisterAsync(explicitFalse.CreateClient(), "newcomer");

        Assert.Equal(HttpStatusCode.Forbidden, absentResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, explicitResponse.StatusCode);
        Assert.Equal(
            await AuthRegistrationTestHost.ReadErrorAsync(explicitResponse),
            await AuthRegistrationTestHost.ReadErrorAsync(absentResponse));
    }

    /// <summary>AC-2.3（反面）：开关关闭时既有账号登录不受影响。</summary>
    [Fact]
    public async Task AC_2_3_SwitchOff_LoginStillWorks()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-login"), allowPublicRegistration: false);
        await AuthRegistrationTestHost.SeedUserAsync(factory, "existing");

        var (status, root) = await AuthRegistrationTestHost.LoginAsync(factory, "existing", Password);

        Assert.Equal(HttpStatusCode.OK, status);
        var data = root!.Value.GetProperty("data");
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("accessToken").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("refreshToken").GetString()));
    }

    /// <summary>AC-2.4（反面）：开关关闭时既有账号 refresh 仍可换出新令牌（旧令牌按既有轮换语义被撤销）。</summary>
    [Fact]
    public async Task AC_2_4_SwitchOff_RefreshStillRotates()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-refresh"), allowPublicRegistration: false);
        await AuthRegistrationTestHost.SeedUserAsync(factory, "existing");
        var session = await AuthRegistrationTestHost.LoginOkAsync(factory, "existing");

        var response = await AuthRegistrationTestHost.RefreshAsync(factory, session.RefreshToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        var newRefreshToken = root.GetProperty("data").GetProperty("refreshToken").GetString();
        Assert.NotNull(newRefreshToken);
        Assert.NotEqual(session.RefreshToken, newRefreshToken);

        // 轮换语义：旧 refresh token 已不可再用
        var reuse = await AuthRegistrationTestHost.RefreshAsync(factory, session.RefreshToken);
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
    }

    /// <summary>AC-2.5（反面）：开关关闭时 /auth/me 仍返回自身信息。</summary>
    [Fact]
    public async Task AC_2_5_SwitchOff_MeStillWorks()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-me"), allowPublicRegistration: false);
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "existing");

        var response = await AuthRegistrationTestHost.Authed(factory, session.AccessToken)
            .GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal("existing", root.GetProperty("data").GetProperty("username").GetString());
    }

    /// <summary>AC-2.6（反面）：开关为 true 也不得改变空库引导语义——首个用户仍是 admin。</summary>
    [Fact]
    public async Task AC_2_6_SwitchOn_EmptyDatabase_FirstUserStillAdmin()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-on-empty"), allowPublicRegistration: true);

        var session = await AuthRegistrationTestHost.RegisterOkAsync(factory, "first-admin");

        var stored = await AuthRegistrationTestHost.FindUserAsync(factory, "first-admin");
        Assert.NotNull(stored);
        Assert.Equal("admin", stored!.Role);
        Assert.Equal(session.UserId, stored.Id);
    }

    /// <summary>
    /// 补强（review 发现）：「users 表为空」按**物理行**判定。
    /// 库里只剩软删除用户时不算空库，开关缺省时不得重新开放匿名注册，更不得让新账号拿到 admin。
    /// （`UserEntity` 带 `DeletedAt == null` 软删除过滤器，用过滤后的 `db.Users` 会误判成空库。）
    /// </summary>
    [Fact]
    public async Task SoftDeletedOnlyDatabase_IsNotEmpty_RegistrationStaysClosed()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-soft"), allowPublicRegistration: false);
        var goneId = await AuthRegistrationTestHost.SeedUserAsync(factory, "gone", role: "admin");
        await AuthRegistrationTestHost.SoftDeleteUserAsync(factory, goneId);

        var before = await AuthRegistrationTestHost.TableCountsAsync(factory);
        var response = await AuthRegistrationTestHost.RegisterAsync(factory.CreateClient(), "newcomer");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var (code, message) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40304, code);
        Assert.Equal("公开注册已关闭，请联系管理员创建账号", message);

        var after = await AuthRegistrationTestHost.TableCountsAsync(factory);
        Assert.Equal(before.Users, after.Users);
        Assert.Null(await AuthRegistrationTestHost.FindUserAsync(factory, "newcomer"));
        // 软删除用户不得被"复活"成 admin
        var gone = await AuthRegistrationTestHost.FindUserAsync(factory, "gone");
        Assert.Equal("admin", gone!.Role);
    }

    /// <summary>
    /// 补强（review 第 2 轮）：「库为空」的口径在**门禁**与**角色判定**两处必须一致。
    /// 只剩软删除用户 + 开关为 true 时，门禁放行（库非空），新用户角色必须是 `user`——
    /// 若角色判定仍用带软删除过滤器的查询，会把新用户错发成 admin（决策记录 §2）。
    /// </summary>
    [Fact]
    public async Task SoftDeletedOnlyDatabase_SwitchOn_NewUserIsPlainUser()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-soft-on"), allowPublicRegistration: true);
        var goneId = await AuthRegistrationTestHost.SeedUserAsync(factory, "gone", role: "admin");
        await AuthRegistrationTestHost.SoftDeleteUserAsync(factory, goneId);

        var response = await AuthRegistrationTestHost.RegisterAsync(factory.CreateClient(), "newcomer");

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal("user", root.GetProperty("data").GetProperty("user").GetProperty("role").GetString());
    }

    /// <summary>补强：开关只影响注册，不影响既有端点对未登录访问的 401 语义。</summary>
    [Fact]
    public async Task SwitchOff_AnonymousMe_Still401()
    {
        using var factory = AuthRegistrationTestHost.Create(
            AuthRegistrationTestHost.NewDbName("reg-anon"), allowPublicRegistration: false);

        var response = await factory.CreateClient().GetAsync("/api/v1/auth/me");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
