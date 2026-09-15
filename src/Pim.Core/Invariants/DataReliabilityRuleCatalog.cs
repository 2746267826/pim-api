using System;
using System.Collections.Generic;
using System.Linq;

namespace Pim.Core.Invariants;

/// <summary>
/// 尺子分组（EPIC #254 §4）：第一层数据自洽、第二层覆盖完整、第三层链路健康。
/// </summary>
public enum DataReliabilityGroup
{
    /// <summary>第一层 · 数据自洽（数据自己不打结）。</summary>
    SelfConsistency,

    /// <summary>第二层 · 覆盖完整（该有的都在）。</summary>
    Coverage,

    /// <summary>第三层 · 链路健康（后台任务还活着）。</summary>
    PipelineHealth
}

/// <summary>
/// 一条尺子的静态元数据：编号、名称、分组、判据原文、阈值口径、设定理由与关联 issue。
/// 判据的"实现"只有一份（<see cref="DataReliabilityInvariants"/>），这里只描述它，供体检接口与设置页面板展示。
/// </summary>
public sealed record DataReliabilityRuleDefinition(
    string Code,
    string InvariantCode,
    int Order,
    string Name,
    DataReliabilityGroup Group,
    string GroupLabel,
    string Criterion,
    string Threshold,
    string Rationale,
    IReadOnlyList<int> RelatedIssues);

