import { useEffect, useState } from 'react'
import { Button, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, InlineAlert, Label, Select, StatusBadge } from '@/components/ui'
import { aiApi, type ConnectionTestResult, type Model, type Capability } from './api'
import { createAiRequestId } from './request-id'

const capabilityNames: Record<Capability, string> = {
  Text: '文本',
  Vision: '视觉',
  Transcription: '语音转写',
}

export const connectionTestStatusNames: Record<ConnectionTestResult['status'], string> = {
  Succeeded: '连接成功',
  Failed: '连接失败',
  TimedOut: '测试超时',
  Blocked: '未发送服务商',
  CapabilityMismatch: '能力不匹配',
  Running: '测试进行中',
  Cancelled: '测试已取消',
  Unknown: '结果未确认',
}

const probeNames: Record<string, string> = {
  'text-minimal': '最小文本请求',
  'vision-32x32-png': '最小合成图片请求',
  'asr-speech-mp3': '内置短语音转写请求',
}

function tone(result: ConnectionTestResult | null) {
  if (!result) return 'info' as const
  if (result.status === 'Succeeded') return 'ok' as const
  if (result.status === 'Blocked' || result.status === 'CapabilityMismatch' ||
    result.status === 'Running' || result.status === 'Unknown') return 'warn' as const
  return 'crit' as const
}

/**
 * Tests a saved model revision. The browser never sees or sends the API key, and saving a model
 * does not trigger a test automatically: every provider attempt is an explicit admin action.
 */
