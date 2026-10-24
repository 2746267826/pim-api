import { useEffect, useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  deleteAppKnowledgeContext,
  getAppKnowledgeApps,
  getAppKnowledgeContexts,
} from '../api/appKnowledge';
import { createAppSignature, deleteAppSignature } from '../api/appSignatures';
import AppKnowledgeContextList from '../components/app-knowledge/AppKnowledgeContextList';
import AppKnowledgeTabs from '../components/app-knowledge/AppKnowledgeTabs';
import PageHeader from '../ui/PageHeader';

const productivities = [
  { value: 'productive', label: '✅ 高效率', color: 'text-green-600 bg-green-50' },
  { value: 'neutral', label: '➖ 中性', color: 'text-slate-600 bg-slate-50' },
  { value: 'distracting', label: '❌ 分散精力', color: 'text-red-600 bg-red-50' },
];

function formatRecentDuration(seconds: number) {
  const minutes = Math.round(seconds / 60);
  return `${minutes.toLocaleString()} 分钟`;
}

function getSourceLabel(source: string) {
  if (source === 'builtin') {
    return '内置';
  }

  if (source === 'learned') {
    return '学习';
  }

  return '自定义';
}

export default function AppKnowledgeBasePage() {
  const queryClient = useQueryClient();
  const [search, setSearch] = useState('');
  const [selectedAppId, setSelectedAppId] = useState<string | null>(null);
  const [showAddForm, setShowAddForm] = useState(false);
  const [form, setForm] = useState({
    processName: '',
    displayName: '',
    categoryPath: '',
    productivity: 'neutral',
    icon: '',
    description: '',
  });

  const { data: apps = [], isLoading } = useQuery({
    queryKey: ['app-knowledge-apps', search],
    queryFn: () => getAppKnowledgeApps(search || undefined),
  });

  const selectedApp = apps.find(app => app.id === selectedAppId) ?? null;

  useEffect(() => {
    if (apps.length === 0) {
      if (selectedAppId !== null) {
        setSelectedAppId(null);
      }
      return;
    }

    if (!selectedAppId || !apps.some(app => app.id === selectedAppId)) {
      setSelectedAppId(apps[0].id);
    }
  }, [apps, selectedAppId]);

  const { data: contexts = [], isLoading: contextsLoading } = useQuery({
    queryKey: ['app-knowledge-contexts', selectedAppId],
    queryFn: () => selectedAppId ? getAppKnowledgeContexts(selectedAppId) : Promise.resolve([]),
    enabled: selectedAppId !== null,
  });

  const createMut = useMutation({
    mutationFn: () => createAppSignature({
      processName: form.processName.trim(),
      displayName: form.displayName.trim(),
      categoryPath: form.categoryPath.trim() || undefined,
      productivity: form.productivity,
      icon: form.icon.trim() || undefined,
      description: form.description.trim() || undefined,
    }),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['app-knowledge-apps'] });
      queryClient.invalidateQueries({ queryKey: ['app-signatures'] });
      setShowAddForm(false);
      setForm({ processName: '', displayName: '', categoryPath: '', productivity: 'neutral', icon: '', description: '' });
    },
  });

  const deleteMut = useMutation({
    mutationFn: (id: string) => deleteAppSignature(id),
    onSuccess: (_result, id) => {
      queryClient.invalidateQueries({ queryKey: ['app-knowledge-apps'] });
      queryClient.invalidateQueries({ queryKey: ['app-knowledge-contexts'] });
      queryClient.invalidateQueries({ queryKey: ['app-signatures'] });
      if (selectedAppId === id) {
        setSelectedAppId(null);
      }
    },
  });

  const contextDeleteMut = useMutation({
    mutationFn: (id: string) => deleteAppKnowledgeContext(id),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['app-knowledge-apps'] });
      queryClient.invalidateQueries({ queryKey: ['app-knowledge-contexts'] });
    },
  });

  return (
    <div className="space-y-4 pb-20">
      <PageHeader
        title="App 知识库"
        subtitle="管理应用、域名、标题模式和分类归属知识"
      />
      <AppKnowledgeTabs active="apps" />

      {/* Search + Add toolbar */}
      <div className="flex flex-wrap items-center gap-3">
        <input
          type="text"
          placeholder="搜索应用名称或进程名..."
          value={search}
          onChange={e => setSearch(e.target.value)}
          className="min-w-0 flex-1 rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm text-slate-700 outline-none transition-colors focus:border-blue-400"
        />
        <button
          type="button"
          onClick={() => setShowAddForm(!showAddForm)}
          className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-blue-700"
        >
          {showAddForm ? '取消' : '+ 添加应用'}
        </button>
      </div>

      {/* Add form */}
      {showAddForm && (
        <div className="space-y-3 rounded-lg border border-slate-200 bg-white p-4">
          <div className="grid grid-cols-1 gap-3 sm:grid-cols-2">
            <div>
              <label className="text-xs font-medium text-slate-500">进程名 *</label>
              <input
                type="text"
                value={form.processName}
                onChange={e => setForm(f => ({ ...f, processName: e.target.value }))}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-slate-500">显示名称 *</label>
              <input
                type="text"
                value={form.displayName}
                onChange={e => setForm(f => ({ ...f, displayName: e.target.value }))}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-slate-500">分类路径（如 工作·编程）</label>
              <input
                type="text"
                value={form.categoryPath}
                onChange={e => setForm(f => ({ ...f, categoryPath: e.target.value }))}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-slate-500">效率评分</label>
              <select
                value={form.productivity}
                onChange={e => setForm(f => ({ ...f, productivity: e.target.value }))}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
              >
                {productivities.map(p => <option key={p.value} value={p.value}>{p.label}</option>)}
              </select>
            </div>
            <div>
              <label className="text-xs font-medium text-slate-500">Emoji 图标</label>
              <input
                type="text"
                value={form.icon}
                onChange={e => setForm(f => ({ ...f, icon: e.target.value }))}
                placeholder="🎮"
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
              />
            </div>
            <div>
              <label className="text-xs font-medium text-slate-500">描述</label>
              <input
                type="text"
                value={form.description}
                onChange={e => setForm(f => ({ ...f, description: e.target.value }))}
                className="mt-1 w-full rounded-lg border border-slate-200 bg-white px-3 py-2 text-sm outline-none focus:border-blue-400"
              />
            </div>
          </div>
          <div className="flex justify-end">
            <button
              type="button"
              onClick={() => createMut.mutate()}
              disabled={!form.processName.trim() || !form.displayName.trim() || createMut.isPending}
              className="rounded-lg bg-blue-600 px-4 py-2 text-sm font-medium text-white transition-colors hover:bg-blue-700 disabled:opacity-50"
            >
              {createMut.isPending ? '提交中...' : '保存'}
            </button>
          </div>
        </div>
      )}

      <div className="grid gap-4 xl:grid-cols-[minmax(0,2fr)_minmax(320px,1fr)]">
        <section className="min-w-0 pb-20">
          {isLoading ? (
            <div className="py-8 text-center text-sm text-slate-500">加载中...</div>
          ) : apps.length === 0 ? (
            <div className="py-8 text-center text-sm text-slate-500">
              {search ? '未找到匹配的应用' : '知识库为空，点击"+ 添加应用"开始添加'}
            </div>
          ) : (
            <div className="overflow-x-auto rounded-lg border border-slate-200">
              <table className="w-full text-sm">
                <thead>
                  <tr className="border-b border-slate-200 bg-slate-50 text-left text-xs font-semibold uppercase tracking-wider text-slate-500">
                    <th className="px-3 py-2">图标</th>
                    <th className="px-3 py-2">显示名称</th>
                    <th className="px-3 py-2">进程名</th>
                    <th className="px-3 py-2">分类路径</th>
                    <th className="px-3 py-2">效率</th>
                    <th className="px-3 py-2">上下文</th>
                    <th className="px-3 py-2">来源</th>
                    <th className="px-3 py-2">操作</th>
                  </tr>
                </thead>
                <tbody>
                  {apps.map(app => {
                    const productivity = productivities.find(p => p.value === app.productivity);
                    const isSelected = app.id === selectedAppId;

                    return (
                      <tr
                        key={app.id}
                        aria-selected={isSelected}
                        onClick={() => setSelectedAppId(app.id)}
                        className={`cursor-pointer border-b border-slate-100 transition-colors last:border-b-0 ${
                          isSelected ? 'bg-blue-50 ring-1 ring-inset ring-blue-200' : 'hover:bg-slate-50'
                        }`}
                      >
                        <td className="px-3 py-2.5 text-lg">{app.icon || '❓'}</td>
                        <td className="px-3 py-2.5 font-medium text-slate-900">
                          <div className="flex flex-col">
                            <span>{app.displayName}</span>
                            {isSelected && <span className="text-xs font-normal text-blue-600">正在查看上下文</span>}
                          </div>
                        </td>
                        <td className="px-3 py-2.5 font-mono text-xs text-slate-500">{app.processName}</td>
                        <td className="px-3 py-2.5 text-slate-600">{app.categoryPath || '-'}</td>
                        <td className="px-3 py-2.5">
                          {productivity ? (
                            <span className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${productivity.color}`}>
                              {productivity.label}
                            </span>
                          ) : '-'}
                        </td>
                        <td className="px-3 py-2.5 text-slate-600">
                          <div className="flex flex-col text-xs">
                            <span>{app.contextCount.toLocaleString()} 个模式</span>
                            <span className={app.pendingContextCount > 0 ? 'text-amber-600' : 'text-slate-400'}>
                              {app.pendingContextCount.toLocaleString()} 项待确认
                            </span>
                          </div>
                        </td>
                        <td className="px-3 py-2.5">
                          <span className={`inline-block rounded-full px-2 py-0.5 text-xs font-medium ${
                            app.source === 'builtin' ? 'bg-slate-100 text-slate-600' : 'bg-blue-50 text-blue-700'
                          }`}
                          >
                            {getSourceLabel(app.source)}
                          </span>
                        </td>
                        <td className="px-3 py-2.5">
                          <button
                            type="button"
                            onClick={event => {
                              event.stopPropagation();
                              if (app.source === 'builtin') {
                                alert('内置项不可删除');
                                return;
                              }
                              if (confirm(`确定删除「${app.displayName}」？`)) {
                                deleteMut.mutate(app.id);
                              }
                            }}
                            disabled={app.source === 'builtin' || deleteMut.isPending}
                            className={`rounded px-2 py-1 text-xs font-medium transition-colors ${
                              app.source === 'builtin'
                                ? 'cursor-not-allowed text-slate-300'
                                : 'text-red-500 hover:bg-red-50'
                            }`}
                          >
                            删除
                          </button>
                        </td>
                      </tr>
                    );
                  })}
                </tbody>
              </table>
            </div>
          )}
        </section>

        <aside className="min-w-0 space-y-3 rounded-lg border border-slate-200 bg-white p-4 max-xl:fixed max-xl:inset-0 max-xl:z-40 max-xl:overflow-auto xl:static fixed inset-0 xl:inset-auto">
          <div className="space-y-3">
            <div>
              <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">上下文模式</p>
              <h2 className="mt-1 text-base font-semibold text-slate-900">
                {selectedApp ? selectedApp.displayName : '选择应用'}
              </h2>
              <p className="mt-1 text-xs text-slate-500">
                {selectedApp
                  ? `${selectedApp.processName} · ${formatRecentDuration(selectedApp.recentAffectedDurationSeconds)} 近期影响`
                  : '选择一行查看上下文知识模式。'}
              </p>
            </div>

            {selectedApp && (
              <div className="flex flex-wrap gap-2 text-xs text-slate-600">
                <span className="rounded border border-slate-200 bg-slate-50 px-2 py-1">
                  {selectedApp.contextCount.toLocaleString()} 个上下文模式
                </span>
                <span className="rounded border border-slate-200 bg-slate-50 px-2 py-1">
                  {formatRecentDuration(selectedApp.recentAffectedDurationSeconds)} 近期影响
                </span>
                {selectedApp.pendingContextCount > 0 && (
                  <span className="rounded border border-amber-200 bg-amber-50 px-2 py-1 text-amber-700">
                    {selectedApp.pendingContextCount.toLocaleString()} 项待确认上下文
                  </span>
                )}
              </div>
            )}
          </div>

          {selectedAppId ? (
            <AppKnowledgeContextList
              contexts={contexts}
              isLoading={contextsLoading}
              onDelete={id => {
                if (confirm('确认删除这个上下文知识模式？')) {
                  contextDeleteMut.mutate(id);
                }
              }}
            />
          ) : (
            <div className="rounded border border-dashed border-slate-200 px-4 py-8 text-center text-sm text-slate-500">
              选择应用行以查看上下文知识。
            </div>
          )}
        </aside>
      </div>
    </div>
  );
}
