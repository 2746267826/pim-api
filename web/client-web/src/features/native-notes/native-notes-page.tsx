import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useInfiniteQuery, useQueries, useQuery, useQueryClient } from '@tanstack/react-query'
import { useBlocker, useNavigate, useSearchParams } from 'react-router'
import {
  ArrowRight,
  BookOpenText,
  FilePlus2,
  Mic,
  RefreshCw,
  Sparkles,
  Upload,
} from 'lucide-react'
import { Button, Card, EmptyState, InlineAlert, Input, PageHeader, Skeleton, StatusBadge } from '@/components/ui'
import { ApiError } from '@/api/client'
import { getApiBase } from '@/lib/apiBase'
import { formatTime } from '@/lib/datetime'
import { useAuth } from '@/features/auth/auth-context'
import { quickNotesApi } from '@/features/quick-notes/api'
import type { QuickNoteListItem } from '@/features/quick-notes/types'
import { groupNoteLibrary, type NoteLibraryEntry } from './library-model'
import { bindNativeNotesApi } from './api'
import { createMaterialId, type NoteMaterialUpload } from './material-model'
import { assertAndroidNotePackageSize, importAndroidNotePackage } from './material-package'
import { NoteEditorPane, type NoteEditorSeed } from './note-material-editor'
import { NoteVersionPanel, type ConflictMaterialState, type PromoteVersionOutcome } from './note-version-panel'
import { versionShortId } from './version-model'
import {
  LEGACY_QUICK_NOTE_NOTICE,
  legacyQuickNotePlainText,
  legacyQuickNoteTitle,
} from './legacy-quick-note'

function currentBinding() {
  return getApiBase() || window.location.origin
}

const EMPTY_NOTE_VERSIONS: NoteMaterialUpload[] = []
const EMPTY_REFRESH_ISSUE: string[] = []

export function NativeNotesPage() {
  const { user } = useAuth()
  const [serverBinding, setServerBinding] = useState(currentBinding)
  useEffect(() => {
    const update = () => setServerBinding(currentBinding())
    window.addEventListener('pim:api-base-changed', update)
    return () => window.removeEventListener('pim:api-base-changed', update)
  }, [])
  return <Workspace
    key={`${user?.id ?? ''}/${user?.role ?? ''}/${serverBinding}`}
    ownerId={user?.id ?? ''}
    role={user?.role}
    serverBinding={serverBinding}
  />
}

