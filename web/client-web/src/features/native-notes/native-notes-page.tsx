import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useInfiniteQuery, useQuery, useQueryClient } from '@tanstack/react-query'
import { useBlocker, useNavigate, useSearchParams } from 'react-router'
import {
  ArrowRight,
  BookOpenText,
  FilePlus2,
  Images,
  Mic,
  RefreshCw,
  Sparkles,
  Upload,
} from 'lucide-react'
import { Button, Card, EmptyState, InlineAlert, Input, PageHeader, Skeleton, StatusBadge } from '@/components/ui'
import { getApiBase } from '@/lib/apiBase'
import { formatTime } from '@/lib/datetime'
import { useAuth } from '@/features/auth/auth-context'
import { quickNotesApi } from '@/features/quick-notes/api'
import type { QuickNoteListItem } from '@/features/quick-notes/types'
import { groupNoteLibrary, type NoteLibraryEntry } from './library-model'
import { bindNativeNotesApi } from './api'
import { createMaterialId } from './material-model'
import { assertAndroidNotePackageSize, importAndroidNotePackage } from './material-package'
import { NoteEditorPane, type NoteEditorSeed } from './note-material-editor'
import {
  LEGACY_QUICK_NOTE_NOTICE,
  legacyQuickNotePlainText,
  legacyQuickNoteTitle,
} from './legacy-quick-note'

function currentBinding() {
  return getApiBase() || window.location.origin
}

export function NativeNotesPage() {
  const { user } = useAuth()
  const [serverBinding, setServerBinding] = useState(currentBinding)
  useEffect(() => {
    const update = () => setServerBinding(currentBinding())
    window.addEventListener('pim:api-base-changed', update)
    return () => window.removeEventListener('pim:api-base-changed', update)
  }, [])
  return <Workspace
    key={`${user?.id ?? ''}/${serverBinding}`}
    ownerId={user?.id ?? ''}
    serverBinding={serverBinding}
  />
}

