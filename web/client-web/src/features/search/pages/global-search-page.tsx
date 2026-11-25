import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react'
import { Link, useSearchParams } from 'react-router'
import {
  Calendar,
  CheckSquare,
  NotebookPen,
  RefreshCw,
  Search,
} from 'lucide-react'
import { ApiError } from '@/api/client'
import {
  Button,
  Card,
  Chip,
  EmptyState,
  InlineAlert,
  Input,
  PageHeader,
  Skeleton,
} from '@/components/ui'
import { useGlobalSearch, useSearchIdentity } from '../queries'
import { searchApi } from '../api'
import type {
  GlobalSearchParams,
  SearchDomain,
  SearchGroup,
  SearchMatch,
  SearchResult,
  SearchScope,
  SearchSort,
} from '../types'

const PAGE_SIZE = 20
const MAX_QUERY_LENGTH = 200

const SCOPE_OPTIONS: Array<{ value: SearchScope; label: string }> = [
  { value: 'all', label: '全部' },
  { value: 'note', label: '笔记' },
  { value: 'task', label: '任务' },
  { value: 'event', label: '日程' },
]

const MATCH_OPTIONS: Array<{ value: SearchMatch; label: string }> = [
  { value: 'all', label: '全部包含' },
  { value: 'any', label: '任一包含' },
]

const SORT_OPTIONS: Array<{ value: SearchSort; label: string }> = [
  { value: 'relevance', label: '相关程度' },
  { value: 'recent_edited', label: '最近编辑' },
  { value: 'recent_created', label: '最近创建' },
]

const DOMAIN_ICON = {
  note: NotebookPen,
  task: CheckSquare,
  event: Calendar,
} satisfies Record<SearchDomain, typeof Search>

function parseScope(value: string | null): SearchScope {
  return value === 'note' || value === 'task' || value === 'event' ? value : 'all'
}

function parseMatch(value: string | null): SearchMatch {
  return value === 'any' ? 'any' : 'all'
}

function parseSort(value: string | null): SearchSort {
  return value === 'recent_edited' || value === 'recent_created'
    ? value
    : 'relevance'
}

function parsePage(value: string | null): number {
  const page = Number(value)
  return Number.isInteger(page) && page >= 1 ? page : 1
}

function formatResultTime(item: SearchResult): string | null {
  const value = item.eventOccurrence?.startsAt ?? item.updatedAt ?? item.createdAt
  if (!value) return null
  const date = new Date(value)
  if (Number.isNaN(date.getTime())) return null
  return new Intl.DateTimeFormat('zh-CN', {
    month: 'numeric',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  }).format(date)
}

function errorDetails(error: unknown): {
  message: string
  code: string | null
  correlationId: string | null
} {
  if (error instanceof ApiError) {
    return {
      message: error.message,
      code: error.code >= 0 ? String(error.code) : null,
      correlationId: error.correlationId,
    }
  }
  return {
    message: error instanceof Error ? error.message : '搜索请求失败，请稍后重试',
    code: null,
    correlationId: null,
  }
}

function SearchResultCard({ item }: { item: SearchResult }) {
  const Icon = DOMAIN_ICON[item.domain] ?? Search
  const fields = item.fieldLocations?.map((location) => location.label) ?? item.matchedFields ?? []
  const time = formatResultTime(item)
  return (
    <Link
      to={item.url}
      viewTransition
      className="group block rounded-ctl border border-border bg-bg px-3 py-3 transition-colors hover:border-border-strong hover:bg-surface-2 focus-visible:outline-2 focus-visible:outline-primary"
    >
      <div className="flex items-start gap-2.5">
        <span className="mt-0.5 grid size-8 shrink-0 place-items-center rounded-ctl bg-surface-2 text-text-3 group-hover:text-primary">
          <Icon className="size-4" aria-hidden />
        </span>
        <div className="min-w-0 flex-1">
          <div className="flex flex-wrap items-center gap-x-2 gap-y-1">
            <span className="truncate text-[14px] font-medium text-text-1">{item.title}</span>
            {time && <span className="tnum text-xs text-text-4">{time}</span>}
            <span className="ml-auto rounded-full bg-surface-2 px-2 py-0.5 text-[11px] text-text-3">
              {item.sourceState === 'server' ? '服务器' : item.sourceState}
            </span>
          </div>
          {item.snippet && (
            <p className="mt-1 line-clamp-2 text-[13px] leading-5 text-text-2">{item.snippet}</p>
          )}
          {fields.length > 0 && (
            <div className="mt-2 flex flex-wrap gap-1">
              {fields.slice(0, 6).map((field, index) => (
                <span key={`${field}-${index}`} className="rounded-full bg-primary-soft px-2 py-0.5 text-[11px] text-primary">
                  命中：{field}
                </span>
              ))}
            </div>
          )}
        </div>
      </div>
    </Link>
  )
}

