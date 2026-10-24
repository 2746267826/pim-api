import { describe, expect, it, vi, beforeEach, afterEach } from 'vitest';
import { render, screen, waitFor } from '@testing-library/react';
import FileThumbnail from '../FileThumbnail';
import type { FileItem } from '../../../types';

/**
 * REQ-23 / AC-23.1 / AC-23.3：真缩略图、懒加载、失败占位。
 *
 * 懒加载是硬要求：打开含 2.5 万张图的目录时，若一次性请求全部缩略图会打爆连接池，
 * 因此这里断言「未进入视口时不发请求」。
 */
function image(id: string, name: string): FileItem {
  return {
    id,
    providerId: 'p-1',
    externalFileId: `ext-${id}`,
    parentExternalFileId: null,
    path: `/图片/${name}`,
    name,
    itemType: 'file',
    mimeType: 'image/jpeg',
    size: 2048,
    etag: null,
    contentHash: null,
    currentVersionId: null,
    permissions: null,
    isDeleted: false,
    deletedAt: null,
    lastSeenAt: null,
    createdAt: '2026-09-01T00:00:00Z',
    modifiedAt: '2026-09-01T00:00:00Z',
    syncedAt: '2026-09-01T00:00:00Z',
    indexStatus: 'not_indexed',
    ai: null,
  };
}

/** 可手动触发的 IntersectionObserver 替身（真实语义：回调里给 isIntersecting）。 */
class ControllableObserver {
  static instances: ControllableObserver[] = [];
  private readonly callback: IntersectionObserverCallback;
  public disconnected = false;

  constructor(callback: IntersectionObserverCallback) {
    this.callback = callback;
    ControllableObserver.instances.push(this);
  }

  observe(): void {
    /* 由用例显式触发 */
  }

  disconnect(): void {
    this.disconnected = true;
  }

  unobserve(): void {}

  trigger(isIntersecting: boolean): void {
    this.callback(
      [{ isIntersecting } as IntersectionObserverEntry],
      this as unknown as IntersectionObserver,
    );
  }
}

describe('FileThumbnail / REQ-23', () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    ControllableObserver.instances = [];
    vi.stubGlobal('IntersectionObserver', ControllableObserver);
    vi.stubGlobal('fetch', fetchMock);
    // jsdom 的 URL 没有 createObjectURL/revokeObjectURL，直接补上（不要替换整个 URL，
    // 否则会丢掉构造函数导致 URL 无法 new）
    (URL as unknown as { createObjectURL: () => string }).createObjectURL = vi.fn(() => 'blob:thumb-1');
    (URL as unknown as { revokeObjectURL: (url: string) => void }).revokeObjectURL = vi.fn();
    fetchMock.mockReset();
    window.localStorage.setItem('accessToken', 'test-token');
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    window.localStorage.clear();
  });

  it('AC-23.3 反面：未进入视口时不请求缩略图（懒加载，避免一次性打爆连接池）', () => {
    render(<FileThumbnail item={image('1', 'a.jpg')} />);

    expect(fetchMock).not.toHaveBeenCalled();
    expect(screen.getByTestId('file-thumbnail').getAttribute('data-thumb-state')).toBe('idle');
  });

  it('AC-23.1 滚动到可见后才请求，且带上鉴权头', async () => {
    fetchMock.mockResolvedValue({ ok: true, blob: async () => new Blob(['x']) });
    render(<FileThumbnail item={image('1', 'a.jpg')} />);

    ControllableObserver.instances[0].trigger(true);

    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(1));
    const [url, init] = fetchMock.mock.calls[0];
    expect(String(url)).toContain('/files/items/1/thumbnail');
    expect(String(url)).toContain('size=medium');
    expect((init as RequestInit).headers).toMatchObject({ Authorization: 'Bearer test-token' });
  });

  it('AC-23.3 失败时显示占位图标（不破图）', async () => {
    fetchMock.mockResolvedValue({ ok: false, status: 404, blob: async () => new Blob([]) });
    render(<FileThumbnail item={image('1', 'a.jpg')} />);

    ControllableObserver.instances[0].trigger(true);

    await waitFor(() => expect(screen.getByTestId('file-thumbnail-placeholder')).toBeTruthy());
    expect(screen.getByTestId('file-thumbnail').getAttribute('data-thumb-state')).toBe('error');
  });

  it('P8：可按 large 档请求', async () => {
    fetchMock.mockResolvedValue({ ok: true, blob: async () => new Blob(['x']) });
    render(<FileThumbnail item={image('2', 'b.jpg')} size="large" />);

    ControllableObserver.instances[0].trigger(true);

    await waitFor(() => expect(fetchMock).toHaveBeenCalled());
    expect(String(fetchMock.mock.calls[0][0])).toContain('size=large');
  });

  it('成功加载后渲染真实图片并释放 blob（避免内存堆积）', async () => {
    fetchMock.mockResolvedValue({ ok: true, blob: async () => new Blob(['x']) });
    const { unmount } = render(<FileThumbnail item={image('3', 'c.jpg')} />);

    ControllableObserver.instances[0].trigger(true);

    await waitFor(() => expect(screen.getByAltText('c.jpg')).toBeTruthy());
    expect(screen.getByAltText('c.jpg').getAttribute('src')).toBe('blob:thumb-1');

    unmount();
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:thumb-1');
  });

  it('未进入视口（isIntersecting=false）不触发请求', () => {
    render(<FileThumbnail item={image('4', 'd.jpg')} />);

    ControllableObserver.instances[0].trigger(false);

    expect(fetchMock).not.toHaveBeenCalled();
  });
});