function Workspace({ ownerId, serverBinding }: { ownerId: string; serverBinding: string }) {
  const navigate = useNavigate()
  const qc = useQueryClient()
  const [params, setParams] = useSearchParams()
  const [search, setSearch] = useState('')
  const [seed, setSeed] = useState<NoteEditorSeed | null>(null)
  const [legacyBusy, setLegacyBusy] = useState('')
  const [packageBusy, setPackageBusy] = useState(false)
  const [message, setMessage] = useState('')
  const [error, setError] = useState('')
  const [editorDirty, setEditorDirty] = useState(false)
  const sessionRef = useRef<WorkspaceSession | null>(null)
  const packageInputRef = useRef<HTMLInputElement>(null)
  const sessionGenerationRef = useRef(0)
  const allowInternalNavigationRef = useRef(false)
  const selectedVersionId = params.get('version') ?? ''

  const assertSessionActive = useCallback(() => {
    const session = sessionRef.current
    if (!session || session.ownerId !== ownerId || session.serverBinding !== serverBinding ||
        session.controller.signal.aborted) {
      const error = new Error('笔记工作区会话已切换，已停止旧请求')
      error.name = 'AbortError'
      throw error
    }
  }, [ownerId, serverBinding])

  useEffect(() => {
    const controller = new AbortController()
    sessionGenerationRef.current += 1
    sessionRef.current = {
      generation: sessionGenerationRef.current,
      ownerId,
      serverBinding,
      controller,
    }
    return () => {
      if (sessionRef.current?.controller === controller) sessionRef.current = null
      controller.abort()
    }
  }, [ownerId, serverBinding])

  const getSessionSignal = useCallback(
    () => sessionRef.current?.controller.signal,
    [],
  )

  const requestApi = useMemo(() => bindNativeNotesApi({
    getSignal: getSessionSignal,
    assertActive: assertSessionActive,
  }), [assertSessionActive, getSessionSignal])

  const blocker = useBlocker(({ currentLocation, nextLocation }) =>
    editorDirty &&
    !allowInternalNavigationRef.current &&
    (currentLocation.pathname !== nextLocation.pathname ||
      currentLocation.search !== nextLocation.search ||
      currentLocation.hash !== nextLocation.hash))

  useEffect(() => {
    if (blocker.state !== 'blocked') return
    if (window.confirm(DIRTY_DRAFT_NOTICE)) blocker.proceed()
    else blocker.reset()
  }, [blocker])

  useEffect(() => {
    if (!editorDirty) return
    const beforeUnload = (event: BeforeUnloadEvent) => {
      event.preventDefault()
      event.returnValue = ''
    }
    window.addEventListener('beforeunload', beforeUnload)
    return () => window.removeEventListener('beforeunload', beforeUnload)
  }, [editorDirty])

  const sources = useInfiniteQuery({
    queryKey: ['native-notes', ownerId, serverBinding, 'sources'],
    queryFn: ({ pageParam, signal }) => requestApi.sources(pageParam, 50, signal),
    initialPageParam: 0,
    getNextPageParam: (last) =>
      last.offset + last.items.length < last.total ? last.offset + last.items.length : undefined,
  })
  const quickNotes = useQuery({
    queryKey: ['native-notes', ownerId, serverBinding, 'legacy-quick-notes'],
    queryFn: () => quickNotesApi.list({ status: 'all', page: 1, pageSize: 20 }),
  })
  const allSources = useMemo(() => sources.data?.pages.flatMap((page) => page.items) ?? [], [sources.data])
  const library = useMemo(() => groupNoteLibrary(allSources), [allSources])
  const filtered = useMemo(() => {
    const query = search.trim().toLocaleLowerCase()
    return query ? library.filter((item) => item.title.toLocaleLowerCase().includes(query)) : library
  }, [library, search])
  const selectedSource = allSources.find((item) => item.versionId === selectedVersionId)
  const selectedNoteVersions = useMemo(() => {
    if (!selectedSource) return []
    return allSources
      .filter((item) => item.noteId === selectedSource.noteId)
      .sort((left, right) => Date.parse(right.createdAt) - Date.parse(left.createdAt))
  }, [allSources, selectedSource?.noteId])
  const conflictCount = library.filter((item) => item.hasConflict).length
  const recordingCount = library.filter((item) => item.recordings > 0).length
  const legacyCount = quickNotes.data?.totalCount ?? 0

  function confirmDiscardEditor(reason: string) {
    if (!editorDirty) return true
    return window.confirm(`${DIRTY_DRAFT_NOTICE}\n\n${reason}`)
  }

  function changeInternalNavigation(change: () => void) {
    allowInternalNavigationRef.current = true
    change()
    queueMicrotask(() => {
      allowInternalNavigationRef.current = false
    })
  }

  function beginNew(initial?: NoteEditorSeed): boolean {
    if (!confirmDiscardEditor(initial?.blocks ? '继续将用笔记包内容替换当前编辑器。' : '继续将切换到一份新的空白材料。')) return false
    setEditorDirty(false)
    setSeed(initial ?? { noteId: createMaterialId(), title: '', body: '' })
    setMessage('')
    setError('')
    changeInternalNavigation(() => setParams({}, { replace: true }))
    return true
  }

  async function importPackage(file: File | undefined) {
    if (!file || packageBusy) return
    setError('')
    setMessage('')
    try {
      assertAndroidNotePackageSize(file.size)
    } catch (failure) {
      setError(errorText(failure))
      return
    }
    setPackageBusy(true)
    try {
      const bytes = new Uint8Array(await file.arrayBuffer())
      assertSessionActive()
      const imported = await importAndroidNotePackage(bytes)
      assertSessionActive()
      if (beginNew({ noteId: createMaterialId(), title: imported.title, body: '', blocks: imported.blocks })) {
        setMessage(`已校验并打开 Android schema-v1 笔记包（${imported.blocks.length} 个有序正文块）。内容尚未上传；保存时仍需明确确认服务器存储。`)
      }
    } catch (failure) {
      if (failure instanceof Error && failure.name === 'AbortError') return
      setError(`笔记包导入失败：${errorText(failure)}`)
    } finally {
      setPackageBusy(false)
    }
  }

  async function beginLegacyCopy(item: QuickNoteListItem) {
    if (legacyBusy || !confirmDiscardEditor('继续将读取旧闪念并替换当前编辑内容。')) return
    setLegacyBusy(item.id)
    setError('')
    try {
      const detail = await quickNotesApi.get(item.id)
      const text = legacyQuickNotePlainText(detail.contentMarkdown)
      beginNew({
        noteId: createMaterialId(),
        title: legacyQuickNoteTitle(detail.contentMarkdown),
        body: text,
        legacyNotice: `${LEGACY_QUICK_NOTE_NOTICE} 原闪念 ${item.id} 未删除，也没有被标记为“已迁移”。`,
      })
    } catch (failure) {
      setError(`旧闪念读取失败：${errorText(failure)}`)
    } finally {
      setLegacyBusy('')
    }
  }

  function openVersion(item: NoteLibraryEntry) {
    openVersionId(item.versionId)
  }

  function openVersionId(versionId: string) {
    if (versionId === selectedVersionId && !seed) return
    if (!confirmDiscardEditor('继续将打开另一条固定材料版本。')) return
    setEditorDirty(false)
    setSeed(null)
    setMessage('')
    setError('')
    changeInternalNavigation(() => setParams({ version: versionId }))
  }

  async function saved(upload: { versionId: string }) {
    setEditorDirty(false)
    setSeed(null)
    setMessage('固定材料版本已保存。页面正在回读实际服务端版本与当前选用状态。')
    await Promise.all([
      qc.invalidateQueries({ queryKey: ['native-notes', ownerId, serverBinding] }),
      qc.invalidateQueries({ queryKey: ['native-note-material'] }),
    ])
    changeInternalNavigation(() => setParams({ version: upload.versionId }))
  }

  return <div className="space-y-5">
    <PageHeader
      title="笔记库"
      subtitle="固定正文、照片与手写预览；处理链始终读取同一条不可变材料版本"
      actions={<>
        <Button size="sm" variant="secondary" loading={packageBusy} disabled={packageBusy} onClick={() => packageInputRef.current?.click()}>
          <Upload className="size-4" aria-hidden />导入 Android 笔记包
        </Button>
        <Button size="sm" variant="primary" onClick={() => beginNew()}><FilePlus2 className="size-4" aria-hidden />新建笔记</Button>
        <Button size="sm" variant="secondary" onClick={() => navigate('/native-notes/processing')}><Sparkles className="size-4" aria-hidden />处理与生成</Button>
        <input ref={packageInputRef} className="hidden" type="file" accept=".zip,application/zip" aria-label="选择 Android 笔记包"
          onChange={(event) => { void importPackage(event.currentTarget.files?.[0]); event.currentTarget.value = '' }} />
      </>}
    />

    <section className="overflow-hidden rounded-card border border-border bg-[radial-gradient(circle_at_10%_10%,var(--color-info-soft),transparent_42%),radial-gradient(circle_at_90%_20%,var(--color-surface-2),transparent_45%),var(--color-bg)] p-5 shadow-card">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="max-w-2xl">
          <p className="text-xs font-semibold tracking-[0.18em] text-primary">NATIVE NOTES · M2/M3</p>
          <h2 className="mt-2 text-xl font-semibold text-text-1">先冻结，再编辑，再处理</h2>
          <p className="mt-2 text-sm leading-6 text-text-2">Web 新建只发布正文与照片的固定版本；已有版本中的录音和手写向量不会被静默丢弃。含录音笔记在 Web 端只读，直到有服务端克隆协议。</p>
        </div>
        <div className="grid min-w-[16rem] grid-cols-2 gap-2 text-xs sm:grid-cols-4">
          <Metric label="笔记" value={String(library.length)} icon={BookOpenText} />
          <Metric label="材料版本" value={String(sources.data?.pages[0]?.total ?? allSources.length)} icon={Images} />
          <Metric label="含录音" value={String(recordingCount)} icon={Mic} />
          <Metric label="旧闪念" value={String(legacyCount)} icon={Sparkles} />
        </div>
      </div>
      {conflictCount > 0 && <InlineAlert className="mt-4" tone="warn" title={`${conflictCount} 篇笔记有冲突候选`}>所有候选材料都保留，不会自动覆盖当前选用版本。</InlineAlert>}
      {message && <InlineAlert className="mt-4" tone="ok">{message}</InlineAlert>}
      {error && <InlineAlert className="mt-4" tone="crit" title="操作未完成">{error}</InlineAlert>}
    </section>

    <div className="grid gap-5 xl:grid-cols-[minmax(280px,.72fr)_minmax(0,1.6fr)]">
      <div className="space-y-5">
        <Card className="overflow-hidden">
          <div className="border-b border-divider p-4">
            <div className="flex items-center justify-between gap-2">
              <div>
                <h2 className="text-sm font-semibold text-text-1">我的笔记</h2>
                <p className="mt-1 text-xs text-text-3">按笔记聚合当前选用版本；版本历史不会被新稿覆盖。</p>
              </div>
              <Button size="sm" variant="ghost" aria-label="刷新笔记库" onClick={() => void sources.refetch()}><RefreshCw className="size-4" aria-hidden /></Button>
            </div>
            <Input className="mt-3" aria-label="搜索笔记标题" placeholder="搜索标题…" value={search} onChange={(event) => setSearch(event.target.value)} />
          </div>
          <div className="max-h-[43rem] overflow-y-auto p-2">
            {sources.isPending ? <div className="space-y-2 p-2">{Array.from({ length: 5 }, (_, index) => <Skeleton key={index} className="h-24" />)}</div>
              : sources.isError ? <div className="p-3"><InlineAlert tone="crit" title="笔记库读取失败">{errorText(sources.error)}</InlineAlert></div>
                : !filtered.length ? <EmptyState title={search ? '没有匹配标题' : '还没有原生笔记'} description={search ? '清空搜索可查看全部笔记。' : '从“新建笔记”开始；也可以把旧闪念主动复制为纯文本新笔记。'} action={<Button size="sm" onClick={() => beginNew()}>新建笔记</Button>} />
                  : <div className="space-y-2">{filtered.map((item) => <button key={item.noteId} type="button" onClick={() => openVersion(item)}
                    className={`block w-full rounded-ctl border p-3 text-left outline-none transition-colors hover:border-border-strong hover:bg-surface ${selectedSource?.noteId === item.noteId ? 'border-primary bg-primary/5' : 'border-border bg-bg'}`}>
                    <div className="flex items-start gap-2">
                      <span className="grid size-8 shrink-0 place-items-center rounded-ctl bg-surface-2 text-primary"><BookOpenText className="size-4" aria-hidden /></span>
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-sm font-medium text-text-1">{item.title}</span>
                        <span className="mt-1 block text-[11px] text-text-4">{formatTime(item.createdAt)} · {item.versionCount} 个已加载版本</span>
                      </span>
                      <ArrowRight className="mt-1 size-4 shrink-0 text-text-4" aria-hidden />
                    </div>
                    <div className="mt-3 flex flex-wrap gap-1.5">
                      {item.recordings > 0 ? <StatusBadge tone="info" dot={false}>{item.recordings} 次录音</StatusBadge> : <StatusBadge tone="neutral" dot={false}>纯文字/图片</StatusBadge>}
                      {item.hasConflict && <StatusBadge tone="warn" dot={false}>冲突候选</StatusBadge>}
                      {!item.isCurrent && <StatusBadge tone="warn" dot={false}>非当前版本</StatusBadge>}
                    </div>
                  </button>)}</div>}
          </div>
          {sources.hasNextPage && <div className="border-t border-divider p-3"><Button size="sm" className="w-full" loading={sources.isFetchingNextPage} onClick={() => void sources.fetchNextPage()}>加载更多材料版本</Button></div>}
        </Card>

        <Card className="overflow-hidden">
          <div className="border-b border-divider p-4">
            <h2 className="text-sm font-semibold text-text-1">旧闪念兼容</h2>
            <p className="mt-1 text-xs leading-5 text-text-3">旧记录仍由原快速记录模块管理，不自动迁移为原生 Markdown。</p>
          </div>
          <div className="space-y-3 p-4">
            <InlineAlert tone="warn" title="旧 HTML 不是原生 Markdown">{LEGACY_QUICK_NOTE_NOTICE}</InlineAlert>
            {quickNotes.isPending ? <Skeleton className="h-20" />
              : quickNotes.isError ? <InlineAlert tone="crit">{errorText(quickNotes.error)}</InlineAlert>
                : !quickNotes.data?.items.length ? <p className="text-xs text-text-3">没有旧闪念记录。</p>
                  : <div className="space-y-2">{quickNotes.data.items.slice(0, 5).map((item) => <div key={item.id} className="rounded-ctl border border-border p-3">
                    <p className="line-clamp-3 whitespace-pre-wrap text-xs leading-5 text-text-2">{item.contentPreview || '（无文本内容）'}</p>
                    <div className="mt-2 flex flex-wrap items-center gap-2">
                      <span className="text-[11px] text-text-4">{formatTime(item.createdAt)}</span>
                      <Button className="ml-auto" size="sm" variant="ghost" onClick={() => navigate('/quick-notes')}>打开旧记录</Button>
                      <Button size="sm" loading={legacyBusy === item.id} onClick={() => void beginLegacyCopy(item)}>纯文本副本</Button>
                    </div>
                  </div>)}</div>}
            <Button size="sm" variant="secondary" className="w-full" onClick={() => navigate('/quick-notes')}>进入快速记录页</Button>
          </div>
        </Card>
      </div>

      <div className="min-w-0">
        {selectedNoteVersions.length > 1 && <Card className="mb-4 p-3">
          <div className="flex flex-wrap items-center justify-between gap-2">
            <div>
              <h2 className="text-sm font-semibold text-text-1">选择固定材料版本</h2>
              <p className="mt-1 text-xs text-text-3">打开历史或冲突候选后，必须明确“设为当前版本”才会改变选用版本。</p>
            </div>
            <span className="text-[11px] text-text-4">{selectedNoteVersions.length} 个已加载版本</span>
          </div>
          <div className="mt-3 flex flex-wrap gap-2">
            {selectedNoteVersions.map((version) => <button
              key={version.versionId}
              type="button"
              aria-pressed={version.versionId === selectedVersionId}
              aria-label={`打开版本 ${formatTime(version.createdAt)}${version.isCurrent ? ' 当前版' : ' 历史版'}${version.hasConflict ? ' 冲突候选' : ''}`}
              onClick={() => openVersionId(version.versionId)}
              className={`rounded-ctl border px-3 py-2 text-left text-xs ${version.versionId === selectedVersionId ? 'border-primary bg-primary/5 text-text-1' : 'border-border bg-bg text-text-2 hover:border-border-strong'}`}
            >
              <span className="block">{formatTime(version.createdAt)}</span>
              <span className="mt-1 flex flex-wrap gap-1">
                <StatusBadge tone={version.isCurrent ? 'ok' : 'neutral'} dot={false}>{version.isCurrent ? '当前版' : '历史版'}</StatusBadge>
                {version.hasConflict && <StatusBadge tone="warn" dot={false}>冲突候选</StatusBadge>}
              </span>
            </button>)}
          </div>
        </Card>}
        <NoteEditorPane
          ownerId={ownerId}
          serverBinding={serverBinding}
          api={requestApi}
          versionId={selectedVersionId || undefined}
          seed={seed ?? undefined}
          source={selectedSource}
          onDirtyChange={setEditorDirty}
          onSaved={(upload) => void saved(upload)}
        />
      </div>
    </div>
  </div>
}

function Metric({ label, value, icon: Icon }: { label: string; value: string; icon: typeof BookOpenText }) {
  return <div className="rounded-ctl border border-border/70 bg-bg/80 p-2.5 backdrop-blur">
    <div className="flex items-center gap-1.5 text-text-4"><Icon className="size-3.5" aria-hidden /><span>{label}</span></div>
    <p className="mt-1 text-lg font-semibold text-text-1">{value}</p>
  </div>
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '请求失败，请刷新核对'
}

interface WorkspaceSession {
  generation: number
  ownerId: string
  serverBinding: string
  controller: AbortController
}

const DIRTY_DRAFT_NOTICE = '当前编辑内容尚未保存，离开后会丢失。确定继续吗？'
