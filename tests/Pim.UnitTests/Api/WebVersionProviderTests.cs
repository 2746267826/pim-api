using Pim.Api.Services;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 镜像内 <c>wwwroot/web-version.json</c> 的读取语义：读得到就用真值（原样，不重算、不重排），
/// 读不到就用「未知 / 未内嵌」哨兵，任何情况都不抛异常（对应 REQ-8 / AC-3 的缺失场景）。
/// </summary>
public sealed class WebVersionProviderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pim-web-version-" + Guid.NewGuid().ToString("N"));

    private string WebRoot
    {
        get
        {
            var dir = Path.Combine(_root, "wwwroot");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    private WebVersionProvider Provider() => new(WebRoot);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理失败不影响断言 */ }
    }

    [Fact]
    public void Current_ReadsAllThreeFieldsVerbatim()
    {
        File.WriteAllText(Path.Combine(WebRoot, WebVersionProvider.FileName),
            """{"version":"26.10.831","gitSha":"abc1234","builtAt":"2026-10-13T12:00:00Z"}""");

        var info = Provider().Current;

        Assert.Equal("26.10.831", info.Version);
        Assert.Equal("abc1234", info.GitSha);
        Assert.Equal("2026-10-13T12:00:00Z", info.BuiltAt);
    }

    [Fact]
    public void Current_ReturnsUnknownWhenFileMissing()
    {
        var info = Provider().Current;

        Assert.Equal(WebVersionProvider.UnknownValue, info.Version);
        Assert.Equal(WebVersionProvider.UnknownValue, info.GitSha);
        Assert.Equal(WebVersionProvider.UnknownValue, info.BuiltAt);
    }

    [Fact]
    public void Current_ReturnsUnknownWhenFileMalformed()
    {
        File.WriteAllText(Path.Combine(WebRoot, WebVersionProvider.FileName), "{ this is not json");

        var info = Provider().Current;

        Assert.Equal(WebVersionProvider.UnknownValue, info.Version);
        Assert.Equal(WebVersionProvider.UnknownValue, info.GitSha);
        Assert.Equal(WebVersionProvider.UnknownValue, info.BuiltAt);
    }

    [Fact]
    public void Current_ReturnsUnknownForFieldsThatAreMissingOrBlank()
    {
        File.WriteAllText(Path.Combine(WebRoot, WebVersionProvider.FileName),
            """{"version":"26.10.831","gitSha":"","builtAt":null}""");

        var info = Provider().Current;

        Assert.Equal("26.10.831", info.Version);
        Assert.Equal(WebVersionProvider.UnknownValue, info.GitSha);
        Assert.Equal(WebVersionProvider.UnknownValue, info.BuiltAt);
    }

    [Fact]
    public void Current_ReadsUpstreamFieldNamesOnly()
    {
        // 字段名是跨仓契约（pim-web 的 web-version.json）：改成别的名字必须读不到，
        // 否则「镜像里是哪一版」会静默退化成 unknown 而没人发现。
        File.WriteAllText(Path.Combine(WebRoot, WebVersionProvider.FileName),
            """{"webVersion":"26.10.831","webSha":"abc1234","webBuiltAt":"2026-10-13T12:00:00Z"}""");

        var info = Provider().Current;

        Assert.Equal(WebVersionProvider.UnknownValue, info.Version);
        Assert.Equal(WebVersionProvider.UnknownValue, info.GitSha);
    }
}
