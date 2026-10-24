import type { MobileDevice } from '../../api/mobile';
import { MOBILE_LIFE_CATEGORIES } from '../../api/mobile';
import type { MobileRangeShortcut } from './mobileFormatting';

export interface MobileAnalyticsHeaderProps {
  rangeShortcut: MobileRangeShortcut;
  rangeStartDate: string;
  rangeEndDate: string;
  selectedDeviceId: string;
  devices: MobileDevice[];
  selectedCategory: string;
  packageName: string;
  includeSystemNoise: boolean;
  isFetching: boolean;
  errorMessage?: string | null;
  onShortcutChange: (shortcut: Exclude<MobileRangeShortcut, 'custom'>) => void;
  onCustomRangeChange: (range: { startDate: string; endDate: string }) => void;
  onDeviceChange: (deviceId: string) => void;
  onCategoryChange: (category: string) => void;
  onPackageNameChange: (packageName: string) => void;
  onIncludeSystemNoiseChange: (include: boolean) => void;
  onRefresh: () => void;
}

const shortcuts: Array<{ key: Exclude<MobileRangeShortcut, 'custom'>; label: string }> = [
  { key: 'today', label: '今天' },
  { key: '7d', label: '7天' },
  { key: '30d', label: '30天' },
];

export default function MobileAnalyticsHeader({
  rangeShortcut,
  rangeStartDate,
  rangeEndDate,
  selectedDeviceId,
  devices,
  selectedCategory,
  packageName,
  includeSystemNoise,
  isFetching,
  errorMessage = null,
  onShortcutChange,
  onCustomRangeChange,
  onDeviceChange,
  onCategoryChange,
  onPackageNameChange,
  onIncludeSystemNoiseChange,
  onRefresh,
}: MobileAnalyticsHeaderProps) {
  return (
    <section className="border-b border-slate-200 bg-white px-4 py-4 sm:px-6">
      <div className="mx-auto flex max-w-[1500px] flex-col gap-4">
        <div className="flex flex-wrap items-start justify-between gap-3">
          <div className="min-w-0">
            <h1 className="text-2xl font-semibold tracking-normal text-slate-950">手机记录</h1>
            <p className="mt-1 text-sm text-slate-500">
              从“包名列表”转成“可解释的使用行为”。热力图只显示强度，日期和小时作为坐标轴。
            </p>
          </div>
          <div className="flex flex-wrap items-center gap-2">
            <div className="flex h-9 rounded-md border border-slate-200 bg-slate-50 p-1" aria-label="日期快捷范围">
            {shortcuts.map(shortcut => {
              const isActive = rangeShortcut === shortcut.key;
              return (
                <button
                  key={shortcut.key}
                  type="button"
                  onClick={() => onShortcutChange(shortcut.key)}
                  className={`min-w-14 rounded px-3 text-sm font-medium ${
                    isActive ? 'bg-slate-950 text-white shadow-sm' : 'text-slate-600 hover:bg-white'
                  }`}
                  aria-pressed={isActive}
                >
                  {shortcut.label}
                </button>
              );
            })}
            <button
              type="button"
              onClick={() => onCustomRangeChange({ startDate: rangeStartDate, endDate: rangeEndDate })}
              className={`min-w-16 rounded px-3 text-sm font-medium ${
                rangeShortcut === 'custom' ? 'bg-slate-950 text-white shadow-sm' : 'text-slate-500 hover:bg-white'
              }`}
              aria-pressed={rangeShortcut === 'custom'}
            >
              自定义
            </button>
            </div>
            <span className="flex h-9 items-center rounded-md border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700">
              北京时间
            </span>
            <button
              type="button"
              onClick={onRefresh}
              className="h-9 rounded-md bg-blue-600 px-3 text-sm font-medium text-white hover:bg-blue-700"
            >
              {isFetching ? '刷新中...' : '刷新'}
            </button>
          </div>
        </div>

        <div className="grid grid-cols-1 gap-3 md:grid-cols-2 xl:grid-cols-6">
          <label className="min-w-0 text-xs font-semibold text-slate-500">
            设备
            <select
              value={selectedDeviceId}
              onChange={event => onDeviceChange(event.target.value)}
              className="mt-1 h-9 w-full rounded-md border border-slate-200 bg-white px-3 text-sm text-slate-900"
            >
              <option value="">全部设备</option>
              {devices.map(device => (
                <option key={device.deviceId} value={device.deviceId}>
                  {device.displayName || device.model || device.deviceId}
                </option>
              ))}
            </select>
          </label>

          <label className="min-w-0 text-xs font-semibold text-slate-500">
              分类
              <select
                value={selectedCategory}
                onChange={event => onCategoryChange(event.target.value)}
              className="mt-1 h-9 w-full rounded-md border border-slate-200 bg-white px-3 text-sm text-slate-900"
              >
                <option value="">全部分类</option>
                {MOBILE_LIFE_CATEGORIES.map(category => (
                  <option key={category} value={category}>
                    {category}
                  </option>
                ))}
              </select>
            </label>

          <label className="min-w-0 text-xs font-semibold text-slate-500">
              App
              <input
                type="search"
                value={packageName}
                onChange={event => onPackageNameChange(event.target.value)}
              placeholder="搜索名称或包名"
              className="mt-1 h-9 w-full rounded-md border border-slate-200 bg-white px-3 text-sm text-slate-900"
              />
            </label>

          <label className="min-w-0 text-xs font-semibold text-slate-500">
            噪声
            <span className="mt-1 flex h-9 items-center justify-between gap-2 rounded-md border border-slate-200 bg-white px-3 text-sm font-medium text-slate-700">
              <span>隐藏系统噪声</span>
              <span className="text-xs text-slate-500">{includeSystemNoise ? '关' : '开'}</span>
              <input
                aria-label="隐藏系统噪声"
                type="checkbox"
                checked={!includeSystemNoise}
                onChange={event => onIncludeSystemNoiseChange(!event.target.checked)}
                className="h-4 w-4"
              />
            </span>
          </label>

          <label className="min-w-0 text-xs font-semibold text-slate-500">
            粒度
            <span className="mt-1 flex h-9 items-center rounded-md border border-slate-200 bg-white px-3 text-sm text-slate-900">
              小时视图
            </span>
          </label>

          <label className="min-w-0 text-xs font-semibold text-slate-500">
            范围
            <span className="mt-1 grid grid-cols-2 gap-1">
              <input
                aria-label="开始日期"
                type="date"
                value={rangeStartDate}
                onChange={event => onCustomRangeChange({ startDate: event.target.value, endDate: rangeEndDate })}
                className="h-9 min-w-0 rounded-md border border-slate-200 bg-white px-2 text-xs text-slate-900"
              />
              <input
                aria-label="结束日期"
                type="date"
                value={rangeEndDate}
                onChange={event => onCustomRangeChange({ startDate: rangeStartDate, endDate: event.target.value })}
                className="h-9 min-w-0 rounded-md border border-slate-200 bg-white px-2 text-xs text-slate-900"
              />
            </span>
          </label>
        </div>

        {errorMessage && (
          <p className="rounded-md border border-red-200 bg-red-50 px-3 py-2 text-sm text-red-700">
            {errorMessage}
          </p>
        )}
      </div>
    </section>
  );
}
