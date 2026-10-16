namespace Pim.Core.Planning;

// 工单 03 D1.4(a)：本文件原先还声明了 TaskPlanningState / TaskSegmentStatus /
// ReminderStatus / ReminderChannel / PlanningSource / ReportKind —— 全部**全仓零引用**
// （只有 HabitCadence 在用）。已按该工单「明确清理（成本最低）」的口径删除，依据：
//
//   1. 死代码本身就是「契约/模型说了但没人兑现」的假承诺，与本仓「契约说的必须成真」相冲突；
//   2. TaskSegmentStatus 是 PascalCase（Planned/Active/Paused/…），而数据库默认值、
//      pim-web 硬编码与既有数据都是小写 planned。若按 D1.4(b) 直接启用，会形成
//      PascalCase / 小写两套值域并存 + 旧数据失配，必须附带映射与回填迁移；
//      本次没有该需求，选 (a) 的成本与风险都最低；
//   3. 任务状态与执行段状态的真值来源改为模块内的小写常量类
//      （Pim.Module.Calendar.Entities.TaskStatuses / TaskSegmentStatuses），
//      它们**有**真实的服务端校验与契约 enum 与之对应，不再是空承诺。
//
// 删除范围仅限上述零引用枚举；HabitCadence 仍被 PlanningDtos.HabitRoutineDto 使用
// （并且已出现在契约里，按整数枚举序列化），保留不动。

public enum HabitCadence
{
    Daily,
    Weekly,
    Monthly,
    Custom
}
