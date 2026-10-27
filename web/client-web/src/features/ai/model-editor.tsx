import { useState } from 'react'
import { Button, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, Input, Label } from '@/components/ui'
import { notifyError } from '@/lib/notify'
import type { Capability, Model, SaveModel } from './api'

export const capabilityNames: Record<Capability, string> = { Text: '文本', Vision: '视觉', Transcription: '语音转写' }
export const capabilities: Capability[] = ['Text', 'Vision', 'Transcription']

export function ModelEditor({ model, save, close }: { model: Model | null; save: (body: SaveModel) => Promise<unknown>; close: () => void }) {
  const [name, setName] = useState(model?.name ?? '')
  const [baseUrl, setBaseUrl] = useState(model?.baseUrl ?? '')
  const [modelId, setModelId] = useState(model?.model ?? '')
  const [key, setKey] = useState('')
  const [enabled, setEnabled] = useState(model?.enabled ?? true)
  const [caps, setCaps] = useState<Capability[]>(model?.capabilities ?? ['Text'])
  const [timeout, setTimeoutValue] = useState(model?.timeoutSeconds ?? 60)
  const [tokens, setTokens] = useState(model?.maxOutputTokens ?? 1024)
  const [saving, setSaving] = useState(false)

  return <Dialog open onOpenChange={(open) => { if (!open && !saving) close() }}>
    <DialogContent showClose={!saving}>
      <DialogHeader title={model ? '编辑模型' : '添加模型'} description="OpenAI 兼容接口 · 凭据仅保存于服务端" />
      <form className="flex min-h-0 flex-col" onSubmit={(event) => {
        event.preventDefault()
        if (!caps.length) { notifyError('请选择至少一种模型能力'); return }
        setSaving(true)
        void save({ name, baseUrl, model: modelId, enabled, capabilities: caps, timeoutSeconds: timeout, maxOutputTokens: tokens,
          apiKey: key || null, expectedRevision: model?.revision ?? null })
          .then(() => { setKey(''); close() })
          .catch((error: unknown) => notifyError(error instanceof Error ? error.message : '保存失败'))
          .finally(() => setSaving(false))
      }}>
        <DialogBody className="space-y-4">
          <div><Label htmlFor="ai-model-name">配置名称</Label><Input id="ai-model-name" required maxLength={120} value={name} onChange={(e) => setName(e.target.value)} placeholder="例如：常用文字模型" /></div>
          <div><Label htmlFor="ai-model-endpoint">API 根地址</Label><Input id="ai-model-endpoint" required type="url" maxLength={2048} value={baseUrl} onChange={(e) => setBaseUrl(e.target.value)} placeholder="https://example.com/v1" />
            <p className="mt-1 text-xs text-text-3">地址由 PIM 服务器访问。填写实际前缀（如 /v1），不包含密钥或查询参数。</p></div>
          <div><Label htmlFor="ai-model-identifier">模型标识</Label><Input id="ai-model-identifier" required maxLength={128} value={modelId} onChange={(e) => setModelId(e.target.value)} placeholder="服务商提供的模型 ID" /></div>
          <div><Label htmlFor="ai-model-key">API Key{model?.hasApiKey ? '（已保存）' : ''}</Label><Input id="ai-model-key" required={!model} type="password" autoComplete="new-password" maxLength={4096} value={key} onChange={(e) => setKey(e.target.value)} />
            <p className="mt-1 text-xs text-text-3">保存后不再回显；编辑时留空保留。无鉴权的本地服务可填写非空占位值。</p></div>
          <fieldset><legend className="mb-2 text-sm font-medium text-text-1">声明能力</legend><div className="flex flex-wrap gap-4">
            {capabilities.map((cap) => <label key={cap} className="flex items-center gap-2 text-sm text-text-2"><input type="checkbox" checked={caps.includes(cap)} onChange={(e) => setCaps((old) => e.target.checked ? [...old, cap] : old.filter((c) => c !== cap))} />{capabilityNames[cap]}</label>)}
          </div><p className="mt-2 text-xs text-text-3">同一模型可声明多种能力。本批提供文本调用，视觉和语音转写调用将在后续接入。</p></fieldset>
          <div className="grid grid-cols-2 gap-3"><div><Label htmlFor="ai-model-timeout">超时（秒）</Label><Input id="ai-model-timeout" type="number" required min={1} max={600} value={timeout} onChange={(e) => setTimeoutValue(Number(e.target.value))} /></div>
            <div><Label htmlFor="ai-model-output">输出 token 上限</Label><Input id="ai-model-output" type="number" required min={1} max={131072} value={tokens} onChange={(e) => setTokens(Number(e.target.value))} /></div></div>
          <label className="flex items-center gap-2 text-sm text-text-2"><input type="checkbox" checked={enabled} onChange={(e) => setEnabled(e.target.checked)} />启用此模型配置</label>
        </DialogBody>
        <DialogFooter><Button type="button" variant="secondary" disabled={saving} onClick={close}>取消</Button><Button type="submit" variant="primary" loading={saving}>保存模型</Button></DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
}
