import { useState } from 'react';
import PcDetailQueryPanel from '../components/pc-tracker/PcDetailQueryPanel';

export default function PcDetailQueryPage() {
  const [filterOpen, setFilterOpen] = useState(false);

  return (
    <div className="max-w-5xl mx-auto pb-20">
      <div className="flex flex-wrap items-center justify-between gap-3 mb-4">
        <h2 className="text-xl font-bold">PC记录 详细数据</h2>
        <button
          type="button"
          onClick={() => setFilterOpen(true)}
          className="pim-button-secondary px-3 py-2 text-sm lg:hidden"
        >
          筛选
        </button>
      </div>

      {/* Metric cards: cardized on small screens */}
      <div className="grid grid-cols-1 gap-3 md:grid-cols-2 mb-4">
        <div className="rounded-lg border border-slate-200 bg-white p-3">
          <p className="text-xs font-semibold text-slate-500">查询范围</p>
          <p className="mt-1 text-sm text-slate-700">按需筛选日期、设备、应用与分类</p>
        </div>
        <div className="rounded-lg border border-slate-200 bg-white p-3">
          <p className="text-xs font-semibold text-slate-500">视图说明</p>
          <p className="mt-1 text-sm text-slate-700">支持解释视图与原始视图切换</p>
        </div>
      </div>

      <div className="bg-white rounded-xl shadow-sm border p-5 overflow-auto">
        <PcDetailQueryPanel />
      </div>

      {filterOpen && (
        <div className="fixed inset-0 z-40 flex justify-end lg:hidden">
          <div className="absolute inset-0 bg-slate-950/30" onClick={() => setFilterOpen(false)} />
          <div className="relative flex h-full w-full max-w-[420px] flex-col overflow-auto bg-white p-4 shadow-xl">
            <div className="flex items-center justify-between">
              <h3 className="text-sm font-semibold text-slate-800">筛选</h3>
              <button type="button" className="text-xs text-slate-500 hover:text-slate-700" onClick={() => setFilterOpen(false)}>
                关闭
              </button>
            </div>
            <p className="mt-4 text-xs text-slate-500">详细筛选在主面板中配置，此抽屉为小屏快捷入口，关闭后返回列表。</p>
            <div className="mt-4 grid grid-cols-1 gap-3">
              <div className="rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-600">日期 / 维度 / 视图筛选</div>
              <div className="rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-600">设备 / 应用 / 分类筛选</div>
              <div className="rounded-lg bg-slate-50 px-3 py-2 text-sm text-slate-600">域名 / 标题 / 网页地址筛选</div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
