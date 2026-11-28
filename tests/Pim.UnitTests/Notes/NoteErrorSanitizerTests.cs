using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Pim.Api.Middleware;
using Pim.Core.Exceptions;
using Pim.Core.Notes;
using Pim.Infrastructure.Auth;
using Pim.Infrastructure.Data;
using Pim.Infrastructure.Data.Entities;
using Xunit;

namespace Pim.UnitTests.Notes;

public class NoteErrorSanitizerTests
{
    [Theory]
    [InlineData("/api/v1/native-notes/versions/1")]
    [InlineData("/API/V1/NATIVE-NOTES/derived-tasks")]
    public void RecognizesNativeNotesPaths(string path) =>
        Assert.True(NoteErrorSanitizer.IsNativeNotesPath(new PathString(path)));

    [Theory]
    [InlineData("/api/v1/notes")]
    [InlineData("/api/v1/native-notes-extra")]
    public void DoesNotClaimUnrelatedPaths(string path) =>
        Assert.False(NoteErrorSanitizer.IsNativeNotesPath(new PathString(path)));

    [Fact]
    public void KeepsPlainActionableChineseErrors()
    {
        const string message = "请先上传完整固定材料，再生成派生稿";
        Assert.Equal(message, NoteErrorSanitizer.SanitizeDomainMessage(message, 400));
    }

    [Theory]
    [InlineData("必须提供有效 timeZoneId")]
    [InlineData("必须提供有效的 IANA 时区")]
    [InlineData("日程建议必须用 start/end 表示时间，不能使用 due")]
    [InlineData("任务建议不能携带 timeZoneId；需要时区时请使用 kind=Event")]
    public void KeepsActionableFieldNamesAndTimeZoneErrors(string message)
    {
        Assert.Equal(message, NoteErrorSanitizer.SanitizeDomainMessage(message, 400));
    }

    [Theory]
    [InlineData("派生任务 11111111-1111-4111-8111-111111111111 不存在")]
    [InlineData("材料摘要 " + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("调用 https://internal.example/v1 失败")]
    [InlineData("读取 /data/pim/native-notes/private.json 失败")]
    [InlineData("attempt 与 fence 不匹配")]
    [InlineData("provider_error")]
    [InlineData("phase mismatch")]
    [InlineData("材料 private/secret.md 不存在")]
    [InlineData("任务 123456 已过期")]
    public void ReplacesInternalDetailWithStatusFallback(string message)
    {
        Assert.Equal(NoteErrorSanitizer.FallbackMessage(409),
            NoteErrorSanitizer.SanitizeDomainMessage(message, 409));
    }

    [Fact]
    public void BadRequestHidesParserDetailFromOrdinaryUsersOnly()
    {
        Assert.Equal("请求参数格式无效", NoteErrorSanitizer.BadRequestMessage("line 1", false));
        Assert.Equal("请求参数格式无效: line 1", NoteErrorSanitizer.BadRequestMessage("line 1", true));
    }

    [Theory]
    [InlineData("user", true, false)]
    [InlineData("admin", true, true)]
    [InlineData("admin", false, false)]
    public async Task MiddlewareSanitizesDomainErrorByCurrentActiveRole(
        string role, bool isActive, bool keepsRaw)
    {
        var userId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"note-error-middleware-{Guid.NewGuid()}")
            .Options;
        await using var db = new PimDbContext(options);
        db.Users.Add(new UserEntity
        {
            Id = userId,
            Username = "error-" + Guid.NewGuid().ToString("N"),
            Email = "error@example.com",
            PasswordHash = "synthetic",
            Role = role,
            IsActive = isActive
        });
        await db.SaveChangesAsync();

        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUserService>(new FixedCurrentUserService(userId, role));
        services.AddSingleton(db);
        await using var provider = services.BuildServiceProvider();

        const string raw =
            "派生任务 11111111-1111-4111-8111-111111111111 调用 provider " +
            "https://internal.example/v1 读取 /data/private/secret.md 失败";
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Path = "/api/v1/native-notes/derived-tasks/11111111-1111-4111-8111-111111111111";
        context.Response.Body = new MemoryStream();
        var middleware = new ExceptionMiddleware(
            _ => throw new DomainException(40901, raw),
            NullLogger<ExceptionMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.Equal(StatusCodes.Status409Conflict, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(context.Response.Body);
        var message = response.RootElement.GetProperty("message").GetString();
        Assert.Equal(keepsRaw ? raw : NoteErrorSanitizer.FallbackMessage(409), message);
        if (keepsRaw)
        {
            Assert.Contains("internal.example", message, StringComparison.Ordinal);
            Assert.Contains("secret.md", message, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("internal.example", message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret.md", message, StringComparison.Ordinal);
        }
    }

    private sealed class FixedCurrentUserService(Guid userId, string role) : ICurrentUserService
    {
        public Guid? UserId { get; } = userId;
        public string? Role { get; } = role;
    }
}
