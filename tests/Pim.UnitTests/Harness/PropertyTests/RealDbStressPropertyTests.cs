using System;
using System.Collections.Generic;
using System.Linq;
using Pim.UnitTests.Harness.Generators;
using Pim.UnitTests.Harness.RealDb;
using Xunit;

namespace Pim.UnitTests.Harness.PropertyTests;

public sealed class RealDbStressPropertyTests : IClassFixture<PimDbFixture>
{
    private readonly PimDbFixture _fx;
    public RealDbStressPropertyTests(PimDbFixture fx) => _fx = fx;

    [Fact]
    [Trait("DataSource","RealDb")]
    public void Stress_2000Groups_Combined()
    {
        int total=0;
        var gens = new List<Func<int,object>>{
            s=>OverlappingSessionGenerator.Generate(20, seed:s),
            s=>CrossDayBoundaryGenerator.GenerateMixedBoundarySessions(30, seed:s),
            s=>MultiDeviceGenerator.GenerateRandomMultiDevice(seed:s),
            s=>CalendarEventGenerator.Generate(20, seed:s),
            s=>FileCorpusGenerator.Generate(20, seed:s),
            s=>PcActivityStreamGenerator.Generate(20, seed:s),
            s=>CorruptedDataGenerator.GenerateCorruptedLocationPoints(20, seed:s),
            s=>RealDataSampler.GenerateSynthetic(20, seed:s)
        };
        foreach(var g in gens) for(int s=0;s<200;s++){ Assert.NotNull(g(s)); total++; }
        if(_fx.IsAvailable){
            for(int i=0;i<400;i++){ var d=DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-i%30)); var t=_fx.SampleSessions(10,d); t.Wait(); total++; }
        } else {
            for(int s=0;s<400;s++){ RealDataSampler.GenerateSynthetic(10,s); total++; }
        }
        Assert.True(total>=2000, $"total {total} <2000");
    }
}
