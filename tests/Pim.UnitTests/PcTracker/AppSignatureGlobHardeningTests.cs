using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Pim.Infrastructure.Data;
using Pim.Module.PcTracker.Entities;
using Pim.Module.PcTracker.Services;
using Xunit;

namespace Pim.UnitTests.PcTracker;

/// <summary>
/// 应用签名 glob（* / ?）匹配的 ReDoS 加固回归测试。
/// 三个 glob→正则匹配点必须共享同一个带超时的实现，
/// 否则恶意/退化模式（如 `*a*a*a*a*a*a*a*a*a*a*b`）会触发灾难性回溯，把 API 线程钉死。
/// </summary>
public class AppSignatureGlobHardeningTests
{
    /// <summary>灾难性回溯模式：glob 语义下等价于 `.*a.*a...*b`，且永不匹配「全 a」输入。</summary>
    private const string CatastrophicPattern = "*a*a*a*a*a*a*a*a*a*a*b";

    /// <summary>足以在无超时实现下产生秒级回溯的输入长度（实测 28 字符约 5.3 秒）。</summary>
    private static readonly string CatastrophicInput = new('a', 28);

    /// <summary>无超时实现实测约 5.3 秒；带 50ms 超时实现约 50-110 毫秒。2 秒阈值两边都有余量。</summary>
    private const int BudgetMs = 2000;

    private static PimDbContext NewDbWithSignature(string pattern)
    {
        PimDbContext.RegisterModuleAssembly(typeof(PcCategoryEntity).Assembly);
        var db = new PimDbContext(new DbContextOptionsBuilder<PimDbContext>()
            .UseInMemoryDatabase($"glob-hardening-{Guid.NewGuid()}")
            .Options);
        db.Set<AppSignatureEntity>().Add(new AppSignatureEntity
        {
            Id = Guid.NewGuid(),
            ProcessName = pattern,
            DisplayName = "evil",
            CategoryPath = CategoryLegacyMapper.ProgrammingTinkering,
            Confidence = 1.0,
            Source = "imported"
        });
        db.SaveChanges();
        return db;
    }

    [Fact]
    public async Task LookupByProcessName_CatastrophicGlobPattern_DoesNotHang()
    {
        await using var db = NewDbWithSignature(CatastrophicPattern);
        var svc = new AppSignatureService(db);

        var sw = Stopwatch.StartNew();
        var result = await svc.LookupByProcessNameAsync(CatastrophicInput, CancellationToken.None);
        sw.Stop();

        Assert.Null(result);
        Assert.True(sw.ElapsedMilliseconds < BudgetMs,
            $"AppSignatureService.LookupByProcessNameAsync 耗时 {sw.ElapsedMilliseconds}ms，" +
            $"超出 {BudgetMs}ms 预算：glob 正则缺少超时（ReDoS）。");
    }

    [Fact]
    public void ResolveDisplayNames_CatastrophicGlobPattern_DoesNotHang()
    {
        var signatures = new[] { (ProcessName: CatastrophicPattern, DisplayName: "evil") };

        var sw = Stopwatch.StartNew();
        var names = AppSignatureMatcher.ResolveDisplayNames([CatastrophicInput], signatures);
        sw.Stop();

        Assert.Empty(names);
        Assert.True(sw.ElapsedMilliseconds < BudgetMs,
            $"AppSignatureMatcher.ResolveDisplayNames 耗时 {sw.ElapsedMilliseconds}ms，" +
            $"超出 {BudgetMs}ms 预算：glob 正则缺少超时（ReDoS）。");
    }

    [Theory]
    // 通配语义保持
    [InlineData("code*", "code-insiders", true)]
    [InlineData("code*.exe", "code-insiders.exe", true)]
    [InlineData("mobaxterm?.exe", "mobaxterm1.exe", true)]
    [InlineData("code*.exe", "code-insiders", false)]
    [InlineData("code?.exe", "code-insiders.exe", false)]
    // 大小写不敏感
    [InlineData("CODE*.EXE", "code-insiders.exe", true)]
    // 非通配模式应交由精确/补 .exe 分支处理，不在此处匹配
    [InlineData("notepad.exe", "notepad.exe", false)]
    // 空值/空白安全性
    [InlineData("", "notepad.exe", false)]
    [InlineData("   ", "notepad.exe", false)]
    [InlineData(null, "notepad.exe", false)]
    [InlineData("*", "", false)]
    [InlineData("*", null, false)]
    public void IsWildcardMatch_KeepsGlobSemantics(string? pattern, string? candidate, bool expected)
    {
        Assert.Equal(expected, AppSignatureGlobMatcher.IsWildcardMatch(pattern, candidate));
    }

    [Fact]
    public void IsWildcardMatch_CatastrophicPattern_TimesOutInsteadOfBacktracking()
    {
        var sw = Stopwatch.StartNew();
        var matched = AppSignatureGlobMatcher.IsWildcardMatch(CatastrophicPattern, CatastrophicInput);
        sw.Stop();

        Assert.False(matched);
        Assert.True(sw.ElapsedMilliseconds < BudgetMs,
            $"glob 匹配耗时 {sw.ElapsedMilliseconds}ms，超出 {BudgetMs}ms 预算：正则超时未生效。");
    }
}
