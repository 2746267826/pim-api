import { useRef, useState } from 'react'
import { Button, Card, Checkbox, InlineAlert, Label, Select, Skeleton } from '@/components/ui'
import type { RouteConfigModelOption, RouteConfigSaveRequest, RouteConfigView } from './types'

export interface RouteConfigEditorProps {
  config: RouteConfigView | null
  accountScope: string
  loading: boolean
  loadError: string | null
  onReload: () => void
  localModels: RouteConfigModelOption[]
  cloudModels: RouteConfigModelOption[]
  onSave?: (request: RouteConfigSaveRequest) => Promise<void> | void
  readOnly?: boolean
  disabled?: boolean
}

type Choice = { state: 'unset' } | { state: 'set'; id: string }
type ConfirmState = { identity: string; value: boolean }
const unset: Choice = { state: 'unset' }
const set = (id: string): Choice => ({ state: 'set', id })
const valueFor = (choice: Choice, fallback: string | null) =>
  choice.state === 'set' ? choice.id : fallback ?? ''
const emptySelection = (choice: Choice, fallback: string | null) =>
  choice.state === 'set' && choice.id === '' && !fallback

const routeOptions = [
  { value: 'local', label: '默认本机执行' },
  { value: 'cloud', label: '默认云端执行' },
]

const modelOptions = (
  models: RouteConfigModelOption[],
  emptyLabel: string,
): { value: string; label: string }[] => [
  { value: '', label: emptyLabel },
  ...models.map((model) => ({
    value: model.id,
    label: `${model.label}${model.enabled ? '' : '（已停用）'}${model.revision ? ` · ${model.revision}` : ''}`,
  })),
]

const usable = (model: RouteConfigModelOption | undefined) =>
  Boolean(model?.enabled && model.revision)

