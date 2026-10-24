import { useState } from 'react';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import {
  getCategoryTree,
  saveCategory,
  deleteCategory,
  seedCategories,
  type CategoryTreeNode,
  type CategorySaveRequest,
} from '../api/pcTracker';
import AppKnowledgeTabs from '../components/app-knowledge/AppKnowledgeTabs';
import PageHeader from '../ui/PageHeader';

function TreeNode({
  node,
  depth,
  onSelect,
  selectedId,
  onDelete,
}: {
  node: CategoryTreeNode;
  depth: number;
  onSelect: (node: CategoryTreeNode) => void;
  selectedId: string | null;
  onDelete: (id: string) => void;
}) {
  const [expanded, setExpanded] = useState(true);
  const hasChildren = node.children.length > 0;
  const isSelected = selectedId === node.id;

  const productivityColors: Record<string, string> = {
    productive: 'bg-emerald-100 text-emerald-700',
    neutral: 'bg-slate-100 text-slate-600',
    distracting: 'bg-rose-100 text-rose-700',
  };

  const productivityLabels: Record<string, string> = {
    productive: '专注',
    neutral: '中性',
    distracting: '休闲',
  };

  return (
    <div>
      <div
        className={`flex items-center gap-2 px-2 py-1.5 rounded cursor-pointer hover:bg-slate-100 transition-colors ${
          isSelected ? 'bg-blue-50 ring-1 ring-blue-200' : ''
        }`}
        style={{ paddingLeft: `${depth * 20 + 8}px` }}
        onClick={() => onSelect(node)}
      >
        <button
          className="w-5 h-5 flex items-center justify-center text-slate-400 hover:text-slate-600"
          onClick={(e) => { e.stopPropagation(); setExpanded(!expanded); }}
        >
          {hasChildren ? (expanded ? '▼' : '▶') : '•'}
        </button>
        {node.icon && <span className="text-base">{node.icon}</span>}
        <span className="w-3 h-3 rounded-full flex-shrink-0" style={{ backgroundColor: node.color }} />
        <span className="text-sm font-medium text-slate-800">{node.name}</span>
        <span className={`text-xs px-1.5 py-0.5 rounded ${productivityColors[node.productivity] || productivityColors.neutral}`}>
          {productivityLabels[node.productivity] || '中性'}
        </span>
        {node.isBuiltin && <span className="text-[10px] text-slate-400 ml-auto">内置</span>}
      </div>
      {expanded && hasChildren && (
        <div>
          {node.children.map(child => (
            <TreeNode
              key={child.id}
              node={child}
              depth={depth + 1}
              onSelect={onSelect}
              selectedId={selectedId}
              onDelete={onDelete}
            />
          ))}
        </div>
      )}
    </div>
  );
}

const defaultColors = [
  '#22C55E', '#3B82F6', '#EC4899', '#F59E0B', '#A855F7',
  '#06B6D4', '#F43F5E', '#10B981', '#6366F1', '#E11D48',
];

const productivityOptions = [
  { value: 'productive', label: '专注', color: 'text-emerald-600' },
  { value: 'neutral', label: '中性', color: 'text-slate-600' },
  { value: 'distracting', label: '休闲', color: 'text-rose-600' },
];

