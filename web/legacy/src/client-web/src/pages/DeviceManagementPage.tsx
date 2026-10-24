import { X } from 'lucide-react';
import { useEffect, useRef, useState } from 'react';
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query';
import { getManagedDevices, renameDevice, previewMerge, mergeDevices, previewDeleteDevice, deleteDevice, exportDevice } from '../api/mobile';
import type { DeviceListItem } from '../api/mobile';
import {
  buildMergeCandidates,
  buildMergePreviewRows,
  deviceOptionLabel,
  mergeIncomingTotal,
  orderMergeCandidates,
  pickDefaultMergeTarget,
  type MergePreview,
} from './deviceMergeModel';

/**
 * 合并设备弹窗（issue #232）。
 *
 * 交互约定：设备列表上的勾选 = 参与合并的设备集合（含要保留的那台），
 * 弹窗里只做一件事——选「保留哪台」。默认选中最近活跃的那台，
 * 其余设备自动列为「并入后移除」，每台都展示可区分信息与各自的记录数。
 */
export function MergeConfirmDialog({
  mergeSel,
  devices,
  onClose,
}: {
  mergeSel: string[];
  devices: DeviceListItem[];
  onClose: () => void;
}) {
  const qc = useQueryClient();
  const selectedDevices = orderMergeCandidates(
    devices.filter(device => mergeSel.includes(device.deviceId)),
  );
  const selectedIds = selectedDevices.map(device => device.deviceId);
  const selectionIsStale = selectedIds.length !== mergeSel.length;
  const [targetId, setTargetId] = useState(() => pickDefaultMergeTarget(selectedDevices));
  // 设备列表可能在弹窗打开期间刷新（react-query 重新拉取）：若原来选中的设备已不在列表里，
  // 退回默认目标，避免把设备合并到一个不在勾选集合里的 device id。
  const effectiveTargetId = selectedDevices.some(device => device.deviceId === targetId)
    ? targetId
    : pickDefaultMergeTarget(selectedDevices);
  const [renderedAt] = useState(() => new Date());
  const dialogRef = useRef<HTMLDivElement>(null);
  const previouslyFocusedRef = useRef<HTMLElement | null>(null);

  useEffect(() => {
    previouslyFocusedRef.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    dialogRef.current?.focus();
    return () => {
      previouslyFocusedRef.current?.focus();
    };
  }, []);

  const mergeMut = useMutation({
    mutationFn: ({ src, tgt }: { src: string[]; tgt: string }) => mergeDevices(src, tgt),
    onSuccess: () => {
      qc.invalidateQueries({ queryKey: ['managed-devices'] });
      onClose();
    },
  });

  // 用「设备列表里真实存在」的勾选集合发请求（纵深防御）：勾选集合失效时下面的 enabled
  // 已经会拦住请求与合并，这里再保证即使条件放宽也不会把已消失的设备发出去。
  const sourceDeviceIds = selectedIds.filter(id => id !== effectiveTargetId);
  const sourceKey = sourceDeviceIds.join('|');
  const previewQuery = useQuery({
    queryKey: ['device-merge-preview', effectiveTargetId, sourceKey],
    queryFn: () => previewMerge(sourceDeviceIds, effectiveTargetId) as Promise<MergePreview>,
    // 勾选集合失效时不预览：此时的「存活子集」不是用户选中的那批设备，预览会误导。
    enabled: Boolean(effectiveTargetId) && sourceDeviceIds.length > 0 && !selectionIsStale,
  });

  // 选项列表用设备列表自带的统计值（稳定），预览明细用后端预览返回的权威记录数。
  const candidates = buildMergeCandidates(selectedDevices, renderedAt);
  const preview = previewQuery.data ?? null;
  const previewRows = buildMergePreviewRows(selectedDevices, effectiveTargetId, preview, renderedAt);
  const sourceRows = previewRows.filter(row => !row.isTarget);
  const incomingTotal = mergeIncomingTotal(previewRows);
  const canMerge = Boolean(effectiveTargetId) && sourceRows.length > 0
    && !selectionIsStale && !mergeMut.isPending;
  const previewError = selectionIsStale
    ? '设备列表已刷新，部分勾选设备已不在列表中，请关闭弹窗后重新选择'
    : mergeSel.length < 2
      ? '至少需要选择 2 台不同的设备'
      : previewQuery.error
        ? (previewQuery.error instanceof Error ? previewQuery.error.message : '预览失败')
        : null;

  // 合并进行中不允许关闭弹窗，否则会丢失错误提示与结果反馈。
  const closeBlocked = mergeMut.isPending;
  const requestClose = () => {
    if (closeBlocked) return;
    onClose();
  };

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-zinc-950/40 backdrop-blur-xs animate-backdrop" onClick={requestClose}>
      <div
        role="dialog"
        aria-modal="true"
        aria-labelledby="merge-confirm-dialog-title"
        tabIndex={-1}
        ref={dialogRef}
        onKeyDown={e => { if (e.key === 'Escape') { e.stopPropagation(); requestClose(); } }}
        className="w-full max-w-lg rounded-xl border border-zinc-200 bg-white shadow-dialog animate-dialog"
        onClick={e => e.stopPropagation()}
      >
        <header className="flex items-center justify-between border-b border-zinc-200 px-5 py-4">
          <h2 id="merge-confirm-dialog-title" className="text-base font-semibold text-zinc-900">合并设备</h2>
          <button
            onClick={requestClose}
            disabled={closeBlocked}
            className="text-zinc-400 hover:text-zinc-600 disabled:opacity-40"
            aria-label="关闭"
          >
            <X className="w-4 h-4" />
          </button>
        </header>
        <div className="overflow-y-auto max-h-[75vh] px-5 py-4 space-y-4">
          <p className="rounded-lg border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-900">
            勾选的 {selectedDevices.length} 台设备都会参与合并，请选择要保留的那台。
            其余设备的数据会并入它，并入完成后这些设备会从设备列表移除；被保留的设备 ID 不变，手机无需重新注册。
          </p>

          <fieldset>
            <legend className="mb-2 block text-sm font-medium text-zinc-700">保留哪台设备</legend>
            <div className="space-y-2">
              {candidates.map(candidate => {
                const isTarget = candidate.deviceId === effectiveTargetId;
                return (
                  <label
                    key={candidate.deviceId}
                    title={deviceOptionLabel(candidate)}
                    className={`flex cursor-pointer items-start gap-3 rounded-lg border px-3 py-2 text-sm ${
                      isTarget ? 'border-blue-300 bg-blue-50' : 'border-zinc-200 hover:bg-zinc-50'
                    }`}
                  >
                    <input
                      type="radio"
                      name="merge-target-device"
                      className="mt-1"
                      value={candidate.deviceId}
                      checked={isTarget}
                      onChange={() => setTargetId(candidate.deviceId)}
                    />
                    <span className="min-w-0 flex-1 space-y-1">
                      <span className="flex flex-wrap items-center gap-2">
                        <span className="font-medium text-zinc-900">{candidate.displayName}</span>
                        {candidate.isOnline && (
                          <span className="rounded-full bg-emerald-100 px-2 py-0.5 text-[11px] font-medium text-emerald-700">当前活跃</span>
                        )}
                        <span
                          className={`rounded-full px-2 py-0.5 text-[11px] font-medium ${
                            isTarget ? 'bg-blue-600 text-white' : 'bg-zinc-200 text-zinc-700'
                          }`}
                        >
                          {isTarget ? '保留' : '并入后移除'}
                        </span>
                      </span>
                      <span className="block text-xs text-zinc-600">
                        ID …{candidate.shortId} · 最后活跃 {candidate.lastSeenLabel} · {candidate.dataCount} 条记录
                      </span>
                    </span>
                  </label>
                );
              })}
            </div>
          </fieldset>

          {previewQuery.isLoading && (
            <p className="text-sm text-zinc-500">正在加载预览...</p>
          )}

          {previewError && (
            <p className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">{previewError}</p>
          )}

          {preview && !previewQuery.isLoading && (
            <div className="rounded-lg border border-zinc-200 bg-zinc-50 p-3 space-y-2">
              <p className="text-sm font-semibold text-zinc-800">合并预览</p>
              <ul className="space-y-1">
                {previewRows.map(row => (
                  <li key={row.deviceId} className="flex items-center justify-between gap-2 text-xs text-zinc-700">
                    <span className="min-w-0 truncate">
                      {row.displayName}
                      <span className="text-zinc-500">（…{row.shortId}）</span>
                    </span>
                    <span className="shrink-0 whitespace-nowrap">
                      {row.isTarget
                        ? <>保留，现有 {row.dataCount} 条</>
                        : <>并入 {row.dataCount} 条后移除</>}
                    </span>
                  </li>
                ))}
              </ul>
              <p className="border-t border-zinc-200 pt-2 text-sm text-zinc-800">
                将并入 <span className="font-bold text-zinc-900">{incomingTotal}</span> 条记录，
                并移除 <span className="font-medium">{sourceRows.length}</span> 台设备
              </p>
            </div>
          )}

          {mergeMut.isError && (
            <p className="rounded-lg border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
              合并失败：{mergeMut.error instanceof Error ? mergeMut.error.message : '请求失败'}
            </p>
          )}
        </div>
        <footer className="flex items-center justify-between border-t border-zinc-200 px-5 py-4">
          <div className="text-xs text-zinc-400">
            {selectedIds.length} 台设备已选 · 保留 {effectiveTargetId ? `…${previewRows.find(row => row.isTarget)?.shortId ?? ''}` : '未选择'}
          </div>
          <div className="flex gap-2">
            <button
              type="button"
              className="px-4 py-2 text-sm rounded-lg border border-zinc-200 text-zinc-600 hover:bg-zinc-50 disabled:opacity-50"
              disabled={closeBlocked}
              onClick={requestClose}
            >
              取消
            </button>
            <button
              type="button"
              className="px-4 py-2 text-sm rounded-lg bg-amber-600 text-white hover:bg-amber-700 disabled:opacity-50"
              disabled={!canMerge}
              onClick={() => {
                if (effectiveTargetId) {
                  mergeMut.mutate({ src: sourceDeviceIds, tgt: effectiveTargetId });
                }
              }}
            >
              {mergeMut.isPending ? '合并中...' : '确认合并'}
            </button>
          </div>
        </footer>
      </div>
    </div>
  );
}

