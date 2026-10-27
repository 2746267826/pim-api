import { useCallback, useEffect, useRef, useState, type ReactNode } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Button, Card, EmptyState, Label, Select, Skeleton, Textarea } from '@/components/ui'
import { aiApi, type CallImage, type CallResult, type Model, type ModelSettings, type VisionRequest } from './api'
import { createAiRequestId } from './request-id'
import { readMediaFile } from './media-file'

const maxImage = 4 * 1024 * 1024
const errorText = (error: unknown) => error instanceof Error ? error.message : '请求失败'

function FilePreview({ file }: { file: File }) {
  const attach = useCallback((node: HTMLImageElement | null) => {
    if (!node) return
    const source = URL.createObjectURL(file); node.src = source
    return () => { node.removeAttribute('src'); URL.revokeObjectURL(source) }
  }, [file])
  return <img ref={attach} alt={`待发送图片：${file.name}`} className="h-28 w-full rounded-ctl bg-surface object-contain" />
}

export function VisionCall({ userId, models, settings, changed, showDetail, renderStatus }: {
  userId: string; models: Model[]; settings: ModelSettings; changed: () => void; showDetail: (id: string) => void; renderStatus: (state: string) => ReactNode
}) {
  const [selected, setSelected] = useState(''); const [system, setSystem] = useState(''); const [prompt, setPrompt] = useState('')
  const [files, setFiles] = useState<File[]>([]); const [selection, setSelection] = useState(''); const [consentedTo, setConsentedTo] = useState('')
  const [submitted, setSubmitted] = useState<{ id: string; target: Model } | null>(null)
  const [pending, setPending] = useState(false); const [result, setResult] = useState<CallResult | null>(null); const [message, setMessage] = useState<string | null>(null)
  const controller = useRef<AbortController | null>(null); const inFlight = useRef(false)
  const fileInput = useRef<HTMLInputElement | null>(null)
  useEffect(() => () => controller.current?.abort(), [])
  const current = models.find((m) => m.id === (selected || settings.visionModelId))
  const target = submitted?.target ?? current
  const approvalKey = target ? `${target.id}/${target.revision}/${target.baseUrl}/${selection}` : ''
  const ready = settings.enabled && current?.enabled && current.capabilities.includes('Vision')
  const locked = pending || submitted !== null
  const total = files.reduce((sum, file) => sum + file.size, 0)
  const selectFiles = (next: File[]) => {
    setConsentedTo(''); setSelection(createAiRequestId()); setResult(null)
    if (next.length > 4 || next.some((f) => !['image/png', 'image/jpeg'].includes(f.type) || f.size === 0 || f.size > maxImage) || next.reduce((s, f) => s + f.size, 0) > 2 * maxImage) {
      setFiles([]); setMessage('请选择1至4张PNG/JPEG；单张最多4MiB，合计最多8MiB。'); return
    }
    setFiles(next); setMessage(null)
  }
  async function send() {
    if (inFlight.current || !target || !ready || consentedTo !== approvalKey || submitted) return
    inFlight.current = true
    const abort = new AbortController(); controller.current = abort
    const id = createAiRequestId(); setSubmitted({ id, target }); setPending(true); setResult(null); setMessage(null)
    try {
      const images = []
      for (const file of files) images.push({ name: file.name, contentType: file.type, base64: await readMediaFile(file, abort.signal) })
      const body: VisionRequest = { clientRequestId: id, expectedUserId: userId, prompt, systemPrompt: system || null,
        imageCount: images.length, images, modelId: selected || null,
        consent: { modelId: target.id, modelRevision: target.revision, endpoint: target.baseUrl, confirmed: true } }
      setResult(await aiApi.visionCall(body, abort.signal))
    } catch (error) { setMessage(abort.signal.aborted ? '已请求取消，请核对本次状态；图片是否已发送以记录为准。' : errorText(error) + '；请先核对记录，页面不会自动重发。') }
    finally { inFlight.current = false; setPending(false); changed() }
  }
  return <Card className="space-y-4 p-5">
    <div><h2 className="text-sm font-semibold text-text-1">多图视觉调用</h2><p className="mt-1 text-xs text-text-3">将正文与本次全部图片共同交给视觉模型，保留实际内容与图片指纹。原图另存入发起账号的默认文件来源，未连接云盘时使用内置存储。</p></div>
    <div><Label htmlFor="ai-vision-model">本次视觉模型</Label><Select id="ai-vision-model" ariaLabel="本次视觉模型" value={selected} disabled={locked}
      options={[{ value: '', label: '使用默认视觉模型' }, ...models.filter((m) => m.enabled && m.capabilities.includes('Vision')).map((m) => ({ value: m.id, label: m.name }))]}
      onValueChange={(value) => { setSelected(value); setConsentedTo('') }} /></div>
    {!ready && <p role="status" className="text-sm text-text-3">请先启用SDK直连并选择可用的视觉模型。</p>}
    <div><Label htmlFor="ai-vision-files">本次图片</Label><input ref={fileInput} id="ai-vision-files" className="sr-only" type="file" multiple accept="image/png,image/jpeg" disabled={locked}
      onChange={(e) => { const next = [...(e.target.files ?? [])]; e.target.value = ''; selectFiles(next) }} />
      <div className="mt-1"><Button variant="secondary" disabled={locked} onClick={() => fileInput.current?.click()}>选择图片</Button></div>
      <p className="mt-1 text-xs text-text-3">最多4张，单张4MiB、合计8MiB；顺序按下方编号保存。当前 {files.length} 张 · {(total / 1024 / 1024).toFixed(2)} MiB。</p></div>
    {files.length > 0 && <div className="grid grid-cols-2 gap-3 lg:grid-cols-4">{files.map((file, index) => <div key={`${selection}/${index}`} className="min-w-0 rounded-ctl border border-border p-2">
      <FilePreview file={file} /><p className="mt-2 truncate text-xs text-text-2">图{index + 1} · {file.name}</p><Button size="sm" variant="ghost" disabled={locked} onClick={() => selectFiles(files.filter((_, i) => i !== index))}>移除图{index + 1}</Button>
    </div>)}</div>}
    <div><Label htmlFor="ai-vision-system">视觉系统提示（可选）</Label><Textarea id="ai-vision-system" value={system} readOnly={locked} rows={2} maxLength={32000} onChange={(e) => setSystem(e.target.value)} /></div>
    <div><Label htmlFor="ai-vision-prompt">正文与图片问题</Label><Textarea id="ai-vision-prompt" value={prompt} readOnly={locked} rows={4} maxLength={200000} onChange={(e) => setPrompt(e.target.value)} placeholder="例如：结合图1照片与图2手写笔记，说明其中的数据关系" /></div>
    {target && <div className="space-y-2 rounded-ctl border border-border bg-surface p-3 text-xs text-text-2"><p>图片发送目标：{target.name} · {target.model}</p><p className="break-all font-mono">{target.baseUrl}</p>
      <p>本次原始图片和正文将由PIM服务器发送至该地址；可能产生模型费用。开始后使用固定配置，地址或版本变化时需重新核对。</p>
      <label className="flex items-start gap-2"><input type="checkbox" className="mt-0.5 size-4 shrink-0 accent-primary" checked={approvalKey !== '' && consentedTo === approvalKey} disabled={locked || !ready || files.length === 0}
        onChange={(e) => setConsentedTo(e.target.checked ? approvalKey : '')} /><span>确认将本次图片发送到上述模型地址</span></label></div>}
    <div className="flex flex-wrap gap-2"><Button variant="primary" loading={pending} disabled={locked || !ready || files.length === 0 || !prompt.trim() || prompt.length + system.length > 200000 || !approvalKey || consentedTo !== approvalKey} onClick={() => void send()}>发送图片并记录</Button>
      {pending && <Button variant="secondary" onClick={() => controller.current?.abort()}>取消视觉等待</Button>}
      {submitted && !pending && <><Button variant="secondary" onClick={() => { void aiApi.visionLookup(submitted.id).then((value) => { setResult(value); setMessage(null); changed() }).catch((e: unknown) => setMessage(errorText(e))) }}>核对视觉调用状态</Button>
        <Button variant="ghost" onClick={() => { setSubmitted(null); setConsentedTo(''); setResult(null); setMessage(null) }}>新建一次视觉调用</Button></>}
    </div>
    {message && <p role="alert" className="rounded-ctl bg-surface p-3 text-sm text-text-2">{message}</p>}
    {result && <div className="space-y-3 rounded-ctl border border-border p-4"><div className="flex flex-wrap items-center justify-between gap-2">{renderStatus(result.status)}<Button size="sm" variant="ghost" onClick={() => showDetail(result.logId)}>查看视觉调用完整记录</Button></div>
      {result.errorMessage && <p role="alert" className="text-sm text-text-2">{result.errorMessage}</p>}
      {result.responseText != null && <pre aria-label="实际视觉回复" className="max-h-80 overflow-auto whitespace-pre-wrap break-words text-sm text-text-1">{result.responseText}</pre>}
      {result.replayed && <p className="text-xs text-text-3">返回已有记录，本次没有重新执行模型调用。</p>}
    </div>}
  </Card>
}