function SearchGroupSection({
  group,
  retryBusy,
  retryError,
  onRetry,
}: {
  group: SearchGroup
  retryBusy: boolean
  retryError: string | null
  onRetry: () => void
}) {
  return (
    <section aria-labelledby={`search-group-${group.domain}`} className="space-y-2">
      <div className="flex items-center gap-2">
        <h2 id={`search-group-${group.domain}`} className="text-sm font-semibold text-text-1">
          {group.label}
        </h2>
        <span className="tnum rounded-full bg-surface-2 px-2 py-0.5 text-[11px] text-text-3">
          {group.totalCount}
        </span>
        {group.failure?.retryable && (
          <Button
            size="sm"
            variant="secondary"
            className="ml-auto"
            disabled={retryBusy}
            onClick={onRetry}
          >
            <RefreshCw className={retryBusy ? 'size-3.5 animate-spin' : 'size-3.5'} aria-hidden />
            {retryBusy ? '重试中' : '重试该领域'}
          </Button>
        )}
      </div>
      {group.failure ? (
        <InlineAlert tone="crit">
          <div className="space-y-1">
            <div>{group.failure.message}</div>
            <div className="text-xs text-text-3">
              错误码 {group.failure.code} · 关联编号 <span className="mono">{group.failure.correlationId}</span>
            </div>
            {retryError && <div className="text-xs">{retryError}</div>}
          </div>
        </InlineAlert>
      ) : group.items.length > 0 ? (
        <div className="space-y-2">
          {group.items.map((item) => <SearchResultCard key={`${item.domain}-${item.id}-${item.eventOccurrence?.recurrenceId ?? ''}`} item={item} />)}
        </div>
      ) : (
        <p className="rounded-ctl border border-dashed border-border px-3 py-4 text-center text-xs text-text-4">
          {group.totalCount > 0 ? '本页该领域没有结果' : '该领域没有命中'}
        </p>
      )}
    </section>
  )
}

