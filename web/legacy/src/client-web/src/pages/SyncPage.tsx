import { useEffect, useMemo, useRef, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  cancelOutlookDeviceCode,
  cancelOutlookSync,
  checkOutlookConnection,
  createOutlookDeviceCode,
  getOutlookSettings,
  getOutlookSyncBatchesPaged,
  outlookBindings,
  outlookDiscover,
  outlookDisconnect,
  outlookLocalDataDelete,
  outlookLocalDataPreview,
  outlookSelection,
  pollOutlookDeviceCode,
  runOutlookSync,
  updateOutlookSettings,
} from '../api/calendar';
import type {
  OutlookAuthorizationSessionResponse,
  OutlookCalendarBindingResponse,
  OutlookPerCalendarResult,
  OutlookSyncBatchResponse,
  UpdateOutlookSettingsRequest,
} from '../types';
import PageHeader from '../ui/PageHeader';
import { Copy, ExternalLink, RefreshCw, Wifi, X } from 'lucide-react';
import { outlookSyncInvalidationKeys } from '../utils/outlookSyncInvalidation';
import { getDeferredAutoRefreshInterval } from '../lib/autoRefresh';

function mapUiStatus(uiStatus?: string | null): string {
  switch (uiStatus) {
    case 'not-configured': return '未配置';
    case 'failed': return '连接失败';
    case 'waiting-auth': return '等待授权';
    case 'connected': return '已连接';
    case 'reauth-required': return '需重新授权';
    default: return uiStatus || '未连接';
  }
}

function formatDateTime(value?: string | null) {
  if (!value) return '';
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return value;
  return date.toLocaleString('zh-CN');
}

function mutationError(error: unknown) {
  return error instanceof Error ? error.message : '请求失败';
}

async function copyToClipboard(text: string) {
  try {
    await navigator.clipboard.writeText(text);
  } catch {
    // Fallback not needed
  }
}

function parsePerCalendarJson(json?: string | null): OutlookPerCalendarResult[] {
  if (!json) return [];
  try {
    return JSON.parse(json) as OutlookPerCalendarResult[];
  } catch {
    return [];
  }
}

function calcCountdownSeconds(expiresAt: string): number {
  const expiresMs = new Date(expiresAt).getTime() - Date.now();
  return expiresMs > 0 ? Math.floor(expiresMs / 1000) : 0;
}

type SyncMode = 'normal' | 'full-resources' | 'range-instances';

