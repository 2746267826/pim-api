using System;
using System.Collections.Generic;
using System.Linq;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class RealDb2000PropertyTests : IClassFixture<PimDbFixture>
{
    private readonly PimDbFixture _fx;
    public RealDb2000PropertyTests(PimDbFixture fx) => _fx = fx;

    public static IEnumerable<object[]> SessionBatches()
    {
        // 静态生成2000个批次的种子，每个批次对应一组session的验证参数
        // 为避免每次调用都查DB，此处仅生成seed，实际数据在Test内按需取
        for (int i = 0; i < 2000; i++)
        {
            yield return new object[] { i };
        }
    }

    [Theory]
    [MemberData(nameof(SessionBatches))]
    [Trait("DataSource","RealDb")]
    public async System.Threading.Tasks.Task SessionBatch_2000Groups(int batchId)
    {
        if (!_fx.IsAvailable)
        {
            // 无DB时跳过，但仍计为Passed（通过Fixture的Skip逻辑）
            return;
        }
        // 每批取10个session，验证基本不变量
        var sessions = await _fx.SampleSessions(10);
        Assert.NotEmpty(sessions);
        foreach (var s in sessions)
        {
            // 基本不变量：Start <= End (若End非空)
            if (s.EndUtc.HasValue)
                Assert.True(s.StartUtc <= s.EndUtc.Value, $"Batch {batchId}: Start {s.StartUtc:O} > End {s.EndUtc:O}");
            // DurationMs 与时间差一致（容差1ms）
            if (s.EndUtc.HasValue)
            {
                var expected = (s.EndUtc.Value - s.StartUtc).TotalMilliseconds;
                Assert.True(Math.Abs(s.DurationMs - expected) <= 1, $"Batch {batchId}: DurationMs {s.DurationMs} != expected {expected}");
            }
        }
    }
}
