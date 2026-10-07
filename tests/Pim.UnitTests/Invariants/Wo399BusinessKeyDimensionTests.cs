using System;
using System.Collections.Generic;
using Pim.Core.Invariants;
using Xunit;

namespace Pim.UnitTests.Invariants;

/// <summary>
/// WO-ISSUES-396-400-20261007 · REQ-3（#399）：S4 手机端业务键必须与
/// <c>mobile_usage_events</c> 的库层唯一约束取**相同维度**
/// （库层唯一索引：<c>(user_id, device_id, package_name, event_type, event_timestamp_utc, class_name)</c>）。
/// <para>
/// 旧键只有 <c>(device, package, timestamp, event_type)</c>，漏掉了 <c>class_name</c> 与 <c>user_id</c>：
/// 同一毫秒切换的两个 Activity 会被判成"重复"（生产 200 组 / 419 行全部如此）。
/// 同 <c>user_id</c> 且同 <c>class_name</c> 的真实重复必须**继续被判出**。
/// </para>
/// 对应 AC-3.2。
/// </summary>
public class Wo399BusinessKeyDimensionTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 18, 0, 12, DateTimeKind.Utc);
    private static readonly DateTime At = new(2026, 10, 7, 7, 17, 43, 702, DateTimeKind.Utc);
    private const string Device = "PHONE-01";
    private const string Package = "com.sankuai.meituan";
    private const string EventType = "UNKNOWN_23";
    private const string Class = "com.sankuai.meituan.activity.ScanQRInMeituanActivity";
    private const string User = "user-1";

    private static List<BusinessRecordKey> TwoMobileKeys(string classA, string classB, string userA, string userB) => new()
    {
        BusinessRecordKey.ForMobile(Device, Package, At, EventType, classA, userA),
        BusinessRecordKey.ForMobile(Device, Package, At, EventType, classB, userB)
    };

    [Fact]
    public void Ac3_2_SameKeySameClass_SameUser_IsDuplicate()
    {
        // 与库层唯一索引完全同维度的两行 → 违法（重复 1 条）。
        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(
            TwoMobileKeys(Class, Class, User, User), referenceTimeUtc: Now);

        Assert.False(result.Pass);
        Assert.Equal(1, result.WindowViolations);
        var violation = Assert.Single(result.Violations);
        Assert.Equal("Mobile", violation.Fields["domain"]);
        Assert.Equal("2", violation.Fields["duplicateCount"]);
    }

    [Fact]
    public void Ac3_2_SameMillisecondDifferentClass_IsCompliant()
    {
        // 同一毫秒、同一应用、同一事件类型，但 class_name 不同（切 Activity）→ 合规。
        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(
            TwoMobileKeys(Class, "com.sankuai.meituan.activity.ArbiterLoadingActivity", User, User),
            referenceTimeUtc: Now);

        Assert.True(result.Pass, result.Detail);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void Ac3_2_SameFourColumnsSameClassDifferentUser_IsCompliant()
    {
        // 同样 4 列 + 同 class_name，但 user_id 不同 → 合规（与库层唯一索引一致）。
        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(
            TwoMobileKeys(Class, Class, User, "user-2"), referenceTimeUtc: Now);

        Assert.True(result.Pass, result.Detail);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void Ac3_2_SameKeyDifferentDevice_IsCompliant()
    {
        // 库层唯一索引含 device_id：同 user / 同 class / 同毫秒但不同设备 → 合规。
        var keys = new List<BusinessRecordKey>
        {
            BusinessRecordKey.ForMobile("PHONE-01", Package, At, EventType, Class, User),
            BusinessRecordKey.ForMobile("PHONE-02", Package, At, EventType, Class, User)
        };

        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(keys, referenceTimeUtc: Now);

        Assert.True(result.Pass, result.Detail);
        Assert.Equal(0, result.TotalViolations);
    }

    [Fact]
    public void Ac3_3_LocationAndPcDomains_StillDetectTheirOwnDuplicates()
    {
        // AC-3.3：另两个域的判据行为不得变化 —— 定位域与 PC 域的真实重复仍要被判出。
        var keys = new List<BusinessRecordKey>
        {
            BusinessRecordKey.ForLocation("DEV-1", At, 31.230416, 121.473701),
            BusinessRecordKey.ForLocation("DEV-1", At, 31.230416, 121.473701),
            BusinessRecordKey.ForPc("DEV-1", At, 60, "active", "chrome.exe", "Chrome", "inst-1"),
            BusinessRecordKey.ForPc("DEV-1", At, 60, "active", "chrome.exe", "Chrome", "inst-1")
        };

        var result = DataReliabilityInvariants.CheckS4_BusinessKeyUnique(keys, referenceTimeUtc: Now);

        Assert.False(result.Pass);
        Assert.Equal(2, result.WindowViolations);
        Assert.Contains(result.Violations, v => v.Fields["domain"] == "Location");
        Assert.Contains(result.Violations, v => v.Fields["domain"] == "Pc");
    }
}
