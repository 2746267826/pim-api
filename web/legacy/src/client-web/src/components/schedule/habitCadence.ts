/**
 * #351：习惯频率与归档态的**单一**展示口径。
 *
 * 背景：后端 `HabitRoutineDto.Cadence` 是普通 enum，序列化为**枚举序号**
 * （实测 `"cadence":0`），而不是名字；`Status` 的归档值在前后端都按
 * 忽略大小写比较。此前每个消费方各写一份 label/过滤逻辑，结果是
 * 「习惯中心已修、今日页习惯卡仍显示裸数字 0」。
 * 把口径收到这里，新增展示面时不再各写各的。
 *
 * 与后端 `PlanningModelService.ParseCadence` 对齐：
 * 枚举名（忽略大小写）与枚举序号都能解析；**不认识的值一律归 Custom**，
 * 而不是静默当成 Daily —— 否则一个通过 API/MCP 建的自定义频率习惯，
 * 会被「每天」筛选命中、并在编辑保存时被改写成 Daily。
 */

export const HABIT_CADENCE_OPTIONS = [
  { value: 'Daily', label: '每天' },
  { value: 'Weekly', label: '每周' },
  { value: 'Monthly', label: '每月' },
  { value: 'Custom', label: '自定义' },
] as const;

/** 归档态取值；比较一律忽略大小写，与前端页签过滤保持同一口径。 */
const ARCHIVED_STATUS = 'archived';

/** 未设置（空值）时的展示文案。 */
const UNSET_LABEL = '未设置';

function isBlank(value: unknown): boolean {
  return value === null || value === undefined || String(value).trim() === '';
}

/** 把 cadence 归一为规范枚举名；不认识的值归 `Custom`（与后端 ParseCadence 一致）。 */
export function habitCadenceValue(cadence: unknown): string {
  // 空值：后端未指定时默认 Daily，这里同样回退 Daily。
  if (isBlank(cadence)) return 'Daily';

  const raw = String(cadence).trim();
  const byName = HABIT_CADENCE_OPTIONS.find(
    option => option.value.toLowerCase() === raw.toLowerCase(),
  );
  if (byName) return byName.value;

  const index = Number(raw);
  if (Number.isInteger(index) && index >= 0 && index < HABIT_CADENCE_OPTIONS.length) {
    return HABIT_CADENCE_OPTIONS[index].value;
  }

  // 未知字符串 / 越界序号：后端会解析成 Custom，这里保持一致，
  // 避免界面显示一个后端并不存在的频率。
  return 'Custom';
}

/** 把 cadence 的「枚举名」或「枚举序号」统一为可读文案。 */
export function habitCadenceLabel(cadence: unknown): string {
  if (isBlank(cadence)) return UNSET_LABEL;

  const raw = String(cadence).trim();
  const canonical = habitCadenceValue(raw);
  const option = HABIT_CADENCE_OPTIONS.find(item => item.value === canonical);
  return option ? option.label : raw;
}

/** 用于「频率」下拉筛选：把习惯的 cadence 归一为小写枚举名。 */
export function habitCadenceFilterValue(cadence: unknown): string {
  return habitCadenceValue(cadence).toLowerCase();
}

/** 归档态判定，与后端图层过滤、前端页签过滤同一口径（忽略大小写）。 */
export function isArchivedHabit(habit: { status?: unknown } | null | undefined): boolean {
  return String(habit?.status ?? '').toLowerCase() === ARCHIVED_STATUS;
}
