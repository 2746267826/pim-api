import { describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import { TreeStatusBanner } from '../FilesPage';

/**
 * AC-10.3 / AC-3.2：目录树状态条的**文案口径**。
 *
 * 这里守的是一个已经踩过的坑：树在补回 AC-4.1 后改为加载「文件 + 目录」的完整子项集合，
 * `totalCount` 因此是**子项**总数；而横幅一度写成「共 N 个文件夹」——在含文件的目录上
 * 会把子项数谎报成目录数（实测根目录 19 个子项里只有 6 个目录）。文案必须与计数口径一致。
 */
describe('TreeStatusBanner', () => {
  it('AC-10.3 加载失败时显示可读原因与重试入口', () => {
    const onRetry = vi.fn();
    render(<TreeStatusBanner state="error" truncation={null} onRetry={onRetry} testIdPrefix="desktop" />);

    const banner = screen.getByTestId('desktop-tree-root-error');
    expect(banner.textContent).toContain('失败');
    fireEvent.click(screen.getByRole('button', { name: '重试' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('AC-3.2 截断文案按「项」表述，不得谎报成「个文件夹」', () => {
    render(
      <TreeStatusBanner
        state="loaded"
        truncation={{ loaded: 2000, total: 8465 }}
        onRetry={vi.fn()}
        testIdPrefix="drawer"
      />,
    );

    const text = screen.getByTestId('drawer-tree-truncated').textContent ?? '';
    expect(text).toContain('2000');
    expect(text).toContain('8465');
    // 计数是子项（含文件与目录）总数，措辞必须一致
    expect(text).toContain('项');
    expect(text).not.toContain('文件夹');
  });

  it('未加载完但未截断 / 加载中时不显示任何状态条（避免噪音）', () => {
    const { rerender } = render(
      <TreeStatusBanner state="loaded" truncation={null} onRetry={vi.fn()} testIdPrefix="desktop" />,
    );
    expect(screen.queryByTestId('desktop-tree-truncated')).toBeNull();
    expect(screen.queryByTestId('desktop-tree-root-error')).toBeNull();

    rerender(<TreeStatusBanner state="loading" truncation={null} onRetry={vi.fn()} testIdPrefix="desktop" />);
    expect(screen.queryByTestId('desktop-tree-root-error')).toBeNull();
  });
});
