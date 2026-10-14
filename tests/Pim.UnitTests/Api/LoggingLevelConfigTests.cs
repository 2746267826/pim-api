using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Pim.Api.Health;
using Pim.Api.Infrastructure;
using Serilog.Events;
using Xunit;

namespace Pim.UnitTests.Api;

/// <summary>
/// 工单 01 工作项 B：日志最低级别与类别覆盖必须**可配置**（不再是代码里的
/// <c>MinimumLevel.Debug()</c>），且降噪不得吞掉 Warning 及以上。
///
/// <para>
/// 断言方式是构造与宿主完全相同的那份 Serilog 配置（<see cref="LoggingConfig.CreateLoggerConfiguration"/>，
/// 落盘路径指向临时目录），再问它"这个级别/这个类别现在会不会输出" —— 测的是配置的**行为**，
/// 不是常量值。
/// </para>
/// </summary>
[Trait("DataSource", "Unit")]
public class LoggingLevelConfigTests
{
    private static IConfiguration NewCfg(params (string Key, string? Value)[] pairs)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => p.Value))
            .Build();

    private static Serilog.Core.Logger NewLogger(IConfiguration cfg, string? environmentLevel = null)
    {
        var levels = LoggingConfig.ResolveLevels(cfg, environmentLevel);
        var sinks = new LoggingConfig.LoggingSinkOptions(
            Path.Combine(Path.GetTempPath(), $"pim-log-level-{Guid.NewGuid():N}", "pim-api-.jsonl"),
            LoggingConfig.DefaultRetainedFileCount,
            LoggingConfig.DefaultFileSizeLimitBytes,
            LoggingConfig.DefaultRollOnFileSizeLimit,
            LokiUrl: null,
            ConsoleToStandardError: false);
        return LoggingConfig.CreateLoggerConfiguration(levels, sinks).CreateLogger();
    }

    [Fact]
    public void WithoutConfiguration_DebugNoiseIsOff_ButWarningAndAboveStayOn()
    {
        using var logger = NewLogger(NewCfg());

        // 默认档 = Information：框架内务（Debug）不再输出
        Assert.False(logger.IsEnabled(LogEventLevel.Debug));
        Assert.False(logger.IsEnabled(LogEventLevel.Verbose));
        // 真错误一条都不能少
        Assert.True(logger.IsEnabled(LogEventLevel.Information));
        Assert.True(logger.IsEnabled(LogEventLevel.Warning));
        Assert.True(logger.IsEnabled(LogEventLevel.Error));
        Assert.True(logger.IsEnabled(LogEventLevel.Fatal));
    }

    [Fact]
    public void ConfiguredDefaultLevel_TakesEffect_WithoutTouchingCode()
    {
        using var stricter = NewLogger(NewCfg(("Logging:LogLevel:Default", "Warning")));
        Assert.False(stricter.IsEnabled(LogEventLevel.Information));
        Assert.True(stricter.IsEnabled(LogEventLevel.Warning));

        using var looser = NewLogger(NewCfg(("Logging:LogLevel:Default", "Debug")));
        Assert.True(looser.IsEnabled(LogEventLevel.Debug));
    }

    [Fact]
    public void EnvironmentVariable_OverridesConfiguration()
    {
        using var logger = NewLogger(NewCfg(("Logging:LogLevel:Default", "Debug")), environmentLevel: "Error");

        Assert.False(logger.IsEnabled(LogEventLevel.Warning));
        Assert.True(logger.IsEnabled(LogEventLevel.Error));
    }

    [Fact]
    public void EntityFrameworkHousekeepingCategories_AreRaisedToWarning_ButOtherCategoriesAreNot()
    {
        using var logger = NewLogger(NewCfg());

        // 逐条 SQL 流水与实体跟踪：内务噪声，默认抬到 Warning
        Assert.False(logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command")
            .IsEnabled(LogEventLevel.Information));
        Assert.False(logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.ChangeTracking")
            .IsEnabled(LogEventLevel.Information));
        // 但 EF 的数据库错误（Error）照旧输出
        Assert.True(logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command")
            .IsEnabled(LogEventLevel.Error));

        // 其它类别不受影响：请求日志等仍按 Default 输出
        Assert.True(logger.ForContext("SourceContext", "Pim.Module.Calendar.Services.ReportService")
            .IsEnabled(LogEventLevel.Information));
    }

    [Fact]
    public void CategoryOverrides_AreConfigurable()
    {
        using var logger = NewLogger(NewCfg(
            ("Logging:LogLevel:Default", "Warning"),
            ("Logging:LogLevel:Microsoft.EntityFrameworkCore.Database.Command", "Information")));

        // 配置可以把某个类别放回更细的档（EF 命令流水的键仍可配）
        Assert.True(logger.ForContext("SourceContext", "Microsoft.EntityFrameworkCore.Database.Command")
            .IsEnabled(LogEventLevel.Information));
        // 未覆盖的类别仍跟随 Default
        Assert.False(logger.ForContext("SourceContext", "Pim.Module.Calendar.Services.ReportService")
            .IsEnabled(LogEventLevel.Information));
    }

    [Fact]
    public void InvalidLevelValue_IsReportedInsteadOfSilentlyIgnored()
    {
        var levels = LoggingConfig.ResolveLevels(NewCfg(
            ("Logging:LogLevel:Default", "Verbose2"),
            ("Logging:LogLevel:Some.Category", "Loud")));

        Assert.Equal(LoggingConfig.DefaultMinimumLevel, levels.MinimumLevel);
        Assert.Contains("Logging:LogLevel:Default=Verbose2", levels.InvalidEntries);
        Assert.Contains("Logging:LogLevel:Some.Category=Loud", levels.InvalidEntries);
        Assert.False(levels.CategoryLevels.ContainsKey("Some.Category"));
    }

    [Fact]
    public void MinimumLevelSource_IsReportedForOperationalEvidence()
    {
        Assert.Equal(
            "默认值 Information",
            LoggingConfig.ResolveLevels(NewCfg()).MinimumLevelSource);
        Assert.Equal(
            "配置 Logging:LogLevel:Default",
            LoggingConfig.ResolveLevels(NewCfg(("Logging:LogLevel:Default", "Warning"))).MinimumLevelSource);
        Assert.Equal(
            LoggingConfig.MinimumLevelEnvironmentVariable,
            LoggingConfig.ResolveLevels(NewCfg(("Logging:LogLevel:Default", "Warning")), "Error").MinimumLevelSource);
    }

    // ---- B2.3：Ai 未启用时不注册 litellm 健康检查（消除恒 Degraded 的周期噪声） ----

    [Fact]
    public void LiteLlmHealthCheck_IsNotRegistered_WhenAiDisabled()
    {
        var registrations = Registrations(("Ai:Enabled", "false"));
        Assert.DoesNotContain(registrations, r => r.Name == "litellm");
        // 核心与其它可选依赖照旧注册
        Assert.Contains(registrations, r => r.Name == "database");
        Assert.Contains(registrations, r => r.Name == "tika");
    }

    [Fact]
    public void LiteLlmHealthCheck_IsRegistered_WhenAiEnabled_SoAnUnreachableDependencyIsStillReported()
    {
        var registrations = Registrations(("AI_ENABLED", "true"));
        Assert.Contains(registrations, r => r.Name == "litellm");
    }

    private static IReadOnlyList<HealthCheckRegistration> Registrations(params (string Key, string? Value)[] pairs)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHttpClientFactory>(new HttpClientFactoryStub());
        services.AddSingleton(NewCfg(pairs));
        services.AddPimHealthChecks(NewCfg(pairs));
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations.ToList();
    }

    private sealed class HttpClientFactoryStub : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
