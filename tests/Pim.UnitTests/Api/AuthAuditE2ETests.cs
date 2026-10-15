using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// WO-AUTH-REGISTER-20261014 REQ-7：三条密码相关操作的审计记录，
/// 以及「审计内容不得包含口令明文或 password_hash」的反面验收。
/// </summary>
public sealed class AuthAuditE2ETests
{
    private const string Password = AuthRegistrationTestHost.DefaultPassword;
    private const string NewPassword = "brand-new-pass";

    private static IEnumerable<string> AllText(Pim.Infrastructure.Data.Entities.AuditLogEntity row)
    {
        yield return row.UserId?.ToString() ?? string.Empty;
        yield return row.ActorType;
        yield return row.Action;
        yield return row.ResourceType;
        yield return row.ResourceId ?? string.Empty;
        yield return row.Source;
        yield return row.Result;
        yield return row.IpAddress ?? string.Empty;
        yield return row.UserAgent ?? string.Empty;
        yield return row.CorrelationId ?? string.Empty;
        yield return row.MetadataJson;
        yield return row.ErrorMessage ?? string.Empty;
    }

    /// <summary>AC-7.1：管理员建号成功后新增一条审计记录（操作者、动作、资源、source=admin）。</summary>
    [Fact]
    public async Task AC_7_1_AdminCreate_WritesAuditRecord()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("audit-create"));
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "root-admin", role: "admin");
        var before = (await AuthRegistrationTestHost.AuditLogsAsync(factory)).Count;

        var create = await AuthRegistrationTestHost.Authed(factory, admin.AccessToken)
            .PostAsJsonAsync("/api/v1/admin/users", new
            {
                username = "newbie",
                email = "newbie@example.com",
                password = Password,
                role = "user"
            });
        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);
        var createdId = Guid.Parse((await AuthRegistrationTestHost.ReadJsonAsync(create))
            .GetProperty("data").GetProperty("id").GetString()!);

        var logs = await AuthRegistrationTestHost.AuditLogsAsync(factory);
        Assert.Equal(before + 1, logs.Count);

        var entry = Assert.Single(logs, l => l.Action.Contains("create", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(admin.UserId, entry.UserId);
        Assert.Equal("user", entry.ResourceType);
        Assert.Equal(createdId.ToString(), entry.ResourceId);
        Assert.Equal("admin", entry.Source);
    }

    /// <summary>AC-7.2：管理员重置密码成功后新增一条审计记录，resourceId 为目标用户 id。</summary>
    [Fact]
    public async Task AC_7_2_AdminReset_WritesAuditRecord()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("audit-reset"));
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "root-admin", role: "admin");
        var targetId = await AuthRegistrationTestHost.SeedUserAsync(factory, "target");
        var before = (await AuthRegistrationTestHost.AuditLogsAsync(factory)).Count;

        var reset = await AuthRegistrationTestHost.Authed(factory, admin.AccessToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = NewPassword });
        Assert.Equal(System.Net.HttpStatusCode.OK, reset.StatusCode);

        var logs = await AuthRegistrationTestHost.AuditLogsAsync(factory);
        Assert.Equal(before + 1, logs.Count);

        var entry = Assert.Single(logs, l => l.Action.Contains("password", StringComparison.OrdinalIgnoreCase)
                                             && l.Source == "admin");
        Assert.Equal(targetId.ToString(), entry.ResourceId);
        Assert.Equal(admin.UserId, entry.UserId);
    }

    /// <summary>AC-7.3：用户修改自己密码成功后新增一条审计记录，resourceId 为该用户 id。</summary>
    [Fact]
    public async Task AC_7_3_SelfPasswordChange_WritesAuditRecord()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("audit-self"));
        var alice = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "alice");
        var before = (await AuthRegistrationTestHost.AuditLogsAsync(factory)).Count;

        var response = await AuthRegistrationTestHost.Authed(factory, alice.AccessToken)
            .PostAsJsonAsync("/api/v1/auth/password", new
            {
                currentPassword = Password,
                newPassword = NewPassword,
                refreshToken = alice.RefreshToken
            });
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        var logs = await AuthRegistrationTestHost.AuditLogsAsync(factory);
        Assert.Equal(before + 1, logs.Count);

        var entry = Assert.Single(logs, l => l.Action.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(alice.UserId.ToString(), entry.ResourceId);
        Assert.Equal(alice.UserId, entry.UserId);
    }

    /// <summary>
    /// AC-7.4（反面）：三条审计记录里都不出现口令明文，也不出现库里的 password_hash。
    ///
    /// review 补强：哈希必须**在每一步操作之后立刻捕获**。只在最后读一次的话，
    /// 管理员重置产生的那一版哈希已被用户自助改密覆盖，重置审计即便泄漏了中间哈希也检不出来。
    /// </summary>
    [Fact]
    public async Task AC_7_4_AuditRowsContainNoPlaintextOrHash()
    {
        using var factory = AuthRegistrationTestHost.Create(AuthRegistrationTestHost.NewDbName("audit-secrets"));
        var admin = await AuthRegistrationTestHost.SeedAndLoginAsync(factory, "root-admin", role: "admin");
        var targetId = await AuthRegistrationTestHost.SeedUserAsync(factory, "target");

        // 三种口令值：建号用的初始口令、重置用的新口令、用户自改用到的当前/新口令
        const string createdPassword = "created-secret-1";
        const string resetPassword = "reset-secret-2";
        const string selfNewPassword = "self-secret-3";

        // 每一步操作后立刻捕获当时库里的 password_hash（含会被后续操作覆盖的中间版本）
        var hashes = new List<string>();
        async Task CaptureHashAsync(string username)
        {
            var user = await AuthRegistrationTestHost.FindUserAsync(factory, username);
            if (user is not null) hashes.Add(user.PasswordHash);
        }

        await CaptureHashAsync("root-admin");
        await CaptureHashAsync("target");   // 重置前的哈希

        var create = await AuthRegistrationTestHost.Authed(factory, admin.AccessToken)
            .PostAsJsonAsync("/api/v1/admin/users", new
            {
                username = "newbie",
                email = "newbie@example.com",
                password = createdPassword,
                role = "user"
            });
        Assert.Equal(System.Net.HttpStatusCode.Created, create.StatusCode);
        await CaptureHashAsync("newbie");

        var reset = await AuthRegistrationTestHost.Authed(factory, admin.AccessToken)
            .PostAsJsonAsync($"/api/v1/admin/users/{targetId}/password", new { newPassword = resetPassword });
        Assert.Equal(System.Net.HttpStatusCode.OK, reset.StatusCode);
        await CaptureHashAsync("target");   // 重置产生的中间哈希

        var target = await AuthRegistrationTestHost.LoginOkAsync(factory, "target", resetPassword);
        var selfChange = await AuthRegistrationTestHost.Authed(factory, target.AccessToken)
            .PostAsJsonAsync("/api/v1/auth/password", new
            {
                currentPassword = resetPassword,
                newPassword = selfNewPassword,
                refreshToken = target.RefreshToken
            });
        Assert.Equal(System.Net.HttpStatusCode.OK, selfChange.StatusCode);
        await CaptureHashAsync("target");   // 自助改密后的哈希

        var logs = await AuthRegistrationTestHost.AuditLogsAsync(factory);
        Assert.NotEmpty(logs);
        Assert.NotEmpty(hashes);

        var secrets = new[] { createdPassword, resetPassword, selfNewPassword, Password };

        foreach (var log in logs)
        {
            var text = string.Join('\n', AllText(log));

            // 口令明文
            foreach (var secret in secrets)
                Assert.DoesNotContain(secret, text, StringComparison.Ordinal);

            // 每一步捕获到的 password_hash（含被覆盖的中间版本）
            foreach (var hash in hashes)
                Assert.DoesNotContain(hash, text, StringComparison.Ordinal);
        }
    }
}
