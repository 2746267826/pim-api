import { describe, expect, it, vi, beforeEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { MemoryRouter } from 'react-router-dom';
import HabitCalendarHeatmap from '../HabitCalendarHeatmap';

/**
 * #351 复审 Important：今日页 / 展览馆的「习惯」卡必须与习惯中心口径一致。
 *
 * 此卡直接调用 `getHabits()` 并列出全部习惯，此前既不过滤归档态、
 * 也不处理"后端把 cadence 序列化为枚举序号"的形态，于是：
 * - 归档后官方 calendar.habits 区块已为空，这张卡却仍显示该习惯（自相矛盾）；
 * - 频率显示成裸数字 "0"。
 */

const getHabits = vi.fn();

vi.mock('../../../api/calendar', () => ({
  getHabits: () => getHabits(),
}));

function habit(overrides: Record<string, unknown> = {}) {
  return {
    id: 'h-1',
    title: '晨间复盘',
    cadence: 'Daily',
    source: 'manual',
    status: 'Active',
    ...overrides,
  };
}

function renderCard() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <MemoryRouter>
        <HabitCalendarHeatmap />
      </MemoryRouter>
    </QueryClientProvider>,
  );
}

describe('HabitCalendarHeatmap #351', () => {
  beforeEach(() => {
    getHabits.mockReset();
  });

  it('归档习惯不出现在今日页习惯卡（与日历图层口径一致）', async () => {
    getHabits.mockResolvedValue([
      habit({ id: 'h-active', title: '活跃习惯', status: 'Active' }),
      habit({ id: 'h-archived', title: '已归档习惯', status: 'Archived' }),
    ]);
    renderCard();

    expect(await screen.findByText('活跃习惯')).toBeTruthy();
    expect(screen.queryByText('已归档习惯')).toBeNull();
  });

  it('归档态大小写不同也照样过滤', async () => {
    getHabits.mockResolvedValue([habit({ title: '小写归档', status: 'archived' })]);
    renderCard();

    await waitFor(() => expect(screen.queryByText('小写归档')).toBeNull());
  });

  it('枚举序号 cadence 显示为可读文案而不是裸数字', async () => {
    getHabits.mockResolvedValue([habit({ cadence: 0 })]);
    renderCard();

    expect(await screen.findByText('每天')).toBeTruthy();
    expect(screen.queryByText('0')).toBeNull();
  });

  it('全部习惯都归档时显示空态引导', async () => {
    getHabits.mockResolvedValue([habit({ title: '已归档', status: 'Archived' })]);
    renderCard();

    expect(await screen.findByText('还没有创建习惯')).toBeTruthy();
    expect(screen.queryByText('已归档')).toBeNull();
  });
});