export function GlobalSearchPage() {
  const [params, setParams] = useSearchParams()
  const query = params.get('q')?.trim() ?? ''
  const scope = parseScope(params.get('scope'))
  const match = parseMatch(params.get('match'))
  const sort = parseSort(params.get('sort'))
  const page = parsePage(params.get('page'))
  const [input, setInput] = useState(query)
  const [retryDomain, setRetryDomain] = useState<SearchDomain | null>(null)
  const [retryResults, setRetryResults] = useState<Partial<Record<SearchDomain, SearchGroup>>>({})
  const [retryError, setRetryError] = useState<{
    domain: SearchDomain
    message: string
  } | null>(null)
  const retryGeneration = useRef(0)
  const retryAbort = useRef<AbortController | null>(null)
  const identity = useSearchIdentity()

  useEffect(() => setInput(query), [query])
  useEffect(() => {
    retryGeneration.current += 1
    retryAbort.current?.abort()
    retryAbort.current = null
    setRetryResults({})
    setRetryError(null)
    setRetryDomain(null)
    return () => {
      retryGeneration.current += 1
      retryAbort.current?.abort()
      retryAbort.current = null
    }
  }, [identity.ownerId, identity.serverBinding, query, scope, match, sort, page])

  const searchParams = useMemo<GlobalSearchParams>(() => ({
    q: query,
    scope,
    match,
    sort,
    page,
    pageSize: PAGE_SIZE,
  }), [query, scope, match, sort, page])
  const search = useGlobalSearch(searchParams, identity)

  function updateParams(patch: Partial<{ q: string; scope: SearchScope; match: SearchMatch; sort: SearchSort; page: number }>) {
    const next = new URLSearchParams()
    const nextQuery = patch.q ?? query
    const nextScope = patch.scope ?? scope
    const nextMatch = patch.match ?? match
    const nextSort = patch.sort ?? sort
    const nextPage = patch.page ?? (patch.q !== undefined || patch.scope !== undefined || patch.match !== undefined || patch.sort !== undefined ? 1 : page)
    if (nextQuery) next.set('q', nextQuery)
    if (nextScope !== 'all') next.set('scope', nextScope)
    if (nextMatch !== 'all') next.set('match', nextMatch)
    if (nextSort !== 'relevance') next.set('sort', nextSort)
    if (nextPage > 1) next.set('page', String(nextPage))
    setParams(next)
  }

  function submit(event: FormEvent) {
    event.preventDefault()
    updateParams({ q: input.trim(), page: 1 })
  }

  async function retry(domain: SearchDomain) {
    if (retryDomain) return
    const generation = retryGeneration.current
    const controller = new AbortController()
    retryAbort.current?.abort()
    retryAbort.current = controller
    setRetryDomain(domain)
    setRetryError(null)
    try {
      // The global page boundary depends on every domain. Re-query the same stable
      // snapshot, then replace only the failed group in the UI.
      const result = await searchApi.query(searchParams, { signal: controller.signal })
      if (generation !== retryGeneration.current || controller.signal.aborted) return
      const group = result.groups.find((item) => item.domain === domain)
      if (!group) throw new Error('重试响应缺少对应领域结果')
      if (group.failure) {
        setRetryError({
          domain,
          message: `${group.failure.message}（${group.failure.code}，${group.failure.correlationId}）`,
        })
        return
      }
      setRetryResults((current) => ({ ...current, [domain]: group }))
    } catch (error) {
      if (controller.signal.aborted || generation !== retryGeneration.current) return
      const detail = errorDetails(error)
      setRetryError({
        domain,
        message: [
          detail.code ? `${detail.message}（${detail.code}）` : detail.message,
          detail.correlationId ? `关联编号 ${detail.correlationId}` : null,
        ].filter(Boolean).join(' · '),
      })
    } finally {
      if (retryAbort.current === controller) retryAbort.current = null
      if (generation === retryGeneration.current) setRetryDomain(null)
    }
  }

  const groups = useMemo(
    () => (search.data?.groups ?? []).map((group) => retryResults[group.domain] ?? group),
    [search.data, retryResults],
  )
  const visibleGroups = groups.filter((group) =>
    group.items.length > 0 || group.failure || group.totalCount > 0)
  const effectiveTotal = groups.reduce((sum, group) => sum + group.totalCount, 0)
  const effectivePages = effectiveTotal === 0
    ? 0
    : Math.max(1, Math.ceil(effectiveTotal / PAGE_SIZE))
  const allFailed = query.length > 0 && !search.isLoading && !search.isError &&
    groups.length > 0 && groups.every((group) => group.failure)
  const globalError = search.isError ? errorDetails(search.error) : null

  return (
    <div className="flex h-full min-h-0 flex-col">
      <PageHeader
        title="全局搜索"
        subtitle="统一搜索任务、日程和笔记；文件搜索仍使用文件页的独立入口。"
      />

      <Card className="p-3">
        <form onSubmit={submit} className="flex flex-wrap items-center gap-2">
          <div className="relative min-w-[220px] flex-1">
            <Search className="pointer-events-none absolute top-2.5 left-2.5 size-4 text-text-4" aria-hidden />
            <Input
              aria-label="全局搜索关键词"
              value={input}
              maxLength={MAX_QUERY_LENGTH}
              onChange={(event) => setInput(event.target.value)}
              placeholder="搜索任务、日程和笔记… 多个关键词用空格分隔"
              className="pl-9"
            />
          </div>
          <Button type="submit" variant="primary">搜索</Button>
        </form>
        <div className="mt-3 flex flex-wrap items-center gap-x-4 gap-y-2">
          <div className="flex flex-wrap items-center gap-1.5" aria-label="搜索范围">
            <span className="text-xs text-text-4">范围</span>
            {SCOPE_OPTIONS.map((option) => (
              <Chip
                key={option.value}
                active={scope === option.value}
                onClick={() => updateParams({ scope: option.value, page: 1 })}
              >
                {option.label}
              </Chip>
            ))}
          </div>
          <div className="flex flex-wrap items-center gap-1.5" aria-label="匹配方式">
            <span className="text-xs text-text-4">匹配</span>
            {MATCH_OPTIONS.map((option) => (
              <Chip
                key={option.value}
                active={match === option.value}
                onClick={() => updateParams({ match: option.value, page: 1 })}
              >
                {option.label}
              </Chip>
            ))}
          </div>
          <div className="flex flex-wrap items-center gap-1.5" aria-label="排序方式">
            <span className="text-xs text-text-4">排序</span>
            {SORT_OPTIONS.map((option) => (
              <Chip
                key={option.value}
                active={sort === option.value}
                onClick={() => updateParams({ sort: option.value, page: 1 })}
              >
                {option.label}
              </Chip>
            ))}
          </div>
        </div>
        <p className="mt-2 text-xs text-text-4">
          默认全部包含；最多 {MAX_QUERY_LENGTH} 个字符、10 个关键词。
        </p>
      </Card>

      <div className="mt-4 min-h-0 flex-1 overflow-y-auto pr-1">
        {!query ? (
          <EmptyState
            title="输入关键词开始搜索"
            description="任务、日程和笔记会按领域分组显示；点击结果可直接打开对应对象。"
          />
        ) : search.isLoading ? (
          <div className="space-y-3" aria-label="搜索中">
            {[0, 1, 2].map((index) => <Skeleton key={index} className="h-24 w-full" />)}
          </div>
        ) : globalError ? (
          <InlineAlert tone="crit">
            <div className="space-y-2">
              <div>{globalError.message}</div>
              <div className="text-xs text-text-3">
                {globalError.code ? `错误码 ${globalError.code}` : '未收到可解析的业务错误码'}
                {globalError.correlationId
                  ? ` · 关联编号 ${globalError.correlationId}`
                  : ''}
              </div>
              <Button size="sm" variant="secondary" onClick={() => void search.refetch()}>
                <RefreshCw className="size-3.5" aria-hidden /> 重试查询
              </Button>
            </div>
          </InlineAlert>
        ) : allFailed ? (
          <div className="space-y-3">
            <InlineAlert tone="crit">所有领域暂时不可用；可分别重试，其他领域结果不会被清空。</InlineAlert>
            {groups.map((group) => (
              <SearchGroupSection
                key={group.domain}
                group={group}
                retryBusy={retryDomain === group.domain}
                retryError={retryError?.domain === group.domain ? retryError.message : null}
                onRetry={() => void retry(group.domain)}
              />
            ))}
          </div>
        ) : visibleGroups.length === 0 ? (
          <EmptyState
            title="没有找到匹配内容"
            description="尝试减少关键词、切换为“任一包含”，或扩大搜索范围。"
          />
        ) : (
          <div className="space-y-5">
            {visibleGroups.map((group) => (
              <SearchGroupSection
                key={group.domain}
                group={group}
                retryBusy={retryDomain === group.domain}
                retryError={retryError?.domain === group.domain ? retryError.message : null}
                onRetry={() => void retry(group.domain)}
              />
            ))}
          </div>
        )}
      </div>

      {query && !search.isLoading && !search.isError && effectivePages > 1 && (
        <div className="mt-3 flex items-center justify-end gap-2 border-t border-divider pt-3">
          <Button
            size="sm"
            variant="secondary"
            disabled={page <= 1}
            onClick={() => updateParams({ page: page - 1 })}
          >
            上一页
          </Button>
          <span className="tnum text-xs text-text-3">第 {page} / {effectivePages} 页</span>
          <Button
            size="sm"
            variant="secondary"
            disabled={page >= effectivePages}
            onClick={() => updateParams({ page: page + 1 })}
          >
            下一页
          </Button>
        </div>
      )}
    </div>
  )
}
