namespace Pim.Module.PcTracker.Services;

/// <summary>
/// 统一的强度分档口径（WO-PC-BACKEND-20260930 REQ-3 / #364，P-2 方案 a）。
/// <para>
/// 背景：<c>intensityScore</c> 这个名字曾在三处表示三种量纲 ——
/// <c>activity-analysis</c> 是 0–4 的「活跃秒数 / 块秒数」比值分档、
/// <c>summary.heatmap</c> 是 0–5 的「活跃分钟」分档、
/// <c>heatmap/grid</c> 干脆是键盘原始计数（实测 12235，而 maxKeyCount=32886）。
/// </para>
/// <para>
/// 本单统一为「<b>活跃时长占窗口时长比例</b>」的 0–5 档整数，字段名 <c>intensityLevel</c>，
/// 并随响应给出上界 <c>intensityMax</c>=5。分档边界取
/// <c>0 / ≤1/12 / ≤1/4 / ≤1/2 / ≤3/4 / &gt;3/4</c>：
/// 对 60 分钟窗口，这四条边界正好等于既有的 5 / 15 / 30 / 45 活跃分钟，
/// 因此 60 分钟块与 <c>summary.heatmap</c> 的小时桶**同值**（AC-3.2），
/// 同时 15–240 分钟的块长都按占比分档，不会出现 240 分钟块恒满档（AC-3.3）。
/// </para>
/// </summary>
public static class PcActivityIntensity
{
    /// <summary>档位上界（响应字段 <c>intensityMax</c> 的取值）。</summary>
    public const int MaxLevel = 5;

    private const double Level1Boundary = 1.0 / 12.0;
    private const double Level2Boundary = 1.0 / 4.0;
    private const double Level3Boundary = 1.0 / 2.0;
    private const double Level4Boundary = 3.0 / 4.0;

    /// <summary>按「活跃时长 / 窗口时长」比例分档；非正比例（含无数据）为 0 档。</summary>
    public static int ForRatio(double activeRatio)
    {
        if (!double.IsFinite(activeRatio) || activeRatio <= 0)
            return 0;
        if (activeRatio <= Level1Boundary)
            return 1;
        if (activeRatio <= Level2Boundary)
            return 2;
        if (activeRatio <= Level3Boundary)
            return 3;
        if (activeRatio <= Level4Boundary)
            return 4;
        return MaxLevel;
    }

    /// <summary>按「活跃秒数 / 窗口秒数」分档。</summary>
    public static int ForSeconds(double activeSeconds, double windowSeconds)
        => windowSeconds <= 0 ? 0 : ForRatio(activeSeconds / windowSeconds);

    /// <summary>按 60 分钟窗口内的活跃分钟数分档（summary / aw-heatmap 的既有口径）。</summary>
    public static int ForMinutes(int activeMinutes)
        => ForRatio(activeMinutes / 60.0);
}