export default function SyncPage() {
  const queryClient = useQueryClient();
  const [clientIdDraft, setClientIdDraft] = useState<string | null>(null);
  const [showSetupGuide, setShowSetupGuide] = useState(true);
  const [session, setSession] = useState<OutlookAuthorizationSessionResponse | null>(null);
  const [sessionError, setSessionError] = useState<string | null>(null);
  const [pollCountdown, setPollCountdown] = useState<number | null>(null);
  const [bindings, setBindings] = useState<OutlookCalendarBindingResponse[]>([]);
  const [selectedBindingIds, setSelectedBindingIds] = useState<string[]>([]);
  const [showLocalPreview, setShowLocalPreview] = useState(false);
  const [localPreviewData, setLocalPreviewData] = useState<{ bindingCount: number; calendarCount: number; eventCount: number } | null>(null);
  const [localDeleteConfirm, setLocalDeleteConfirm] = useState(false);
  const [syncMode, setSyncMode] = useState<SyncMode>('normal');
  const [rangeStart, setRangeStart] = useState('');
  const [rangeEnd, setRangeEnd] = useState('');
  const [batchesPage, setBatchesPage] = useState(1);
  const [countdownActive, setCountdownActive] = useState(false);
  const pollTimeoutRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const countdownIntervalRef = useRef<ReturnType<typeof setInterval> | null>(null);

  const { data: settings, isLoading: settingsLoading } = useQuery({
    queryKey: ['outlook-settings'],
    queryFn: getOutlookSettings,
  });

  const { data: batchesData, isLoading: batchesLoading } = useQuery({
    queryKey: ['outlook-sync-batches', batchesPage],
    queryFn: () => getOutlookSyncBatchesPaged({ page: batchesPage, pageSize: 20 }),
    refetchInterval: getDeferredAutoRefreshInterval,
  });

  const settingsMutation = useMutation({
    mutationFn: (data: UpdateOutlookSettingsRequest) => updateOutlookSettings(data),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['outlook-settings'] });
      queryClient.invalidateQueries({ queryKey: ['workbench-outlook-settings'] });
    },
  });

  const deviceCodeMutation = useMutation({
    mutationFn: createOutlookDeviceCode,
    onSuccess: (data) => {
      setSession(data);
      setSessionError(null);
      scheduleNextPoll(data);
      if (data.expiresAt) {
        setPollCountdown(calcCountdownSeconds(data.expiresAt));
        setCountdownActive(true);
      }
    },
    onError: (error) => {
      setSessionError(mutationError(error));
    },
  });

  const pollMutation = useMutation({
    mutationFn: (sessionId: string) => pollOutlookDeviceCode(sessionId),
    onSuccess: (data) => {
      setSession(data);
      setSessionError(null);
      if (data.status === 'connected') {
        stopPolling();
        setBatchesPage(1);
        queryClient.invalidateQueries({ queryKey: ['outlook-settings'] });
        queryClient.invalidateQueries({ queryKey: ['workbench-outlook-settings'] });
      } else if (data.status === 'failed' || data.status === 'canceled') {
        stopPolling();
      } else if (data.status === 'starting' || data.status === 'waiting-for-user') {
        scheduleNextPoll(data);
      }
    },
    onError: (error, variables) => {
      setSessionError(mutationError(error));
      schedulePoll(variables);
    },
  });

  const cancelDeviceCodeMutation = useMutation({
    mutationFn: (sessionId: string) => cancelOutlookDeviceCode(sessionId),
    onSuccess: () => {
      stopPolling();
      setSession(null);
      setSessionError(null);
    },
  });

  const discoverMutation = useMutation({
    mutationFn: outlookDiscover,
    onSuccess: (data) => {
      setBindings(data);
      setSelectedBindingIds(data.filter(b => b.isSelected).map(b => b.id));
    },
  });

  /**
   * Re-reads the stored bindings (read-only; does not call Graph like 发现日历 does) so a
   * change made by an automatic sync shows up in the list below. Since #309 the common case
   * is a calendar disappearing entirely: once its absence is confirmed the calendar is
   * mirror-deleted and its binding row is gone, so the list must be re-read to drop the entry.
   */
  async function refreshBindings() {
    try {
      const data = await outlookBindings();
      setBindings(data);
    } catch {
      // The list is a convenience view of server state; a failed refresh must not break sync.
    }
  }

  const selectionMutation = useMutation({
    mutationFn: (ids: string[]) => outlookSelection(ids),
    onSuccess: (data) => {
      setBindings(data);
      setSelectedBindingIds(data.filter(b => b.isSelected).map(b => b.id));
      queryClient.invalidateQueries({ queryKey: ['calendar-layers'] });
      queryClient.invalidateQueries({ queryKey: ['workbench-calendar-layers'] });
    },
  });

  const syncMutation = useMutation({
    mutationFn: (request: { mode: SyncMode; bindingIds?: string[]; rangeStart?: string; rangeEnd?: string }) => {
      return runOutlookSync({
        mode: request.mode,
        calendarBindingIds: request.bindingIds,
        rangeStart: request.rangeStart,
        rangeEnd: request.rangeEnd,
      });
    },
    onSuccess: () => {
      setBatchesPage(1);
      for (const queryKey of outlookSyncInvalidationKeys) {
        queryClient.invalidateQueries({ queryKey });
      }
      // A sync can remove a calendar (mirror-delete) or change its remote state; refresh
      // the binding list so the「日历选择」stays accurate without a manual 发现日历.
      void refreshBindings();
    },
  });

  const cancelSyncMutation = useMutation({
    mutationFn: (batchId: string) => cancelOutlookSync(batchId),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['outlook-sync-batches'] });
    },
  });

  const disconnectMutation = useMutation({
    mutationFn: outlookDisconnect,
    onSuccess: () => {
      setSession(null);
      setBindings([]);
      setShowLocalPreview(false);
      setLocalPreviewData(null);
      setLocalDeleteConfirm(false);
      setBatchesPage(1);
      queryClient.invalidateQueries({ queryKey: ['outlook-settings'] });
      for (const queryKey of outlookSyncInvalidationKeys) {
        queryClient.invalidateQueries({ queryKey });
      }
    },
  });

  const localDataDeleteMutation = useMutation({
    mutationFn: outlookLocalDataDelete,
    onSuccess: () => {
      setLocalDeleteConfirm(false);
      setLocalPreviewData(null);
      setShowLocalPreview(false);
      setBatchesPage(1);
      for (const queryKey of outlookSyncInvalidationKeys) {
        queryClient.invalidateQueries({ queryKey });
      }
    },
  });

  const localDataPreviewMutation = useMutation({
    mutationFn: outlookLocalDataPreview,
    onSuccess: (data) => {
      setLocalPreviewData(data);
      setShowLocalPreview(true);
    },
    onError: () => {
      setLocalPreviewData(null);
    },
  });

  const checkConnectionMutation = useMutation({
    mutationFn: checkOutlookConnection,
    onSuccess: (data) => {
      queryClient.setQueryData(['outlook-settings'], data);
      queryClient.setQueryData(['workbench-outlook-settings'], data);
    },
  });

  const retryPerCalendarMutation = useMutation({
    mutationFn: ({
      batch,
      bindingId,
    }: {
      batch: OutlookSyncBatchResponse;
      bindingId: string;
    }) => {
      const safeMode: SyncMode =
        (batch.mode === 'normal' || batch.mode === 'full-resources' || batch.mode === 'range-instances')
          ? batch.mode
          : 'normal';
      return runOutlookSync({
        mode: safeMode,
        retryOfBatchId: batch.id,
        calendarBindingIds: [bindingId],
        rangeStart: batch.requestedWindowStart ?? undefined,
        rangeEnd: batch.requestedWindowEnd ?? undefined,
      });
    },
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['outlook-sync-batches'] });
      // A successful retry clears the remote state; reflect that immediately.
      void refreshBindings();
    },
  });

  const clientId = clientIdDraft ?? settings?.clientId ?? '';

  function saveSettings(event: React.FormEvent<HTMLFormElement>) {
    event.preventDefault();
    settingsMutation.mutate({ clientId: clientId.trim() });
  }

  function startDeviceCode() {
    setSession(null);
    setSessionError(null);
    setPollCountdown(null);
    stopPolling();
    deviceCodeMutation.mutate();
  }

  function stopPolling() {
    if (pollTimeoutRef.current) {
      clearTimeout(pollTimeoutRef.current);
      pollTimeoutRef.current = null;
    }
    if (countdownIntervalRef.current) {
      clearInterval(countdownIntervalRef.current);
      countdownIntervalRef.current = null;
    }
    setPollCountdown(null);
    setCountdownActive(false);
  }

  function schedulePoll(sessionId: string) {
    if (pollTimeoutRef.current) {
      clearTimeout(pollTimeoutRef.current);
    }
    pollTimeoutRef.current = setTimeout(() => {
      pollMutation.mutate(sessionId);
    }, 3000);
  }

  function scheduleNextPoll(s: OutlookAuthorizationSessionResponse) {
    if (!s.id) return;
    if (s.status === 'connected' || s.status === 'failed' || s.status === 'canceled') return;
    schedulePoll(s.id);
  }

  useEffect(() => {
    return () => stopPolling();
  }, []);

  function handleCancelDeviceCode() {
    if (session?.id) {
      cancelDeviceCodeMutation.mutate(session.id);
    }
  }

  useEffect(() => {
    if (!countdownActive) return;
    const interval = setInterval(() => {
      setPollCountdown(prev => {
        if (prev === null || prev <= 1) {
          clearInterval(interval);
          if (pollTimeoutRef.current) {
            clearTimeout(pollTimeoutRef.current);
            pollTimeoutRef.current = null;
          }
          setCountdownActive(false);
          return null;
        }
        return prev - 1;
      });
    }, 1000);
    countdownIntervalRef.current = interval;
    return () => {
      clearInterval(interval);
      countdownIntervalRef.current = null;
    };
  }, [countdownActive]);

  function requestDeviceCode() {
    startDeviceCode();
  }

  function handleCopyCode(code: string | null | undefined) {
    if (code) copyToClipboard(code);
  }

  function toggleBinding(id: string) {
    setSelectedBindingIds(prev =>
      prev.includes(id) ? prev.filter(b => b !== id) : [...prev, id],
    );
  }

  function toggleGroup(groupBindings: OutlookCalendarBindingResponse[], checked: boolean) {
    const groupIds = groupBindings.map(b => b.id);
    setSelectedBindingIds(prev => {
      const without = prev.filter(b => !groupIds.includes(b));
      return checked ? [...without, ...groupIds] : without;
    });
  }

  const groupedBindings = useMemo(() => {
    const map = new Map<string, OutlookCalendarBindingResponse[]>();
    for (const b of bindings) {
      const key = b.groupName || '';
      if (!map.has(key)) map.set(key, []);
      map.get(key)!.push(b);
    }
    return Array.from(map.entries()).sort(([a], [b]) => a.localeCompare(b));
  }, [bindings]);

  function saveSelection() {
    selectionMutation.mutate(selectedBindingIds);
  }

  function runSync(mode: SyncMode) {
    syncMutation.mutate({
      mode,
      bindingIds: mode === 'full-resources' ? selectedBindingIds : undefined,
      rangeStart: mode === 'range-instances' ? rangeStart : undefined,
      rangeEnd: mode === 'range-instances' ? rangeEnd : undefined,
    });
  }

  function handleForceFetchAll() {
    syncMutation.mutate({
      mode: 'full-resources',
      bindingIds: selectedBindingIds,
    });
  }

  function handleCancelBatch(batchId: string) {
    cancelSyncMutation.mutate(batchId);
  }

  const batches = batchesData?.items ?? [];
  const batchTotalPages = batchesData ? Math.ceil(batchesData.total / batchesData.pageSize) : 1;

  const displayStatus = mapUiStatus(settings?.uiStatus);

  function renderRemoteState(remoteState: string) {
    if (remoteState === 'paused') return '暂停';
    if (remoteState === 'remote-missing') return '缺失';
    return null;
  }

  return (
    <div className="mx-auto w-full max-w-[1300px] space-y-6 pb-20">
      <PageHeader
        title="微软同步"
        subtitle="借助 Microsoft Graph API 同步 Outlook 日历。"
      />

      {/* Client ID Settings */}
      <section className="pim-panel min-w-0 rounded-lg border p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-sm font-semibold text-slate-950">Microsoft 设置</h2>
          <span className="rounded-full bg-slate-100 px-2.5 py-1 text-xs font-semibold text-slate-600">
            来源：Outlook
          </span>
        </div>
        <p className="mt-1 text-xs text-slate-500">
          状态：{settingsLoading ? '加载中' : displayStatus}
          {settings?.lastSyncedAt ? ` / 上次同步：${formatDateTime(settings.lastSyncedAt)}` : ''}
        </p>

        <form onSubmit={saveSettings} className="mt-3 flex flex-wrap items-end gap-3">
          <label className="min-w-0 flex-1 text-sm">
            <span className="text-xs font-semibold text-slate-500">Client ID</span>
              <input
              value={clientIdDraft ?? settings?.clientId ?? ''}
              onChange={event => setClientIdDraft(event.target.value)}
              pattern="^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$"
              required
              title="请输入有效的 UUID (例如 11111111-1111-1111-1111-111111111111)"
              className="mt-1 min-h-[44px] w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 outline-none focus:border-blue-400"
              placeholder="Azure 应用 Client ID"
            />
          </label>
          <button
            type="submit"
            disabled={settingsMutation.isPending}
            className="pim-button-primary min-h-[44px] shrink-0 px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            {settingsMutation.isPending ? '保存中' : '保存'}
          </button>
        </form>
        <div className="mt-2 flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => {
              checkConnectionMutation.mutate();
            }}
            disabled={checkConnectionMutation.isPending}
            className="pim-button-secondary px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-60"
            title="检查连接"
            aria-label="检查连接"
          >
            <Wifi className="mr-1 inline-block h-3.5 w-3.5" />
            检查连接
          </button>
          {checkConnectionMutation.isSuccess && (
            <span className="self-end text-xs text-slate-600">
              {mapUiStatus(checkConnectionMutation.data.uiStatus)}
            </span>
          )}
          {checkConnectionMutation.isError && (
            <span className="text-xs text-red-600 self-end">检查失败</span>
          )}
        </div>
        {settingsMutation.isError && (
          <p className="mt-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {mutationError(settingsMutation.error)}
          </p>
        )}

        <button
          type="button"
          onClick={() => setShowSetupGuide(v => !v)}
          className="mt-2 text-xs font-semibold text-blue-600 hover:text-blue-800"
          title="显示设置指南"
          aria-label="显示设置指南"
        >
          {showSetupGuide ? '隐藏设置指南' : '查看设置指南'}
        </button>

        {showSetupGuide && (
          <div className="mt-3 rounded-lg border border-slate-200 bg-slate-50 px-4 py-3 text-xs leading-6 text-slate-700">
            <p className="font-semibold text-slate-900">Entra 应用注册步骤：</p>
            <ol className="ml-4 list-decimal">
              <li>前往 Microsoft Entra 管理中心 &gt; 应用注册 &gt; 新注册</li>
              <li>在"重定向 URI"中选择<strong>公共客户端/本机 (移动和桌面)</strong> 类型，输入以下重定向 URI：<code className="rounded bg-slate-200 px-1">https://login.microsoftonline.com/common/oauth2/nativeclient</code></li>
              <li>注册完成后，复制<strong>应用程序(客户端) ID</strong> 粘贴到上方 Client ID 输入框</li>
              <li>进入"API 权限" &gt; 添加权限 &gt; Microsoft Graph &gt; 委托权限</li>
              <li>勾选 <code className="rounded bg-slate-200 px-1">Calendars.ReadWrite</code></li>
              <li>确认"身份验证"设置中已启用<strong>公共客户端流</strong></li>
              <li>保存 Client ID 后，点击下方"获取设备代码"开始 OAuth 流程</li>
            </ol>
          </div>
        )}
      </section>

      {/* Device Code + Authorization */}
      <section className="pim-panel min-w-0 p-4">
        <div className="flex items-center justify-between gap-2">
          <h2 className="text-sm font-semibold text-slate-950">设备代码授权</h2>
          <div className="flex gap-2">
            {session && session.status !== 'connected' && (
              <button
                type="button"
                onClick={handleCancelDeviceCode}
                disabled={cancelDeviceCodeMutation.isPending}
                className="pim-button-secondary px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-60"
                title="取消设备代码授权"
                aria-label="取消"
              >
                <X className="h-4 w-4" />
              </button>
            )}
            <button
              type="button"
              onClick={requestDeviceCode}
              disabled={deviceCodeMutation.isPending}
              className="pim-button-secondary px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-60"
              title="获取设备代码"
              aria-label="获取设备代码"
            >
              获取代码
            </button>
          </div>
        </div>

        {cancelDeviceCodeMutation.isError && (
          <p className="mt-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            取消失败：{mutationError(cancelDeviceCodeMutation.error)}
          </p>
        )}

        {deviceCodeMutation.isPending && (
          <p className="mt-3 text-xs text-slate-500">正在请求设备代码...</p>
        )}

        {session ? (
          <div className="mt-3 space-y-3">
            {session.status === 'connected' ? (
              <p className="rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
                已授权{session.accountDisplayName ? `：${session.accountDisplayName}` : ''}
              </p>
            ) : (
              <>
                <div className="flex flex-wrap gap-2">
                  {session.verificationUri && (
                    <a
                      href={session.verificationUri}
                      target="_blank"
                      rel="noreferrer"
                      className="inline-flex items-center gap-1.5 rounded-lg border border-blue-200 bg-blue-50 px-3 py-2 text-sm font-semibold text-blue-700 hover:bg-blue-100"
                      title="打开 Microsoft 登录页面"
                      aria-label="打开 Microsoft"
                    >
                      <ExternalLink className="h-4 w-4" />
                      打开 Microsoft
                    </a>
                  )}
                </div>

                <div data-testid="device-code-status" className="min-w-[200px] rounded-lg border border-slate-200 bg-slate-50 px-3 py-2">
                  <p className="text-xs font-semibold text-slate-500">用户代码</p>
                  <p className="mt-1 font-mono text-xl font-semibold tracking-[0.18em] text-slate-950">
                    {session.userCode}
                  </p>
                  <div className="mt-1 flex gap-2">
                    <button
                      type="button"
                      onClick={() => handleCopyCode(session.userCode)}
                      className="text-xs font-semibold text-blue-600 hover:text-blue-800"
                      title="复制代码"
                      aria-label="复制代码"
                    >
                      <Copy className="mr-1 inline-block h-3 w-3" />
                      复制代码
                    </button>
                  </div>
                </div>

                <div className="flex flex-wrap items-center gap-2">
                  {pollCountdown !== null && (
                    <span className="inline-block w-20 text-xs font-semibold text-slate-500">
                      {Math.floor(pollCountdown / 60)}:{(pollCountdown % 60).toString().padStart(2, '0')}
                    </span>
                  )}
                  <button
                    type="button"
                    onClick={requestDeviceCode}
                    disabled={deviceCodeMutation.isPending}
                    className="pim-button-secondary px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-60"
                    title="刷新"
                    aria-label="刷新"
                  >
                    <RefreshCw className="mr-1 inline-block h-3.5 w-3.5" />
                    刷新
                  </button>
                </div>

                {session.expiresAt && (
                  <p className="text-xs text-slate-400">过期时间：{formatDateTime(session.expiresAt)}</p>
                )}

                {session.accountLoginHint && (
                  <p className="text-xs text-slate-500">登录提示：{session.accountLoginHint}</p>
                )}
              </>
            )}

            {session.errorMessage && (
              <p className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
                {session.errorMessage}
              </p>
            )}
          </div>
        ) : (
          !deviceCodeMutation.isPending && (
            <p className="mt-3 rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
              授权后即可连接微软账号。
            </p>
          )
        )}
        {sessionError && (
          <p className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {sessionError}
          </p>
        )}
      </section>

      {/* Calendar Discovery & Selection */}
      <section className="pim-panel min-w-0 p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-sm font-semibold text-slate-950">日历选择</h2>
          <div className="flex gap-2">
            <button
              type="button"
              onClick={() => discoverMutation.mutate()}
              disabled={discoverMutation.isPending}
              className="pim-button-secondary px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-60"
            >
              发现日历
            </button>
            {bindings.length > 0 && (
              <button
                type="button"
                onClick={saveSelection}
                disabled={selectionMutation.isPending}
                className="pim-button-primary px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-60"
              >
                保存选择
              </button>
            )}
          </div>
        </div>

        {discoverMutation.isPending && (
          <p className="mt-3 text-xs text-slate-500">正在发现日历...</p>
        )}

        {groupedBindings.length > 0 && (
          <div className="mt-3 space-y-3">
            {groupedBindings.map(([groupName, groupBindings]) => {
              const selectedCount = groupBindings.filter(b => selectedBindingIds.includes(b.id)).length;
              const allSelected = groupBindings.length > 0 && selectedCount === groupBindings.length;

              return (
                <div key={groupName || '__ungrouped'} className="rounded-lg border border-slate-200 bg-white p-3">
                  <label className="flex items-center gap-2 text-sm font-semibold text-slate-700">
                    <input
                      type="checkbox"
                      checked={allSelected}
                      disabled={groupBindings.length === 0}
                      onChange={event => toggleGroup(groupBindings, event.target.checked)}
                      className="rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                    />
                    {groupName || '未分组'}
                  </label>
                  <div className="mt-2 space-y-1.5">
                    {groupBindings.map(binding => {
                      const stateTags: string[] = [];
                      if (!binding.canEdit) stateTags.push('只读');
                      const remoteLabel = renderRemoteState(binding.remoteState);
                      if (remoteLabel) stateTags.push(remoteLabel);

                      return (
                        <label
                          key={binding.id}
                          className="ml-6 flex items-center gap-2 text-sm text-slate-600"
                        >
                          <input
                            type="checkbox"
                            checked={selectedBindingIds.includes(binding.id)}
                            onChange={() => toggleBinding(binding.id)}
                            className="rounded border-slate-300 text-blue-600 focus:ring-blue-500"
                          />
                          <span className="truncate">{binding.name}</span>
                          {stateTags.length > 0 && (
                            <span className="shrink-0 text-[11px] font-semibold text-amber-600">
                              ({stateTags.join(', ')})
                            </span>
                          )}
                        </label>
                      );
                    })}
                  </div>
                </div>
              );
            })}
          </div>
        )}
      </section>

      {/* Sync Controls */}
      <section className="pim-panel min-w-0 p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-sm font-semibold text-slate-950">同步操作</h2>
          <span className="text-xs text-slate-500">
            已选 {selectedBindingIds.length} 个日历
          </span>
        </div>

        {syncMutation.data && (
          <p className="mt-2 rounded-lg border border-emerald-200 bg-emerald-50 px-3 py-2 text-sm text-emerald-700">
            同步已启动 (批次 {syncMutation.data.id})
          </p>
        )}
        {syncMutation.isError && (
          <p className="mt-2 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {mutationError(syncMutation.error)}
          </p>
        )}

        <div className="mt-3 flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => runSync('normal')}
            disabled={syncMutation.isPending}
            className="pim-button-primary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
            title="立即同步"
            aria-label="立即同步"
          >
            {syncMutation.isPending ? '同步中' : '立即同步'}
          </button>
          <button
            type="button"
            onClick={() => runSync('full-resources')}
            disabled={syncMutation.isPending}
            className="pim-button-secondary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
            title="深度同步：刷新所有 Microsoft 日程并回填新支持字段"
            aria-label="深度同步"
          >
            深度同步
          </button>
          <button
            type="button"
            onClick={handleForceFetchAll}
            disabled={syncMutation.isPending}
            className="pim-button-secondary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
            title="强制获取全部日程"
            aria-label="强制获取全部日程"
          >
            <RefreshCw className="mr-1 inline-block h-3.5 w-3.5" />
            强制获取全部日程
          </button>
        </div>

        <p className="mt-2 text-xs leading-5 text-slate-500">
          深度同步会刷新所有 Microsoft 日程并回填新支持字段；立即同步只拉取变更。
        </p>

        {/* Range sync */}
        <div className="mt-3">
          <button
            type="button"
            onClick={() => setSyncMode(prev => prev === 'range-instances' ? 'normal' : 'range-instances')}
            className="text-xs font-semibold text-blue-600 hover:text-blue-800"
          >
            {syncMode === 'range-instances' ? '收起日期范围' : '指定日期范围同步'}
          </button>
          {syncMode === 'range-instances' && (
            <div className="mt-2 flex flex-wrap items-end gap-3">
              <label className="text-sm">
                <span className="text-xs font-semibold text-slate-500">开始</span>
                <input
                  type="date"
                  value={rangeStart}
                  onChange={event => setRangeStart(event.target.value)}
                  className="mt-1 block rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 outline-none focus:border-blue-400"
                />
              </label>
              <label className="text-sm">
                <span className="text-xs font-semibold text-slate-500">结束</span>
                <input
                  type="date"
                  value={rangeEnd}
                  onChange={event => setRangeEnd(event.target.value)}
                  className="mt-1 block rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-800 outline-none focus:border-blue-400"
                />
              </label>
              <button
                type="button"
                onClick={() => runSync('range-instances')}
                disabled={syncMutation.isPending || !rangeStart || !rangeEnd}
                className="pim-button-primary px-4 py-2 text-sm disabled:cursor-not-allowed disabled:opacity-60"
              >
                同步范围
              </button>
            </div>
          )}
        </div>
      </section>

      {/* Sync Batches (Paged History) */}
      <section className="pim-panel min-w-0 overflow-hidden p-4">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <h2 className="text-sm font-semibold text-slate-950">同步历史</h2>
          {syncMutation.isError && (
            <span className="rounded-full bg-red-50 px-2.5 py-1 text-xs font-semibold text-red-700">
              {mutationError(syncMutation.error)}
            </span>
          )}
        </div>

        {batchesLoading ? (
          <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
            正在加载...
          </p>
        ) : batches.length === 0 ? (
          <p className="mt-4 rounded-lg border border-dashed border-slate-200 px-3 py-6 text-center text-sm text-slate-500">
            暂无同步记录。
          </p>
        ) : (
          <div className="mt-4 space-y-3">
            {batches.map(batch => {
              const perCalendar = parsePerCalendarJson(batch.perCalendarJson);
              const retryableCalendars = perCalendar.filter(
                pc => pc.status === 'failed' || pc.status === 'partial',
              );
              // #309：远端已删除的日历会被跟随删除（移入回收站）。这是"同步成功"
              // 的结果而非错误，所以单独列出，避免用户以为数据丢了。
              const mirrorDeletedSteps = (batch.steps ?? []).filter(
                step => step.status === 'mirror-deleted',
              );
              const mirrorDeletedEventTotal = perCalendar
                .filter(pc => pc.mirrorDeleted)
                .reduce((sum, pc) => sum + pc.deletedCount, 0);

              return (
                <article key={batch.id} className="rounded-lg border border-slate-200 bg-white p-3">
                <div className="flex flex-wrap items-start justify-between gap-3">
                  <div className="min-w-0">
                    <p className="truncate text-sm font-semibold text-slate-950">{batch.status}</p>
                    <p className="mt-1 text-xs text-slate-500">
                      {batch.provider} / {formatDateTime(batch.startedAt)}{batch.finishedAt ? ` → ${formatDateTime(batch.finishedAt)}` : ''}
                      {batch.mode ? ` / ${batch.mode}` : ''}
                    </p>
                  </div>
                  <div className="flex flex-wrap gap-1.5 text-[11px] font-semibold">
                    <span className="rounded-full bg-slate-100 px-2 py-0.5 text-slate-600">读取 {batch.readCount}</span>
                    <span className="rounded-full bg-emerald-50 px-2 py-0.5 text-emerald-700">创建 {batch.createdCount}</span>
                    <span className="rounded-full bg-blue-50 px-2 py-0.5 text-blue-700">更新 {batch.updatedCount}</span>
                    <span className="rounded-full bg-amber-50 px-2 py-0.5 text-amber-700">冲突 {batch.conflictCount}</span>
                    <span className="rounded-full bg-red-50 px-2 py-0.5 text-red-700">错误 {batch.failureCount}</span>
                  </div>
                </div>

                {mirrorDeletedSteps.length > 0 && (
                  <div className="mt-3 rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-sm text-amber-800">
                    <p className="font-semibold">
                      已在 Outlook 端删除 {mirrorDeletedSteps.length} 个日历，PIM 已跟随删除并移入回收站
                      {mirrorDeletedEventTotal > 0 ? `（含 ${mirrorDeletedEventTotal} 条日程）` : ''}。
                    </p>
                    <ul className="mt-1 space-y-0.5 text-xs text-amber-700">
                      {mirrorDeletedSteps.map(step => (
                        <li key={step.name}>{step.detail}</li>
                      ))}
                    </ul>
                    <p className="mt-1 text-xs text-amber-700">
                      如需保留这些数据，可到「回收站」恢复；恢复后它们会作为 PIM 本地日历使用，不再与 Outlook 关联。
                    </p>
                  </div>
                )}

                {batch.errorSummary && (
                  <p className="mt-3 rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{batch.errorSummary}</p>
                )}

                {retryableCalendars.length > 0 && (
                  <div className="mt-2 space-y-1">
                    {retryableCalendars.map(pc => (
                      <div key={pc.bindingId} className="flex items-center justify-between gap-2 rounded-lg bg-red-50 px-3 py-1.5 text-xs">
                        <span className="text-red-700">
                          {pc.calendarName}: {pc.failures[0]?.message ?? '错误'}
                        </span>
                        <button
                          type="button"
                          onClick={() => {
                            retryPerCalendarMutation.mutate({ batch, bindingId: pc.bindingId });
                          }}
                          disabled={retryPerCalendarMutation.isPending}
                          className="font-semibold text-blue-600 hover:text-blue-800 disabled:opacity-60"
                          title="重试"
                          aria-label="重试"
                        >
                          重试
                        </button>
                      </div>
                    ))}
                  </div>
                )}

                <div className="mt-2 flex flex-wrap gap-1">
                  {(batch.status === 'running' || batch.status === 'pending') && (
                    <button
                      type="button"
                      onClick={() => handleCancelBatch(batch.id)}
                      disabled={cancelSyncMutation.isPending}
                      className="text-xs font-semibold text-red-600 hover:text-red-800 disabled:opacity-60"
                      title="取消"
                      aria-label="取消"
                    >
                      取消
                    </button>
                  )}
                </div>
              </article>
              );
            })}
          </div>
        )}

        {/* Pagination */}
        {batchTotalPages > 1 && (
          <div className="mt-4 flex items-center justify-center gap-2">
            <button
              type="button"
              onClick={() => setBatchesPage(p => Math.max(1, p - 1))}
              disabled={batchesPage <= 1}
              className="pim-button-secondary px-3 py-1.5 text-sm disabled:opacity-50"
            >
              上一页
            </button>
            <span className="text-xs text-slate-500">{batchesPage} / {batchTotalPages}</span>
            <button
              type="button"
              onClick={() => setBatchesPage(p => Math.min(batchTotalPages, p + 1))}
              disabled={batchesPage >= batchTotalPages}
              className="pim-button-secondary px-3 py-1.5 text-sm disabled:opacity-50"
            >
              下一页
            </button>
          </div>
        )}
      </section>

      {/* Local Data Management */}
      <section className="pim-panel min-w-0 p-4">
        <h2 className="text-sm font-semibold text-slate-950">本地数据管理</h2>
        <p className="mt-1 text-xs text-slate-500">
          管理本地缓存的 Microsoft 日历数据。此操作仅移除本地数据，不修改 Outlook 云端。
        </p>
        <div className="mt-3 flex flex-wrap gap-2">
          <button
            type="button"
            onClick={() => localDataPreviewMutation.mutate()}
            disabled={localDataPreviewMutation.isPending}
            className="pim-button-secondary px-3 py-1.5 text-sm disabled:cursor-not-allowed disabled:opacity-60"
          >
            预览本地数据
          </button>
          {(disconnectMutation.isPending || localDataDeleteMutation.isPending || localDataPreviewMutation.isPending) && (
            <span className="text-xs text-slate-500">处理中...</span>
          )}
        </div>

        {localDataPreviewMutation.isError && (
          <p className="mt-2 text-xs text-red-600">本地数据预览失败，请重试。</p>
        )}

        {showLocalPreview && localPreviewData && (
          <div className="mt-3 rounded-lg border border-slate-200 bg-slate-50 p-3">
            <div className="grid grid-cols-3 gap-2 text-center text-sm">
              <div>
                <p className="font-semibold text-slate-800">{localPreviewData.bindingCount}</p>
                <p className="text-xs text-slate-500">绑定</p>
              </div>
              <div>
                <p className="font-semibold text-slate-800">{localPreviewData.calendarCount}</p>
                <p className="text-xs text-slate-500">日历</p>
              </div>
              <div>
                <p className="font-semibold text-slate-800">{localPreviewData.eventCount}</p>
                <p className="text-xs text-slate-500">事件</p>
              </div>
            </div>
            {!localDeleteConfirm ? (
              <button
                type="button"
                onClick={() => setLocalDeleteConfirm(true)}
                className="mt-3 w-full rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm font-semibold text-red-700 hover:bg-red-100"
              >
                移除本地 Microsoft 数据
              </button>
            ) : (
              <div className="mt-3 space-y-2">
                <p className="text-xs font-semibold text-red-700">
                  此操作仅移除本地数据，不修改 Outlook 云端。确定要删除？
                </p>
                <div className="flex gap-2">
                  <button
                    type="button"
                    onClick={() => localDataDeleteMutation.mutate()}
                    disabled={localDataDeleteMutation.isPending}
                    className="rounded-lg border border-red-200 bg-red-600 px-3 py-2 text-sm font-semibold text-white hover:bg-red-700 disabled:opacity-60"
                  >
                    确认移除
                  </button>
                  <button
                    type="button"
                    onClick={() => setLocalDeleteConfirm(false)}
                    className="pim-button-secondary px-3 py-2 text-sm"
                  >
                    取消
                  </button>
                </div>
              </div>
            )}
          </div>
        )}
      </section>

      {/* Disconnect */}
      <section className="pim-panel min-w-0 p-4 border-red-200">
        <div className="flex flex-wrap items-center justify-between gap-2">
          <div>
            <h2 className="text-sm font-semibold text-slate-950">断开连接</h2>
            <p className="mt-1 text-xs text-slate-500">断开 Microsoft 账号连接。本地数据不受影响。</p>
          </div>
          <button
            type="button"
            onClick={() => disconnectMutation.mutate()}
            disabled={disconnectMutation.isPending}
            className="rounded-lg border border-red-200 bg-white px-3 py-1.5 text-sm font-semibold text-red-700 hover:bg-red-50 disabled:opacity-60"
          >
            {disconnectMutation.isPending ? '断开中' : '断开连接'}
          </button>
        </div>
      </section>
    </div>
  );
}
