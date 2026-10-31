import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { Button, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, Input, Label, Select } from '@/components/ui'
import { notifyError } from '@/lib/notify'
import { aiApi, type AsrPreset, type Capability, type Model, type SaveModel } from './api'
import { asrAudioLimitLabel, asrModelOptionLabel, isMimoPro } from './asr-model-guidance'

export const capabilityNames: Record<Capability, string> = { Text: '文本', Vision: '视觉', Transcription: '语音转写' }
export const capabilities: Capability[] = ['Text', 'Vision', 'Transcription']

export function ModelEditor({ model, save, close, initialAsr = false }: { model: Model | null; save: (body: SaveModel) => Promise<unknown>; close: () => void; initialAsr?: boolean }) {
  const [name, setName] = useState(model?.name ?? '')
  const [baseUrl, setBaseUrl] = useState(model?.baseUrl ?? '')
  const [modelId, setModelId] = useState(model?.model ?? '')
  const [key, setKey] = useState('')
  const [enabled, setEnabled] = useState(model?.enabled ?? true)
  const [caps, setCaps] = useState<Capability[]>(model?.capabilities ?? (initialAsr ? ['Transcription'] : ['Text']))
  const [presetId, setPresetId] = useState(model?.asrPresetId ?? '')
  const [timeout, setTimeoutValue] = useState(model?.timeoutSeconds ?? 60)
  const [tokens, setTokens] = useState(model?.maxOutputTokens ?? 1024)
  const [saving, setSaving] = useState(false)
  const asr = caps.includes('Transcription')
  const endpointChanged = Boolean(model && baseUrl.trim().replace(/\/+$/, '') !== model.baseUrl.trim().replace(/\/+$/, ''))
  const keyRequired = !model || endpointChanged || !model.hasApiKey
  const presets = useQuery({ queryKey: ['ai-asr-presets'], queryFn: ({ signal }) => aiApi.asrPresets(signal), enabled: asr })
  const preset = presets.data?.find((p) => p.id === presetId)
  function choosePreset(value: string) {
    setPresetId(value); const next: AsrPreset | undefined = presets.data?.find((p) => p.id === value)
    if (!next) return
    setCaps(['Transcription']); setBaseUrl(next.defaultBaseUrl); setModelId(next.defaultModel); setKey('')
    if (!name) setName(next.name)
  }

  return <Dialog open onOpenChange={(open) => { if (!open && !saving) close() }}>
    <DialogContent showClose={!saving}>
      <DialogHeader title={model ? (asr ? '编辑 ASR 配置' : '编辑模型') : (asr ? '添加 ASR 配置' : '添加模型')}
        description={asr ? '语音转写接入预设 · 凭据仅保存于服务端' : 'OpenAI 兼容接口 · 凭据仅保存于服务端'} />
      <form className="flex min-h-0 flex-col" onSubmit={(event) => {
        event.preventDefault()
        if (!caps.length) { notifyError('请选择至少一种模型能力'); return }
        if (asr && !preset) { notifyError('请选择可用的ASR接入预设'); return }
        if (keyRequired && !key.trim()) { notifyError('API地址已变化，必须重新输入 API Key；不会沿用旧地址的凭据'); return }
        setSaving(true)
        void save({ name, baseUrl, model: modelId, enabled, capabilities: caps, timeoutSeconds: timeout, maxOutputTokens: tokens,
          apiKey: key || null, expectedRevision: model?.revision ?? null, ...(asr ? { asrPresetId: presetId } : {}) })
          .then(() => { setKey(''); close() })
          .catch((error: unknown) => notifyError(error instanceof Error ? error.message : '保存失败'))
          .finally(() => setSaving(false))
      }}>
        <DialogBody className="space-y-4">
          <div><Label htmlFor="ai-model-name">配置名称</Label><Input id="ai-model-name" required maxLength={120} value={name} onChange={(e) => setName(e.target.value)} placeholder="例如：常用文字模型" /></div>
          {asr && <div className="space-y-2"><Label htmlFor="ai-asr-preset">ASR接入预设</Label><Select id="ai-asr-preset" ariaLabel="ASR接入预设" value={presetId} disabled={saving || presets.isLoading}
            options={[{ value: '', label: '请选择预设' }, ...(presets.data ?? []).map((p) => ({ value: p.id, label: `${p.name} · v${p.version}` }))]} onValueChange={choosePreset} />
            {presets.isError && <p role="alert" className="text-xs text-text-3">ASR预设加载失败。<Button type="button" size="sm" variant="ghost" onClick={() => void presets.refetch()}>重试</Button></p>}
            <p className="text-xs text-text-3">预设集中定义接入方式、支持模型、格式和语言。切换预设会清空本次凭据输入；ASR使用独立配置。</p>
            {preset && <div className="rounded-ctl bg-surface px-3 py-2 text-xs leading-5 text-text-2">
              <p>支持格式：{preset.formats.join(' / ')} · {asrAudioLimitLabel(preset.maxAudioBytes)}</p>
              <p>上限按服务端解码后的音频大小校验；页面不会把未支持的URL导入方式显示为可用。</p>
            </div>}</div>}
          <div><Label htmlFor="ai-model-endpoint">API 根地址</Label><Input id="ai-model-endpoint" required type="url" maxLength={2048} value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="https://example.com/v1" />
            <p className="mt-1 text-xs text-text-3">地址由 PIM 服务器访问。填写实际前缀（如 /v1），不包含密钥或查询参数。</p></div>
          <div><Label htmlFor="ai-model-identifier">模型标识</Label>{asr && preset && preset.models.length > 1 ? <Select id="ai-model-identifier" ariaLabel="模型标识" value={modelId} options={preset.models.map((value) => ({ value, label: asrModelOptionLabel(value) }))} onValueChange={setModelId} />
            : <Input id="ai-model-identifier" required maxLength={128} value={modelId} readOnly={asr} onChange={(e) => setModelId(e.target.value)} placeholder="服务商提供的模型 ID" />}
            {asr && isMimoPro(modelId) && <p role="alert" className="mt-1 text-xs leading-5 text-warn">MiMo Pro 不推荐用于长音频；课堂录音等长材料请优先使用推荐的长音频模型 Flash。</p>}</div>
          <div><Label htmlFor="ai-model-key">API Key{model?.hasApiKey ? '（已保存）' : ''}</Label><Input id="ai-model-key" required={keyRequired} type="password" autoComplete="new-password" maxLength={4096} value={key} onChange={(e) => setKey(e.target.value)} />
            <p className="mt-1 text-xs text-text-3">{endpointChanged ? 'API 地址已变化，必须重新输入 API Key；不会沿用旧地址的凭据。' : '保存后不再回显；地址不变时编辑可留空保留原凭据。无鉴权的本地服务可填写非空占位值。'}</p></div>
          <fieldset><legend className="mb-2 text-sm font-medium text-text-1">声明能力</legend><div className="flex flex-wrap gap-4">
            {capabilities.map((cap) => <label key={cap} className="flex items-center gap-2 text-sm text-text-2"><input type="checkbox" checked={caps.includes(cap)} disabled={Boolean(model?.asrPresetId)} onChange={(e) => {
              if (cap === 'Transcription') { setCaps(e.target.checked ? ['Transcription'] : ['Text']); setPresetId('') }
              else { setPresetId(''); setCaps((old) => e.target.checked ? [...old.filter((c) => c !== 'Transcription'), cap] : old.filter((c) => c !== cap)) }
            }} />{capabilityNames[cap]}</label>)}
          </div><p className="mt-2 text-xs text-text-3">文本与视觉可共用模型；ASR通过预设接入并隔离凭据。</p></fieldset>
          <div className="grid grid-cols-2 gap-3"><div><Label htmlFor="ai-model-timeout">超时（秒）</Label><Input id="ai-model-timeout" type="number" required min={1} max={600} value={timeout} onChange={(e) => setTimeoutValue(Number(e.target.value))} /></div>
            {!asr && <div><Label htmlFor="ai-model-output">输出 token 上限</Label><Input id="ai-model-output" type="number" required min={1} max={131072} value={tokens} onChange={(e) => setTokens(Number(e.target.value))} /></div>}</div>
          <label className="flex items-center gap-2 text-sm text-text-2"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />启用此模型配置</label>
        </DialogBody>
        <DialogFooter><Button type="button" variant="secondary" disabled={saving} onClick={close}>取消</Button><Button type="submit" variant="primary" loading={saving}>保存模型</Button></DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
}
