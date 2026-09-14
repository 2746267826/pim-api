namespace Pim.Module.Mobile.Services;

/// <summary>
/// 手机端同步批次状态词表与判据（#241 / #242 / #243）。
/// <para>
/// 语义约定：
/// <list type="bullet">
/// <item><c>pending</c>：批次已受理但尚未处理完（进程崩溃、请求中断、正在处理）—— 积压监控据此发现"只落一半"的批次。</item>
/// <item><c>completed</c>：处理完成。<b>条目级校验拒绝（rejected）不影响批次状态</b>，只体现在 rejected_count 与 error_json。</item>
/// <item><c>failed</c>：确实有条目失败（failed_count &gt; 0）。</item>
/// <item><c>completed-with-errors</c>：历史遗留状态，写入侧不再产生；因 failed_count = 0 时它并不代表失败，覆盖率与质量判据都不再把它当失败。</item>
/// </list>
/// 判据只有一份实现（EPIC #254 §6），避免"面板一套、测试一套"。
/// </para>
/// </summary>
public static class MobileSyncBatchStatus
{
    public const string Pending = "pending";
    public const string Completed = "completed";
    public const string Failed = "failed";

    /// <summary>历史遗留状态：条目级拒绝曾被误当作批次失败（#241）。</summary>
    public const string LegacyCompletedWithErrors = "completed-with-errors";

    /// <summary>
    /// "还在动"的判定窗口：处理中的批次在该窗口内没有刷新过租约，就认为处理者已经消失。
    /// 积压巡检、质量面板与设备删除守卫共用这一条线。
    /// </summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(30);

    /// <summary>是否处于"处理中"（写入侧只写 <see cref="Pending"/>，其余为兼容保留）。</summary>
    public static bool IsActive(string? status)
        => status is not null
            && (status.Equals(Pending, StringComparison.OrdinalIgnoreCase)
                || status.Equals("processing", StringComparison.OrdinalIgnoreCase)
                || status.Equals("syncing", StringComparison.OrdinalIgnoreCase));

    /// <summary>是否已终态（不再需要处理）。</summary>
    public static bool IsTerminal(string? status) => !IsActive(status);

    /// <summary>
    /// 批次是否算"失败"：只要有条目失败，或状态被显式标为失败类。
    /// failed_count = 0 的 <c>completed-with-errors</c> 不算失败（S11 / INV-M21）。
    /// </summary>
    public static bool IsFailed(int failedCount, string? status)
        => failedCount > 0
            || (status is not null
                && status.Equals(Failed, StringComparison.OrdinalIgnoreCase));

    /// <summary>该批次的窗口能否作为"已覆盖"依据：未失败且已完成处理（#242）。</summary>
    public static bool ProvidesCoverage(int failedCount, string? status)
        => failedCount == 0
            && status is not null
            && (status.Equals(Completed, StringComparison.OrdinalIgnoreCase)
                || status.Equals(LegacyCompletedWithErrors, StringComparison.OrdinalIgnoreCase));
}
