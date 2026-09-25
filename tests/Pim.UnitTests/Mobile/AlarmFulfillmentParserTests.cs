using System;
using System.Linq;
using Pim.Core.Liveness;
using Xunit;

namespace Pim.UnitTests.Mobile;

/// <summary>
/// REQ-18 叫醒兑现负载的解析。
///
/// 重点守两类**会抬高兑现率**的解析错误：
/// 1. 把读不懂的记录当成「延迟 0」（完美按时）；
/// 2. 把「未执行」的记录当成「执行了但延迟未知」而塞进分母。
/// 两者都会让保活看起来比实际更好，正是这套指标最不该出现的偏差。
/// </summary>
public sealed class AlarmFulfillmentParserTests
{
    private static readonly DateTimeOffset Occurred = new(2026, 9, 25, 10, 0, 0, TimeSpan.Zero);

    private static long Millis(DateTimeOffset at) => at.ToUnixTimeMilliseconds();

    [Fact]
    public void Parse_ExecutedOnTime_YieldsDelayFromPayload()
    {
        var scheduled = Occurred.AddMinutes(-3);
        var payload =
            $"{{\"scheduledAtUtcMillis\":{Millis(scheduled)},\"actualAtUtcMillis\":{Millis(Occurred)}," +
            "\"delayMillis\":180000,\"outcome\":\"executed\"}";

        var sample = AlarmFulfillmentParser.Parse(Occurred, payload);

        Assert.NotNull(sample);
        Assert.Equal(3d, sample!.DelayMinutes!.Value, 3);
    }

    /// <summary>AC-18.3：未执行的记录延迟为 null（不进分母），而不是 0。</summary>
    [Fact]
    public void Parse_NotExecuted_HasNullDelay()
    {
        var payload =
            $"{{\"scheduledAtUtcMillis\":{Millis(Occurred)},\"actualAtUtcMillis\":null," +
            "\"delayMillis\":null,\"outcome\":\"not-executed\"}";

        var sample = AlarmFulfillmentParser.Parse(Occurred, payload);

        Assert.NotNull(sample);
        Assert.Null(sample!.DelayMinutes);
    }

    /// <summary>因暂停跳过同样是未执行（AC-20.3：不计为失败也不进分母）。</summary>
    [Fact]
    public void Parse_SkippedPaused_HasNullDelay()
    {
        var payload =
            $"{{\"scheduledAtUtcMillis\":{Millis(Occurred)},\"delayMillis\":null," +
            "\"outcome\":\"skipped-paused\"}";

        Assert.Null(AlarmFulfillmentParser.Parse(Occurred, payload)!.DelayMinutes);
    }

    /// <summary>
    /// 关键反面：负载里有 outcome=executed 但两个时刻字段都缺 → **不可判定**，返回 null，
    /// 绝不猜成延迟 0（那会把它当成完美按时，抬高兑现率）。
    /// </summary>
    [Fact]
    public void Parse_ExecutedButNoTiming_NeverAssumesZeroDelay()
    {
        var payload = $"{{\"outcome\":\"executed\"}}";

        Assert.Null(AlarmFulfillmentParser.Parse(Occurred, payload));
    }

    /// <summary>坏的 JSON 不得让整批失败，按不可判定处理。</summary>
    [Fact]
    public void Parse_MalformedJson_ReturnsNull()
    {
        Assert.Null(AlarmFulfillmentParser.Parse(Occurred, "{not json"));
        Assert.Null(AlarmFulfillmentParser.Parse(Occurred, ""));
        Assert.Null(AlarmFulfillmentParser.Parse(Occurred, null));
        Assert.Null(AlarmFulfillmentParser.Parse(Occurred, "[1,2,3]"));
    }

    /// <summary>AC-18.1：只有实际时刻、没写延迟时，由两者相减得出（三者算术一致）。</summary>
    [Fact]
    public void Parse_DerivesDelayFromTimestampsWhenDelayMissing()
    {
        var scheduled = Occurred.AddMinutes(-20);
        var payload =
            $"{{\"scheduledAtUtcMillis\":{Millis(scheduled)}," +
            $"\"actualAtUtcMillis\":{Millis(Occurred)},\"outcome\":\"suppressed\"}}";

        var sample = AlarmFulfillmentParser.Parse(Occurred, payload);

        Assert.NotNull(sample);
        Assert.Equal(20d, sample!.DelayMinutes!.Value, 3);
    }

    /// <summary>批量解析：不可解析的条目被跳过，不影响其它条目。</summary>
    [Fact]
    public void ParseAll_SkipsUnparsableEntries()
    {
        var scheduled = Occurred.AddMinutes(-1);
        var good =
            $"{{\"scheduledAtUtcMillis\":{Millis(scheduled)},\"delayMillis\":60000,\"outcome\":\"executed\"}}";

        var samples = AlarmFulfillmentParser.ParseAll(new[]
        {
            (Occurred, good),
            (Occurred, "{broken"),
            (Occurred, $"{{\"outcome\":\"executed\"}}"),
        });

        Assert.Single(samples);
    }

    /// <summary>端到端口径：解析结果进入兑现率计算后，未执行的不进分母。</summary>
    [Fact]
    public void ParseThenCompute_ExcludesNotExecutedFromDenominator()
    {
        var scheduled = Occurred.AddMinutes(-2);
        var payloads = new[]
        {
            (Occurred, $"{{\"scheduledAtUtcMillis\":{Millis(scheduled)},\"delayMillis\":120000,\"outcome\":\"executed\"}}"),
            (Occurred, $"{{\"scheduledAtUtcMillis\":{Millis(scheduled)},\"delayMillis\":null,\"outcome\":\"not-executed\"}}"),
        };

        var fulfillment = DeviceLivenessCalculator.ComputeFulfillment(
            AlarmFulfillmentParser.ParseAll(payloads));

        Assert.NotNull(fulfillment);
        Assert.Equal(1, fulfillment!.Considered);
        Assert.Equal(1, fulfillment.ExcludedNoActualTime);
        Assert.Equal(1d, fulfillment.Rate);
    }
}