function Workspace({ ownerId, role, serverBinding }: { ownerId: string; role?: string; serverBinding: string }) {
  const canViewVersions = role === 'admin'
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
  const [refreshIssueState, setRefreshIssueState] = useState<{ noteId: string; failed: string[] }>({ noteId: '', failed: [] })
  const [returnNoteId] = useState(() => {
    const key = returnVersionStorageKey(ownerId, serverBinding)
    try {
      const noteId = sessionStorage.getItem(key)
      sessionStorage.removeItem(key)
      return noteId ?? ''
    } catch {
      return ''
    }
  })
  const sessionRef = useRef<WorkspaceSession | null>(null)
  const packageInputRef = useRef<HTMLInputElement>(null)
  const sessionGenerationRef = useRef(0)
  const promoteInFlightRef = useRef(false)
  const [promoteInFlight, setPromoteInFlight] = useState(false)
  const [promotingVersionId, setPromotingVersionId] = useState('')
  const allowInternalNavigationRef = useRef(false)
  const selectedNoteId = params.get('note') ?? ''
  const selectedVersionId = canViewVersions ? params.get('version') ?? '' : ''
  const changeInternalNavigation = useCallback((change: () => void) => {
    allowInternalNavigationRef.current = true
    change()
    queueMicrotask(() => {
      allowInternalNavigationRef.current = false
    })
  }, [])

  useEffect(() => {
    if (canViewVersions || (!params.has('version') && !params.has('versionId'))) return
    changeInternalNavigation(() => {
      const next = new URLSearchParams(params)
      next.delete('version')
      next.delete('versionId')
      setParams(next, { replace: true })
    })
  }, [canViewVersions, changeInternalNavigation, params, setParams])

  useEffect(() => {
    if (!returnNoteId || params.has('note')) return
    changeInternalNavigation(() => setParams({ note: returnNoteId }, { replace: true }))
  }, [changeInternalNavigation, params, returnNoteId, setParams])

  const sessionIsActive = useCallback(() => {
    const session = sessionRef.current
    return Boolean(session && session.ownerId === ownerId && session.serverBinding === serverBinding &&
      !session.controller.signal.aborted)
  }, [ownerId, serverBinding])

  const assertSessionActive = useCallback(() => {
    if (!sessionIsActive()) throw sessionChangedError()
  }, [sessionIsActive])

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
    queryKey: ['native-notes', ownerId, serverBinding, 'notes'],
    queryFn: ({ pageParam, signal }) => requestApi.notes(pageParam, 50, signal),
    initialPageParam: 0,
    getNextPageParam: (last) =>
      last.offset + last.items.length < last.total ? last.offset + last.items.length : undefined,
  })
  const selectedCurrent = useQuery({
    queryKey: ['native-notes', ownerId, serverBinding, 'current', selectedNoteId],
    queryFn: ({ signal }) => requestApi.current(selectedNoteId, signal),
    enabled: Boolean(selectedNoteId),
  })
  const selectedVersions = useQuery({
    queryKey: ['native-notes', ownerId, serverBinding, 'versions', selectedNoteId],
    queryFn: ({ signal }) => requestApi.versions(selectedNoteId, signal),
    enabled: Boolean(selectedNoteId),
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
  const selectedSource = selectedCurrent.data?.upload
  const selectedNoteVersions = selectedVersions.data ?? EMPTY_NOTE_VERSIONS
  const conflictVersions = useMemo(
    () => selectedNoteVersions.filter((version) =>
      !version.isCurrent && (version.hasConflict || version.phase === 'Conflict')),
    [selectedNoteVersions],
  )
  const conflictMaterialQueries = useQueries({
    queries: conflictVersions.map((version) => ({
      queryKey: ['native-notes', ownerId, serverBinding, 'conflict-material', version.versionId],
      queryFn: ({ signal }: { signal: AbortSignal }) => requestApi.version(version.versionId, signal),
      enabled: Boolean(selectedNoteId),
      staleTime: 5_000,
    })),
  })
  const conflictMaterials = useMemo(() => {
    const result: Record<string, ConflictMaterialState> = {}
    conflictVersions.forEach((version, index) => {
      const query = conflictMaterialQueries[index]
      result[version.versionId] = {
        material: query?.data,
        loading: query?.isPending ?? true,
        error: query?.error ?? undefined,
      }
    })
    return result
  }, [conflictMaterialQueries, conflictVersions])
  const legacyCount = quickNotes.data?.totalCount ?? 0
  const refreshIssue = refreshIssueState.noteId === selectedNoteId ? refreshIssueState.failed : EMPTY_REFRESH_ISSUE

  function blockNavigationWhilePromoting() {
    if (!promoteInFlightRef.current) return false
    setMessage(canViewVersions
      ? '正在提交版本选择，请等待服务器确认；完成前不能切换笔记。'
      : '正在提交内容选择，请等待服务器确认；完成前不能切换笔记。')
    return true
  }

  function confirmDiscardEditor(reason: string) {
    if (!editorDirty) return true
    return window.confirm(`${DIRTY_DRAFT_NOTICE}\n\n${reason}`)
  }

  function beginNew(initial?: NoteEditorSeed): boolean {
    if (blockNavigationWhilePromoting()) return false
    if (!confirmDiscardEditor(initial?.blocks ? '继续将用笔记包内容替换当前编辑器。' : '继续将切换到一份新的空白材料。')) return false
    setEditorDirty(false)
    setSeed(initial ?? { noteId: createMaterialId(), title: '', body: '' })
    setMessage('')
    setError('')
    changeInternalNavigation(() => setParams({}, { replace: true }))
    return true
  }

  async function importPackage(file: File | undefined) {
    if (!file || packageBusy || blockNavigationWhilePromoting()) return
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
    if (legacyBusy || blockNavigationWhilePromoting() ||
        !confirmDiscardEditor('继续将读取旧闪念并替换当前编辑内容。')) return
    setLegacyBusy(item.id)
    setError('')
    try {
      const detail = await quickNotesApi.get(item.id)
      assertSessionActive()
      const text = legacyQuickNotePlainText(detail.contentMarkdown)
      beginNew({
        noteId: createMaterialId(),
        title: legacyQuickNoteTitle(detail.contentMarkdown),
        body: text,
        legacyNotice: `${LEGACY_QUICK_NOTE_NOTICE} 原闪念 ${item.id} 未删除，也没有被标记为“已迁移”。`,
      })
    } catch (failure) {
      if (isAbortError(failure) || !sessionIsActive()) return
      setError(`旧闪念读取失败：${errorText(failure)}`)
    } finally {
      setLegacyBusy('')
    }
  }

  function openVersion(item: NoteLibraryEntry) {
    openNote(item.noteId)
  }

  function openProcessing(action: 'transcribe' | 'organize') {
    if (!selectedSource || editorDirty || blockNavigationWhilePromoting()) return
    try {
      sessionStorage.setItem(returnVersionStorageKey(ownerId, serverBinding), selectedSource.noteId)
    } catch {
      setMessage('无法在本机暂存返回位置；处理完成后可从笔记列表重新打开该笔记。')
    }
    const query = new URLSearchParams({ noteId: selectedSource.noteId })
    if (canViewVersions) query.set('versionId', selectedSource.versionId)
    query.set('action', action)
    navigate(`/native-notes/processing?${query.toString()}`)
  }

  function openNote(noteId: string) {
    if (noteId === selectedNoteId && !seed) return
    if (blockNavigationWhilePromoting()) return
    if (!confirmDiscardEditor('继续将打开另一篇笔记。')) return
    setEditorDirty(false)
    setSeed(null)
    setMessage('')
    setError('')
    changeInternalNavigation(() => setParams({ note: noteId }))
  }

  function openVersionId(versionId: string) {
    if (blockNavigationWhilePromoting()) return
    if (!confirmDiscardEditor('继续查看另一版笔记。')) return
    setEditorDirty(false)
    setSeed(null)
    changeInternalNavigation(() => setParams({ note: selectedNoteId, version: versionId }))
  }

  async function saved(upload: NoteMaterialUpload) {
    if (!sessionIsActive()) return
    setEditorDirty(false)
    setSeed(null)
    setMessage(canViewVersions
      ? '固定材料版本已保存。页面正在回读实际服务端版本与当前选用状态。'
      : '笔记已保存到服务器，正在回读最新内容。')
    try {
      await Promise.all([
        qc.invalidateQueries({ queryKey: ['native-notes', ownerId, serverBinding] }),
        qc.invalidateQueries({ queryKey: ['native-note-material'] }),
      ])
      assertSessionActive()
      changeInternalNavigation(() => setParams({ note: upload.noteId }))
    } catch (failure) {
      if (!isAbortError(failure) && sessionIsActive()) {
        setError(canViewVersions
          ? `版本已保存，但页面未能回到该笔记：${errorText(failure)}`
          : `笔记已保存，但页面未能回到这篇笔记：${errorText(failure)}`)
      }
    }
  }

  async function refreshVersionState(): Promise<string[]> {
    const materialQueries = conflictMaterialQueries
    const results = await Promise.allSettled([
      selectedCurrent.refetch(),
      selectedVersions.refetch(),
      ...materialQueries.map((query) => query.refetch()),
    ])
    const failed: string[] = []
    if (refreshFailed(results[0])) failed.push(canViewVersions ? '当前版本' : '当前内容')
    if (refreshFailed(results[1])) failed.push(canViewVersions ? '版本历史' : '冲突状态')
    if (results.slice(2).some((result) => refreshFailed(result))) {
      failed.push(canViewVersions ? '冲突候选材料' : '另一份内容')
    }
    setRefreshIssueState({ noteId: selectedNoteId, failed })
    void sources.refetch().catch(() => undefined)
    void qc.invalidateQueries({ queryKey: ['native-note-material', ownerId, serverBinding] })
    void qc.invalidateQueries({ queryKey: ['native-note-material-hydrated', ownerId, serverBinding] })
    return failed
  }

  async function promoteVersion(versionId: string): Promise<PromoteVersionOutcome> {
    const selectionNoun = canViewVersions ? '版本选择' : '内容选择'
    if (!sessionIsActive()) {
      return { status: 'failed', message: `账号或服务器已切换，本次${selectionNoun}未提交；请在新会话中重新读取。` }
    }
    const expectedCurrentVersionId =
      selectedCurrent.data?.upload.currentVersionId ?? selectedCurrent.data?.upload.versionId ?? null
    if (promoteInFlightRef.current) {
      return { status: 'failed', message: `上一次${selectionNoun}仍在等待服务器确认，请勿重复提交。` }
    }
    promoteInFlightRef.current = true
    setPromoteInFlight(true)
    setPromotingVersionId(versionId)
    setMessage('')
    setError('')
    try {
      const upload = await requestApi.promote(versionId, expectedCurrentVersionId, getSessionSignal())
      assertSessionActive()
      setRefreshIssueState({ noteId: selectedNoteId, failed: [] })
      const failed = await refreshVersionState()
      assertSessionActive()
      setError('')
      setMessage(canViewVersions
        ? '已采用所选内容，服务器已将它设为当前版本；另一份内容仍保留在版本历史中。'
        : '已采用这份内容；另一份内容已保留在服务器。')
      changeInternalNavigation(() => setParams({ note: upload.noteId }))
      return {
        status: 'adopted',
        message: failed.length > 0
          ? canViewVersions
            ? `已采用所选内容；未能重新读取${failed.join('、')}。另一份版本仍保留在版本历史中，请刷新核对当前状态。`
            : `已采用这份内容；暂时无法刷新${failed.join('、')}，请重新打开这篇笔记核对。`
          : canViewVersions
            ? '已采用所选内容。另一份版本仍保留在版本历史中，可随时回看。'
            : '已采用这份内容，另一份内容仍保留在服务器。',
      }
    } catch (failure) {
      if (isAbortError(failure) || !sessionIsActive()) {
        return {
          status: 'failed',
          message: `账号或服务器已切换，本次${selectionNoun}已停止；未确认是否生效，请在新会话中重新读取。`,
        }
      }
      if (failure instanceof ApiError && failure.status === 409) {
        const failed = await refreshVersionState()
        assertSessionActive()
        if (failed.length > 0) {
          return {
            status: 'stale',
            message: canViewVersions
              ? `服务器拒绝了本次版本选择；未能重新读取${failed.join('、')}。为避免在旧数据上继续选择，已隐藏冲突候选，请刷新核对后重试。`
              : `服务器拒绝了本次选择；暂时无法刷新${failed.join('、')}。为避免在旧内容上继续选择，已隐藏冲突选择，请重新读取并核对。`,
          }
        }
        return {
          status: 'stale',
          message: canViewVersions
            ? '服务器拒绝了本次版本选择；已重新读取当前内容和冲突候选，请按最新状态重新选择。'
            : '服务器拒绝了本次选择；已重新读取当前内容和另一份内容，请核对后重新选择。',
        }
      }
      const failed = await refreshVersionState()
      assertSessionActive()
      return {
        status: 'failed',
        message: failed.length > 0
          ? canViewVersions
            ? `${errorText(failure)}。未确认本次选择是否成功，请刷新核对后再选择；未能重新读取${failed.join('、')}，已隐藏冲突候选。`
            : `${errorText(failure)}。未确认本次选择是否成功，请刷新核对后再选择；暂时无法刷新${failed.join('、')}，已隐藏冲突选择。`
          : canViewVersions
            ? `${errorText(failure)}。未确认本次选择是否成功，请刷新核对后再选择；已重新读取当前版本和冲突候选。`
            : `${errorText(failure)}。未确认本次选择是否成功，请刷新核对后再选择；已重新读取当前内容和另一份内容。`,
      }
    } finally {
      promoteInFlightRef.current = false
      setPromoteInFlight(false)
      setPromotingVersionId('')
    }
  }

  function openCurrentVersion() {
    if (!selectedVersionId) return
    if (blockNavigationWhilePromoting()) return
    if (!confirmDiscardEditor('继续回到服务器当前版本。')) return
    setEditorDirty(false)
    setSeed(null)
    changeInternalNavigation(() => setParams({ note: selectedNoteId }))
  }

  function cancelConflictSelection() {
    if (promoteInFlightRef.current) {
      setMessage('正在提交“采用这份内容”，请等待服务器确认；完成前不能取消。')
      return
    }
    setError('')
    setMessage(canViewVersions
      ? '已收起冲突选择，未提交版本变更；冲突候选仍保留在版本历史中，可稍后处理。'
      : '已收起冲突选择，未提交任何更改；另一份内容仍保留在服务器。')
    if (canViewVersions && selectedVersionId) {
      changeInternalNavigation(() => setParams({ note: selectedNoteId }))
    }
  }

  return <div className="space-y-4">
    <PageHeader
      title="笔记库"
      subtitle="写下、找回并继续处理你的笔记"
      actions={<>
        <Button size="sm" variant="primary" onClick={() => beginNew()}><FilePlus2 className="size-4" aria-hidden />新建笔记</Button>
        <input ref={packageInputRef} className="hidden" type="file" accept=".zip,application/zip" aria-label="选择 Android 笔记包"
          onChange={(event) => { void importPackage(event.currentTarget.files?.[0]); event.currentTarget.value = '' }} />
      </>}
    />

    {message && <InlineAlert tone="ok">{message}</InlineAlert>}
    {error && <InlineAlert tone="crit" title="操作未完成">{error}</InlineAlert>}
    <p className="text-xs leading-5 text-text-3">
      {canViewVersions
        ? '已保存版本与冲突候选都会保留。含录音的当前版本可在保留原件的前提下续写；历史版本仅供阅读。'
        : '笔记会保存到服务器。发生真实冲突时，可先比较服务器当前内容与另一份已保存内容，再决定采用哪一份。'}
    </p>

    <div className="grid gap-4 xl:grid-cols-[minmax(260px,.72fr)_minmax(0,1.6fr)]">
      <div className="min-w-0">
        <Card className="overflow-hidden">
          <div className="border-b border-divider p-4">
            <div className="flex items-center justify-between gap-2">
              <div>
                <h2 className="text-sm font-semibold text-text-1">最近笔记</h2>
                <p className="mt-1 text-xs text-text-3">{canViewVersions ? '按当前材料更新排列' : '按最近更新排列'}</p>
              </div>
              <Button size="sm" variant="ghost" aria-label="刷新笔记库" onClick={() => void sources.refetch()}><RefreshCw className="size-4" aria-hidden /></Button>
            </div>
            <Input className="mt-3" aria-label="搜索笔记标题" placeholder="搜索标题…" value={search} onChange={(event) => setSearch(event.target.value)} />
          </div>
          <div className="max-h-[min(72vh,52rem)] overflow-y-auto p-2">
            {sources.isPending ? <div className="space-y-2 p-2">{Array.from({ length: 5 }, (_, index) => <Skeleton key={index} className="h-24" />)}</div>
              : sources.isError ? <div className="p-3"><InlineAlert tone="crit" title="笔记库读取失败">{errorText(sources.error)}</InlineAlert></div>
                : !filtered.length ? <EmptyState title={search ? '没有匹配标题' : '还没有原生笔记'} description={search ? '清空搜索可查看全部笔记。' : '从“新建笔记”开始；也可以把旧闪念主动复制为纯文本新笔记。'} action={<Button size="sm" onClick={() => beginNew()}>新建笔记</Button>} />
                  : <div className="space-y-2">{filtered.map((item) => <button key={item.noteId} type="button" onClick={() => openVersion(item)}
                    className={`block w-full rounded-ctl border p-3 text-left outline-none transition-colors hover:border-border-strong hover:bg-surface ${selectedSource?.noteId === item.noteId ? 'border-primary bg-primary/5' : 'border-border bg-bg'}`}>
                    <div className="flex items-start gap-2">
                      <span className="grid size-8 shrink-0 place-items-center rounded-ctl bg-surface-2 text-primary"><BookOpenText className="size-4" aria-hidden /></span>
                      <span className="min-w-0 flex-1">
                        <span className="block truncate text-sm font-medium text-text-1">{item.title}</span>
                        <span className="mt-1 block text-[11px] text-text-4">
                          {canViewVersions
                            ? <>当前版本 <span className="mono">{versionShortId(item.currentVersionId)}</span> · 点击打开</>
                            : '点击打开'}
                        </span>
                      </span>
                      <ArrowRight className="mt-1 size-4 shrink-0 text-text-4" aria-hidden />
                    </div>
                    <div className="mt-3 flex flex-wrap gap-1.5">
                      {item.hasConflict && <StatusBadge tone="warn" dot={false}>有待处理冲突</StatusBadge>}
                      {!item.hasConflict && <StatusBadge tone="ok" dot={false}>{canViewVersions ? '当前版' : '已同步'}</StatusBadge>}
                    </div>
                  </button>)}</div>}
          </div>
          {sources.hasNextPage && <div className="border-t border-divider p-3"><Button size="sm" className="w-full" loading={sources.isFetchingNextPage} onClick={() => void sources.fetchNextPage()}>加载更多笔记</Button></div>}
        </Card>

        <details className="rounded-card border border-border bg-bg px-4 py-3 shadow-card">
          <summary className="cursor-pointer text-sm font-medium text-text-2">更多：旧记录与导入{legacyCount > 0 ? ` · ${legacyCount} 条旧记录` : ''}</summary>
          <div className="mt-4 space-y-4">
            <section className="space-y-3" aria-labelledby="legacy-notes-title">
              <div>
                <h2 id="legacy-notes-title" className="text-sm font-semibold text-text-1">旧快速记录</h2>
                <p className="mt-1 text-xs leading-5 text-text-3">仍由快速记录服务单独管理；这里只能主动复制成新笔记，不会自动迁移或删除原记录。</p>
              </div>
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
            <Button size="sm" variant="secondary" onClick={() => navigate('/quick-notes')}>打开旧快速记录</Button>
            </section>
            <section className="space-y-3 border-t border-divider pt-4" aria-labelledby="note-import-title">
              <h2 id="note-import-title" className="text-sm font-semibold text-text-1">从 Android 导入</h2>
              <p className="text-xs leading-5 text-text-3">云端笔记会直接显示在笔记列表中，无需导入。此处仅用于可选的 Android 文件交换或备份恢复；内容先进入编辑器，保存到服务器前仍需确认。</p>
              <Button size="sm" variant="secondary" loading={packageBusy} disabled={packageBusy} onClick={() => packageInputRef.current?.click()}>
                <Upload className="size-4" aria-hidden />选择笔记包
              </Button>
            </section>
          </div>
        </details>
      </div>

      <div className="min-w-0">
        {selectedSource && !selectedVersionId && <section className="mb-3 flex flex-wrap items-center gap-2 rounded-card border border-border bg-bg p-3" aria-label="笔记处理操作">
          <span className="mr-auto text-xs font-medium text-text-2">按需处理</span>
          {recordingCount(selectedCurrent.data?.inputJson) > 0
            ? <Button size="sm" variant="secondary" disabled={editorDirty} onClick={() => openProcessing('transcribe')}>
              <Mic className="size-4" aria-hidden />转写录音
            </Button>
            : <Button size="sm" variant="secondary" disabled={editorDirty} onClick={() => openProcessing('organize')}>
              <Sparkles className="size-4" aria-hidden />整理笔记
            </Button>}
          {editorDirty && <p className="w-full text-xs text-text-3">请先保存当前编辑，再开始处理；未保存内容不会发送。</p>}
        </section>}
        {selectedNoteId && <div className="min-w-0">
          <NoteVersionPanel
            key={selectedNoteId}
            mode={canViewVersions ? 'admin' : 'conflict'}
            noteId={selectedNoteId}
            versions={selectedNoteVersions}
            versionsLoading={selectedVersions.isPending}
            versionsError={selectedVersions.error}
            refreshIssue={refreshIssue}
            current={selectedCurrent.data}
            currentLoading={selectedCurrent.isPending}
            currentError={selectedCurrent.error}
            selectedVersionId={selectedVersionId}
            conflictMaterials={conflictMaterials}
            promoteInFlight={promoteInFlight}
            promotingVersionId={promotingVersionId}
            onOpenVersion={openVersionId}
            onOpenCurrent={openCurrentVersion}
            onRefresh={() => {
              if (blockNavigationWhilePromoting()) return
              void refreshVersionState()
            }}
            onCancelConflict={cancelConflictSelection}
            onPromote={promoteVersion}
          />
        </div>}
        {selectedNoteId && !selectedVersionId && selectedCurrent.isPending
          ? <Card className="p-6"><Skeleton className="h-8 w-2/3" /><Skeleton className="mt-4 h-56" /><p className="mt-3 text-xs text-text-3">{canViewVersions ? '正在读取当前版本…' : '正在读取笔记…'}</p></Card>
          : selectedNoteId && !selectedVersionId && selectedCurrent.isError
            ? <Card className="p-5"><InlineAlert tone="crit" title={canViewVersions ? '当前版本读取失败' : '笔记读取失败'}>{errorText(selectedCurrent.error)}</InlineAlert></Card>
            : <NoteEditorPane
              ownerId={ownerId}
              serverBinding={serverBinding}
              api={requestApi}
              showVersionDetails={canViewVersions}
              versionId={selectedVersionId || selectedSource?.versionId || selectedCurrent.data?.upload.versionId}
              seed={seed ?? undefined}
              onDirtyChange={setEditorDirty}
              onSaved={(upload) => void saved(upload)}
              promoteInFlight={promoteInFlight}
              promotingVersionId={promotingVersionId}
              onPromoteVersion={promoteVersion}
            />}
      </div>
    </div>
  </div>
}

function errorText(error: unknown) {
  return error instanceof Error ? error.message : '请求失败，请刷新核对'
}

function refreshFailed(result: PromiseSettledResult<{ isError: boolean }>) {
  return result.status === 'rejected' || result.value.isError
}

function isAbortError(error: unknown) {
  return error instanceof Error && error.name === 'AbortError'
}

function sessionChangedError() {
  const error = new Error('笔记工作区会话已切换，已停止旧请求')
  error.name = 'AbortError'
  return error
}

function returnVersionStorageKey(ownerId: string, serverBinding: string) {
  return `pim:native-notes:return-version:${ownerId}:${serverBinding}`
}

function recordingCount(inputJson?: string) {
  if (!inputJson) return 0
  try {
    const parsed = JSON.parse(inputJson) as { recordings?: unknown[] }
    return Array.isArray(parsed.recordings) ? parsed.recordings.length : 0
  } catch {
    return 0
  }
}

interface WorkspaceSession {
  generation: number
  ownerId: string
  serverBinding: string
  controller: AbortController
}

const DIRTY_DRAFT_NOTICE = '当前编辑内容尚未保存，离开后会丢失。确定继续吗？'
