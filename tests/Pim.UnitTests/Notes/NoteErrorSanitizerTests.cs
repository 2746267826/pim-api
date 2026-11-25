using Microsoft.AspNetCore.Http;
using Pim.Api.Middleware;
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
    [InlineData("派生任务 11111111-1111-4111-8111-111111111111 不存在")]
    [InlineData("材料摘要 " + "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("调用 https://internal.example/v1 失败")]
    [InlineData("读取 /data/pim/native-notes/private.json 失败")]
    [InlineData("attempt 与 fence 不匹配")]
    [InlineData("provider_error")]
    [InlineData("VendorPhase")]
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
}
