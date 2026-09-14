using System;
using System.Collections.Generic;
using System.Text;

namespace Pim.Core.Invariants;

/// <summary>
/// 不变量阈值配置项（服务端配置项，默认值取自 EPIC §5 阈值总表）。
/// 支持通过 appsettings / IOptions 动态调整；若配置非法自动回退默认值并在判定结果中标注。
/// </summary>
public sealed class InvariantOptions
{
    public static InvariantOptions Default { get; } = new();

    /// <summary>
    /// T1a: 输入密度线（判定"有人在操作"），单位：次/分钟，默认 >= 1.0
    /// </summary>
    public double MinInputDensityPerMinute { get; set; } = 1.0;

    /// <summary>
    /// T1b: 超长线（超过即需出示证据），单位：分钟，默认 30.0
    /// </summary>
    public double LongEventThresholdMinutes { get; set; } = 30.0;

    /// <summary>
    /// T2: 无下线声明的空档红线阈值，单位：分钟，默认 30.0
    /// </summary>
    public double UndeclaredOfflineGapMinutes { get; set; } = 30.0;

    /// <summary>
    /// T2: 上传滞后 p99 红线阈值，单位：分钟，默认 30.0
    /// </summary>
    public double MaxUploadLagP99Minutes { get; set; } = 30.0;

    /// <summary>
    /// T3: 手机汇总滞后阈值，单位：小时，默认 4.0（2个窗口）
    /// </summary>
    public double MobileSummaryLagHours { get; set; } = 4.0;

    /// <summary>
    /// T4: 新增与存量欠账分界窗口，单位：小时，默认 24.0
    /// </summary>
    public double RecentWindowHours { get; set; } = 24.0;

    /// <summary>
    /// T5: 单日活跃硬上限，单位：小时，默认 24.0
    /// </summary>
    public double MaxDailyActiveHours { get; set; } = 24.0;

    /// <summary>
    /// T5: 清醒窗口，单位：小时，默认 16.0
    /// </summary>
    public double AwakeWindowHours { get; set; } = 16.0;

    /// <summary>
    /// T5: 清醒窗口警告线系数，默认 0.90（16h * 0.9 = 14.4h）
    /// </summary>
    public double AwakeWindowWarningRatio { get; set; } = 0.90;

    /// <summary>
    /// T6: 覆盖率红线，默认 0.95 (< 95% 报红)
    /// </summary>
    public double CoverageRedRatio { get; set; } = 0.95;

    /// <summary>
    /// T6: 覆盖率黄线，默认 0.99 (< 99% 报黄)
    /// </summary>
    public double CoverageYellowRatio { get; set; } = 0.99;

    /// <summary>
    /// T7: 违规样例展示最大数量，默认 10
    /// </summary>
    public int MaxSampleCount { get; set; } = 10;

    /// <summary>
    /// S5: 时钟超前容差，单位：分钟，默认 5.0
    /// </summary>
    public double ClockSkewToleranceMinutes { get; set; } = 5.0;

    /// <summary>
    /// S7: 时间轴断档标记判定阈值，单位：分钟，默认 15.0
    /// </summary>
    public double TimelineGapThresholdMinutes { get; set; } = 15.0;

    /// <summary>
    /// 默认计算容差（如时钟浮点/网络抖动），默认 0.05 (5%)
    /// </summary>
    public double Tolerance { get; set; } = 0.05;

    /// <summary>
    /// 校验配置是否合法；若非法返回错误说明
    /// </summary>
    public bool Validate(out string? errorMessage)
    {
        var errors = new List<string>();

        if (MinInputDensityPerMinute < 0) errors.Add("MinInputDensityPerMinute must be >= 0");
        if (LongEventThresholdMinutes <= 0) errors.Add("LongEventThresholdMinutes must be > 0");
        if (UndeclaredOfflineGapMinutes <= 0) errors.Add("UndeclaredOfflineGapMinutes must be > 0");
        if (MaxUploadLagP99Minutes <= 0) errors.Add("MaxUploadLagP99Minutes must be > 0");
        if (MobileSummaryLagHours <= 0) errors.Add("MobileSummaryLagHours must be > 0");
        if (RecentWindowHours <= 0) errors.Add("RecentWindowHours must be > 0");
        if (MaxDailyActiveHours <= 0 || MaxDailyActiveHours > 24.0) errors.Add("MaxDailyActiveHours must be > 0 and <= 24.0");
        if (AwakeWindowHours <= 0 || AwakeWindowHours > 24.0) errors.Add("AwakeWindowHours must be > 0 and <= 24.0");
        if (AwakeWindowWarningRatio <= 0 || AwakeWindowWarningRatio > 1.0) errors.Add("AwakeWindowWarningRatio must be in (0, 1.0]");
        if (CoverageRedRatio <= 0 || CoverageRedRatio > 1.0) errors.Add("CoverageRedRatio must be in (0, 1.0]");
        if (CoverageYellowRatio <= 0 || CoverageYellowRatio > 1.0) errors.Add("CoverageYellowRatio must be in (0, 1.0]");
        if (CoverageRedRatio > CoverageYellowRatio) errors.Add("CoverageRedRatio cannot be greater than CoverageYellowRatio");
        if (MaxSampleCount <= 0) errors.Add("MaxSampleCount must be > 0");
        if (ClockSkewToleranceMinutes < 0) errors.Add("ClockSkewToleranceMinutes must be >= 0");
        if (TimelineGapThresholdMinutes <= 0) errors.Add("TimelineGapThresholdMinutes must be > 0");
        if (Tolerance < 0) errors.Add("Tolerance must be >= 0");

        if (errors.Count > 0)
        {
            errorMessage = string.Join("; ", errors);
            return false;
        }

        errorMessage = null;
        return true;
    }

    /// <summary>
    /// 解析传入配置：若为空或非法，安全回退到默认值并在返回中标注。
    /// </summary>
    public static (InvariantOptions resolved, bool fallback, string? note) Resolve(InvariantOptions? options)
    {
        if (options == null)
            return (Default, false, null);

        if (!options.Validate(out var error))
        {
            return (Default, true, $"配置非法已回退默认值: {error}");
        }

        return (options, false, null);
    }
}