/// <summary>
/// 13 根基准尺子（S1–S13）的元数据总表。
/// 判据原文与阈值口径取自 EPIC #254 §4/§5，与 <see cref="DataReliabilityInvariants"/> 的方法注释保持一致。
/// </summary>
public static class DataReliabilityRuleCatalog
{
    /// <summary>按 <see cref="DataReliabilityRuleDefinition.Order"/> 升序排列的 13 条尺子。</summary>
    public static IReadOnlyList<DataReliabilityRuleDefinition> All { get; } = new[]
    {
        new DataReliabilityRuleDefinition(
            Code: "S1",
            InvariantCode: "INV-P16",
            Order: 1,
            Name: "同类型事件不重叠",
            Group: DataReliabilityGroup.SelfConsistency,
            GroupLabel: "数据自洽",
            Criterion: "同设备、同事件类型的事件区间两两不相交（不存在 A.start < B.end 且 B.start < A.end）；父子层级关系另立规则，不在此判据内。",
            Threshold: "重叠对数 = 0；最近 24 小时（T4）内发生的重叠计为「新增」红线，更早的计入「存量」只计数不当红线。",
            Rationale: "同一设备在同一时刻不可能产生两个同级别的互斥前台焦点或互斥状态；出现重叠说明采集端裁剪或入库去重损坏，会让时长与频次同时虚高。",
            RelatedIssues: new[] { 249 }),

        new DataReliabilityRuleDefinition(
            Code: "S2",
            InvariantCode: "INV-P17",
            Order: 2,
            Name: "超长事件必须拿得出活动证据",
            Group: DataReliabilityGroup.SelfConsistency,
            GroupLabel: "数据自洽",
            Criterion: "超过超长线的事件，其区间内必须命中三态之一：操作活跃（键鼠输入密度达标）、观看活跃（区间内存在媒体活动）、明确空档（事件本身就是「缺数据」类）。三者都不满足判「疑似未收尾」，必须显式标记且不得计入活跃时长。",
            Threshold: "超长线 30 分钟（T1b）；操作活跃输入密度线 1 次/分钟（T1a）。不设单条时长上限——长不等于假，判的是「这段时长里有没有人」。",
            Rationale: "实测打游戏 84 次/分钟、挂机 0.01 次/分钟，相差三个数量级；真实操作或媒体播放即使挂机也会留下心跳，超过 30 分钟毫无信号大概率是采集端没收到退出事件造成的僵尸时长。",
            RelatedIssues: new[] { 251 }),

        new DataReliabilityRuleDefinition(
            Code: "S3",
            InvariantCode: "INV-P18",
            Order: 3,
            Name: "单日时长有界",
            Group: DataReliabilityGroup.SelfConsistency,
            GroupLabel: "数据自洽",
            Criterion: "按 Asia/Shanghai 04:00 起算的业务日，单设备单日活跃时长（先做三态过滤，再对重叠区间合并去重）不得超过硬上限；超过清醒窗口警告线则报黄。",
            Threshold: "硬上限 24 小时（T5）；警告线 = 清醒窗口 16 小时 × 90% = 14.4 小时。",
            Rationale: "一天物理上只有 24 小时；用户作息约 8:00–23:30，清醒时间约 16 小时，超过 14.4 小时说明极高强度活跃或存在异常累积，20 小时的警告线没有意义。",
            RelatedIssues: Array.Empty<int>()),

        new DataReliabilityRuleDefinition(
            Code: "S4",
            InvariantCode: "INV-C18",
            Order: 4,
            Name: "业务键唯一（不重复）",
            Group: DataReliabilityGroup.SelfConsistency,
            GroupLabel: "数据自洽",
            Criterion: "定位按 (device, recorded_at, lat, lon) 唯一；手机事件按 (device, package, event_time, event_type) 唯一；PC 事件按 (device, timestamp, duration, event_type, app_name, browser, instance_id) 唯一。",
            Threshold: "新增重复行 = 0；更早的重复行计入「存量」只计数。",
            Rationale: "重复事件会导致时长与频次双重虚高，破坏聚合指标的可信度；实测定位 1065/6280 行重复、手机同刻重复 1245 条，都属于无唯一键约束的历史欠账。",
            RelatedIssues: new[] { 246 }),

        new DataReliabilityRuleDefinition(
            Code: "S5",
            InvariantCode: "INV-P19",
            Order: 5,
            Name: "时钟可信",
            Group: DataReliabilityGroup.SelfConsistency,
            GroupLabel: "数据自洽",
            Criterion: "事件时间戳不得超前服务端接收时间超过容差；仅存在历史违规时降级为黄。",
            Threshold: "容差 5.0 分钟（ClockSkewToleranceMinutes）。",
            Rationale: "客户端时钟与网络授时之间存在少许偏差或时钟漂移属正常现象，5 分钟是工业标准网络时间容限；超过 5 分钟属于严重超前或时钟穿越，会让事件落到未来时间轴上。",
            RelatedIssues: Array.Empty<int>()),

        new DataReliabilityRuleDefinition(
            Code: "S6",
            InvariantCode: "INV-P20",
            Order: 6,
            Name: "设备必须自己声明下线",
            Group: DataReliabilityGroup.Coverage,
            GroupLabel: "覆盖完整",
            Criterion: "设备停止出数必须自己有交代：没有下线声明的空档超过阈值判红；上传滞后 p99 超过阈值同样判红。",
            Threshold: "无声明空档 30 分钟（T2）；上传滞后 p99 30 分钟（T2）。实测上传滞后 p99 = 23 分钟、相邻事件间隔 p99 = 22.7 分钟。",
            Rationale: "现代操作系统的关机与睡眠都有系统钩子，停摆本身可以被告知；没有声明就突然停止 30 分钟，说明采集端崩溃或掉线，用户看到的「没记录」与真实行为无法区分。",
            RelatedIssues: new[] { 252 }),

        new DataReliabilityRuleDefinition(
            Code: "S7",
            InvariantCode: "INV-P21",
            Order: 7,
            Name: "断档必须在时间轴上被标记",
            Group: DataReliabilityGroup.Coverage,
            GroupLabel: "覆盖完整",
            Criterion: "相邻时间线区间之间超过阈值的空洞，必须被「缺数据」类事件（gap 或等价标记）完整覆盖，不得留无解释的空白。",
            Threshold: "未标记空洞 = 0；断档判定阈值 15 分钟。",
            Rationale: "超过 15 分钟的无数据空洞若在 UI 上被直接拼接或留白，用户无法分辨是设备没用还是系统漏记；实测 29/29 处空洞全都没有标记。",
            RelatedIssues: new[] { 252 }),

        new DataReliabilityRuleDefinition(
            Code: "S8",
            InvariantCode: "INV-C19",
            Order: 8,
            Name: "日界一致（三层口径）",
            Group: DataReliabilityGroup.Coverage,
            GroupLabel: "覆盖完整",
            Criterion: "同一时刻在「数据字段的日期桶」「按日接口的查询窗口」「页面展示的业务日」三处必须归属同一天，统一为 Asia/Shanghai 业务日、04:00 起算（业务日 D = [D 04:00, D+1 04:00)）。",
            Threshold: "不一致行数 = 0。",
            Rationale: "跨夜作息（凌晨 0–4 点工作）属于前一天的夜间延伸；三层口径不一致会出现「列表查得到、聚合统计丢失」的假丢失。当前数据字段层实测 0 行偏离，接口层与展示层需接口契约测试覆盖。",
            RelatedIssues: new[] { 236, 239 }),

        new DataReliabilityRuleDefinition(
            Code: "S9",
            InvariantCode: "INV-C20",
            Order: 9,
            Name: "有缺口必有信号",
            Group: DataReliabilityGroup.Coverage,
            GroupLabel: "覆盖完整",
            Criterion: "覆盖率低于红线时，报告状态必须报红或报黄，绝不得报「正常」；分母无法精准界定时必须输出未知并说明口径，而不是给出一个好看的假比例。",
            Threshold: "覆盖率 = 有效数据时长 ÷ 设备在线时长；< 95% 报红、< 99% 报黄（T6）。",
            Rationale: "95% 覆盖率是个人生活记录可信度的基准底线；低覆盖率若显示「正常」属于静默掩盖故障，实测断流一天仍报正常的根因就在这条尺子缺席。",
            RelatedIssues: new[] { 244 }),

        new DataReliabilityRuleDefinition(
            Code: "S10",
            InvariantCode: "INV-C21",
            Order: 10,
            Name: "后台任务必须有产出",
            Group: DataReliabilityGroup.PipelineHealth,
            GroupLabel: "链路健康",
            Criterion: "分类补齐、汇总入库、派生表构建等后台任务，在最近执行窗口内产出必须大于 0；存在可处理数据却产出 0 行判红。",
            Threshold: "可处理数据 > 0 且产出 = 0 判红。",
            Rationale: "后台任务常因未捕获异常退出、无限等待或查询条件脱节导致空转；空转产出 0 却报成功属于致命静默失败，实测分类补齐自 09-01 起每天产出 0 却无人发现。",
            RelatedIssues: new[] { 234 }),

        new DataReliabilityRuleDefinition(
            Code: "S11",
            InvariantCode: "INV-M21",
            Order: 11,
            Name: "状态语义自洽",
            Group: DataReliabilityGroup.PipelineHealth,
            GroupLabel: "链路健康",
            Criterion: "批次状态必须与其内部计数自洽：failed_count = 0 的批次不得处于 failed / completed-with-errors；failed_count > 0 的批次不得处于 completed；处理计数（accepted/failed/rejected/skipped）全为 0 的批次不得处于 completed。",
            Threshold: "状态语义不自洽的批次数 = 0。",
            Rationale: "客户端条目级校验拒绝（例如零时长过滤）曾被误当成整批失败，导致质量面板误报同步失败并引导用户无意义重试；实测 102/102 个非 completed 批次的 failed_count 都是 0。",
            RelatedIssues: new[] { 241 }),

        new DataReliabilityRuleDefinition(
            Code: "S12",
            InvariantCode: "INV-M22",
            Order: 12,
            Name: "派生表在使用",
            Group: DataReliabilityGroup.PipelineHealth,
            GroupLabel: "链路健康",
            Criterion: "派生表（时间线块、使用聚合）在最近窗口内有源数据时必须非空；若设计为在线计算，必须显式声明，不允许「存在一张没人写的空表」这种含糊状态。",
            Threshold: "最近 24 小时（T4）有源数据时，派生表行数 > 0（或显式声明在线计算）。",
            Rationale: "死表或未初始化的空派生表会让查询落入空表返回空白，或让开发者误以为已有预聚合而引发性能雪崩；实测手机时间线块与使用聚合均为 0 行。",
            RelatedIssues: new[] { 247 }),

        new DataReliabilityRuleDefinition(
            Code: "S13",
            InvariantCode: "INV-P22",
            Order: 13,
            Name: "实例唯一",
            Group: DataReliabilityGroup.PipelineHealth,
            GroupLabel: "链路健康",
            Criterion: "同一 device_id 在任一时刻只应有一条独立采集流，按 instance_id 或互斥的轮询相位判定。",
            Threshold: "同一小时内出现 ≥ 2 条互斥采集流判红。",
            Rationale: "多实例同时采集同一设备会产生竞态覆盖、双倍计数与会话断裂，破坏时序完整性；客户端已加互斥，尺子用于确认它真的生效。",
            RelatedIssues: new[] { 250 })
    };

    /// <summary>按编号查找尺子（大小写不敏感）；未知编号返回 null。</summary>
    public static DataReliabilityRuleDefinition? Find(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        return All.FirstOrDefault(definition =>
            string.Equals(definition.Code, code.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 体检结果字典键，形如 <c>S1_INV-P16</c>。
    /// 必须与 <c>DataReliabilityQualityInspector</c> 既有的键格式完全一致，否则会破坏既有监控与测试。
    /// </summary>
    public static string BuildKey(DataReliabilityRuleDefinition definition) =>
        $"{definition.Code}_{definition.InvariantCode}";

    /// <summary>四态判据结果映射为体检接口使用的字符串状态：red / yellow / green / unknown。</summary>
    public static string NormalizeStatus(InvariantStatus status) => status switch
    {
        InvariantStatus.Fail => "red",
        InvariantStatus.Warning => "yellow",
        InvariantStatus.Pass => "green",
        _ => "unknown"
    };

    /// <summary>字符串状态对应的中文标签。</summary>
    public static string StatusLabel(string status) => status?.ToLowerInvariant() switch
    {
        "red" => "红",
        "yellow" => "黄",
        "green" => "绿",
        _ => "未知"
    };
}
