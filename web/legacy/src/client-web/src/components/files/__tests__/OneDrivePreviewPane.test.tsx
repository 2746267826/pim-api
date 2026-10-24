import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import OneDrivePreviewPane, { isOfficeDocument, isTextEditable } from '../OneDrivePreviewPane';
import * as filesApi from '../../../api/files';
import type { FileItem } from '../../../types';

vi.mock('../../../api/files', async importOriginal => {
  const actual = await importOriginal<typeof import('../../../api/files')>();
  return {
    ...actual,
    getOneDriveText: vi.fn(),
    saveOneDriveText: vi.fn(),
    getOneDriveSnapshots: vi.fn(),
    restoreOneDriveSnapshot: vi.fn(),
    getOneDrivePreviewUrl: vi.fn(),
    getDownloadUrl: vi.fn(),
  };
});

const mockedText = vi.mocked(filesApi.getOneDriveText);
const mockedSave = vi.mocked(filesApi.saveOneDriveText);
const mockedSnapshots = vi.mocked(filesApi.getOneDriveSnapshots);
const mockedRestore = vi.mocked(filesApi.restoreOneDriveSnapshot);
const mockedPreviewUrl = vi.mocked(filesApi.getOneDrivePreviewUrl);
const mockedDownloadUrl = vi.mocked(filesApi.getDownloadUrl);

function makeItem(overrides: Partial<FileItem>): FileItem {
  return {
    id: 'item-1',
    providerId: 'p-1',
    externalFileId: 'ext-1',
    parentExternalFileId: null,
    path: '/a.txt',
    name: 'a.txt',
    itemType: 'file',
    mimeType: 'text/plain',
    size: 12,
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
    indexStatus: '',
    ai: null,
    ...overrides,
  };
}

describe('类型分类', () => {
  it('isTextEditable 判定文本可编辑', () => {
    expect(isTextEditable(makeItem({ mimeType: 'text/plain' }))).toBe(true);
    expect(isTextEditable(makeItem({ mimeType: null, name: 'a.md' }))).toBe(true);
    expect(isTextEditable(makeItem({ mimeType: 'application/pdf', name: 'a.pdf' }))).toBe(false);
  });

  it('isOfficeDocument 判定 Office 文档', () => {
    expect(isOfficeDocument(makeItem({ mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document', name: 'a.docx' }))).toBe(true);
    expect(isOfficeDocument(makeItem({ mimeType: null, name: 'b.xlsx' }))).toBe(true);
    expect(isOfficeDocument(makeItem({ mimeType: 'text/plain', name: 'c.txt' }))).toBe(false);
  });
});

describe('OneDrivePreviewPane', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockedSnapshots.mockResolvedValue([]);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  /**
   * REQ-20 / AC-20.1（验收 F-3 的同源问题）：预览面板的「下载」按钮一度用
   * `apiDownloadBlob('/files/items/{id}/content')` 跟随 302 把**整个文件读进页面内存**，
   * 大文件尤其危险。这里用真实 fetch 计数守住：下载不得触发任何内容体请求，
   * 而应改为取 JSON 直链后交给浏览器。
   */
  it('AC-20.1 下载按钮不得把文件体读进页面内存', async () => {
    const fetchSpy = vi.fn().mockResolvedValue({
      ok: true,
      status: 200,
      url: 'https://my.microsoftpersonalcontent.com/dl?tempauth=abc',
      blob: async () => new Blob([]),
    });
    vi.stubGlobal('fetch', fetchSpy);
    const openSpy = vi.fn();
    vi.stubGlobal('open', openSpy);

    mockedPreviewUrl.mockResolvedValue('https://www.onedrive.com/preview?resid=x');
    mockedSnapshots.mockResolvedValue([]);
    mockedText.mockResolvedValue({ content: 'x', mimeType: 'text/plain', size: 1, truncated: false });
    mockedDownloadUrl.mockResolvedValue('https://my.microsoftpersonalcontent.com/dl?tempauth=abc');

    // 用文本文件：PDF 预览本身就会拉一次 /content 做内联渲染（另一个用途），
    // 会混淆「下载是否多拉了文件体」这一断言。文本文件不触发该路径，能把下载动作单独隔离出来。
    render(<OneDrivePreviewPane item={makeItem({ mimeType: 'text/plain', name: 'a.txt' })} />);

    const downloadButton = await screen.findByText('下载');
    fireEvent.click(downloadButton.closest('a') ?? downloadButton);
    await waitFor(() => expect(filesApi.getDownloadUrl).toHaveBeenCalled());

    // 关键：整个下载过程中没有对 302 内容端点发起请求（否则浏览器会真下载一次文件体）
    const fetchedUrls = fetchSpy.mock.calls.map(call => String(call[0]));
    expect(fetchedUrls.filter(url => url.includes('/content')), `fetched=${JSON.stringify(fetchedUrls)}`).toHaveLength(0);
    await waitFor(() => expect(openSpy).toHaveBeenCalled());
  });

  it('未选中时显示空态', () => {
    render(<OneDrivePreviewPane item={null} />);
    expect(screen.getByText('选择一个文件查看预览')).toBeTruthy();
  });

  it('文本文件可编辑、保存并展示快照恢复', async () => {
    mockedText.mockResolvedValue({ content: '旧内容', mimeType: 'text/plain', size: 9, truncated: false });
    mockedSave.mockResolvedValue('');
    mockedSnapshots
      .mockResolvedValueOnce([{ id: 's1', path: '/a.txt', name: 'a.txt', content: '更早版本', byteSize: 12, reason: 'pre-edit', createdAt: '2026-09-01T00:00:00Z' }])
      .mockResolvedValue([{ id: 's2', path: '/a.txt', name: 'a.txt', content: '旧内容', byteSize: 9, reason: 'pre-edit', createdAt: '2026-09-02T00:00:00Z' }]);
    mockedRestore.mockResolvedValue('');
    mockedText.mockResolvedValueOnce({ content: '旧内容', mimeType: 'text/plain', size: 9, truncated: false });

    render(<OneDrivePreviewPane item={makeItem()} onToast={() => {}} />);
    expect(await screen.findByText('a.txt')).toBeTruthy();

    fireEvent.click(screen.getByText('编辑文本'));
    const editor = await screen.findByTestId('text-editor');
    expect(editor).toBeTruthy();

    const textarea = screen.getByLabelText('文本内容') as HTMLTextAreaElement;
    await waitFor(() => expect(textarea.value).toBe('旧内容'));
    fireEvent.change(textarea, { target: { value: '新内容' } });

    mockedText.mockResolvedValue({ content: '新内容', mimeType: 'text/plain', size: 9, truncated: false });
    fireEvent.click(screen.getByText('保存到 OneDrive'));
    await waitFor(() => expect(mockedSave).toHaveBeenCalledWith('item-1', '新内容'));

    // 快照列表展示并恢复
    expect(await screen.findByText(/旧内容/)).toBeTruthy();
    fireEvent.click(screen.getByText('恢复'));
    await waitFor(() => expect(mockedRestore).toHaveBeenCalledWith('item-1', 's2'));
  });

  it('Office 文档拉取预览地址并渲染 iframe', async () => {
    mockedPreviewUrl.mockResolvedValue('https://preview.example.com/embed');
    render(
      <OneDrivePreviewPane
        item={makeItem({ name: '报告.docx', mimeType: 'application/vnd.openxmlformats-officedocument.wordprocessingml.document' })}
      />,
    );
    await waitFor(() => expect(mockedPreviewUrl).toHaveBeenCalledWith('item-1'));
    expect(await screen.findByTestId('office-preview')).toBeTruthy();
  });
});