export default function DeviceManagementPage() {
  const qc = useQueryClient();
  const [sortBy, setSortBy] = useState<'lastSeen'|'data'>('lastSeen');
  const { data: devices, isLoading } = useQuery({ queryKey: ['managed-devices', sortBy], queryFn: () => getManagedDevices(sortBy) });
  const [renameId, setRenameId] = useState<string | null>(null);
  const [renameVal, setRenameVal] = useState('');
  const [mergeSel, setMergeSel] = useState<string[]>([]);
  const [showMergeDialog, setShowMergeDialog] = useState(false);
  const renameMut = useMutation({ mutationFn: ({id, name}:{id:string,name:string})=>renameDevice(id,name), onSuccess:()=>qc.invalidateQueries({queryKey:['managed-devices']})});
  const delMut = useMutation({ mutationFn: (id:string)=>deleteDevice(id), onSuccess:()=>qc.invalidateQueries({queryKey:['managed-devices']})});
  if (isLoading) return <div className="p-4">加载中...</div>;

  const handleMerge = () => {
    setShowMergeDialog(true);
  };

  return <div className="p-4 space-y-4">
    <h1 className="text-xl font-semibold">设备管理</h1>
    <div className="flex gap-2">
      <button onClick={()=>setSortBy('lastSeen')} className={`px-3 py-1 rounded ${sortBy==='lastSeen'?'bg-slate-900 text-white':'bg-slate-100'}`}>按活跃</button>
      <button onClick={()=>setSortBy('data')} className={`px-3 py-1 rounded ${sortBy==='data'?'bg-slate-900 text-white':'bg-slate-100'}`}>按数据量</button>
    </div>

    {/* 批量操作与状态引导栏 */}
    <div className="flex items-center justify-between rounded-lg border border-slate-200 bg-slate-50 px-4 py-3">
      <p className="text-sm text-slate-600">
        {mergeSel.length === 0
          ? '请勾选需要合并或管理的设备'
          : mergeSel.length === 1
            ? '已选 1 台，请至少勾选 2 台设备以执行数据合并'
            : `已选 ${mergeSel.length} 台设备（勾选集合即参与合并的设备，含要保留的那台）`}
      </p>
      {mergeSel.length >= 2 && (
        <button
          type="button"
          className="rounded-lg bg-amber-600 px-4 py-2 text-sm font-medium text-white hover:bg-amber-700"
          onClick={handleMerge}
        >
          合并选中的设备...
        </button>
      )}
    </div>

    <div className="space-y-2">
      {(devices??[]).map(d=> <div key={d.deviceId} className="border p-3 rounded flex justify-between">
        <div className="space-y-1">
          <div className="font-medium">{d.displayName || d.deviceId} <span className="text-xs text-slate-500">{d.isOnline?'在线':'离线'}</span></div>
          <div className="text-xs text-slate-600">{d.brand} {d.model} · {d.osVersion} · App {d.appVersion}</div>
          <div className="text-xs">ID: {d.deviceId} <button className="ml-2 text-blue-600" onClick={()=>navigator.clipboard.writeText(d.deviceId)}>复制</button></div>
          <div className="text-xs">注册: {d.registeredAtUtc} · 最后活跃: {d.lastSeenAtUtc}</div>
          <div className="text-xs">sessions:{d.sessionCount} events:{d.eventCount} locations:{d.locationCount} 范围:{d.earliest}~{d.latest} 占用:{d.storageEstimateKb}KB</div>
          <div className="text-xs">同步:{d.syncStatus} 数据质量:{d.dataQuality} 存储:{d.storagePressure}</div>
          {renameId===d.deviceId ? <div className="flex gap-2"><input value={renameVal} onChange={e=>setRenameVal(e.target.value)} maxLength={50} className="border px-2 py-1"/><button onClick={()=>{renameMut.mutate({id:d.deviceId,name:renameVal}); setRenameId(null);}} className="bg-blue-600 text-white px-2">保存</button><button onClick={()=>setRenameId(null)}>取消</button></div> : <button className="text-blue-600 text-xs" onClick={()=>{setRenameId(d.deviceId); setRenameVal(d.displayName);}}>重命名</button>}
        </div>
        <div className="flex flex-col gap-2">
          <label className="text-xs"><input type="checkbox" checked={mergeSel.includes(d.deviceId)} onChange={e=> setMergeSel(prev=> e.target.checked? [...prev,d.deviceId] : prev.filter(x=>x!==d.deviceId))}/> 合并</label>
          <button className="text-xs text-red-600" onClick={async()=>{ const p=await previewDeleteDevice(d.deviceId); if(confirm(`删除 ${p.displayName} 将删除 sessions:${p.sessionCount} events:${p.eventCount} 不可恢复 确认?`)) delMut.mutate(d.deviceId);}}>删除</button>
          <button className="text-xs text-slate-600" onClick={async()=>{ const blob=await exportDevice(d.deviceId); const url=URL.createObjectURL(blob); const a=document.createElement('a'); a.href=url; a.download=`pim-export-${d.displayName}-${new Date().toISOString().slice(0,10)}.json`; a.click();}}>导出</button>
          <a className="text-xs text-blue-600" href={`/devices/${d.deviceId}`}>详情</a>
        </div>
      </div>)}
    </div>

    {showMergeDialog && (
      <MergeConfirmDialog
        mergeSel={mergeSel}
        devices={devices ?? []}
        onClose={() => {
          setShowMergeDialog(false);
          setMergeSel([]);
        }}
      />
    )}
  </div>;
}
