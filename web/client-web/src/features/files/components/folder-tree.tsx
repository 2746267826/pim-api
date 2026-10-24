import { useEffect, useState } from 'react'
import { ChevronDown, ChevronRight, Folder, FolderOpen, RefreshCw } from 'lucide-react'
import { filesApi } from '../api'
import type { FileItem } from '../types'
import { Skeleton } from '@/components/ui'
import { cn } from '@/lib/utils'

/*
 * 文件夹树（02 §files：懒加载、逐文件夹分页、20 页硬上限并明示截断、错误重试）。
 * 展开/收起不切目录；单击/双击进入目录。
 */

const PAGE_LIMIT = 20

interface TreeState {
  items: FileItem[]
  loading: boolean
  error: string | null
  truncated: boolean
  loaded: boolean
}

export function FolderTree({
  providerId,
  currentPath,
  onNavigate,
}: {
  /** 目录树必须绑定到具体账号：不同网盘的 path 空间不能混用 */
  providerId: string
  currentPath: string
  onNavigate: (path: string) => void
}) {
  const [expanded, setExpanded] = useState<Set<string>>(() => new Set(['/']))
  const [byPath, setByPath] = useState<Record<string, TreeState>>({})

  useEffect(() => {
    // 切换账号时目录树必须整棵重载，否则会把上一个网盘的目录当成这个网盘的
    setByPath({})
    setExpanded(new Set(['/']))
    void load('/')
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [providerId])

  /** 逐文件夹顺序拉取（硬上限 20 页/文件夹） */
  async function load(path: string) {
    setByPath((prev) => ({
      ...prev,
      [path]: { items: prev[path]?.items ?? [], loading: true, error: null, truncated: false, loaded: true },
    }))
    try {
      let page = 1
      const all: FileItem[] = []
      let truncated = false
      for (;;) {
        const res = await filesApi.listItems({ path, providerId, page, pageSize: 100, type: 'folder' })
        all.push(...res.result.items)
        if (page >= res.result.totalPages) break
        if (page >= PAGE_LIMIT) {
          truncated = true
          break
        }
        page++
      }
      setByPath((prev) => ({
        ...prev,
        [path]: { items: all, loading: false, error: null, truncated, loaded: true },
      }))
    } catch (err) {
      setByPath((prev) => ({
        ...prev,
        [path]: {
          items: prev[path]?.items ?? [],
          loading: false,
          error: err instanceof Error ? err.message : '加载失败',
          truncated: false,
          loaded: true,
        },
      }))
    }
  }

  function toggle(path: string) {
    setExpanded((prev) => {
      const next = new Set(prev)
      if (next.has(path)) next.delete(path)
      else {
        next.add(path)
        if (!byPath[path]?.loaded) void load(path)
      }
      return next
    })
  }

  return (
    <div className="space-y-0.5 text-[13px]">
      <TreeRow
        path="/"
        name="我的文件"
        depth={0}
        isRoot
        expanded={expanded.has('/')}
        active={currentPath === '/'}
        state={byPath['/']}
        onToggle={() => toggle('/')}
        onNavigate={onNavigate}
        onReload={() => void load('/')}
        renderChildren={
          expanded.has('/') ? (
            <TreeChildren
              parentPath="/"
              parentState={byPath['/']}
              expanded={expanded}
              currentPath={currentPath}
              byPath={byPath}
              onToggle={toggle}
              onNavigate={onNavigate}
              onReload={load}
              depth={1}
            />
          ) : null
        }
      />
    </div>
  )
}

function TreeChildren({
  parentPath,
  parentState,
  expanded,
  currentPath,
  byPath,
  onToggle,
  onNavigate,
  onReload,
  depth,
}: {
  parentPath: string
  parentState: TreeState | undefined
  expanded: Set<string>
  currentPath: string
  byPath: Record<string, TreeState>
  onToggle: (p: string) => void
  onNavigate: (p: string) => void
  onReload: (p: string) => Promise<void>
  depth: number
}) {
  if (!parentState || (parentState.loading && parentState.items.length === 0)) {
    return (
      <div className="py-1" style={{ paddingLeft: depth * 14 + 20 }}>
        <Skeleton className="h-4 w-24" />
      </div>
    )
  }
  if (parentState.error) {
    return (
      <div className="flex items-center gap-1 py-1 text-[11px] text-crit" style={{ paddingLeft: depth * 14 + 20 }}>
        {parentState.error}
        <button type="button" onClick={() => void onReload(parentPath)} className="underline outline-none">重试</button>
      </div>
    )
  }
  return (
    <>
      {parentState.items.map((item) => {
        const childPath = item.path
        return (
          <TreeRow
            key={item.id}
            path={childPath}
            name={item.name}
            depth={depth}
            expanded={expanded.has(childPath)}
            active={currentPath === childPath}
            state={byPath[childPath]}
            onToggle={() => onToggle(childPath)}
            onNavigate={onNavigate}
            onReload={() => void onReload(childPath)}
            renderChildren={
              expanded.has(childPath) ? (
                <TreeChildren
                  parentPath={childPath}
                  parentState={byPath[childPath]}
                  expanded={expanded}
                  currentPath={currentPath}
                  byPath={byPath}
                  onToggle={onToggle}
                  onNavigate={onNavigate}
                  onReload={onReload}
                  depth={depth + 1}
                />
              ) : null
            }
          />
        )
      })}
      {parentState.truncated && (
        <p className="py-1 text-[10px] text-warn" style={{ paddingLeft: depth * 14 + 20 }}>
          子文件夹过多，仅显示前 {PAGE_LIMIT}00 项
        </p>
      )}
    </>
  )
}

function TreeRow({
  path,
  name,
  depth,
  isRoot,
  expanded,
  active,
  state,
  onToggle,
  onNavigate,
  onReload,
  renderChildren,
}: {
  path: string
  name: string
  depth: number
  isRoot?: boolean
  expanded: boolean
  active: boolean
  state: TreeState | undefined
  onToggle: () => void
  onNavigate: (p: string) => void
  onReload: () => void
  renderChildren: React.ReactNode
}) {
  return (
    <div>
      <div
        className={cn(
          'group flex h-7 items-center gap-0.5 rounded-ctl pr-1 transition-colors',
          active ? 'bg-primary-soft text-primary-hover' : 'text-text-2 hover:bg-surface',
        )}
        style={{ paddingLeft: depth * 14 }}
      >
        <button
          type="button"
          aria-label={expanded ? '收起' : '展开'}
          onClick={onToggle}
          className="grid size-5 shrink-0 place-items-center rounded-[4px] text-text-3 outline-none hover:bg-surface-2"
        >
          {expanded ? <ChevronDown className="size-3.5" aria-hidden /> : <ChevronRight className="size-3.5" aria-hidden />}
        </button>
        {expanded && isRoot ? (
          <FolderOpen className="size-3.5 shrink-0 text-text-3" aria-hidden />
        ) : (
          <Folder className="size-3.5 shrink-0 text-text-3" aria-hidden />
        )}
        <button
          type="button"
          onClick={() => onNavigate(path)}
          className="min-w-0 flex-1 truncate pl-1 text-left outline-none"
          title={path}
        >
          {name}
        </button>
        {state?.error && (
          <button type="button" aria-label="重试" onClick={onReload} className="shrink-0 text-crit outline-none">
            <RefreshCw className="size-3" aria-hidden />
          </button>
        )}
      </div>
      {renderChildren}
    </div>
  )
}