export function RouteConfigEditor({
  config,
  accountScope,
  loading,
  loadError,
  onReload,
  localModels,
  cloudModels,
  onSave,
  readOnly = false,
  disabled = false,
}: RouteConfigEditorProps) {
  const [draft, setDraft] = useState<string | null>(null)
  const [defaultRouteId, setDefaultRouteId] = useState('')
  const [localModelId, setLocalModelId] = useState<Choice>(unset)
  const [cloudModelId, setCloudModelId] = useState<Choice>(unset)
  const [confirmState, setConfirmState] = useState<ConfirmState | null>(null)
  const [busy, setBusy] = useState(false)
  const [notice, setNotice] = useState('')
  const [saveError, setSaveError] = useState('')
  const [saveScope, setSaveScope] = useState<string | null>(null)
  const submitting = useRef(false)

  if (loading && !config) return <Card className="p-5"><Skeleton className="h-48" /></Card>

  if (loadError || !config) {
    return <Card className="space-y-3 p-5">
      <h2 className="text-sm font-semibold text-text-1">转写路由设置</h2>
      <InlineAlert tone="crit" title="路由设置读取失败">
        {loadError ?? '当前没有可用的路由配置。'}；不会自动回退或改写默认路由。
      </InlineAlert>
      <Button size="sm" variant="secondary" onClick={onReload}>重新读取</Button>
    </Card>
  }

  const identity = `${accountScope}\n${config.revision}\n${config.consentVersion}`
  const draftCurrent = draft === null || draft === identity
  const confirmed = confirmState?.identity === identity && confirmState.value
  const setConfirmed = (value: boolean) => setConfirmState({ identity, value })
  const selectedRoute = draftCurrent && defaultRouteId ? defaultRouteId : config.defaultRouteId
  const selectedLocal = valueFor(draftCurrent ? localModelId : unset, config.localModelId)
  const selectedCloud = valueFor(draftCurrent ? cloudModelId : unset, config.cloudModelId)
  const localProfile = localModels.find((model) => model.id === selectedLocal && usable(model))
  const cloudProfile = cloudModels.find((model) => model.id === selectedCloud && usable(model) &&
    model.cloudConfigured !== false)
  const cloudReadyNow = Boolean(cloudProfile && (config.cloudConfigured || cloudProfile.cloudConfigured === true))
  const defaultLocalBlocked = selectedRoute === 'local' && !localProfile
  const defaultCloudBlocked = selectedRoute === 'cloud' && !cloudReadyNow
  const locked = readOnly || disabled || busy || !onSave
  const canSave = Boolean(!locked && !defaultLocalBlocked && !defaultCloudBlocked && confirmed && draftCurrent)
  const hasConfiguredCloudProfile = cloudModels.some((model) => usable(model) && model.cloudConfigured !== false)

  function edit(action: () => void) {
    if (!draftCurrent) {
      setDefaultRouteId('')
      setLocalModelId(unset)
      setCloudModelId(unset)
    }
    setDraft(identity)
    setNotice('')
    setSaveError('')
    setSaveScope(null)
    setConfirmed(false)
    action()
  }

  async function save() {
    if (!canSave || !onSave || submitting.current || !config) return
    const currentConfig = config
    const submittedIdentity = identity
    const currentLocal = draftCurrent ? localModelId : unset
    const currentCloud = draftCurrent ? cloudModelId : unset
    submitting.current = true
    setBusy(true)
    setNotice('')
    setSaveError('')
    setSaveScope(submittedIdentity)
    try {
      const request: RouteConfigSaveRequest = {
        accountScope,
        expectedRevision: currentConfig.revision,
        defaultRouteId: selectedRoute,
        consentVersion: currentConfig.consentVersion,
        confirmed: true,
      }
      if (currentLocal.state === 'set' && !emptySelection(currentLocal, currentConfig.localModelId)) {
        request.localModelId = currentLocal.id || null
        request.localModelRevision = currentLocal.id ? localProfile?.revision ?? null : null
      }
      if (currentCloud.state === 'set' && !emptySelection(currentCloud, currentConfig.cloudModelId)) {
        request.cloudModelId = currentCloud.id || null
      }
      if (selectedRoute === 'local' && emptySelection(currentLocal, currentConfig.localModelId)) {
        if (currentConfig.defaultRouteId === 'local') request.localModelId = null
        else request.localModelId = undefined
      } else if (selectedRoute === 'local') {
        request.localModelId = selectedLocal
        request.localModelRevision = localProfile?.revision ?? null
      } else {
        request.cloudModelId = selectedCloud
      }
      await onSave(request)
      setNotice('路由设置已保存；新任务按新设置创建，已存在的任务保持原目标。')
      setConfirmState(null)
    } catch (error) {
      setSaveError(`${error instanceof Error ? error.message : '保存失败'}；请核对最新配置后再手动保存，页面不会自动重试或覆盖他人修改。`)
    } finally {
      submitting.current = false
      setBusy(false)
    }
  }

  return <Card className="space-y-4 p-5">
    <div>
      <h2 className="text-sm font-semibold text-text-1">转写路由设置</h2>
      <p className="mt-1 text-xs text-text-3">
        配置版本 {config.consentVersion} · 更新于 {config.updatedAtLabel} · 保存时会携带当前版本号与账号作用域，冲突时需要重新核对。
      </p>
    </div>
    <div className="max-w-sm"><Label htmlFor="note-asr-default-route">默认转写路由</Label>
      <Select id="note-asr-default-route" ariaLabel="默认转写路由" value={selectedRoute}
        options={routeOptions} disabled={locked}
        onValueChange={(value) => edit(() => setDefaultRouteId(value))} /></div>
    <div className="grid gap-3 sm:grid-cols-2">
      <div><Label htmlFor="note-asr-local-model">本机模型（可留空）</Label>
        <Select id="note-asr-local-model" ariaLabel="本机模型" value={selectedLocal}
          options={modelOptions(localModels, '未选择本机模型')} disabled={locked}
          onValueChange={(value) => edit(() => setLocalModelId(set(value)))} /></div>
      <div><Label htmlFor="note-asr-cloud-model">云端模型</Label>
        <Select id="note-asr-cloud-model" ariaLabel="云端模型" value={selectedCloud}
          options={modelOptions(cloudModels, '未选择云端模型')} disabled={locked}
          onValueChange={(value) => edit(() => setCloudModelId(set(value)))} /></div>
    </div>
    <p className="text-xs text-text-3">
      本机与云端配置彼此独立保存；默认路由只决定新任务默认使用哪一个。默认本机不要求云端密钥，默认云端必须有已配置的可用模型。
    </p>
    {defaultLocalBlocked && <InlineAlert tone="warn" title="本机默认路由缺少可用模型">
      请选择一个已启用、带版本标识的本机模型；也可以切换为已配置的云端默认路由。
    </InlineAlert>}
    {defaultCloudBlocked && <InlineAlert tone="warn" title="云端尚未配置">
      {hasConfiguredCloudProfile
        ? '已有可用云端配置；选择该配置并保存云端模型后，才能把默认路由改为云端。'
        : '管理员还没有配置可用的云端模型；不能把默认路由改为云端，也不会自动回退到本机。'}
    </InlineAlert>}
    <label className="flex items-start gap-2 text-xs text-text-2">
      <Checkbox ariaLabel="确认保存路由设置" checked={confirmed} disabled={locked}
        onCheckedChange={setConfirmed} />
      <span>确认按当前配置保存路由；已有任务与历史结果保持不变。</span>
    </label>
    <div className="flex flex-wrap gap-2">
      <Button variant="primary" loading={busy} disabled={!canSave} onClick={() => void save()}>保存路由设置</Button>
      <Button variant="secondary" disabled={locked} onClick={() => edit(() => {
        setDefaultRouteId('')
        setLocalModelId(unset)
        setCloudModelId(unset)
        setNotice('')
        setSaveError('')
      })}>还原当前配置</Button>
    </div>
    {notice && saveScope === identity && <InlineAlert tone="ok">{notice}</InlineAlert>}
    {saveError && saveScope === identity && <InlineAlert tone="crit" title="保存未确认">{saveError}</InlineAlert>}
  </Card>
}