export function ConnectionTestDialog({ model, close, onShowLog }: {
  model: Model
  close: () => void
  onShowLog?: (logId: string) => void
}) {
  const testable = model.capabilities.filter((capability) => capability in capabilityNames)
  const [capability, setCapability] = useState<Capability>(testable[0] ?? 'Text')
  const [confirmed, setConfirmed] = useState(false)
  const [requestId, setRequestId] = useState(() => createAiRequestId())
  const [busy, setBusy] = useState(false)
  const [loadingLatest, setLoadingLatest] = useState(false)
  const [attempted, setAttempted] = useState(false)
  const [result, setResult] = useState<ConnectionTestResult | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    let active = true
    setLoadingLatest(true)
    setResult(null)
    setError('')
    void aiApi.connectionTestLatest(model.id, model.revision, capability)
      .then((latest) => {
        if (!active || !latest) return
        setResult(latest)
        setAttempted(true)
      })
      .catch((failure: unknown) => {
        if (active) setError(failure instanceof Error
          ? `读取最近测试记录失败：${failure.message}`
          : '读取最近测试记录失败，请稍后重试。')
      })
      .finally(() => { if (active) setLoadingLatest(false) })
    return () => { active = false }
  }, [capability, model.id, model.revision])

  async function test(id: string) {
    if (busy || !confirmed) return
    setBusy(true); setError(''); setAttempted(true)
    try {
      const value = await aiApi.testConnection(model.id, {
        clientRequestId: id,
        expectedRevision: model.revision,
        capability,
      })
      setResult(value)
    } catch (failure) {
      setError(failure instanceof Error
        ? `${failure.message}。测试请求标识已保留；若结果未确认，请用同一标识重试核对，不要改用其他标识。`
        : '测试失败，请刷新核对；不会自动再次发送。')
    } finally {
      setBusy(false)
    }
  }

  async function check(requestIdentity: string) {
    if (busy) return
    setBusy(true); setError('')
    try {
      const latest = await aiApi.connectionTestLatest(model.id, model.revision, capability, requestIdentity)
      if (!latest) {
        setResult(null)
        setError('尚无本次请求的测试结果；原请求可能仍在处理或被服务端拒绝。请保留原请求标识，稍后再核对。')
        return
      }
      if (latest.clientRequestId !== requestIdentity || latest.modelRevision !== model.revision ||
        latest.capability !== capability) {
        setResult(null)
        setError('核对结果与本次请求标识不一致，已拒绝显示其他请求或修订的结果；请保留原请求标识后再核对。')
        return
      }
      setResult(latest)
    } catch (failure) {
      setError(failure instanceof Error ? `核对失败：${failure.message}` : '核对失败，请稍后重试。')
    } finally {
      setBusy(false)
    }
  }

  function restart() {
    setRequestId(createAiRequestId())
    setResult(null)
    setError('')
    setConfirmed(false)
    setAttempted(false)
  }

  return <Dialog open onOpenChange={(open) => { if (!open && !busy) close() }}>
    <DialogContent showClose={!busy}>
      <DialogHeader title="测试模型连接"
        description="使用已保存的服务地址、模型标识和凭据发送一次最小真实探测；保存配置不会自动测试。" />
      <DialogBody className="space-y-4">
        <div className="rounded-ctl border border-border bg-surface px-3 py-2 text-xs leading-5 text-text-2">
          <p className="font-medium text-text-1">{model.name}</p>
          <p className="break-all font-mono">{model.model}</p>
          <p className="break-all">{model.baseUrl}</p>
          <p>凭据：{model.hasApiKey ? '已保存，服务端解密后使用，不会回显' : '未保存，测试将直接失败'}</p>
        </div>
        <div>
          <Label htmlFor="ai-connection-test-capability">测试能力</Label>
          <Select id="ai-connection-test-capability" ariaLabel="测试能力" value={capability}
            disabled={busy || loadingLatest || testable.length === 0 || (attempted && !result)}
            options={testable.map((value) => ({ value, label: capabilityNames[value] }))}
            onValueChange={(value) => {
              setCapability(value as Capability)
              setRequestId(createAiRequestId())
              setConfirmed(false)
              setAttempted(false)
              setResult(null)
              setError('')
            }} />
          {!testable.length && <p role="alert" className="mt-1 text-xs text-text-3">该配置没有可测试能力，请先编辑并保存能力。</p>}
        </div>
        <InlineAlert tone="warn" title="可能产生服务商费用">
          测试会实际请求服务商：文本发送最小提示，视觉发送 32×32 合成图片，语音转写发送内置短语音并核对可读转写。
          只有服务商返回可解析且与样例匹配的结果才会显示成功；超时或失败仍可能已计费。
        </InlineAlert>
        <label className="flex items-start gap-2 text-xs leading-5 text-text-2">
          <input type="checkbox" className="mt-0.5 size-4 shrink-0 accent-primary"
            checked={confirmed} disabled={busy || loadingLatest || Boolean(result) || attempted || !testable.length}
            onChange={(event) => setConfirmed(event.target.checked)} />
          <span>我已核对模型、地址与能力，理解测试可能产生服务商费用。</span>
        </label>
        {loadingLatest && <p role="status" className="text-xs text-text-3">正在读取当前修订的最近测试记录…</p>}
        {error && <p role="alert" className="rounded-ctl bg-warn-soft p-3 text-xs leading-5 text-text-2">{error}</p>}
        {result && <div className="space-y-2 rounded-ctl border border-border p-3 text-xs leading-5 text-text-2">
          <div className="flex flex-wrap items-center gap-2">
            <StatusBadge dot={false} tone={tone(result)}>{connectionTestStatusNames[result.status]}</StatusBadge>
            <span>{probeNames[result.probe] ?? result.probe}</span>
            <span>{result.durationMs} ms</span>
            {result.replayed && <span>· 已有记录</span>}
          </div>
          <p>{result.providerAttempted ? '服务商请求已实际发出。' : '本次未发送服务商请求，不产生服务商费用。'}</p>
          {result.responsePreview && <p className="break-all rounded-ctl bg-surface p-2 font-mono">{result.responsePreview}</p>}
          {result.errorMessage && <p role="alert">{result.errorMessage}{result.errorCode ? `（${result.errorCode}）` : ''}</p>}
          <div className="flex flex-wrap items-center justify-between gap-2">
            <span className="break-all font-mono">调用记录 {result.logId}</span>
            {onShowLog && <Button size="sm" variant="ghost" onClick={() => onShowLog(result.logId)}>查看调用记录</Button>}
          </div>
        </div>}
      </DialogBody>
      <DialogFooter>
        <Button type="button" variant="secondary" disabled={busy} onClick={close}>关闭</Button>
        {result
          ? <div className="flex flex-wrap gap-2">
              {result.status === 'Running' && <Button type="button" variant="secondary" loading={busy}
                onClick={() => void check(result.clientRequestId)}>核对最新结果</Button>}
              <Button type="button" variant="primary" loading={busy} onClick={restart}>重新测试（新请求）</Button>
            </div>
          : attempted
            ? <div className="flex flex-wrap gap-2">
                <Button type="button" variant="secondary" loading={busy}
                  onClick={() => void check(requestId)}>用同一标识核对</Button>
                <Button type="button" variant="primary" loading={busy}
                  onClick={restart}>重新测试（新请求）</Button>
              </div>
          : <Button type="button" variant="primary" loading={busy}
              disabled={!confirmed || !testable.length} onClick={() => void test(requestId)}>开始测试</Button>}
      </DialogFooter>
    </DialogContent>
  </Dialog>
}
