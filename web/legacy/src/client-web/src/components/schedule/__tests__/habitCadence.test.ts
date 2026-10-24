import { describe, expect, it } from 'vitest';
import {
  habitCadenceFilterValue,
  habitCadenceLabel,
  habitCadenceValue,
  isArchivedHabit,
} from '../habitCadence';

/**
 * #351：习惯频率/归档口径必须与后端一致。
 *
 * 后端 `HabitRoutineDto.Cadence` 是普通 enum，序列化为**枚举序号**
 * （`HabitCadence`: Daily=0, Weekly=1, Monthly=2, Custom=3），
 * 而 `ParseCadence` 对**不认识的值一律归 Custom**。
 * 若前端把不认识的值当成 Daily，一个自定义频率习惯会被「每天」筛选命中，
 * 并在编辑保存时被改写成 Daily（复审 Minor）。
 */
describe('habitCadence 口径', () => {
  it('枚举序号映射为可读文案', () => {
    expect(habitCadenceLabel(0)).toBe('每天');
    expect(habitCadenceLabel(1)).toBe('每周');
    expect(habitCadenceLabel(2)).toBe('每月');
    expect(habitCadenceLabel(3)).toBe('自定义');
  });

  it('枚举名（含大小写变体）映射为可读文案', () => {
    expect(habitCadenceLabel('Daily')).toBe('每天');
    expect(habitCadenceLabel('daily')).toBe('每天');
    expect(habitCadenceLabel('WEEKLY')).toBe('每周');
    expect(habitCadenceLabel('Monthly')).toBe('每月');
  });

  it('未知值归 Custom（与后端 ParseCadence 一致），不冒充 Daily', () => {
    // 后端把 "Weekdays"/"biweekly" 之类解析成 HabitCadence.Custom。
    expect(habitCadenceValue('Weekdays')).toBe('Custom');
    expect(habitCadenceValue('biweekly')).toBe('Custom');
    expect(habitCadenceValue(99)).toBe('Custom');
    expect(habitCadenceLabel('Weekdays')).toBe('自定义');

    // 关键回归：未知值不得被"每天"筛选命中。
    expect(habitCadenceFilterValue('Weekdays')).not.toBe('daily');
    expect(habitCadenceFilterValue(99)).not.toBe('daily');
  });

  it('空值回退 Daily（与后端 NormalizeShort 的默认值一致）', () => {
    expect(habitCadenceValue(null)).toBe('Daily');
    expect(habitCadenceValue(undefined)).toBe('Daily');
    expect(habitCadenceValue('')).toBe('Daily');
    expect(habitCadenceValue('   ')).toBe('Daily');
    // 展示层对空值给"未设置"，不谎报为"每天"。
    expect(habitCadenceLabel(null)).toBe('未设置');
    expect(habitCadenceLabel('')).toBe('未设置');
  });

  it('归档态判定忽略大小写', () => {
    expect(isArchivedHabit({ status: 'Archived' })).toBe(true);
    expect(isArchivedHabit({ status: 'archived' })).toBe(true);
    expect(isArchivedHabit({ status: 'ARCHIVED' })).toBe(true);
    expect(isArchivedHabit({ status: 'Active' })).toBe(false);
    expect(isArchivedHabit({})).toBe(false);
    expect(isArchivedHabit(null)).toBe(false);
  });
});
