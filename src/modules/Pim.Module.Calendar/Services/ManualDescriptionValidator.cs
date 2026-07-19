using System.Text.RegularExpressions;
using Pim.Core.Exceptions;

namespace Pim.Module.Calendar.Services;

public static partial class ManualDescriptionValidator
{
    public static void EnsureSafe(string? description)
    {
        if (description is null)
            return;

        if (DangerousTagPattern().IsMatch(description))
            throw new DomainException(02013, "描述中不允许包含可执行的 HTML 标签（script、iframe、object、embed）");

        if (EventHandlerPattern().IsMatch(description))
            throw new DomainException(02013, "描述中不允许包含事件处理属性（on*）");
    }

    [GeneratedRegex(@"<(?:script|iframe|object|embed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousTagPattern();

    [GeneratedRegex(@"\bon\w+\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EventHandlerPattern();
}