export default function CategoryTreePage() {
  const queryClient = useQueryClient();
  const [selectedNode, setSelectedNode] = useState<CategoryTreeNode | null>(null);
  const [showForm, setShowForm] = useState(false);
  const [form, setForm] = useState<CategorySaveRequest>({
    name: '',
    color: '#22C55E',
    icon: '',
    productivity: 'neutral',
    sortOrder: 0,
    parentId: null,
  });

  const { data: tree = [], isLoading } = useQuery({
    queryKey: ['category-tree'],
    queryFn: getCategoryTree,
  });

  const saveMutation = useMutation({
    mutationFn: saveCategory,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['category-tree'] });
      setShowForm(false);
      setForm({ name: '', color: '#22C55E', icon: '', productivity: 'neutral', sortOrder: 0, parentId: null });
    },
  });

  const deleteMutation = useMutation({
    mutationFn: deleteCategory,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['category-tree'] });
      setSelectedNode(null);
    },
  });

  const seedMutation = useMutation({
    mutationFn: seedCategories,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['category-tree'] });
    },
  });

  const handleSelect = (node: CategoryTreeNode) => {
    setSelectedNode(node);
    setForm({
      id: node.id,
      name: node.name,
      color: node.color,
      icon: node.icon || '',
      productivity: node.productivity,
      sortOrder: node.sortOrder,
      parentId: node.parentId,
    });
    setShowForm(true);
  };

  const handleAddChild = (parent: CategoryTreeNode) => {
    setSelectedNode(null);
    setForm({
      name: '',
      color: defaultColors[Math.floor(Math.random() * defaultColors.length)],
      icon: '',
      productivity: 'neutral',
      sortOrder: parent.children.length,
      parentId: parent.id,
    });
    setShowForm(true);
  };

  const handleAddRoot = () => {
    setSelectedNode(null);
    setForm({
      name: '',
      color: defaultColors[Math.floor(Math.random() * defaultColors.length)],
      icon: '',
      productivity: 'neutral',
      sortOrder: tree.length,
      parentId: null,
    });
    setShowForm(true);
  };

  const handleSubmit = () => {
    if (!form.name.trim()) return;
    saveMutation.mutate(form);
  };

  const handleDelete = (id: string) => {
    if (!confirm('确定删除此分类？')) return;
    deleteMutation.mutate(id);
  };

  return (
    <div className="space-y-4 pb-20">
      <PageHeader title="分类树" subtitle="作为 App 知识库的目标分类结构" />
      <AppKnowledgeTabs active="categories" />

      <div className="grid grid-cols-1 gap-4">
        {/* Left: Tree - single column, list area pb-20 */}
        <div className="pim-panel p-4 pb-20 overflow-auto">
          <div className="flex items-center justify-between mb-4">
            <h2 className="text-sm font-semibold text-slate-800">分类树</h2>
            <div className="flex gap-2">
              <button
                className="px-3 py-1.5 text-xs font-medium bg-slate-100 text-slate-600 rounded hover:bg-slate-200 transition-colors"
                onClick={() => seedMutation.mutate()}
                disabled={seedMutation.isPending}
              >
                {seedMutation.isPending ? '初始化中...' : '初始化默认'}
              </button>
              <button
                className="px-3 py-1.5 text-xs font-medium bg-blue-500 text-white rounded hover:bg-blue-600 transition-colors"
                onClick={handleAddRoot}
              >
                + 添加根分类
              </button>
            </div>
          </div>

          {isLoading ? (
            <div className="text-sm text-slate-400 py-8 text-center">加载中...</div>
          ) : tree.length === 0 ? (
            <div className="text-sm text-slate-400 py-8 text-center">
              暂无分类数据。
              <button className="text-blue-500 hover:text-blue-600 ml-1" onClick={() => seedMutation.mutate()}>
                点击初始化默认分类
              </button>
            </div>
          ) : (
            <div className="space-y-0.5">
              {tree.map(node => (
                <TreeNode
                  key={node.id}
                  node={node}
                  depth={0}
                  onSelect={handleSelect}
                  selectedId={selectedNode?.id || null}
                  onDelete={handleDelete}
                />
              ))}
            </div>
          )}
        </div>

        {/* Right: Edit Panel - desktop inline */}
        <div className="hidden lg:block pim-panel p-4">
          {showForm ? (
            <>
              <div className="flex items-center justify-between mb-4">
                <h2 className="text-sm font-semibold text-slate-800">
                  {form.id ? '编辑分类' : '新建分类'}
                </h2>
                <button
                  className="text-xs text-slate-400 hover:text-slate-600"
                  onClick={() => setShowForm(false)}
                >
                  关闭
                </button>
              </div>

              <div className="space-y-3">
                <div>
                  <label className="block text-xs text-slate-500 mb-1">名称</label>
                  <input
                    className="w-full px-2.5 py-1.5 text-sm border border-slate-200 rounded focus:outline-none focus:ring-1 focus:ring-blue-400"
                    value={form.name}
                    onChange={e => setForm({ ...form, name: e.target.value })}
                    placeholder="分类名称"
                  />
                </div>

                <div>
                  <label className="block text-xs text-slate-500 mb-1">图标 (emoji)</label>
                  <input
                    className="w-full px-2.5 py-1.5 text-sm border border-slate-200 rounded focus:outline-none focus:ring-1 focus:ring-blue-400"
                    value={form.icon || ''}
                    onChange={e => setForm({ ...form, icon: e.target.value })}
                    placeholder="🎮"
                    maxLength={4}
                  />
                </div>

                <div>
                  <label className="block text-xs text-slate-500 mb-1">颜色</label>
                  <div className="flex flex-wrap gap-1.5">
                    {defaultColors.map(color => (
                      <button
                        key={color}
                        className={`w-6 h-6 rounded-full border-2 transition-all ${
                          form.color === color ? 'border-slate-800 scale-110' : 'border-transparent'
                        }`}
                        style={{ backgroundColor: color }}
                        onClick={() => setForm({ ...form, color })}
                      />
                    ))}
                  </div>
                </div>

                <div>
                  <label className="block text-xs text-slate-500 mb-1">生产力属性</label>
                  <div className="flex gap-2">
                    {productivityOptions.map(opt => (
                      <button
                        key={opt.value}
                        className={`px-3 py-1.5 text-xs font-medium rounded border transition-all ${
                          form.productivity === opt.value
                            ? 'bg-slate-800 text-white border-slate-800'
                            : 'bg-white text-slate-600 border-slate-200 hover:border-slate-300'
                        }`}
                        onClick={() => setForm({ ...form, productivity: opt.value })}
                      >
                        {opt.label}
                      </button>
                    ))}
                  </div>
                </div>

                <div className="flex gap-2 pt-2">
                  <button
                    className="flex-1 px-3 py-1.5 text-sm font-medium bg-blue-500 text-white rounded hover:bg-blue-600 transition-colors disabled:opacity-50"
                    onClick={handleSubmit}
                    disabled={saveMutation.isPending || !form.name.trim()}
                  >
                    {saveMutation.isPending ? '保存中...' : '保存'}
                  </button>
                  {form.id && !form.parentId && (
                    <button
                      className="px-3 py-1.5 text-xs font-medium bg-rose-50 text-rose-600 rounded hover:bg-rose-100 transition-colors"
                      onClick={() => selectedNode && handleAddChild(selectedNode)}
                    >
                      + 添加子分类
                    </button>
                  )}
                  {form.id && (
                    <button
                      className="px-3 py-1.5 text-xs font-medium text-red-500 hover:text-red-600 transition-colors"
                      onClick={() => form.id && handleDelete(form.id)}
                    >
                      删除
                    </button>
                  )}
                </div>
              </div>
            </>
          ) : (
            <div className="text-sm text-slate-400 py-8 text-center">
              选择左侧分类进行编辑<br />
              或点击「添加根分类」创建新分类
            </div>
          )}
        </div>
      </div>

      {/* Mobile drawer: fixed inset-0 on small screens */}
      {showForm && (
        <div className="fixed inset-0 z-40 flex justify-end lg:hidden">
          <div className="absolute inset-0 bg-slate-950/30" onClick={() => setShowForm(false)} />
          <div className="relative flex h-full w-full max-w-[420px] flex-col overflow-auto bg-white p-4 shadow-xl">
            <div className="flex items-center justify-between mb-4">
              <h2 className="text-sm font-semibold text-slate-800">
                {form.id ? '编辑分类' : '新建分类'}
              </h2>
              <button
                className="text-xs text-slate-400 hover:text-slate-600"
                onClick={() => setShowForm(false)}
              >
                关闭
              </button>
            </div>
            <div className="space-y-3">
              <div>
                <label className="block text-xs text-slate-500 mb-1">名称</label>
                <input
                  className="w-full px-2.5 py-1.5 text-sm border border-slate-200 rounded focus:outline-none focus:ring-1 focus:ring-blue-400"
                  value={form.name}
                  onChange={e => setForm({ ...form, name: e.target.value })}
                  placeholder="分类名称"
                />
              </div>
              <div>
                <label className="block text-xs text-slate-500 mb-1">图标 (emoji)</label>
                <input
                  className="w-full px-2.5 py-1.5 text-sm border border-slate-200 rounded focus:outline-none focus:ring-1 focus:ring-blue-400"
                  value={form.icon || ''}
                  onChange={e => setForm({ ...form, icon: e.target.value })}
                  placeholder="🎮"
                  maxLength={4}
                />
              </div>
              <div>
                <label className="block text-xs text-slate-500 mb-1">颜色</label>
                <div className="flex flex-wrap gap-1.5">
                  {defaultColors.map(color => (
                    <button
                      key={color}
                      className={`w-6 h-6 rounded-full border-2 ${form.color === color ? 'border-slate-800 scale-110' : 'border-transparent'}`}
                      style={{ backgroundColor: color }}
                      onClick={() => setForm({ ...form, color })}
                    />
                  ))}
                </div>
              </div>
              <div>
                <label className="block text-xs text-slate-500 mb-1">生产力属性</label>
                <div className="flex gap-2">
                  {productivityOptions.map(opt => (
                    <button
                      key={opt.value}
                      className={`px-3 py-1.5 text-xs font-medium rounded border ${form.productivity === opt.value ? 'bg-slate-800 text-white border-slate-800' : 'bg-white text-slate-600 border-slate-200'}`}
                      onClick={() => setForm({ ...form, productivity: opt.value })}
                    >
                      {opt.label}
                    </button>
                  ))}
                </div>
              </div>
              <div className="flex gap-2 pt-2">
                <button
                  className="flex-1 px-3 py-1.5 text-sm font-medium bg-blue-500 text-white rounded hover:bg-blue-600 disabled:opacity-50"
                  onClick={handleSubmit}
                  disabled={saveMutation.isPending || !form.name.trim()}
                >
                  {saveMutation.isPending ? '保存中...' : '保存'}
                </button>
                {form.id && (
                  <button
                    className="px-3 py-1.5 text-xs font-medium text-red-500 hover:text-red-600"
                    onClick={() => form.id && handleDelete(form.id)}
                  >
                    删除
                  </button>
                )}
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}
