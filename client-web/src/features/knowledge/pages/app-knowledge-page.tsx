import { useMemo, useState } from 'react'
import { Link, useLocation } from 'react-router'
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query'
import { ChevronDown, ChevronRight, Plus, Trash2 } from 'lucide-react'
import { apiDelete, apiGet, apiPost } from '@/api/client'
import { notifyError, notifySuccess } from '@/lib/notify'
import { Button, Card, CardTitle, Drawer, DrawerBody, DrawerContent, DrawerHeader, EmptyState, Input, Label, PageHeader, Segmented, Select, Skeleton, StatusBadge } from '@/components/ui'
import { cn } from '@/lib/utils'

/* ── 应用知识库（/app-knowledge-base） ─────────────────────── */

interface KnowledgeApp {
  id: string
  processName: string
  displayName: string
  categoryPath: string | null
  productivity: string | null
  source: string
  icon: string | null
  contextCount: number
  pendingContextCount: number
}

interface KnowledgeContext {
  id: string
  processName: string
  patternType: string
  patternValue: string
  targetCategoryName: string | null
  scopeSummary: string
  source: string
  enabled: boolean
  affectedRecordCount: number
}

const PRODUCTIVITY_META: Record<string, { label: string; tone: 'ok' | 'warn' | 'neutral' }> = {
  productive: { label: '高效率', tone: 'ok' },
  neutral: { label: '中性', tone: 'neutral' },
  distracting: { label: '分散精力', tone: 'warn' },
}

