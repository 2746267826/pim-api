using System;
using System.Collections.Generic;
using System.Text.Json;

namespace Pim.Core.Liveness;

/// <summary>
/// 设备端叫醒兑现负载的解析（REQ-18）。
///
/// 放在 Pim.Core 而不是模块里：设备端字段是**两端契约**（工单 §7.1），
/// 解析口径只有一份实现，服务端摘要、Web 页面与 MCP 工具都从这里取数。
///
/// 解析失败一律当作「不可判定」返回 null，**不猜 0 延迟**——
/// 猜成 0 会把一条读不懂的记录当成「完美按时」，直接抬高兑现率（AC-18.3 的反面）。
/// </summary>
public static class AlarmFulfillmentParser
{
    /// <summary>
    /// 解析一条闹钟兑现事件的负载。
    /// </summary>
    /// <param name="occurredAtUtc">事件时刻（负载里缺少预定时刻时作为兜底）。</param>
    /// <returns>解析成功返回样本；负载不可解析时返回 null。</returns>
    public static AlarmFulfillmentSample? Parse(DateTimeOffset occurredAtUtc, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var scheduled = ReadTimestamp(root, "scheduledAtUtcMillis") ?? occurredAtUtc;

            // delayMillis 为 null / 缺失 / 非数值 → 没有实际执行（AC-18.3：不进分母）。
            var delayMillis = ReadNullableDouble(root, "delayMillis");
            var actual = ReadTimestamp(root, "actualAtUtcMillis");

            double? delayMinutes = null;
            if (delayMillis is { } millis)
            {
                delayMinutes = millis / 60_000d;
            }
            else if (actual is { } actualAt)
            {
                // 兼容只有实际时刻、没写延迟的负载：由两者相减得出（AC-18.1 三者算术一致）。
                delayMinutes = (actualAt - scheduled).TotalMinutes;
            }

            // 既没有延迟也没有实际时刻 = 未执行；显式区分「缺字段」与「真的没执行」：
            // 只有 outcome 表明真的执行过、却两样都缺时，才视为不可判定。
            if (delayMinutes is null && actual is null && !LooksExecuted(root))
            {
                return new AlarmFulfillmentSample(scheduled, null);
            }

            if (delayMinutes is null)
            {
                return null;
            }

            return new AlarmFulfillmentSample(scheduled, delayMinutes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>批量解析；不可解析的条目被跳过（不中断整批）。</summary>
    public static IReadOnlyList<AlarmFulfillmentSample> ParseAll(
        IEnumerable<(DateTimeOffset OccurredAtUtc, string? PayloadJson)> events)
    {
        var result = new List<AlarmFulfillmentSample>();
        foreach (var (occurredAt, payload) in events)
        {
            if (Parse(occurredAt, payload) is { } sample)
            {
                result.Add(sample);
            }
        }

        return result;
    }

    /// <summary>负载里的 outcome 是否表示「真的执行了链路」。</summary>
    private static bool LooksExecuted(JsonElement root)
    {
        if (!root.TryGetProperty("outcome", out var outcome) || outcome.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var value = outcome.GetString();
        return value is "executed" or "suppressed" or "pull-failed";
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement root, string property)
    {
        var millis = ReadNullableDouble(root, property);
        return millis is { } value ? DateTimeOffset.FromUnixTimeMilliseconds((long)value) : null;
    }

    private static double? ReadNullableDouble(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var element))
        {
            return null;
        }

        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetDouble(out var number) => number,
            JsonValueKind.String when double.TryParse(
                element.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => null,
        };
    }
}
