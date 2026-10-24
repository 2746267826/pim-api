import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { queryPcDetail, getPcQuality } from '../../api/pcTracker';
import type { DetailQueryParams, PcDetailRecord, PcQualityResponse } from '../../types';

const detailCsvColumns: { key: keyof PcDetailRecord; label: string }[] = [
  { key: 'recordType', label: '记录类型' },
  { key: 'start', label: '开始时间' },
  { key: 'end', label: '结束时间' },
  { key: 'durationSeconds', label: '持续秒数' },
  { key: 'deviceId', label: '设备' },
  { key: 'appName', label: '应用进程' },
  { key: 'displayName', label: '应用名称' },
  { key: 'categoryName', label: '分类' },
  { key: 'title', label: '标题' },
  { key: 'url', label: '网页地址' },
  { key: 'domain', label: '域名' },
  { key: 'path', label: '路径' },
  { key: 'isLocalFile', label: '本地文件' },
  { key: 'browserAppName', label: '浏览器应用' },
  { key: 'browserWindowTitle', label: '浏览器窗口标题' },
  { key: 'audible', label: '有声音' },
  { key: 'incognito', label: '隐身模式' },
  { key: 'tabCount', label: '标签页数量' },
  { key: 'absorbedShortEventsCount', label: '吸收短页面数' },
  { key: 'absorbedDurationSeconds', label: '吸收时长秒数' },
  { key: 'sourceWebEventIds', label: '来源页面事件' },
  { key: 'sourceWindowEventIds', label: '来源窗口事件' },
  { key: 'keyPresses', label: '按键数' },
  { key: 'totalClicks', label: '点击数' },
  { key: 'mouseDistance', label: '鼠标距离' },
  { key: 'scrollDistance', label: '滚动距离' },
  { key: 'keyCounts', label: '按键明细' },
  { key: 'raw', label: '原始数据' },
];

function formatCsvValue(row: PcDetailRecord, key: keyof PcDetailRecord) {
  const value = row[key];
  if (value == null) return '';
  const text = typeof value === 'object' ? JSON.stringify(value) : String(value);
  return /^[=+\-@\t\r\n]/.test(text) ? `'${text}` : text;
}

function downloadCSV(items: PcDetailRecord[], filename: string) {
  if (!items.length) return;
  const header = detailCsvColumns.map(c => JSON.stringify(c.label)).join(',');
  const rows = items.map(row =>
    detailCsvColumns.map(c => JSON.stringify(formatCsvValue(row, c.key))).join(',')
  );
  const csv = [header, ...rows].join('\n');
  const blob = new Blob(['\uFEFF' + csv], { type: 'text/csv;charset=utf-8' });
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = filename;
  a.click();
  URL.revokeObjectURL(url);
}

function formatDate(value: string | null) {
  if (!value) return '-';
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? '-' : date.toLocaleString('zh-CN');
}

function formatNumber(value: number | null | undefined) {
  return value ?? '-';
}

function formatDurationSeconds(value: number | null | undefined) {
  return value == null ? '-' : `${Math.round(value)} 秒`;
}

function formatBoolean(value: boolean | null | undefined) {
  if (value == null) return '-';
  return value ? '是' : '否';
}

function formatRecordType(value: string) {
  const labels: Record<string, string> = {
    window: '窗口',
    afk: '离开',
    'input-minute': '输入分钟',
    'app-input': '应用输入',
    'key-input': '按键输入',
    'web-page': '页面',
    web: '原始页面',
  };
  return labels[value] ?? value;
}

