using System.Text.RegularExpressions;

namespace Pim.Module.PcTracker.Services;

/// <summary>
/// 应用签名 glob 通配匹配（<c>*</c> / <c>?</c>）的唯一实现。
///
/// 背景：`pc_app_signatures.process_name` 允许写入任意通配模式，`*` 会被翻译成正则的 `.*`。
/// 形如 <c>*a*a*a*a*a*a*a*a*a*a*b</c> 的模式在长输入上会触发灾难性回溯（ReDoS），
/// 因此在把 glob 转成正则时必须统一带上超时；命中超时按「不匹配」处理。
///
/// 所有 glob 匹配点（<see cref="ActivityClassifier"/>、<see cref="AppSignatureService"/>、
/// <see cref="AppSignatureMatcher"/>）都必须走这里，避免各处保护不一致。
/// </summary>
public static class AppSignatureGlobMatcher
{
    /// <summary>单个模式的正则匹配预算。与 <see cref="ActivityClassificationRuleEvaluator"/> 保持同一量级。</summary>
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// 判断 <paramref name="candidate"/> 是否匹配通配 <paramref name="pattern"/>。
    /// <paramref name="pattern"/> 为空或不含通配符、<paramref name="candidate"/> 为空时返回 false；
    /// 正则回溯超时同样返回 false（视为不匹配，绝不抛出）。
    /// </summary>
    public static bool IsWildcardMatch(string? pattern, string? candidate)
    {
        if (string.IsNullOrWhiteSpace(pattern) || string.IsNullOrEmpty(candidate))
            return false;

        if (!pattern.Contains('*') && !pattern.Contains('?'))
            return false;

        try
        {
            var regex = "^" + Regex.Escape(pattern)
                .Replace(@"\*", ".*")
                .Replace(@"\?", ".") + "$";
            return Regex.IsMatch(candidate, regex, RegexOptions.IgnoreCase, RegexTimeout);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