export function CallImages({ logId, owner, viewer, running = false }: { logId: string; owner: string | null | undefined; viewer: string; running?: boolean }) {
  const allowed = owner === viewer
  const images = useQuery({ queryKey: ['ai', viewer, 'images', logId, running], queryFn: ({ signal }) => aiApi.images(logId, signal), enabled: allowed, refetchInterval: running ? 3000 : false })
  if (!allowed) return <p className="text-xs text-text-3">原始图片仅调用发起账号可查看。</p>
  if (images.isLoading) return <Skeleton className="h-32" />
  if (images.isError) return <EmptyState title="图片清单读取失败" description={errorText(images.error)} action={<Button onClick={() => void images.refetch()}>重试</Button>} />
  return <div className="space-y-2"><h3 className="text-sm font-medium text-text-1">本次图片材料（{images.data?.length ?? 0}）</h3>
    <p className="text-xs text-text-3">图片按原顺序保留；读取时核对原始指纹。文件被删除或改动会明确提示，调用文字记录仍保留。</p>
    <div className="grid gap-3 sm:grid-cols-2">{images.data?.map((image) => <StoredImage key={image.id} image={image} logId={logId} />)}</div>
  </div>
}

function StoredImage({ image, logId }: { image: CallImage; logId: string }) {
  const [url, setUrl] = useState(''); const [error, setError] = useState(''); const [retry, setRetry] = useState(0)
  useEffect(() => {
    if (!image.stored) return
    const abort = new AbortController(); let source = ''
    void aiApi.image(logId, image.id, abort.signal).then((blob) => {
      if (abort.signal.aborted) return
      source = URL.createObjectURL(blob); setUrl(source)
    }).catch((e: unknown) => { if (!abort.signal.aborted) setError(errorText(e)) })
    return () => { abort.abort(); if (source) URL.revokeObjectURL(source) }
  }, [logId, image.id, image.stored, retry])
  return <div className="min-w-0 space-y-2 rounded-ctl border border-border p-3"><p className="break-all text-xs font-medium text-text-1">图{image.index + 1} · {image.name}</p>
    {!image.stored ? <p className="text-xs text-text-3">本次材料尚未成功保存，尚无完整图片集的发送记录。</p> : error ? <div className="text-xs text-text-3"><p>图片无法读取或解码，可能已移除、改动或文件来源不可用。{error}</p><Button variant="ghost" size="sm" onClick={() => { setUrl(''); setError(''); setRetry((value) => value + 1) }}>重试图片</Button></div>
      : url ? <img src={url} alt={`本次输入图${image.index + 1}：${image.name}`} className="max-h-64 w-full rounded-ctl bg-surface object-contain" onError={() => setError('图片解码失败')} /> : <Skeleton className="h-28" />}
    <details className="text-xs text-text-3"><summary className="cursor-pointer">图片指纹</summary><p>{image.contentType} · {image.sizeBytes} bytes</p><p className="break-all font-mono">SHA256 {image.sha256}</p></details>
  </div>
}
