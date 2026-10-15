using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// WO-AUTH-REGISTER-20261014 REQ-3 / REQ-4：
/// 管理员建号 <c>POST /api/v1/admin/users</c> 与管理员重置密码 <c>POST /api/v1/admin/users/{id}/password</c>。
///
/// 真实宿主装配 + 真实路由 + 真实 admin 鉴权 + 隔离 InMemory 库。
/// </summary>
public sealed class AdminCreateUserE2ETests
{
    private const string Password = AuthRegistrationTestHost.DefaultPassword;

    private static Task<HttpResponseMessage> CreateUserAsync(HttpClient client, object payload)
        => client.PostAsJsonAsync("/api/v1/admin/users", payload);

    private static async Task<(string AccessToken, string RefreshToken)> AdminSessionAsync(
        WebApplicationFactory<Program> factory, string username = "root-admin")
    {
        var session = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, username, role: "admin");
        return (session.AccessToken, session.RefreshToken);
    }

    // ===== REQ-3 =====

    /// <summary>AC-3.1：admin 建号 → 201 + ApiResponse&lt;AdminUserDto&gt; 全字段。</summary>
    [Fact]
    public async Task AC_3_1_AdminCreatesUser_Returns201WithAdminUserDto()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-create"));
        var (adminToken, _) = await AdminSessionAsync(factory);

        var response = await CreateUserAsync(AuthRegistrationTestHost.Authed(factory, adminToken), new
        {
            username = "newbie",
            email = "newbie@example.com",
            displayName = "新同学",
            password = Password,
            role = "admin"
        });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        var data = root.GetProperty("data");
        Assert.Equal("newbie", data.GetProperty("username").GetString());
        Assert.Equal("newbie@example.com", data.GetProperty("email").GetString());
        Assert.Equal("新同学", data.GetProperty("displayName").GetString());
        Assert.Equal("admin", data.GetProperty("role").GetString());
        Assert.True(data.GetProperty("isActive").GetBoolean());
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("id").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(data.GetProperty("createdAt").GetString()));
    }

    /// <summary>AC-3.2：新账号可登录并取到自身信息。</summary>
    [Fact]
    public async Task AC_3_2_CreatedUser_CanLoginAndReadMe()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-login"));
        var (adminToken, _) = await AdminSessionAsync(factory);

        var create = await CreateUserAsync(AuthRegistrationTestHost.Authed(factory, adminToken), new
        {
            username = "newbie",
            email = "newbie@example.com",
            password = Password,
            role = "user"
        });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var session = await AuthRegistrationTestHost.LoginOkAsync(factory, "newbie", Password);
        Assert.False(string.IsNullOrWhiteSpace(session.AccessToken));
        Assert.False(string.IsNullOrWhiteSpace(session.RefreshToken));

        var me = await AuthRegistrationTestHost.Authed(factory, session.AccessToken).GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(me);
        Assert.Equal("newbie", root.GetProperty("data").GetProperty("username").GetString());
    }

    /// <summary>
    /// AC-3.3：六项参数校验逐项生效，且沿用注册的既有码。
    /// 每项单独一条断言（证据逐项列出），不合并成一条「整体通过」。
    /// </summary>
    [Fact]
    public async Task AC_3_3_ValidationErrors_AllSixItems()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-validate"));
        var (adminToken, _) = await AdminSessionAsync(factory);
        var admin = AuthRegistrationTestHost.Authed(factory, adminToken);

        async Task<int> CodeOfAsync(object payload)
        {
            var response = await CreateUserAsync(admin, payload);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var (code, _) = await AuthRegistrationTestHost.ReadErrorAsync(response);
            return code;
        }

        // 1) 用户名空 → 40001
        Assert.Equal(40001, await CodeOfAsync(new
        {
            username = string.Empty, email = "someone@example.com", password = Password, role = "user"
        }));

        // 2) 邮箱格式非法 → 40003
        Assert.Equal(40003, await CodeOfAsync(new
        {
            username = "someone", email = "not-an-email", password = Password, role = "user"
        }));

        // 3) 密码 7 位 → 40004
        Assert.Equal(40004, await CodeOfAsync(new
        {
            username = "someone", email = "someone@example.com", password = "seven77", role = "user"
        }));

        // 4) 用户名 51 字符 → 40005
        Assert.Equal(40005, await CodeOfAsync(new
        {
            username = new string('u', 51), email = "someone@example.com", password = Password, role = "user"
        }));

        // 5) 密码 101 字符 → 40007
        Assert.Equal(40007, await CodeOfAsync(new
        {
            username = "someone", email = "someone@example.com", password = new string('p', 101), role = "user"
        }));

        // 6) 显示名 101 字符 → 40008
        Assert.Equal(40008, await CodeOfAsync(new
        {
            username = "someone",
            email = "someone@example.com",
            displayName = new string('d', 101),
            password = Password,
            role = "user"
        }));

        // 六项全部被拒 → 一个用户都不该落库
        Assert.Null(await AuthRegistrationTestHost.FindUserAsync(factory, "someone"));
    }

    /// <summary>AC-3.4a：用户名重复 → 409 / 01003。</summary>
    [Fact]
    public async Task AC_3_4a_DuplicateUsername_Returns409With01003()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-dup-user"));
        var (adminToken, _) = await AdminSessionAsync(factory);
        await AuthRegistrationTestHost.SeedUserAsync(factory, "taken", email: "taken@example.com");

        var response = await CreateUserAsync(AuthRegistrationTestHost.Authed(factory, adminToken), new
        {
            username = "taken",
            email = "other@example.com",
            password = Password,
            role = "user"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var (code, message) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(01003, code);
        Assert.Equal("用户名已存在", message);
    }

    /// <summary>AC-3.4b：邮箱重复（仅大小写不同）→ 409 / 01004。</summary>
    [Fact]
    public async Task AC_3_4b_DuplicateEmail_CaseInsensitive_Returns409With01004()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-dup-mail"));
        var (adminToken, _) = await AdminSessionAsync(factory);
        await AuthRegistrationTestHost.SeedUserAsync(factory, "taken", email: "shared@example.com");

        var response = await CreateUserAsync(AuthRegistrationTestHost.Authed(factory, adminToken), new
        {
            username = "fresh",
            email = "SHARED@example.com",
            password = Password,
            role = "user"
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var (code, message) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(01004, code);
        Assert.Equal("邮箱已存在", message);
    }

    /// <summary>AC-3.4c：role 传 superuser → 400 / 40041。</summary>
    [Fact]
    public async Task AC_3_4c_InvalidRole_Returns400With40041()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-badrole"));
        var (adminToken, _) = await AdminSessionAsync(factory);

        var response = await CreateUserAsync(AuthRegistrationTestHost.Authed(factory, adminToken), new
        {
            username = "fresh",
            email = "fresh@example.com",
            password = Password,
            role = "superuser"
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (code, message) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40041, code);
        Assert.Equal("无效的角色，仅支持 admin / user", message);
        Assert.Null(await AuthRegistrationTestHost.FindUserAsync(factory, "fresh"));
    }

    /// <summary>AC-3.5：role 未传或传空 → 角色为 user（不默认创建管理员）。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AC_3_5_RoleOmittedOrBlank_DefaultsToUser(bool sendBlankRole)
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-defrole"));
        var (adminToken, _) = await AdminSessionAsync(factory);

        var payload = new Dictionary<string, object?>
        {
            ["username"] = "fresh",
            ["email"] = "fresh@example.com",
            ["password"] = Password
        };
        if (sendBlankRole) payload["role"] = "";

        var response = await CreateUserAsync(AuthRegistrationTestHost.Authed(factory, adminToken), payload);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal("user", root.GetProperty("data").GetProperty("role").GetString());
    }

    /// <summary>AC-3.6：邮箱小写归一；新用户 is_active = true 且未软删除。</summary>
    [Fact]
    public async Task AC_3_6_EmailNormalized_UserActiveAndNotDeleted()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-norm"));
        var (adminToken, _) = await AdminSessionAsync(factory);

        var response = await CreateUserAsync(AuthRegistrationTestHost.Authed(factory, adminToken), new
        {
            username = "Mixed",
            email = "Mixed.Case@Example.COM",
            password = Password,
            role = "user"
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);

        var stored = await AuthRegistrationTestHost.FindUserAsync(factory, "Mixed");
        Assert.NotNull(stored);
        Assert.Equal("mixed.case@example.com", stored!.Email);
        Assert.True(stored.IsActive);
        Assert.Null(stored.DeletedAt);
    }

    /// <summary>AC-3.7（反面）：普通用户建号 → 403 且不新增用户。</summary>
    [Fact]
    public async Task AC_3_7_NonAdmin_Returns403_NoRowWritten()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-403"));
        var member = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "member");
        var before = await AuthRegistrationTestHost.TableCountsAsync(factory);

        var response = await CreateUserAsync(AuthRegistrationTestHost.Authed(factory, member.AccessToken), new
        {
            username = "sneaky",
            email = "sneaky@example.com",
            password = Password,
            role = "admin"
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var after = await AuthRegistrationTestHost.TableCountsAsync(factory);
        Assert.Equal(before.Users, after.Users);
        Assert.Null(await AuthRegistrationTestHost.FindUserAsync(factory, "sneaky"));
    }

    /// <summary>AC-3.8（反面）：匿名建号 → 401 且不新增用户。</summary>
    [Fact]
    public async Task AC_3_8_Anonymous_Returns401_NoRowWritten()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-401"));
        await AuthRegistrationTestHost.SeedUserAsync(factory, "root-admin", role: "admin");
        var before = await AuthRegistrationTestHost.TableCountsAsync(factory);

        var response = await CreateUserAsync(factory.CreateClient(), new
        {
            username = "sneaky",
            email = "sneaky@example.com",
            password = Password,
            role = "admin"
        });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var after = await AuthRegistrationTestHost.TableCountsAsync(factory);
        Assert.Equal(before.Users, after.Users);
        Assert.Null(await AuthRegistrationTestHost.FindUserAsync(factory, "sneaky"));
    }

    // ===== REQ-4 =====

    /// <summary>AC-4.1：admin 重置密码 → 200 + AdminUserDto；新密码可登录、旧密码 401。</summary>
    [Fact]
    public async Task AC_4_1_ResetPassword_NewWorks_OldFails()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-reset"));
        var (adminToken, _) = await AdminSessionAsync(factory);
        var targetId = await AuthRegistrationTestHost.SeedUserAsync(factory, "target");

        var response = await AuthRegistrationTestHost.Authed(factory, adminToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = "brand-new-pass" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var root = await AuthRegistrationTestHost.ReadJsonAsync(response);
        Assert.Equal(targetId, Guid.Parse(root.GetProperty("data").GetProperty("id").GetString()!));

        var (newStatus, _) = await AuthRegistrationTestHost.LoginAsync(factory, "target", "brand-new-pass");
        Assert.Equal(HttpStatusCode.OK, newStatus);

        var (oldStatus, _) = await AuthRegistrationTestHost.LoginAsync(factory, "target", Password);
        Assert.Equal(HttpStatusCode.Unauthorized, oldStatus);
    }

    /// <summary>AC-4.2：目标 id 不存在 → 404 / 40040（沿用既有管理端约定）。</summary>
    [Fact]
    public async Task AC_4_2_UnknownTarget_Returns404With40040()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-reset-404"));
        var (adminToken, _) = await AdminSessionAsync(factory);

        var response = await AuthRegistrationTestHost.Authed(factory, adminToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{Guid.NewGuid()}/password", new { newPassword = "brand-new-pass" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        var (code, message) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40040, code);
        Assert.Equal("用户不存在", message);
    }

    /// <summary>AC-4.3：新密码 7 位 → 400 / 40004，且旧密码仍可登录（校验失败不落库）。</summary>
    [Fact]
    public async Task AC_4_3_ShortNewPassword_RejectedAndOldPasswordKept()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-reset-short"));
        var (adminToken, _) = await AdminSessionAsync(factory);
        var targetId = await AuthRegistrationTestHost.SeedUserAsync(factory, "target");

        var response = await AuthRegistrationTestHost.Authed(factory, adminToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = "seven77" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (code, _) = await AuthRegistrationTestHost.ReadErrorAsync(response);
        Assert.Equal(40004, code);

        var (status, _) = await AuthRegistrationTestHost.LoginAsync(factory, "target", Password);
        Assert.Equal(HttpStatusCode.OK, status);
    }

    /// <summary>AC-4.4（反面）：普通用户 403、匿名 401，两种情形下目标密码均未改变。</summary>
    [Fact]
    public async Task AC_4_4_NonAdminAndAnonymous_Rejected_PasswordUnchanged()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("adm-reset-authz"));
        var member = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "member");
        var targetId = await AuthRegistrationTestHost.SeedUserAsync(factory, "target");

        var asMember = await AuthRegistrationTestHost.Authed(factory, member.AccessToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = "brand-new-pass" });
        Assert.Equal(HttpStatusCode.Forbidden, asMember.StatusCode);

        var asAnonymous = await factory.CreateClient()
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = "brand-new-pass" });
        Assert.Equal(HttpStatusCode.Unauthorized, asAnonymous.StatusCode);

        var (status, _) = await AuthRegistrationTestHost.LoginAsync(factory, "target", Password);
        Assert.Equal(HttpStatusCode.OK, status);
        var (newStatus, _) = await AuthRegistrationTestHost.LoginAsync(factory, "target", "brand-new-pass");
        Assert.Equal(HttpStatusCode.Unauthorized, newStatus);
    }
}
