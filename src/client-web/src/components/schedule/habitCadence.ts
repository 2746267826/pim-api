/**
 * #351：习惯频率与归档态的**单一**展示口径。
 *
 * 背景：后端 `HabitRoutineDto.Cadence` 是普通 enum，序列化为**枚举序号**
 * （实测 `"cadence":0`），而不是名字；`Status` 的归档值在前后端都按
 * 忽略大小写比较。此前每个消费方各写一份 label/过滤逻辑，结果是
 * 「习惯中心已修、今日页习惯卡仍显示裸数字 0」。
 * 把口径收到这里，新增展示面时不再各写各的。
 */

export const HABIT_CADENCE_OPTIONS = [
  { value: 'Daily', label: '每天' },
  { value: 'Weekly', label: '每周' },
  { value: 'Monthly', label: '每月' },
] as const;

/** 归档态取值；比较一律忽略大小写，与前端页签过滤保持同一口径。 */
const ARCHIVED_STATUS = 'archived';

/** 把 cadence 的「枚举名」或「枚举序号」统一为可读文案。 */
export function habitCadenceLabel(cadence: unknown): string {
  const raw = String(cadence ?? '');
  const byName = HABIT_CADENCE_OPTIONS.find(
    option => option.value.toLowerCase() === raw.toLowerCase(),
  );
  if (byName) return byName.label;

  const index = Number(raw);
  if (Number.isInteger(index) && index >= 0 && index < HABIT_CADENCE_OPTIONS.length) {
    return HABIT_CADENCE_OPTIONS[index].label;
  }

  return raw || '未设置';
}

/** 把 cadence 归一为后端接受的枚举名（编辑表单回填用）。 */
export function habitCadenceValue(cadence: unknown): string {
  const raw = String(cadence ?? '');
  const byName = HABIT_CADENCE_OPTIONS.find(
    option => option.value.toLowerCase() === raw.toLowerCase(),
  );
  if (byName) return byName.value;

  const index = Number(raw);
  if (Number.isInteger(index) && index >= 0 && index < HABIT_CADENCE_OPTIONS.length) {
    return HABIT_CADENCE_OPTIONS[index].value;
  }

  return 'Daily';
}

/** 用于「频率」下拉筛选：把习惯的 cadence 归一为小写枚举名。 */
export function habitCadenceFilterValue(cadence: unknown): string {
  return habitCadenceValue(cadence).toLowerCase();
}

/** 归档态判定，与后端图层过滤、前端页签过滤同一口径（忽略大小写）。 */
export function isArchivedHabit(habit: { status?: unknown } | null | undefined): boolean {
  return String(habit?.status ?? '').toLowerCase() === ARCHIVED_STATUS;
}