function renderMainDetail(row: PcDetailRecord) {
  if (row.recordType === 'web-page') {
    return (
      <div className="min-w-[280px] max-w-[420px] space-y-1">
        <div className="font-medium text-gray-800 truncate" title={row.title ?? undefined}>
          {row.title || '-'}
        </div>
        <div className="text-gray-600 truncate" title={row.domain ?? undefined}>
          {row.domain || '-'}
        </div>
        <div className="text-gray-500 truncate" title={row.url ?? undefined}>
          {row.url || '-'}
        </div>
        {row.raw != null && (
          <div className="text-gray-400 truncate" title={JSON.stringify(row.raw)}>
            原始数据：{JSON.stringify(row.raw)}
          </div>
        )}
      </div>
    );
  }

  if (row.recordType === 'web') {
    return (
      <div className="min-w-[280px] max-w-[420px] space-y-1">
        <div className="font-medium text-gray-800 truncate" title={row.title ?? undefined}>
          {row.title || '-'}
        </div>
        <div className="text-gray-600 truncate" title={row.domain ?? undefined}>
          {row.domain || '-'}
        </div>
        <div className="text-gray-500 truncate" title={row.url ?? undefined}>
          {row.url || '-'}
        </div>
        {row.raw != null && (
          <div className="text-gray-400 truncate" title={JSON.stringify(row.raw)}>
            原始数据：{JSON.stringify(row.raw)}
          </div>
        )}
      </div>
    );
  }

  return (
    <div className="min-w-[220px] max-w-[320px] truncate" title={row.title ?? undefined}>
      {row.title || '-'}
    </div>
  );
}

function renderBrowserSource(row: PcDetailRecord) {
  if (row.recordType !== 'web-page' && row.recordType !== 'web') {
    return row.displayName || row.appName || '-';
  }

  return (
    <div className="min-w-[180px] max-w-[260px] space-y-1">
      <div className="truncate" title={row.browserAppName ?? row.displayName ?? row.appName ?? undefined}>
        {row.browserAppName || row.displayName || row.appName || '-'}
      </div>
      <div className="text-gray-500 truncate" title={row.browserWindowTitle ?? undefined}>
        {row.browserWindowTitle || '-'}
      </div>
    </div>
  );
}

function renderExtraInfo(row: PcDetailRecord) {
  if (row.recordType === 'web-page') {
    const absorbedCount = row.absorbedShortEventsCount ?? 0;
    const absorbedDuration = row.absorbedDurationSeconds ?? 0;
    return (
      <div className="min-w-[150px] space-y-1 text-gray-700">
        <div>吸收短页面：{absorbedCount} 条</div>
        <div>吸收时长：{formatDurationSeconds(absorbedDuration)}</div>
        <div>标签页：{formatNumber(row.tabCount)}</div>
        <div>本地文件：{formatBoolean(row.isLocalFile)}</div>
      </div>
    );
  }

  if (row.recordType === 'web') {
    return (
      <div className="min-w-[140px] space-y-1 text-gray-700">
        <div>有声音：{formatBoolean(row.audible)}</div>
        <div>隐身：{formatBoolean(row.incognito)}</div>
        <div>标签页：{formatNumber(row.tabCount)}</div>
      </div>
    );
  }

  return (
    <div className="min-w-[120px] space-y-1 text-gray-700">
      <div>按键：{formatNumber(row.keyPresses)}</div>
      <div>点击：{formatNumber(row.totalClicks)}</div>
      <div>滚动：{formatNumber(row.scrollDistance)}</div>
    </div>
  );
}

function getEmptyStateText(quality: PcQualityResponse | undefined) {
  if (!quality) return '暂无数据';

  const codes = new Set(quality.issues.map(issue => issue.code));
  if (codes.has('no-keystats-samples-in-range') || codes.has('missing-keystats-samples')) return '查询范围内没有 KeyStats 分钟样本';
  if (codes.has('no-aw-events-in-range') || codes.has('missing-aw-events')) return '查询范围内没有 ActivityWatch 原始事件';
  if (codes.has('missing-aw-window-bucket')) return 'ActivityWatch 窗口采集源不可用';
  if (codes.has('missing-aw-window-events')) return '查询范围内没有 ActivityWatch 窗口事件';
  if (codes.has('missing-aw-afk-events')) return '查询范围内没有 ActivityWatch 离开状态事件';
  if (codes.has('keystats-insufficient-samples')) return 'KeyStats 样本不足，暂时无法生成输入时间线';
  if (codes.has('timeline-inputs-incomplete')) return '解释时间线的 ActivityWatch 或 KeyStats 输入不完整';
  if (codes.has('missing-windows-daemon-heartbeat')) return 'Windows daemon 尚未上报状态';
  if (quality.overallStatus === 'Unknown') return '新环境可能仍在等待采样';
  return '暂无数据';
}

