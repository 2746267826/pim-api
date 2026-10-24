import { useCallback, useEffect, useRef, useState } from 'react';
import { getOneDriveBindingStatus, startOneDriveBinding } from '../../api/files';
import type { OneDriveBindingStatus } from '../../types';

interface OneDriveBindDialogProps {
  onClose: () => void;
  onConnected: () => void;
  initialClientId?: string;
}

type BindPhase = 'input' | 'awaiting' | 'failed';

const POLL_INTERVAL_MS = 5000;

/**
 * OneDrive 绑定流程：输入 Azure 应用 Client ID → 设备码登录 → 轮询状态 → connected。
 */
export default function OneDriveBindDialog({ onClose, onConnected, initialClientId = '' }: OneDriveBindDialogProps) {
  const [phase, setPhase] = useState<BindPhase>('input');
  const [clientId, setClientId] = useState(initialClientId);
  const [error, setError] = useState<string | null>(null);
  const [userCode, setUserCode] = useState<string | null>(null);
  const [verificationUri, setVerificationUri] = useState<string | null>(null);
  const [status, setStatus] = useState<OneDriveBindingStatus | null>(null);
  const providerIdRef = useRef<string | null>(null);
  const connectedRef = useRef(false);

  const startBinding = useCallback(async () => {
    const trimmed = clientId.trim();
    if (!trimmed) {
      setError('请填写 Azure 应用注册的 Client ID');
      return;
    }
    setError(null);
    try {
      const start = await startOneDriveBinding(trimmed);
      providerIdRef.current = start.providerId;
      setUserCode(start.userCode);
      setVerificationUri(start.verificationUri);
      setPhase('awaiting');
    } catch (e) {
      setError(e instanceof Error ? e.message : '绑定启动失败');
    }
  }, [clientId]);

  useEffect(() => {
    if (phase !== 'awaiting') return;
    let cancelled = false;
    let intervalMs = POLL_INTERVAL_MS;
    let timer = 0;
    const poll = async () => {
      const providerId = providerIdRef.current;
      if (!providerId) return;
      try {
        const next = await getOneDriveBindingStatus(providerId);
        if (cancelled) return;
        setStatus(next);
        if (next.status === 'connected' && !connectedRef.current) {
          connectedRef.current = true;
          onConnected();
        }
        if (next.pollIntervalSeconds && next.pollIntervalSeconds * 1000 > intervalMs) {
          // RFC 8628 slow_down：按服务端提示降低轮询频率
          intervalMs = next.pollIntervalSeconds * 1000;
        }
        if (next.status === 'expired' || next.status === 'denied') {
          setPhase('failed');
          setError(next.status === 'denied' ? '授权被拒绝' : '设备码已过期，请重新绑定');
        }
      } catch {
        // 网络抖动：等待下一轮
      }
    };
    const schedule = () => {
      timer = window.setTimeout(async () => {
        await poll();
        if (!cancelled) schedule();
      }, intervalMs);
    };
    poll();
    schedule();
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [phase, onConnected]);

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/40 px-4" role="dialog" aria-label="绑定 OneDrive">
      <div className="pim-card w-full max-w-md p-6">
        <h2 className="text-base font-semibold text-[var(--pim-text)]">绑定 OneDrive</h2>

        {phase === 'input' && (
          <div className="mt-4 space-y-3">
            <label className="block text-sm text-[var(--pim-text-muted)]" htmlFor="onedrive-client-id">
              Azure 应用注册的 Client ID（与 Outlook 同步使用的应用一致即可）
            </label>
            <input
              id="onedrive-client-id"
              className="w-full rounded-lg border border-[var(--pim-border)] bg-white px-3 py-2 text-sm"
              value={clientId}
              onChange={e => setClientId(e.target.value)}
              placeholder="00000000-0000-0000-0000-000000000000"
            />
            {error && <p className="text-sm text-[var(--pim-danger)]">{error}</p>}
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" className="pim-button-secondary px-3" onClick={onClose}>取消</button>
              <button type="button" className="pim-button-primary px-4" onClick={startBinding}>获取设备码</button>
            </div>
          </div>
        )}

        {phase === 'awaiting' && (
          <div className="mt-4 space-y-3">
            <p className="text-sm text-[var(--pim-text-muted)]">
              1. 打开 <a className="text-[var(--pim-primary)] underline" href={verificationUri ?? '#'} target="_blank" rel="noreferrer">{verificationUri}</a>
            </p>
            <p className="text-sm text-[var(--pim-text-muted)]">2. 输入设备代码</p>
            <p className="select-all rounded-lg bg-[var(--pim-surface-muted)] px-3 py-2 text-center text-lg font-semibold tracking-widest">
              {userCode}
            </p>
            <p className="text-xs text-[var(--pim-text-muted)]">
              {status?.status === 'pending' ? '等待你在微软页面完成登录…（自动检测）' : '正在确认授权…'}
            </p>
            <div className="flex justify-end pt-2">
              <button type="button" className="pim-button-secondary px-3" onClick={onClose}>关闭</button>
            </div>
          </div>
        )}

        {phase === 'failed' && (
          <div className="mt-4 space-y-3">
            <p className="text-sm text-[var(--pim-danger)]">{error}</p>
            <div className="flex justify-end gap-2 pt-2">
              <button type="button" className="pim-button-secondary px-3" onClick={() => { setPhase('input'); setError(null); }}>重新绑定</button>
              <button type="button" className="pim-button-secondary px-3" onClick={onClose}>关闭</button>
            </div>
          </div>
        )}
      </div>
    </div>
  );
}