export function AppKnowledgeBasePage() {
  const location = useLocation()
  const qc = useQueryClient()
  const [search, setSearch] = useState('')
  const [addOpen, setAddOpen] = useState(false)
  const [contextApp, setContextApp] = useState<KnowledgeApp | null>(null)
  const [editingApp, setEditingApp] = useState<KnowledgeApp | null>(null)

  const apps = useQuery({
    queryKey: ['knowledge', 'apps', search],
    queryFn: () => apiGet<KnowledgeApp[]>(`/api/v1/pc/app-knowledge/apps${search ? `?search=${encodeURIComponent(search)}` : ''}`),
  })

  const invalidate = () => void qc.invalidateQueries({ queryKey: ['knowledge'] })

  return (
    <div>
      <PageHeader
        title="应用知识库"
        subtitle="应用/域名知识与分类树（替代旧的 PC 分类规则页）"
        actions={
          <>
            <Segmented
              value={location.pathname.endsWith('/categories') ? 'categories' : 'apps'}
              onValueChange={() => {}}
              options={[
                { value: 'apps', label: <Link to="/app-knowledge-base">应用</Link> },
                { value: 'categories', label: <Link to="/app-knowledge-base/categories">分类</Link> },
              ]}
            />
            <Button variant="primary" size="sm" onClick={() => setAddOpen((o) => !o)}>
              <Plus className="size-4" aria-hidden /> 添加应用
            </Button>
          </>
        }
      />

      {/* 内联新增/编辑表单 */}
      {addOpen && <AddAppForm onDone={() => { setAddOpen(false); invalidate() }} />}
      {editingApp && (
        <AddAppForm
          editing={editingApp}
          onDone={() => {
            setEditingApp(null)
            invalidate()
          }}
        />
      )}

      <div className="mb-3 flex items-center gap-2">
        <Input className="w-56" placeholder="搜索进程名/显示名…" value={search} onChange={(e) => setSearch(e.target.value)} />
        <span className="tnum ml-auto text-xs text-text-4">{apps.data?.length ?? 0} 个应用</span>
      </div>

      <Card className="overflow-x-auto">
        <table className="w-full text-[13px]">
          <thead>
            <tr className="bg-surface text-left text-xs text-text-3">
              <th className="px-3 py-2 font-medium">应用</th>
              <th className="px-3 py-2 font-medium">分类路径</th>
              <th className="px-3 py-2 font-medium">效率</th>
              <th className="px-3 py-2 text-right font-medium">上下文</th>
              <th className="px-3 py-2 font-medium">来源</th>
              <th className="px-3 py-2" />
            </tr>
          </thead>
          <tbody className="divide-y divide-divider">
            {apps.isLoading ? (
              <tr><td colSpan={6}><Skeleton className="m-3 h-24" /></td></tr>
            ) : (apps.data?.length ?? 0) === 0 ? (
              <tr><td colSpan={6}><EmptyState size="sm" title="没有匹配的应用" description="点击「添加应用」录入，或等待守护进程学习。" /></td></tr>
            ) : (
              apps.data!.map((a) => {
                const meta = a.productivity ? PRODUCTIVITY_META[a.productivity] : null
                const builtin = a.source === 'builtin'
                return (
                  <tr key={a.id} className="transition-colors hover:bg-surface">
                    <td className="px-3 py-2">
                      <button type="button" onClick={() => setContextApp(a)} className="flex items-center gap-2 text-left outline-none">
                        <span className="grid size-6 shrink-0 place-items-center rounded-ctl bg-surface-2 text-xs">{a.icon ?? '▦'}</span>
                        <span className="min-w-0">
                          <span className="block truncate text-text-1">{a.displayName}</span>
                          <span className="mono block truncate text-[10px] text-text-4">{a.processName}</span>
                        </span>
                      </button>
                    </td>
                    <td className="px-3 py-2 text-text-3">{a.categoryPath ?? '—'}</td>
                    <td className="px-3 py-2">{meta ? <StatusBadge tone={meta.tone} dot={false}>{meta.label}</StatusBadge> : '—'}</td>
                    <td className="tnum px-3 py-2 text-right text-text-3">
                      {a.contextCount}
                      {a.pendingContextCount > 0 && <span className="ml-1 text-[11px] text-warn">（待确认 {a.pendingContextCount}）</span>}
                    </td>
                    <td className="px-3 py-2">
                      <StatusBadge tone={builtin ? 'info' : 'ok'} dot={false}>{builtin ? '内置' : a.source === 'learned' ? '学习' : '自定义'}</StatusBadge>
                    </td>
                    <td className="px-3 py-2">
                      <div className="flex items-center justify-end gap-0.5">
                        <Button variant="ghost" size="sm" onClick={() => setEditingApp(a)}>
                          编辑
                        </Button>
                        {builtin ? (
                          <span className="text-[11px] text-text-4">内置不可删</span>
                        ) : (
                          <button
                            type="button"
                            aria-label={`删除 ${a.displayName}`}
                            className="rounded-ctl p-1 text-text-4 outline-none hover:text-crit"
                            onClick={async () => {
                              if (!window.confirm(`删除「${a.displayName}」的签名记录？`)) return
                              try {
                                await apiDelete(`/api/v1/pc/app-signatures/${a.id}`)
                                notifySuccess('已删除')
                                invalidate()
                              } catch (err) {
                                notifyError(err instanceof Error ? err.message : '删除失败')
                              }
                            }}
                          >
                            <Trash2 className="size-3.5" aria-hidden />
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                )
              })
            )}
          </tbody>
        </table>
      </Card>

      {/* 上下文侧板 */}
      {contextApp && <ContextPanel app={contextApp} onClose={() => setContextApp(null)} />}
    </div>
  )
}

/** 应用签名表单：新建（editing=null）或编辑已有条目 */
function AddAppForm({ onDone, editing }: { onDone: () => void; editing?: KnowledgeApp | null }) {
  const qc = useQueryClient()
  const isEdit = editing != null
  const [form, setForm] = useState({
    processName: editing?.processName ?? '',
    displayName: editing?.displayName ?? '',
    categoryPath: editing?.categoryPath ?? '',
    productivity: editing?.productivity ?? 'neutral',
    description: '',
  })

  return (
    <Card className="mb-3 p-4">
      <CardTitle>{isEdit ? `编辑应用：${editing!.displayName}` : '添加应用'}</CardTitle>
      <div className="mt-3 grid grid-cols-2 gap-3 md:grid-cols-3 xl:grid-cols-5">
        <div><Label>进程名</Label><Input className="h-8" value={form.processName} onChange={(e) => setForm((f) => ({ ...f, processName: e.target.value }))} /></div>
        <div><Label>显示名</Label><Input className="h-8" value={form.displayName} onChange={(e) => setForm((f) => ({ ...f, displayName: e.target.value }))} /></div>
        <div><Label>分类路径</Label><Input className="h-8" value={form.categoryPath} onChange={(e) => setForm((f) => ({ ...f, categoryPath: e.target.value }))} /></div>
        <div>
          <Label>效率</Label>
          <Select className="h-8" value={form.productivity} onValueChange={(v) => setForm((f) => ({ ...f, productivity: v }))} options={[
            { value: 'productive', label: '高效率' }, { value: 'neutral', label: '中性' }, { value: 'distracting', label: '分散精力' },
          ]} />
        </div>
        <div><Label>描述</Label><Input className="h-8" value={form.description} onChange={(e) => setForm((f) => ({ ...f, description: e.target.value }))} /></div>
      </div>
      <div className="mt-3 flex gap-2">
        <Button
          variant="primary"
          size="sm"
          disabled={!form.processName.trim() || !form.displayName.trim()}
          onClick={async () => {
            try {
              // 新增与更新同一端点（按进程名 upsert；编辑时以原进程名为键）
              /*
               * 不发送 icon：契约 SaveAppSignatureRequest 没有该字段，服务端
               * SaveAsync 也从不写 Icon（响应里的 icon 来自在线查询）。
               * 送上来的 emoji 永远无法持久化，所以表单不再提供该输入；
               * 列表仍展示响应里的 icon。
               */
              await apiPost('/api/v1/pc/app-signatures/', {
                ...form,
                categoryPath: form.categoryPath || null,
                description: form.description || null,
              })
              notifySuccess(isEdit ? '已保存修改' : '已添加')
              qc.invalidateQueries({ queryKey: ['knowledge'] })
              onDone()
            } catch (err) {
              notifyError(err instanceof Error ? err.message : isEdit ? '保存失败' : '添加失败')
            }
          }}
        >
          {isEdit ? '保存修改' : '保存'}
        </Button>
        <Button variant="secondary" size="sm" onClick={onDone}>取消</Button>
        {isEdit && (
          <span className="self-center text-[11px] text-text-4">
            内置条目可修正显示名/分类/效率等展示信息
          </span>
        )}
      </div>
    </Card>
  )
}

function ContextPanel({ app, onClose }: { app: KnowledgeApp; onClose: () => void }) {
  const qc = useQueryClient()
  const contexts = useQuery({
    queryKey: ['knowledge', 'contexts', app.id],
    queryFn: () => apiGet<KnowledgeContext[]>(`/api/v1/pc/app-knowledge/apps/${app.id}/contexts`),
  })

  return (
    <Drawer open onOpenChange={(o) => !o && onClose()}>
      <DrawerContent side="right">
        <DrawerHeader title={app.displayName} description={`${app.processName} · ${app.contextCount} 条上下文`} />
        <DrawerBody className="space-y-2">
          {contexts.isLoading ? (
            <Skeleton className="h-24" />
          ) : (contexts.data?.length ?? 0) === 0 ? (
            <EmptyState size="sm" title="暂无上下文知识" description="分类建议被采纳后会沉淀到这里。" />
          ) : (
            contexts.data!.map((c) => (
              <div key={c.id} className="rounded-ctl border border-border px-3 py-2">
                <div className="flex items-center gap-2">
                  <StatusBadge tone="info" dot={false}>{c.patternType}</StatusBadge>
                  <span className="mono min-w-0 flex-1 truncate text-xs text-text-2">{c.patternValue}</span>
                  <button
                    type="button"
                    aria-label="删除上下文"
                    className="rounded-ctl p-0.5 text-text-4 outline-none hover:text-crit"
                    onClick={async () => {
                      await apiDelete(`/api/v1/pc/app-knowledge/contexts/${c.id}`)
                      notifySuccess('已删除')
                      qc.invalidateQueries({ queryKey: ['knowledge'] })
                    }}
                  >
                    <Trash2 className="size-3.5" aria-hidden />
                  </button>
                </div>
                <div className="mt-1 text-[11px] text-text-4">
                  → {c.targetCategoryName ?? '未指定分类'} · 影响 {c.affectedRecordCount} 条 · {c.enabled ? '已启用' : '已停用'}
                </div>
              </div>
            ))
          )}
        </DrawerBody>
      </DrawerContent>
    </Drawer>
  )
}

/* ── 分类树（/app-knowledge-base/categories） ───────────────── */

interface CategoryNode {
  id: string
  parentId: string | null
  name: string
  color: string
  icon: string | null
  productivity: 'productive' | 'neutral' | 'distracting'
  sortOrder: number
  isBuiltin: boolean
  children: CategoryNode[]
}

const COLOR_SWATCHES = ['#2563EB', '#16A34A', '#F59E0B', '#8B5CF6', '#EC4899', '#14B8A6', '#F97316', '#64748B']
const EMOJI_CHOICES = ['💻', '📚', '🎬', '💬', '📄', '🎮', '⚙️', '📦', '🌐', '✍️']

export function CategoryTreePage() {
  const qc = useQueryClient()
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const tree = useQuery({ queryKey: ['knowledge', 'tree'], queryFn: () => apiGet<CategoryNode[]>('/api/v1/pc/categories/tree'), staleTime: 60_000 })

  const flat = useMemo(() => flatten(tree.data ?? []), [tree.data])
  const selected = flat.find((n) => n.id === selectedId) ?? null

  const invalidate = () => void qc.invalidateQueries({ queryKey: ['knowledge'] })

  return (
    <div>
      <PageHeader
        title="分类树"
        subtitle="层级分类、颜色与生产力属性（应用/域名标注的目标分类）"
        actions={
          <>
            <Segmented
              value="categories"
              onValueChange={() => {}}
              options={[
                { value: 'apps', label: <Link to="/app-knowledge-base">应用</Link> },
                { value: 'categories', label: <Link to="/app-knowledge-base/categories">分类</Link> },
              ]}
            />
            <Button
              variant="secondary"
              size="sm"
              onClick={async () => {
                await apiPost('/api/v1/pc/categories/seed', {})
                notifySuccess('已初始化默认分类')
                invalidate()
              }}
            >
              初始化默认
            </Button>
            <Button variant="primary" size="sm" onClick={() => setSelectedId('new')}>
              <Plus className="size-4" aria-hidden /> 添加根分类
            </Button>
          </>
        }
      />

      <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_360px]">
        <Card className="p-3">
          {tree.isLoading ? (
            <Skeleton className="h-40" />
          ) : (tree.data?.length ?? 0) === 0 ? (
            <EmptyState title="还没有分类" description="点击「初始化默认」载入种子分类，或手动添加。" />
          ) : (
            <div className="space-y-0.5">
              {tree.data!.map((n) => (
                <TreeNode key={n.id} node={n} depth={0} selectedId={selectedId} onSelect={setSelectedId} />
              ))}
            </div>
          )}
        </Card>

        {/* 编辑面板：桌面内联 */}
        <Card className="h-fit p-4">
          {selectedId === 'new' ? (
            <CategoryEditor node={null} parentId={null} onDone={() => { setSelectedId(null); invalidate() }} />
          ) : selected ? (
            <CategoryEditor node={selected} parentId={selected.parentId} onDone={() => { setSelectedId(null); invalidate() }} />
          ) : (
            <EmptyState size="sm" title="选择左侧分类进行编辑" description="或点击「添加根分类」。" />
          )}
        </Card>
      </div>
    </div>
  )
}

function flatten(nodes: CategoryNode[], out: CategoryNode[] = []): CategoryNode[] {
  for (const n of nodes) {
    out.push(n)
    flatten(n.children ?? [], out)
  }
  return out
}

function TreeNode({ node, depth, selectedId, onSelect }: { node: CategoryNode; depth: number; selectedId: string | null; onSelect: (id: string) => void }) {
  const [open, setOpen] = useState(depth < 1)
  const hasChildren = (node.children?.length ?? 0) > 0
  const meta = PRODUCTIVITY_META[node.productivity]

  return (
    <div>
      <div
        className={cn(
          'flex h-8 items-center gap-1 rounded-ctl pr-2 transition-colors',
          selectedId === node.id ? 'bg-primary-soft text-primary-hover' : 'text-text-2 hover:bg-surface',
        )}
        style={{ paddingLeft: depth * 16 + 4 }}
      >
        <button
          type="button"
          aria-label={open ? '收起' : '展开'}
          onClick={() => setOpen((o) => !o)}
          className={cn('grid size-5 shrink-0 place-items-center rounded-[4px] outline-none', hasChildren ? 'text-text-3 hover:bg-surface-2' : 'invisible')}
        >
          {open ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
        </button>
        <button type="button" onClick={() => onSelect(node.id)} className="flex min-w-0 flex-1 items-center gap-2 text-left outline-none">
          <span className="size-2.5 shrink-0 rounded-[3px]" style={{ backgroundColor: node.color }} aria-hidden />
          {node.icon && <span className="text-xs">{node.icon}</span>}
          <span className="truncate text-[13px]">{node.name}</span>
          <StatusBadge tone={meta.tone} dot={false} className="ml-1 shrink-0">{meta.label}</StatusBadge>
          {node.isBuiltin && <span className="shrink-0 text-[10px] text-text-4">内置</span>}
        </button>
      </div>
      {open && hasChildren && (
        <div>
          {node.children.map((c) => (
            <TreeNode key={c.id} node={c} depth={depth + 1} selectedId={selectedId} onSelect={onSelect} />
          ))}
        </div>
      )}
    </div>
  )
}

function CategoryEditor({ node, parentId, onDone }: { node: CategoryNode | null; parentId: string | null; onDone: () => void }) {
  const qc = useQueryClient()
  const [name, setName] = useState(node?.name ?? '')
  const [color, setColor] = useState(node?.color ?? COLOR_SWATCHES[0]!)
  const [icon, setIcon] = useState(node?.icon ?? '')
  const [productivity, setProductivity] = useState<'productive' | 'neutral' | 'distracting'>(node?.productivity ?? 'neutral')
  const [error, setError] = useState<string | null>(null)

  const save = useMutation({
    mutationFn: () =>
      apiPost<CategoryNode>('/api/v1/pc/categories/', {
        id: node?.id,
        parentId: node?.id ? node.parentId : parentId,
        name: name.trim(),
        color,
        icon: icon || null,
        productivity,
      }),
    onSuccess: () => {
      notifySuccess(node ? '已保存' : '已创建')
      qc.invalidateQueries({ queryKey: ['knowledge'] })
      onDone()
    },
    onError: (err) => setError(err instanceof Error ? err.message : '保存失败'),
  })

  const addChild = useMutation({
    mutationFn: () => apiPost<CategoryNode>('/api/v1/pc/categories/', { parentId: node?.id ?? null, name: '新子分类', color, productivity: 'neutral' }),
    onSuccess: () => {
      notifySuccess('已添加子分类')
      qc.invalidateQueries({ queryKey: ['knowledge'] })
    },
    onError: (err) => setError(err instanceof Error ? err.message : '添加失败'),
  })

  return (
    <div className="space-y-3">
      <CardTitle>{node ? '编辑分类' : '新建分类'}</CardTitle>
      {error && <div className="rounded-ctl border border-crit-border bg-crit-soft px-3 py-2 text-[13px] text-crit">{error}</div>}
      <div>
        <Label htmlFor="cat-name">名称</Label>
        <Input id="cat-name" value={name} onChange={(e) => setName(e.target.value)} />
      </div>
      <div>
        <Label>图标（emoji）</Label>
        <div className="flex flex-wrap gap-1">
          {EMOJI_CHOICES.map((e) => (
            <button
              key={e}
              type="button"
              onClick={() => setIcon(e === icon ? '' : e)}
              className={cn('grid size-7 place-items-center rounded-ctl border text-sm outline-none', icon === e ? 'border-primary bg-primary-soft' : 'border-border hover:border-border-strong')}
            >
              {e}
            </button>
          ))}
        </div>
      </div>
      <div>
        <Label>颜色</Label>
        <div className="flex flex-wrap gap-1.5">
          {COLOR_SWATCHES.map((c) => (
            <button
              key={c}
              type="button"
              aria-label={`选择颜色 ${c}`}
              onClick={() => setColor(c)}
              className={cn('size-6 rounded-full border-2 outline-none transition-transform', color === c ? 'scale-110 border-text-1' : 'border-transparent')}
              style={{ backgroundColor: c }}
            />
          ))}
        </div>
      </div>
      <div>
        <Label>生产力属性</Label>
        <Segmented
          value={productivity}
          onValueChange={(v) => setProductivity(v)}
          options={[
            { value: 'productive', label: '高效率' },
            { value: 'neutral', label: '中性' },
            { value: 'distracting', label: '分散精力' },
          ]}
        />
      </div>
      <div className="flex flex-wrap gap-2 pt-1">
        <Button variant="primary" size="sm" disabled={!name.trim()} loading={save.isPending} onClick={() => save.mutate()}>
          保存
        </Button>
        {node && !node.isBuiltin && (
          <>
            <Button variant="secondary" size="sm" loading={addChild.isPending} onClick={() => addChild.mutate()}>
              <Plus className="size-3.5" aria-hidden /> 添加子分类
            </Button>
            <Button
              variant="danger-soft"
              size="sm"
              onClick={async () => {
                if (!window.confirm(`删除分类「${node.name}」？其子分类与应用归属会受影响。`)) return
                try {
                  await apiDelete(`/api/v1/pc/categories/${node.id}`)
                  notifySuccess('已删除')
                  qc.invalidateQueries({ queryKey: ['knowledge'] })
                  onDone()
                } catch (err) {
                  setError(err instanceof Error ? err.message : '删除失败')
                }
              }}
            >
              <Trash2 className="size-3.5" aria-hidden /> 删除
            </Button>
          </>
        )}
        {node?.isBuiltin && <span className="self-center text-[11px] text-text-4">内置分类不可删除</span>}
      </div>
      <p className="text-[11px] text-text-4">分类树的改动会影响标注队列与上下文确认的建议目标。</p>
    </div>
  )
}
