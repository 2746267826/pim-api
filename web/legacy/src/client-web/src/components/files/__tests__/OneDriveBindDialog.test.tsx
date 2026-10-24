import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { fireEvent, render, screen } from '@testing-library/react';
import OneDriveBindDialog from '../OneDriveBindDialog';
import * as filesApi from '../../../api/files';

vi.mock('../../../api/files', () => ({
  startOneDriveBinding: vi.fn(),
  getOneDriveBindingStatus: vi.fn(),
}));

const mockedStart = vi.mocked(filesApi.startOneDriveBinding);
const mockedStatus = vi.mocked(filesApi.getOneDriveBindingStatus);

describe('OneDriveBindDialog', () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  /**
   * 用例失败放最后执行：react-query 的 `retryDelay` 默认是**指数退避的真实定时器**
   * （已在 src/main.tsx 的 defaultOptions 里设定）。用例里失败一次就会留下一个
   * 几千毫秒的真实 setTimeout；若其后紧跟 `vi.useFakeTimers()` 的用例，
   * 假定时器会接管/捕获这些真实定时器，使轮询推进失效、`findBy*` 也永远等不到。
   *
   * 该顺序依赖曾在 CI 上表现为「轮询到 connected 时回调 onConnected 0 次调用」
   * 与「denied 状态展示失败并可重新绑定 5s 超时」两条随机失败（本地 -t 单跑必过）。
   *
   * 这里显式收口：用例结束时恢复真实定时器并清掉所有挂起的定时器，
   * 不让任何残留定时器跨用例传染。
   */
  afterEach(() => {
    vi.clearAllTimers();
    vi.useRealTimers();
  });

  it('未填 Client ID 时提示错误而不发请求', async () => {
    render(<OneDriveBindDialog onClose={() => {}} onConnected={() => {}} />);
    fireEvent.click(screen.getByText('获取设备码'));
    expect(await screen.findByText('请填写 Azure 应用注册的 Client ID')).toBeTruthy();
    expect(mockedStart).not.toHaveBeenCalled();
  });

  it('提交后显示设备码与验证链接', async () => {
    mockedStart.mockResolvedValue({
      providerId: 'p-1',
      userCode: 'ABCD-1234',
      verificationUri: 'https://www.microsoft.com/link',
      expiresIn: 900,
    });
    render(<OneDriveBindDialog onClose={() => {}} onConnected={() => {}} />);
    fireEvent.change(screen.getByLabelText('Azure 应用注册的 Client ID（与 Outlook 同步使用的应用一致即可）'), {
      target: { value: 'cid-1' },
    });
    fireEvent.click(screen.getByText('获取设备码'));

    expect(await screen.findByText('ABCD-1234')).toBeTruthy();
    expect(mockedStart).toHaveBeenCalledWith('cid-1');
    expect(screen.getByText('https://www.microsoft.com/link')).toBeTruthy();
  });

  it('轮询到 connected 时回调 onConnected', async () => {
    vi.useFakeTimers();
    mockedStart.mockResolvedValue({
      providerId: 'p-1',
      userCode: 'ABCD-1234',
      verificationUri: 'https://www.microsoft.com/link',
      expiresIn: 900,
    });
    mockedStatus
      .mockResolvedValueOnce({
        status: 'pending', driveId: null, accountId: null, accountName: null,
        userCode: null, verificationUri: null, deviceCodeExpiresAt: null,
      })
      .mockResolvedValue({
        status: 'connected', driveId: 'd1', accountId: 'a1', accountName: 'User',
        userCode: null, verificationUri: null, deviceCodeExpiresAt: null,
      });
    const onConnected = vi.fn();
    render(<OneDriveBindDialog onClose={() => {}} onConnected={onConnected} />);

    fireEvent.change(screen.getByLabelText('Azure 应用注册的 Client ID（与 Outlook 同步使用的应用一致即可）'), {
      target: { value: 'cid-1' },
    });
    fireEvent.click(screen.getByText('获取设备码'));
    await vi.advanceTimersByTimeAsync(100);
    await vi.advanceTimersByTimeAsync(2100);
    await vi.advanceTimersByTimeAsync(2100);
    await vi.advanceTimersByTimeAsync(2100);

    expect(mockedStatus).toHaveBeenCalled();
    expect(onConnected).toHaveBeenCalledTimes(1);
  });

  it('denied 状态展示失败并可重新绑定', async () => {
    mockedStart.mockResolvedValue({
      providerId: 'p-1',
      userCode: 'ABCD-1234',
      verificationUri: 'https://www.microsoft.com/link',
      expiresIn: 900,
    });
    mockedStatus.mockResolvedValue({
      status: 'denied', driveId: null, accountId: null, accountName: null,
      userCode: null, verificationUri: null, deviceCodeExpiresAt: null,
    });
    render(<OneDriveBindDialog onClose={() => {}} onConnected={() => {}} />);
    fireEvent.change(screen.getByLabelText('Azure 应用注册的 Client ID（与 Outlook 同步使用的应用一致即可）'), {
      target: { value: 'cid-1' },
    });
    fireEvent.click(screen.getByText('获取设备码'));
    expect(await screen.findByText('授权被拒绝')).toBeTruthy();
    fireEvent.click(screen.getByText('重新绑定'));
    expect(screen.getByText('获取设备码')).toBeTruthy();
  });
});
