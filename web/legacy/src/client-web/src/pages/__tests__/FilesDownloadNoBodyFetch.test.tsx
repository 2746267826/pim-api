import { describe, expect, it, vi, beforeEach } from 'vitest';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import FilesPage from '../FilesPage';
import type { FileItem } from '../../types';

/**
 * REQ-20 / AC-20.1：下载**不得**把文件体拉进页面。
 *
 * 这一组守的是一次验收打回（F-3）：`startDownload` 一度用
 * `fetch('/api/v1/files/items/{id}/download', { redirect: 'follow' })` 只为读 `response.url`，
 * 但浏览器会顺着 302 真发一次文件请求并下载响应体——等于为了拿一个链接，
 * 把整个文件（验收时是 ≥500MB）多传了一遍，与 AC-20.1「不把整文件读入页面内存」相悖。
 *
 * 现在取直链走 JSON 端点（`/download-url`），页面只拿一个 URL 字符串。
 * 用例用**真实 fetch 计数**证明 302 内容端点不再被调用，而不是只看代码里写了什么。
 */

vi.mock('../../auth/AuthContext', () => ({
  useAuth: () => ({ isAuthenticated: true, username: 'tester', role: 'admin', logout: vi.fn() }),
}));

const bigFile: FileItem = {
  id: '11111111-1111-1111-1111-111111111111',
  providerId: 'p-1',
  externalFileId: 'ext-big',
  parentExternalFileId: null,
  path: '/huge.bin',
  name: 'huge.bin',
  itemType: 'file',
  mimeType: 'application/octet-stream',
  size: 500 * 1024 * 1024,
  etag: null,
  contentHash: null,
  currentVersionId: null,
  permissions: null,
  isDeleted: false,
  deletedAt: null,
  lastSeenAt: null,
  createdAt: '2026-09-01T00:00:00Z',
  modifiedAt: '2026-09-02T00:00:00Z',
  syncedAt: '2026-09-02T00:00:00Z',
  indexStatus: 'not_indexed',
  ai: null,
};

vi.mock('../../api/files', async () => {
  const actual = await vi.importActual<typeof import('../../api/files')>('../../api/files');
  const item = {
    id: '11111111-1111-1111-1111-111111111111',
    providerId: 'p-1',
    externalFileId: 'ext-big',
    parentExternalFileId: null,
    path: '/huge.bin',
    name: 'huge.bin',
    itemType: 'file',
    mimeType: 'application/octet-stream',
    size: 500 * 1024 * 1024,
    etag: null,
    contentHash: null,
    currentVersionId: null,
    permissions: null,
    isDeleted: false,
    deletedAt: null,
    lastSeenAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    modifiedAt: '2026-09-02T00:00:00Z',
    syncedAt: '2026-09-02T00:00:00Z',
    indexStatus: 'not_indexed',
    ai: null,
  };
  return {
    ...actual,
    getFileProviders: vi.fn().mockResolvedValue([
      {
        id: 'p-1',
        provider: 'onedrive',
        displayName: 'OneDrive',
        status: 'connected',
        lastSyncAt: null,
        lastSyncStatus: null,
        lastError: null,
        createdAt: '2026-09-01T00:00:00Z',
      },
    ]),
    getFileItems: vi.fn().mockResolvedValue({
      result: { items: [item], totalCount: 1, page: 1, pageSize: 100, totalPages: 1 },
    }),
    getOneDriveSyncStatus: vi.fn().mockResolvedValue({ status: 'idle', lastSyncAt: null, lastError: null }),
    getDownloadUrl: vi.fn().mockResolvedValue('https://my.microsoftpersonalcontent.com/dl?tempauth=abc'),
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

describe('FilesPage 下载（REQ-20 / AC-20.1）', () => {
  let fetchSpy: ReturnType<typeof vi.fn>;

  beforeEach(() => {
    vi.clearAllMocks();
    window.localStorage.clear();
    // 模拟「浏览器跟随 302 到微软 CDN」的真实结果：ok=true 且 response.url 已是直链。
    // 必须给足真实形状，否则旧实现会先在 response.ok 上抛错，
    // 用例就变成「因为 stub 太假而失败」，证明不了它真能识别「多传了一次文件体」。
    fetchSpy = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      url: 'https://my.microsoftpersonalcontent.com/dl?tempauth=abc',
      arrayBuffer: async () => new ArrayBuffer(0),
    });
    vi.stubGlobal('fetch', fetchSpy);
    vi.stubGlobal('open', vi.fn());
  });

  it('AC-20.1 取直链只调 JSON 端点，绝不 fetch 302 内容端点（不把文件体拉进页面）', async () => {
    renderPage();

    const row = await screen.findByTestId('file-row');
    fireEvent.click(within(row).getByTestId('row-menu-trigger'));
    const download = await screen.findByText('下载');
    fireEvent.click(download);

    // >100MB 要走确认
    const confirmOk = await screen.findByTestId('download-confirm-ok');
    fireEvent.click(confirmOk);

    await waitFor(() => expect(window.open).toHaveBeenCalled());

    // 关键断言：整个下载流程中，浏览器没有对 302 内容端点发起过任何 fetch
    const fetchedUrls = fetchSpy.mock.calls.map(call => String(call[0]));
    expect(fetchedUrls.some(url => url.includes('/download'))).toBe(false);
    expect(fetchedUrls.some(url => url.includes('/content'))).toBe(false);

    // 打开的是微软直链
    expect(String((window.open as unknown as ReturnType<typeof vi.fn>).mock.calls[0][0])).toContain(
      'my.microsoftpersonalcontent.com',
    );
  });
});
