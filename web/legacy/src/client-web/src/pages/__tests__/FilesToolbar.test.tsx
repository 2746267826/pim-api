import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import FilesPage from '../FilesPage';

/**
 * REQ-11 / REQ-13 / REQ-21：文件页工具条上「上传」「新建文件夹」「传输任务」「我的分享」四个入口。
 *
 * 这里守的是一个已经踩到的坑：`transfer-toggle` 与 `my-shares-button` 一度**嵌套**在一起
 * （「我的分享」写成了「传输任务」的子按钮）。按钮里嵌按钮是非法 HTML，
 * 且子按钮会盖住父按钮的命中区域，实测：
 * - 点「传输任务」→ 命中的是「我的分享」，于是**同时**打开了传输面板和「我的分享」弹窗；
 * - 该弹窗是全屏遮罩（z-[60]），弹窗一出现，后面所有点击都被它拦下（端到端脚本卡死在这里）。
 * 所以四个入口必须是平级的兄弟节点，点谁只触发谁。
 */

vi.mock('../../auth/AuthContext', () => ({
  useAuth: () => ({ isAuthenticated: true, username: 'tester', role: 'admin', logout: vi.fn() }),
}));

vi.mock('../../api/files', async () => {
  const actual = await vi.importActual<typeof import('../../api/files')>('../../api/files');
  // vi.mock 的工厂函数会被提升到文件顶部，不能引用外部变量，因此这里内联同样的 provider
  const p = {
    id: 'p-1',
    provider: 'onedrive',
    displayName: 'OneDrive',
    status: 'connected',
    lastSyncAt: null,
    lastSyncStatus: null,
    lastError: null,
    createdAt: '2026-09-01T00:00:00Z',
  };
  return {
    ...actual,
    getFileProviders: vi.fn().mockResolvedValue([p]),
    getFileItems: vi.fn().mockResolvedValue({ result: { items: [], totalCount: 0, page: 1, pageSize: 100, totalPages: 1 } }),
    searchFiles: vi.fn().mockResolvedValue({ items: [], totalCount: 0, page: 1, pageSize: 100, totalPages: 1 }),
    getOneDriveSyncStatus: vi.fn().mockResolvedValue({ status: 'idle', lastSyncAt: null, lastError: null }),
    getAllShares: vi.fn().mockResolvedValue([]),
  };
});

vi.mock('../../components/files/upload/TransferPanel', async () => {
  const actual = await vi.importActual<typeof import('../../components/files/upload/TransferPanel')>(
    '../../components/files/upload/TransferPanel',
  );
  return {
    ...actual,
    default: ({ open, onClose }: { open: boolean; onClose: () => void }) =>
      open ? (
        <div data-testid="transfer-panel">
          <button type="button" aria-label="关闭传输面板" onClick={onClose}>
            关闭
          </button>
        </div>
      ) : null,
  };
});

function renderPage() {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  return render(
    <QueryClientProvider client={queryClient}>
      <FilesPage />
    </QueryClientProvider>,
  );
}

describe('FilesPage 工具条入口', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
  });

  it('REQ-11/13/21 四个入口是平级按钮，不得互相嵌套', async () => {
    renderPage();

    const upload = await screen.findByTestId('upload-input');
    const newFolder = screen.getByTestId('new-folder-button');
    const transfer = screen.getByTestId('transfer-toggle');
    const myShares = screen.getByTestId('my-shares-button');

    // 按钮里不能再有按钮：既非法，也会让父按钮的命中区域被子按钮覆盖
    for (const [name, el] of [
      ['new-folder-button', newFolder],
      ['transfer-toggle', transfer],
      ['my-shares-button', myShares],
    ] as const) {
      expect(el.querySelector('button'), `${name} 中不应嵌套其它按钮`).toBeNull();
    }
    expect(upload.closest('button')).toBeNull();
  });

  it('REQ-13 点「传输任务」只切换传输面板，不得顺带打开「我的分享」', async () => {
    renderPage();
    const transfer = await screen.findByTestId('transfer-toggle');

    fireEvent.click(transfer);
    await waitFor(() => expect(screen.getByTestId('transfer-panel')).toBeTruthy());
    // 关键断言：点传输任务不应弹出「我的分享」全屏遮罩
    expect(screen.queryByTestId('my-shares-dialog')).toBeNull();

    fireEvent.click(transfer);
    await waitFor(() => expect(screen.queryByTestId('transfer-panel')).toBeNull());
    expect(screen.queryByTestId('my-shares-dialog')).toBeNull();
  });

  it('REQ-21 点「我的分享」只打开分享弹窗，不得顺带打开传输面板', async () => {
    renderPage();
    const myShares = await screen.findByTestId('my-shares-button');

    fireEvent.click(myShares);
    await waitFor(() => expect(screen.getByTestId('my-shares-dialog')).toBeTruthy());
    expect(screen.queryByTestId('transfer-panel')).toBeNull();
  });

  it('REQ-15 点「新建文件夹」只打开新建弹窗，不得受其它入口干扰', async () => {
    renderPage();
    const newFolder = await screen.findByTestId('new-folder-button');

    fireEvent.click(newFolder);
    await waitFor(() => expect(screen.getByTestId('new-folder-dialog')).toBeTruthy());
    expect(screen.queryByTestId('my-shares-dialog')).toBeNull();
    expect(screen.queryByTestId('transfer-panel')).toBeNull();
  });
});