export default function PcDetailQueryPanel() {
  const [params, setParams] = useState<DetailQueryParams>({ page: 1, pageSize: 20 });

  const { data, isLoading } = useQuery({
    queryKey: ['pc-detail', params],
    queryFn: () => queryPcDetail(params),
  });

  const { data: quality } = useQuery({
    queryKey: ['pc-detail-quality', params.dateFrom, params.dateTo],
    queryFn: () => getPcQuality({ dateFrom: params.dateFrom, dateTo: params.dateTo }),
    enabled: !data || data.items.length === 0,
  });

  const update = (key: keyof DetailQueryParams, value: unknown) =>
    setParams(p => ({ ...p, [key]: value || undefined, page: key === 'page' ? Number(value) : 1 }));

  return (
    <div className="space-y-4">
      <div className="grid grid-cols-1 gap-3 md:grid-cols-3 xl:grid-cols-4">
        <div>
          <label className="text-xs text-gray-500">开始日期</label>
          <input type="date" className="w-full border rounded-lg px-3 py-2 text-sm"
            onChange={e => update('dateFrom', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">结束日期</label>
          <input type="date" className="w-full border rounded-lg px-3 py-2 text-sm"
            onChange={e => update('dateTo', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">维度</label>
          <select className="w-full border rounded-lg px-3 py-2 text-sm"
            onChange={e => update('dimension', e.target.value || undefined)}>
            <option value="">全部</option>
            <option value="hour">小时</option>
            <option value="day">天</option>
            <option value="month">月</option>
            <option value="year">年</option>
          </select>
        </div>
        <div>
          <label className="text-xs text-gray-500">视图</label>
          <select className="w-full border rounded-lg px-3 py-2 text-sm"
            defaultValue="interpreted"
            onChange={e => update('view', e.target.value || undefined)}>
            <option value="interpreted">解释视图</option>
            <option value="raw">原始视图</option>
          </select>
        </div>
        <div>
          <label className="text-xs text-gray-500">事件类型</label>
          <select className="w-full border rounded-lg px-3 py-2 text-sm"
            onChange={e => update('eventType', e.target.value || undefined)}>
            <option value="">全部</option>
            <option value="web-page">页面</option>
            <option value="web">原始页面</option>
            <option value="window">窗口</option>
            <option value="afk">离开</option>
            <option value="input-minute">输入分钟</option>
            <option value="app-input">应用输入</option>
            <option value="key-input">按键输入</option>
          </select>
        </div>
        <div>
          <label className="text-xs text-gray-500">设备</label>
          <input type="text" className="w-full border rounded-lg px-3 py-2 text-sm" placeholder="输入设备 ID"
            onChange={e => update('deviceId', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">应用</label>
          <input type="text" className="w-full border rounded-lg px-3 py-2 text-sm" placeholder="进程名"
            onChange={e => update('appName', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">分类</label>
          <input type="text" className="w-full border rounded-lg px-3 py-2 text-sm" placeholder="分类名"
            onChange={e => update('categoryName', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">按键名</label>
          <input type="text" className="w-full border rounded-lg px-3 py-2 text-sm" placeholder="例如空格键"
            onChange={e => update('keyName', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">域名</label>
          <input type="text" className="w-full border rounded-lg px-3 py-2 text-sm" placeholder="例如 example.com"
            onChange={e => update('domain', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">标题</label>
          <input type="text" className="w-full border rounded-lg px-3 py-2 text-sm" placeholder="页面标题关键词"
            onChange={e => update('title', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">网页地址</label>
          <input type="text" className="w-full border rounded-lg px-3 py-2 text-sm" placeholder="网页地址关键词"
            onChange={e => update('url', e.target.value)} />
        </div>
        <div>
          <label className="text-xs text-gray-500">排序</label>
          <select className="w-full border rounded-lg px-3 py-2 text-sm"
            onChange={e => update('sortBy', e.target.value || undefined)}>
            <option value="">默认</option>
            <option value="keyPresses">按键数</option>
            <option value="totalClicks">点击数</option>
            <option value="date">日期</option>
          </select>
        </div>
      </div>

      {data && data.items.length > 0 && (
        <div className="flex gap-2">
          <button className="px-3 py-1 text-xs bg-green-50 text-green-700 border border-green-200 rounded-lg hover:bg-green-100"
            onClick={() => downloadCSV(data.items, `pc-detail-${new Date().toISOString().slice(0, 10)}.csv`)}>
            导出表格
          </button>
          <button className="px-3 py-1 text-xs bg-blue-50 text-blue-700 border border-blue-200 rounded-lg hover:bg-blue-100"
            onClick={() => {
              const json = JSON.stringify(data.items, null, 2);
              const blob = new Blob([json], { type: 'application/json' });
              const url = URL.createObjectURL(blob);
              const a = document.createElement('a');
              a.href = url;
              a.download = `pc-detail-${new Date().toISOString().slice(0, 10)}.json`;
              a.click();
              URL.revokeObjectURL(url);
            }}>
            导出原始数据
          </button>
        </div>
      )}

      <div className="overflow-x-auto">
        {isLoading ? (
          <div className="py-8 text-center text-gray-400">查询中...</div>
        ) : !data || !data.items.length ? (
          <div className="py-8 text-center text-gray-400">{getEmptyStateText(quality)}</div>
        ) : (
          <table className="w-full text-sm border-collapse">
            <thead>
              <tr className="border-b bg-gray-50">
                <th className="text-left px-3 py-2 text-xs text-gray-500 font-medium">类型</th>
                <th className="text-left px-3 py-2 text-xs text-gray-500 font-medium">开始</th>
                <th className="text-left px-3 py-2 text-xs text-gray-500 font-medium">结束</th>
                <th className="text-left px-3 py-2 text-xs text-gray-500 font-medium">设备</th>
                <th className="text-left px-3 py-2 text-xs text-gray-500 font-medium">来源</th>
                <th className="text-left px-3 py-2 text-xs text-gray-500 font-medium">详情</th>
                <th className="text-left px-3 py-2 text-xs text-gray-500 font-medium">补充信息</th>
                <th className="text-right px-3 py-2 text-xs text-gray-500 font-medium">持续时间</th>
              </tr>
            </thead>
            <tbody>
              {data.items.map((row, i) => (
                <tr key={`${row.recordType}-${row.start}-${i}`} className="border-b hover:bg-gray-50">
                  <td className="px-3 py-2 text-xs text-gray-700 whitespace-nowrap">{formatRecordType(row.recordType)}</td>
                  <td className="px-3 py-2 text-xs text-gray-700 whitespace-nowrap">{formatDate(row.start)}</td>
                  <td className="px-3 py-2 text-xs text-gray-700 whitespace-nowrap">{formatDate(row.end)}</td>
                  <td className="px-3 py-2 text-xs text-gray-700">{row.deviceId || '-'}</td>
                  <td className="px-3 py-2 text-xs text-gray-700">{renderBrowserSource(row)}</td>
                  <td className="px-3 py-2 text-xs text-gray-700">{renderMainDetail(row)}</td>
                  <td className="px-3 py-2 text-xs text-gray-700">{renderExtraInfo(row)}</td>
                  <td className="px-3 py-2 text-xs text-gray-700 text-right whitespace-nowrap">
                    {formatDurationSeconds(row.durationSeconds)}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>

      {data && data.totalPages > 1 && (
        <div className="flex items-center justify-center gap-2">
          <button className="px-2 py-1 text-xs border rounded disabled:opacity-30"
            disabled={(params.page || 1) <= 1}
            onClick={() => update('page', Math.max(1, (params.page || 1) - 1))}>上一页</button>
          <span className="text-xs text-gray-500">第 {data.page} / {data.totalPages} 页，共 {data.totalCount} 条</span>
          <button className="px-2 py-1 text-xs border rounded disabled:opacity-30"
            disabled={(params.page || 1) >= data.totalPages}
            onClick={() => update('page', (params.page || 1) + 1)}>下一页</button>
        </div>
      )}
    </div>
  );
}
