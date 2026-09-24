using System.Text.Json;

namespace Pim.Module.Mobile.Services;

/// <summary>
/// 死因标识与中文标签的唯一映射（REQ-1 / REQ-2 / REQ-7）。
/// <para>
/// 设备端上报的是 Android <c>ApplicationExitInfo</c> 的原因常量名（如 <c>REASON_LOW_MEMORY</c>）
/// 或强停检测得到的 <c>kind</c>；服务端在这里翻译成统一死因键与简体中文标签
/// （AC-26.1：新增可见文案为简体中文），页面与摘要在任何设备上都显示同一套措辞。
/// </para>
/// <para>
/// <b>未知死因必须带推断依据</b>（AC-7.1）：查不到映射的原因一律落到 <c>unknown</c>，
/// 并把原始原因常量当作推断依据返回，绝不显示成"无异常"（AC-1.3 / AC-7.5）。
/// </para>
/// </summary>
public static class MobileLivenessCauseClassifier
{
    public const string UnknownCause = "unknown";
    public const string NoRecordCause = "no-record";
    public const string ForceStopCause = "force-stop";
    public const string RebootCause = "reboot";
    public const string SentinelClearedCause = "sentinel-cleared-permission";

    private static readonly Dictionary<string, (string Cause, string Label)> ExitReasonMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["REASON_LOW_MEMORY"] = ("low-memory", "内存不足被系统回收"),
            ["REASON_SIGNALED"] = ("signaled", "被系统信号终止"),
            ["REASON_FREEZER"] = ("freezer", "被冻结器回收（缓存进程）"),
            ["REASON_USER_REQUESTED"] = ("user-requested", "用户强停"),
            ["REASON_ANR"] = ("anr", "应用无响应（ANR）"),
            ["REASON_CRASH"] = ("crash", "应用崩溃（Java）"),
            ["REASON_CRASH_NATIVE"] = ("crash-native", "原生崩溃"),
            ["REASON_EXCESSIVE_RESOURCE_USAGE"] = ("excessive-resource", "资源占用过高被系统终止"),
            ["REASON_EXIT_SELF"] = ("exit-self", "应用主动退出"),
            ["REASON_DEPENDENCY_DIED"] = ("dependency-died", "依赖进程死亡"),
            ["REASON_INITIALIZATION_FAILURE"] = ("init-failure", "初始化失败"),
            ["REASON_PACKAGE_STATE_CHANGE"] = ("package-state-change", "包状态变更"),
            ["REASON_PACKAGE_UPDATED"] = ("package-updated", "应用被更新"),
            ["REASON_PERMISSION_CHANGE"] = ("permission-change", "权限变更"),
            ["REASON_OTHER"] = ("other", "其他原因"),
            ["REASON_UNKNOWN"] = (UnknownCause, "系统未给出原因"),
        };

    private static readonly Dictionary<string, (string Cause, string Label)> ForceStopKindMap =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["force-stop"] = (ForceStopCause, "疑似强停"),
            ["reboot"] = (RebootCause, "设备重启"),
            ["sentinel-cleared-permission"] = (SentinelClearedCause, "哨兵被清空（权限变更）"),
        };

    /// <summary>把一条取证事件的负载翻译成死因（无死因语义时返回 null，例如心跳）。</summary>
    public static (string Cause, string Label, string? Inference)? Classify(string eventType, string payloadJson)
    {
        if (eventType is not ("process-exit" or "force-stop"))
        {
            return null;
        }

        using var document = TryParse(payloadJson);
        var root = document?.RootElement;

        if (eventType == "force-stop")
        {
            var kind = ReadString(root, "kind") ?? "force-stop";
            if (ForceStopKindMap.TryGetValue(kind, out var mapped))
            {
                return (mapped.Cause, mapped.Label, ReadString(root, "inference"));
            }

            return (UnknownCause, "系统未给出原因", $"未知的强停检测结果：{kind}");
        }

        var reason = ReadString(root, "reason");
        if (!string.IsNullOrWhiteSpace(reason) && ExitReasonMap.TryGetValue(reason, out var exitMapped))
        {
            return (exitMapped.Cause, exitMapped.Label, ReadString(root, "inference"));
        }

        if (string.IsNullOrWhiteSpace(reason))
        {
            // AC-1.3：系统未提供退出记录时页面显示"未知"并给出可推断线索，不得给出"无异常"结论。
            return (NoRecordCause, "系统未提供退出记录", "设备未取到该次进程退出的原因，无法判定死因。");
        }

        return (UnknownCause, "系统未给出原因", $"未能识别的退出原因：{reason}");
    }

    /// <summary>读取负载里的一个字符串字段（负载畸形时返回 null，调用方按"读到不到"处理）。</summary>
    public static string? ReadString(JsonElement? root, string propertyName)
    {
        if (root is not { } element || element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    /// <summary>读取负载里的一个整数字段（不可解析时返回 null，绝不填猜测值，AC-4.2）。</summary>
    public static int? ReadInt(JsonElement? root, string propertyName)
    {
        var text = ReadString(root, propertyName);
        return int.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>读取负载里的一个长整数字段。</summary>
    public static long? ReadLong(JsonElement? root, string propertyName)
    {
        var text = ReadString(root, propertyName);
        return long.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>安全解析 jsonb 文本；畸形负载返回 null 而不是抛异常。</summary>
    public static JsonDocument? TryParse(string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
