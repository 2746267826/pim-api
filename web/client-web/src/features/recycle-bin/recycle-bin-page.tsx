import { useEffect, useMemo, useRef, useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { RotateCcw, Trash2, RefreshCw } from 'lucide-react'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import {
  Button,
  Card,
  Chip,
  ConfirmDialog,
  EmptyState,
  InlineAlert,
  Input,
  PageHeader,
  Skeleton,
  StatusBadge,
  Switch,
} from '@/components/ui'
import {
  recycleBinApi,
  type RecycleBinBatchReceipt,
  type RecycleBinItem,
  type RecycleBinItemRef,
} from './api'

const DOMAINS = ['note', 'file', 'task', 'calendar'] as const
type Domain = (typeof DOMAINS)[number]
type Action = 'restore' | 'purge'

const DOMAIN_LABELS: Record<Domain, string> = {
  note: '笔记',
  file: '文件',
  task: '任务',
  calendar: '日历',
}

const KIND_LABELS: Record<string, string> = {
  note: '笔记',
  file: '文件',
  folder: '文件夹',
  task: '任务',
  'task-book': '任务本',
  event: '日程',
  calendar: '日历',
}

function itemKey(item: Pick<RecycleBinItem, 'domain' | 'id'>): string {
  return `${item.domain}:${item.id}`
}

function refOf(key: string, item?: RecycleBinItem): RecycleBinItemRef {
  const separator = key.indexOf(':')
  return {
    domain: key.slice(0, separator),
    id: key.slice(separator + 1),
    expectedDeletedAt: item?.deletedAt,
  }
}

function statusTone(status: string): 'ok' | 'warn' | 'crit' | 'neutral' {
  if (status === 'success') return 'ok'
  if (status === 'source_pending') return 'warn'
  if (status === 'failed') return 'crit'
  return 'neutral'
}

function statusLabel(status: string): string {
  if (status === 'success') return '成功'
  if (status === 'source_pending') return '来源待处理'
  if (status === 'failed') return '失败'
  return status
}

/** 全域回收站：四域分组、跨域多选、服务器回执与可重试的部分成功。 */
export function RecycleBinPage() {
  const qc = useQueryClient()
  const [domain, setDomain] = useState<'all' | Domain>('all')
  const [search, setSearch] = useState('')
  const [page, setPage] = useState(1)
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [restoreAsCopy, setRestoreAsCopy] = useState(false)
  const [purgeOpen, setPurgeOpen] = useState(false)
  const [pendingAction, setPendingAction] = useState<Action | null>(null)
  const [receipt, setReceipt] = useState<RecycleBinBatchReceipt | null>(null)
  const [unconfirmed, setUnconfirmed] = useState('')
  const knownItems = useRef(new Map<string, RecycleBinItem>())

  const list = useQuery({
    queryKey: ['recycle-bin', domain, search, page],
    queryFn: ({ signal }) => recycleBinApi.list({
      domain,
      search,
      page,
      pageSize: 50,
    }, signal),
    placeholderData: (previous) => previous,
  })

  const pageItems = list.data?.items ?? []
  const itemsByKey = useMemo(
    () => new Map(pageItems.map((item) => [itemKey(item), item])),
    [pageItems],
  )

  useEffect(() => {
    for (const item of pageItems) knownItems.current.set(itemKey(item), item)
  }, [pageItems])

  const selectedRefs = useMemo(
    () => [...selected].map((key) => refOf(key, itemsByKey.get(key) ?? knownItems.current.get(key))),
    [selected, itemsByKey],
  )
  const retryItems = useMemo(
    () => receipt?.items.filter((item) => item.status !== 'success') ?? [],
    [receipt],
  )

  function updateSelection(key: string, checked: boolean) {
    setSelected((previous) => {
      const next = new Set(previous)
      if (checked) next.add(key)
      else next.delete(key)
      return next
    })
  }

  async function execute(action: Action, items: RecycleBinItemRef[]) {
    if (!items.length || pendingAction) return
    setPendingAction(action)
    setUnconfirmed('')
    try {
      const next = action === 'restore'
        ? await recycleBinApi.restore(items, restoreAsCopy)
        : await recycleBinApi.purge(items)
      setReceipt(next)
      const retryable = new Set(
        next.items
          .filter((item) => item.status !== 'success')
          .map((item) => `${item.domain}:${item.id}`),
      )
      setSelected((previous) => new Set([...previous].filter((key) => retryable.has(key))))
      await qc.invalidateQueries({ queryKey: ['recycle-bin'] })
      if (next.succeeded > 0) {
        notifySuccess(action === 'restore'
          ? `已恢复 ${next.succeeded} 项`
          : `已永久删除 ${next.succeeded} 项`)
      }
      if (next.failed > 0) notifyError(`${next.failed} 项未完成，可按回执重试`)
    } catch (error) {
      // 没有服务器回执就绝不把任何一行在本地改成成功；请求可能已经在服务端生效。
      setUnconfirmed(error instanceof Error ? error.message : '网络或服务器未返回回执')
    } finally {
      setPendingAction(null)
      setPurgeOpen(false)
    }
  }

  function retryOne(domainValue: string, id: string) {
    const key = `${domainValue}:${id}`
    void execute(
      receipt?.action === 'purge' ? 'purge' : 'restore',
      [refOf(key, itemsByKey.get(key) ?? knownItems.current.get(key))],
    )
  }

  const allPageKeys = pageItems.map(itemKey)
  const allPageSelected = allPageKeys.length > 0 && allPageKeys.every((key) => selected.has(key))

  return (
    <div className="space-y-4">
      <PageHeader
        title="回收站"
        subtitle="统一查看笔记、文件、任务与日历；恢复或永久删除均以服务器逐项回执为准"
        actions={
          <Button variant="secondary" size="sm" loading={list.isFetching} onClick={() => void list.refetch()}>
            <RefreshCw className="size-4" aria-hidden /> 刷新
          </Button>
        }
      />

      <div className="flex flex-wrap items-center gap-2">
        <div className="flex flex-wrap gap-1.5" aria-label="回收站领域筛选">
          <Chip active={domain === 'all'} onClick={() => { setDomain('all'); setPage(1); setSelected(new Set()) }}>
            全部 {list.data?.totalCount ?? 0}
          </Chip>
          {DOMAINS.map((value) => (
            <Chip
              key={value}
              active={domain === value}
              onClick={() => { setDomain(value); setPage(1); setSelected(new Set()) }}
            >
              {DOMAIN_LABELS[value]} {list.data?.groups.find((group) => group.domain === value)?.totalCount ?? 0}
            </Chip>
          ))}
        </div>
        <Input
          className="ml-auto w-64"
          aria-label="搜索回收站"
          placeholder="搜索标题、文件名或原属…"
          value={search}
          onChange={(event) => { setSearch(event.target.value); setPage(1); setSelected(new Set()) }}
        />
      </div>

      <div className="flex flex-wrap items-center gap-2 rounded-card border border-border bg-bg px-3 py-2 shadow-card">
        <label className="flex items-center gap-2 text-xs text-text-2" htmlFor="restore-as-copy">
          <Switch id="restore-as-copy" checked={restoreAsCopy} onCheckedChange={setRestoreAsCopy} />
          日程/任务冲突时恢复为副本
        </label>
        <span className="tnum ml-auto text-xs text-text-3">已选 {selected.size} 项</span>
        <Button
          variant="secondary"
          size="sm"
          disabled={!selectedRefs.length || pendingAction != null}
          loading={pendingAction === 'restore'}
          onClick={() => void execute('restore', selectedRefs)}
        >
          <RotateCcw className="size-3.5" aria-hidden /> 恢复选中
        </Button>
        <Button
          variant="danger-soft"
          size="sm"
          disabled={!selectedRefs.length || pendingAction != null}
          loading={pendingAction === 'purge'}
          onClick={() => setPurgeOpen(true)}
        >
          <Trash2 className="size-3.5" aria-hidden /> 永久删除选中
        </Button>
      </div>

      {unconfirmed && (
        <InlineAlert tone="crit" title="结果未确认">
          服务器没有返回批次回执：{unconfirmed}。请求可能已经生效，请刷新后在列表中核对；本页不会把任何项目标记为成功。
        </InlineAlert>
      )}

      {receipt && (
        <Card className="p-4" aria-label="服务器批次回执">
          <div className="flex flex-wrap items-center gap-2">
            <h2 className="text-sm font-semibold text-text-1">服务器批次回执</h2>
            <StatusBadge tone={receipt.failed > 0 ? 'crit' : receipt.sourcePending > 0 ? 'warn' : 'ok'} dot={false}>
              成功 {receipt.succeeded} · 失败 {receipt.failed} · 来源待处理 {receipt.sourcePending}
            </StatusBadge>
            <span className="mono ml-auto text-[10px] text-text-4">{receipt.receiptId}</span>
          </div>
          <div className="mt-3 divide-y divide-divider">
            {receipt.items.map((item) => (
              <div key={`${item.index}:${item.domain}:${item.id}`} className="flex flex-wrap items-start gap-2 py-2">
                <StatusBadge tone={statusTone(item.status)} dot={false}>{statusLabel(item.status)}</StatusBadge>
                <div className="min-w-0 flex-1">
                  <div className="truncate text-[13px] text-text-1">{item.title}</div>
                  {item.message && <div className="mt-0.5 text-xs text-text-3">{item.message}</div>}
                </div>
                {item.status !== 'success' && (
                  <Button variant="ghost" size="sm" onClick={() => retryOne(item.domain, item.id)}>
                    单项重试
                  </Button>
                )}
              </div>
            ))}
          </div>
          {retryItems.length > 0 && (
            <div className="mt-3 flex justify-end">
              <Button
                variant="secondary"
                size="sm"
                onClick={() => void execute(
                  receipt.action === 'purge' ? 'purge' : 'restore',
                  retryItems.map((item) => {
                    const key = `${item.domain}:${item.id}`
                    return refOf(key, itemsByKey.get(key) ?? knownItems.current.get(key))
                  }),
                )}
              >
                重试未成功项（{retryItems.length}）
              </Button>
            </div>
          )}
        </Card>
      )}

      {list.isPending ? (
        <div className="space-y-2">{Array.from({ length: 4 }, (_, index) => <Skeleton key={index} className="h-24" />)}</div>
      ) : list.isError ? (
        <InlineAlert tone="crit" title="回收站读取失败">
          {list.error instanceof Error ? list.error.message : '请求失败，请刷新核对'}
        </InlineAlert>
      ) : pageItems.length === 0 ? (
        <Card><EmptyState title="回收站是空的" description="删除的笔记、文件、任务和日历会出现在这里。" /></Card>
      ) : (
        <div className="space-y-4">
          {DOMAINS.map((value) => {
            const items = pageItems.filter((item) => item.domain === value)
            if (!items.length) return null
            const total = list.data?.groups.find((group) => group.domain === value)?.totalCount ?? items.length
            return (
              <Card key={value} className="overflow-x-auto">
                <div className="flex items-center gap-2 border-b border-divider px-3 py-2">
                  <h2 className="text-sm font-semibold text-text-1">{DOMAIN_LABELS[value]}</h2>
                  <span className="tnum text-xs text-text-4">{total} 项</span>
                  <label className="ml-auto flex items-center gap-1.5 text-xs text-text-3">
                    <input
                      type="checkbox"
                      className="size-3.5 accent-primary"
                      checked={items.every((item) => selected.has(itemKey(item)))}
                      onChange={(event) => {
                        setSelected((previous) => {
                          const next = new Set(previous)
                          for (const item of items) {
                            if (event.target.checked) next.add(itemKey(item))
                            else next.delete(itemKey(item))
                          }
                          return next
                        })
                      }}
                    />
                    选择本页{value === 'file' ? '文件' : DOMAIN_LABELS[value]}
                  </label>
                </div>
                <table className="w-full text-[13px]">
                  <thead>
                    <tr className="bg-surface text-left text-xs text-text-3">
                      <th className="w-9 px-3 py-2">
                        <input
                          type="checkbox"
                          aria-label="全选当前页"
                          className="size-3.5 accent-primary"
                          checked={allPageSelected}
                          onChange={(event) => setSelected(event.target.checked ? new Set(allPageKeys) : new Set())}
                        />
                      </th>
                      <th className="px-2 py-2 font-medium">名称</th>
                      <th className="px-2 py-2 font-medium">来源 / 原属</th>
                      <th className="px-2 py-2 font-medium">删除时间</th>
                      <th className="px-2 py-2 font-medium">状态</th>
                    </tr>
                  </thead>
                  <tbody className="divide-y divide-divider">
                    {items.map((item) => {
                      const key = itemKey(item)
                      return (
                        <tr key={key} className="hover:bg-surface">
                          <td className="px-3 py-2">
                            <input
                              type="checkbox"
                              aria-label={`选择 ${item.title}`}
                              className="size-3.5 accent-primary"
                              checked={selected.has(key)}
                              onChange={(event) => updateSelection(key, event.target.checked)}
                            />
                          </td>
                          <td className="px-2 py-2">
                            <div className="flex flex-wrap items-center gap-1.5">
                              <span className="text-text-1">{item.title}</span>
                              <StatusBadge tone="neutral" dot={false}>
                                {KIND_LABELS[item.kind] ?? item.kind}
                              </StatusBadge>
                            </div>
                          </td>
                          <td className="px-2 py-2 text-text-3">
                            {item.location || sourceLabel(item.source)}
                          </td>
                          <td className="tnum px-2 py-2 text-text-3">{formatTime(item.deletedAt)}</td>
                          <td className="px-2 py-2">
                            {item.sourcePending ? (
                              <StatusBadge tone="warn" dot={false}>来源待处理</StatusBadge>
                            ) : (
                              <StatusBadge tone="neutral" dot={false}>可恢复</StatusBadge>
                            )}
                            {item.sourcePending && item.sourceMessage && (
                              <div className="mt-1 max-w-[260px] text-[11px] text-text-4">{item.sourceMessage}</div>
                            )}
                          </td>
                        </tr>
                      )
                    })}
                  </tbody>
                </table>
              </Card>
            )
          })}
        </div>
      )}

      {(list.data?.totalPages ?? 0) > 1 && (
        <div className="flex items-center justify-end gap-2 text-[13px] text-text-3">
          <Button variant="secondary" size="sm" disabled={page <= 1} onClick={() => setPage((value) => value - 1)}>
            上一页
          </Button>
          <span className="tnum">{page} / {list.data!.totalPages}</span>
          <Button
            variant="secondary"
            size="sm"
            disabled={page >= list.data!.totalPages}
            onClick={() => setPage((value) => value + 1)}
          >
            下一页
          </Button>
        </div>
      )}

      <ConfirmDialog
        open={purgeOpen}
        onOpenChange={setPurgeOpen}
        tone="danger"
        title="永久删除所选项目"
        description="此操作不可撤销。内置文件与服务器内容、笔记原稿/材料/转写/派生数据将永久删除；外部云盘来源只会返回“来源待处理”。"
        requireText="永久删除"
        confirmLabel="永久删除"
        loading={pendingAction === 'purge'}
        onConfirm={() => execute('purge', selectedRefs)}
      />
    </div>
  )
}

function sourceLabel(source: string): string {
  if (source === 'builtin') return '内置存储'
  if (source === 'onedrive') return 'OneDrive'
  if (source === 'google') return 'Google Drive'
  return source
}
