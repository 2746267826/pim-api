import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import HabitsPage from '../HabitsPage';

/**
 * issue #351：习惯创建后必须能在界面上编辑 / 归档 / 删除。
 *
 * 此前后端只有「列表 / 创建 / 登记 occurrence」三个端点，前端也只有一个创建表单，
 * 「归档」页签按 status=archived 过滤却没有任何入口能把习惯置为归档 —— 该页签永远为空。
 *
 * 这里守住的是"入口真的存在且真的调用了对应端点"：
 * - 缺任一端点 → 用户只能直接改数据库；
 * - 归档后不从「执行中」消失 → 界面与页签语义自相矛盾。
 */

const getHabits = vi.fn();
const updateHabit = vi.fn();
const archiveHabit = vi.fn();
const deleteHabit = vi.fn();

vi.mock('../../api/calendar', () => ({
  getHabits: () => getHabits(),
  updateHabit: (...args: unknown[]) => updateHabit(...args),
  archiveHabit: (...args: unknown[]) => archiveHabit(...args),
  deleteHabit: (...args: unknown[]) => deleteHabit(...args),
  createHabit: vi.fn(),
}));

function habit(overrides: Partial<{ id: string; title: string; cadence: unknown; source: string; status: string }> = {}) {
  return {
    id: 'h-1',
    title: '晨间复盘',
    cadence: 'Daily',
    source: 'manual',
    status: 'Active',
    ...overrides,
  };
}

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <HabitsPage />
    </QueryClientProvider>,
  );
}

describe('HabitsPage #351', () => {
  beforeEach(() => {
    getHabits.mockReset();
    updateHabit.mockReset();
    archiveHabit.mockReset();
    deleteHabit.mockReset();
    getHabits.mockResolvedValue([habit()]);
    updateHabit.mockResolvedValue(habit());
    archiveHabit.mockResolvedValue(habit({ status: 'Archived' }));
    deleteHabit.mockResolvedValue({ id: 'h-1' });
  });

  it('renders edit, archive and delete entry points for an active habit', async () => {
    renderPage();

    expect(await screen.findByText('晨间复盘')).toBeTruthy();
    expect(screen.getByTestId('habit-edit-h-1')).toBeTruthy();
    expect(screen.getByTestId('habit-archive-h-1')).toBeTruthy();
    expect(screen.getByTestId('habit-delete-h-1')).toBeTruthy();
  });

  it('edits a habit through the update endpoint', async () => {
    renderPage();
    fireEvent.click(await screen.findByTestId('habit-edit-h-1'));

    const titleInput = screen.getByLabelText('习惯名称');
    fireEvent.change(titleInput, { target: { value: '晨间复盘（改）' } });
    fireEvent.change(screen.getByLabelText('习惯频率'), { target: { value: 'Weekly' } });
    fireEvent.click(screen.getByTestId('habit-save-h-1'));

    await waitFor(() => expect(updateHabit).toHaveBeenCalledTimes(1));
    expect(updateHabit).toHaveBeenCalledWith('h-1', expect.objectContaining({
      title: '晨间复盘（改）',
      cadence: 'Weekly',
    }));
  });

  it('archives a habit through the archive endpoint', async () => {
    renderPage();
    fireEvent.click(await screen.findByTestId('habit-archive-h-1'));

    await waitFor(() => expect(archiveHabit).toHaveBeenCalledWith('h-1'));
  });

  it('deletes a habit through the delete endpoint after confirmation', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(true);
    renderPage();
    fireEvent.click(await screen.findByTestId('habit-delete-h-1'));

    await waitFor(() => expect(deleteHabit).toHaveBeenCalledWith('h-1'));
    confirmSpy.mockRestore();
  });

  it('does not delete when the user cancels the confirmation', async () => {
    const confirmSpy = vi.spyOn(window, 'confirm').mockReturnValue(false);
    renderPage();
    fireEvent.click(await screen.findByTestId('habit-delete-h-1'));

    await waitFor(() => expect(deleteHabit).not.toHaveBeenCalled());
    confirmSpy.mockRestore();
  });

  it('shows archived habits only under the archive tab and offers no archive action there', async () => {
    getHabits.mockResolvedValue([habit({ status: 'Archived', title: '已归档习惯' })]);
    renderPage();

    // 「执行中」页签（默认）不应出现归档习惯
    expect(await screen.findByText('当前筛选下没有习惯记录。')).toBeTruthy();

    fireEvent.click(screen.getByRole('radio', { name: '归档' }));
    expect(await screen.findByText('已归档习惯')).toBeTruthy();
    // 归档页签里不再提供「归档」动作（已归档无需重复归档）
    expect(screen.queryByTestId('habit-archive-h-1')).toBeNull();
  });

  it('renders a numeric cadence value readably', async () => {
    // 后端把 HabitCadence 序列化为枚举序号（0=Daily）；界面不得显示成裸数字。
    getHabits.mockResolvedValue([habit({ cadence: 0 })]);
    renderPage();

    // 卡片上的频率徽标应显示「每天 · Active」，而不是裸数字 "0 · Active"。
    expect(await screen.findByText(/每天 · /)).toBeTruthy();
    expect(screen.queryByText(/^0 · /)).toBeNull();
  });

  /**
   * 复审 Important：频率筛选必须处理"后端下发枚举序号"这一真实形态。
   * 此前用 `String(habit.cadence).toLowerCase() === 'daily'` 比较，
   * 序号 0 会得到 "0" !== "daily"，于是选「每天」时明明有每日习惯却显示空态。
   */
  it('按频率筛选时能匹配后端下发的枚举序号 cadence', async () => {
    getHabits.mockResolvedValue([
      habit({ id: 'h-daily', title: '每日习惯', cadence: 0 }),
      habit({ id: 'h-weekly', title: '每周习惯', cadence: 1 }),
    ]);
    renderPage();

    expect(await screen.findByText('每日习惯')).toBeTruthy();
    expect(screen.getByText('每周习惯')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('频率'), { target: { value: 'daily' } });

    expect(await screen.findByText('每日习惯')).toBeTruthy();
    expect(screen.queryByText('每周习惯')).toBeNull();
    expect(screen.queryByText('当前筛选下没有习惯记录。')).toBeNull();
  });

  /**
   * 复审 Important：编辑表单必须回显已保存的描述，否则"只改标题"会让用户
   * 在不知情的情况下把描述重新提交/清空。
   */
  it('编辑表单回显已保存的描述', async () => {
    getHabits.mockResolvedValue([habit({ description: '已保存的描述' })]);
    renderPage();

    fireEvent.click(await screen.findByTestId('habit-edit-h-1'));

    const descriptionInput = screen.getByLabelText('习惯描述') as HTMLInputElement;
    expect(descriptionInput.value).toBe('已保存的描述');
  });

  it('清空描述后提交的是空串（显式清空，而不是"未传"）', async () => {
    getHabits.mockResolvedValue([habit({ description: '待清空' })]);
    renderPage();

    fireEvent.click(await screen.findByTestId('habit-edit-h-1'));
    fireEvent.change(screen.getByLabelText('习惯描述'), { target: { value: '' } });
    fireEvent.click(screen.getByTestId('habit-save-h-1'));

    await waitFor(() => expect(updateHabit).toHaveBeenCalledTimes(1));
    expect(updateHabit).toHaveBeenCalledWith('h-1', expect.objectContaining({ description: '' }));
  });
});
