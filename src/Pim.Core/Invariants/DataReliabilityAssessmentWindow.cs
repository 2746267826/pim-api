using System;

namespace Pim.Core.Invariants;

/// <summary>
/// 考核窗与考核线（WO-RELIABILITY-WINDOW-20260928 REQ-1）。
///
/// <para><b>考核线</b> = <c>max(体检时刻 − 考核窗时长, 进程启动时刻)</c>，取**较晚**的一条：</para>
/// <list type="bullet">
///   <item><description>刚重启（进程启动时刻晚于滚动窗起点）→ 考核线 = 进程启动时刻，重启前的一切违规自动进历史欠账；</description></item>
///   <item><description>重启满一个考核窗之后 → 考核线回到 7 天滚动窗。</description></item>
/// </list>
///
/// <para>
/// 本类型只做这一件事，是为了让"取较晚者"这条口径只有一份实现：8 条分档尺子、取数下限归位
/// （S3 / S4 采样 / S5 主取数）与面板账本起始时刻都从这里取值。
/// </para>
/// </summary>
public static class DataReliabilityAssessmentWindow
{
    /// <summary>考核窗时长的合法下界（小时），见 REQ-5 / D7。</summary>
    public const double MinHours = 1.0;

    /// <summary>考核窗时长的合法上界（小时，30 天），见 REQ-5 / D7。</summary>
    public const double MaxHours = 720.0;

    /// <summary>
    /// 解析考核线。
    /// </summary>
    /// <param name="inspectionTimeUtc">体检时刻（可注入，测试用）。</param>
    /// <param name="assessmentWindowHours">考核窗时长（小时，默认 168 = 7 天）。</param>
    /// <param name="processStartedAtUtc">
    /// 体检服务所在进程的本次启动时刻；**未注入时回退纯滚动窗**（D2）——
    /// 判据层是纯函数，不能强制依赖宿主进程状态。
    /// </param>
    public static DateTime ResolveAssessmentStartUtc(
        DateTime inspectionTimeUtc,
        double assessmentWindowHours,
        DateTime? processStartedAtUtc)
    {
        var rollingStartUtc = inspectionTimeUtc.AddHours(-assessmentWindowHours);

        if (processStartedAtUtc is not { } processStartedAt)
        {
            return rollingStartUtc;
        }

        // 进程启动时刻落在未来 = 时钟被回拨或注入值错误。此时若照单全收，考核线会晚于体检时刻，
        // 窗内永远为空 → 所有违规都被折成"历史欠账"、整块面板变绿（静默假绿灯）。
        // 这种不可信输入一律回退到滚动窗：宁可照常判色，也不亮假绿。
        if (processStartedAt > inspectionTimeUtc)
        {
            return rollingStartUtc;
        }

        // 取较晚者：不得取较早者（否则"重启后旧违规仍算窗内"，AC-1.4）。
        return processStartedAt > rollingStartUtc ? processStartedAt : rollingStartUtc;
    }
}
