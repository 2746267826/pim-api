import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { RefreshCw } from 'lucide-react'
import { Button, Card, Checkbox, InlineAlert, PageHeader, Skeleton, Switch } from '@/components/ui'
import { useAuth } from '@/features/auth/auth-context'
import { notifyError, notifySuccess } from '@/lib/notify'
import { noteProcessingApi } from './api'
import type { NoteProcessingPreferences } from './api'

export function NoteProcessingSettingsPage() {
  const { user } = useAuth()
  const queryClient = useQueryClient()
  const [busy, setBusy] = useState(false)
  const [authorizing, setAuthorizing] = useState(false)
  const [authorizationConfirmed, setAuthorizationConfirmed] = useState(false)
  const [actionError, setActionError] = useState('')
  const queryKey = ['note-processing-preferences', user?.id ?? '']
  const preferences = useQuery({
    queryKey,
    queryFn: ({ signal }) => noteProcessingApi.preferences(signal),
    enabled: Boolean(user),
  })

  async function save(autoProcessingEnabled: boolean, confirmStanding = false) {
    if (!preferences.data) return
    setBusy(true)
    setActionError('')
    try {
      const saved = await noteProcessingApi.savePreferences({
        expectedRevision: preferences.data.revision,
        autoProcessingEnabled,
        confirmStandingAuthorization: confirmStanding,
        acknowledgePossibleCharge: confirmStanding,
        ...(autoProcessingEnabled
          ? { defaultTargetRevision: preferences.data.currentDefaultTargetRevision }
          : {}),
      })
      queryClient.setQueryData(queryKey, saved)
      setAuthorizing(false)
      setAuthorizationConfirmed(false)
      notifySuccess(autoProcessingEnabled
        ? '自动处理已开启；每篇材料会冻结并持久化实际目标、材料范围与费用提示，可在处理页核对'
        : '自动处理已关闭；已清除持续授权')
    } catch (error) {
      const message = error instanceof Error ? error.message : '笔记处理设置保存失败，请刷新核对'
      setActionError(message)
      notifyError(message)
      await preferences.refetch()
    } finally {
      setBusy(false)
    }
  }

  function toggleAuto(checked: boolean) {
    if (!checked) {
      setAuthorizing(false)
      setAuthorizationConfirmed(false)
      void save(false)
      return
    }
    if (preferences.data?.standingAuthorizationValid) {
      void save(true)
      return
    }
    setActionError('')
    setAuthorizationConfirmed(false)
    setAuthorizing(true)
  }

  return <div className="space-y-5">
    <PageHeader
      title="笔记处理"
      subtitle="结束记录后的服务器处理偏好"
      actions={<Button variant="secondary" size="sm" disabled={preferences.isFetching} onClick={() => void preferences.refetch()}>
        <RefreshCw className="size-4" aria-hidden />刷新
      </Button>}
    />
    {preferences.isPending && <Skeleton className="h-36" />}
    {preferences.isError && <InlineAlert tone="crit" title="笔记处理设置读取失败">
      {preferences.error instanceof Error ? preferences.error.message : '请求失败，请刷新核对'}
      <Button size="sm" variant="ghost" onClick={() => void preferences.refetch()}>重试</Button>
    </InlineAlert>}
    {preferences.data && <Card className="space-y-4 p-5">
      <div className="flex flex-wrap items-start justify-between gap-4">
        <div className="min-w-0">
          <h2 className="text-sm font-semibold text-text-1">结束后自动处理笔记</h2>
          <p className="mt-1 max-w-[680px] text-xs leading-5 text-text-3">
            开启前必须确认处理目标、材料范围与可能费用。开启后，完整材料收到服务器回执时才会按已确认的持续授权创建转写和整理任务；没有有效授权或默认目标不可用时材料保持待发起，不会自动更换目标或创建付费任务。
          </p>
        </div>
        <Switch
          id="note-processing-auto"
          checked={preferences.data.autoProcessingEnabled}
          disabled={busy}
          onCheckedChange={toggleAuto}
          ariaLabel="结束后自动处理笔记"
        />
      </div>
      <p className="text-xs leading-5 text-text-3">
        当前状态：{preferences.data.autoProcessingEnabled ? '已开启' : '已关闭'}。
        {preferences.data.standingAuthorizationValid
          ? ' 已有有效持续授权；每篇材料会冻结并持久化实际目标，可在处理页核对。'
          : ' 当前没有有效持续授权，自动处理不会创建付费任务。'}
      </p>
      {preferences.data.autoProcessingEnabled && !preferences.data.standingAuthorizationValid &&
        <InlineAlert tone="warn" title="自动处理缺少有效持续授权">
          当前设置不会创建新的付费任务。重新确认处理目标、材料范围与费用提示后才能恢复自动处理。
          <Button size="sm" variant="ghost" onClick={() => {
            setActionError('')
            setAuthorizationConfirmed(false)
            setAuthorizing(true)
          }}>
            补充持续授权
          </Button>
        </InlineAlert>}
      <div className="space-y-1 rounded-ctl border border-border bg-bg p-3 text-xs leading-5 text-text-2">
        <DefaultTargets preferences={preferences.data} />
        <p><span className="font-medium text-text-1">处理目的：</span>{preferences.data.purposeText}</p>
        <p><span className="font-medium text-text-1">材料范围：</span>{preferences.data.materialScopeText}</p>
        <p className="text-warn">{preferences.data.costNotice}</p>
        <p className="break-all font-mono text-[11px] text-text-3">当前目标策略 revision：{preferences.data.currentDefaultTargetRevision}</p>
        {preferences.data.standingAuthorizationTargetRevision &&
          <p className="break-all font-mono text-[11px] text-text-3">持续授权目标 revision：{preferences.data.standingAuthorizationTargetRevision}</p>}
        {preferences.data.standingAuthorizationRevision
          ? <p className="break-all font-mono text-[11px] text-text-3">持续授权 revision：{preferences.data.standingAuthorizationRevision}</p>
          : <p className="text-text-3">持续授权 revision：未建立</p>}
      </div>
      {actionError && <InlineAlert tone="crit" title="自动处理设置未保存">{actionError}</InlineAlert>}
    </Card>}
    {authorizing && preferences.data && <Card className="space-y-3 p-5" aria-label="确认持续授权">
      <div>
        <h2 className="text-sm font-semibold text-text-1">确认持续授权</h2>
        <p className="mt-1 text-xs leading-5 text-text-3">
          确认后才会保存账号级持续授权。后续每篇材料仍会生成独立计划并冻结实际目标；处理目的、材料范围、费用提示，或账号默认模型、预设、路由、端点配置变化时，授权会失效并要求重新确认。
        </p>
      </div>
      <div className="space-y-1 rounded-ctl border border-border bg-bg p-3 text-xs leading-5 text-text-2">
        <DefaultTargets preferences={preferences.data} />
        <p><span className="font-medium text-text-1">处理目的：</span>{preferences.data.purposeText}</p>
        <p><span className="font-medium text-text-1">材料范围：</span>{preferences.data.materialScopeText}</p>
        <p className="text-warn">{preferences.data.costNotice}</p>
        <p className="break-all font-mono text-[11px] text-text-3">目标策略 revision：{preferences.data.currentDefaultTargetRevision}</p>
      </div>
      <label className="flex items-start gap-2 text-xs leading-5 text-text-2">
        <Checkbox checked={authorizationConfirmed}
          onCheckedChange={(checked) => setAuthorizationConfirmed(checked)} className="mt-0.5"
          ariaLabel="确认持续授权、材料范围与可能费用" />
        <span>我已核对处理目标、材料范围与费用提示，并同意在材料完整后自动创建付费转写与整理任务。</span>
      </label>
      <div className="flex flex-wrap gap-2">
        <Button variant="primary" loading={busy}
          disabled={!authorizationConfirmed}
          onClick={() => void save(true, true)}>
          确认并开启自动处理
        </Button>
        <Button variant="ghost" disabled={busy} onClick={() => {
          setAuthorizing(false)
          setAuthorizationConfirmed(false)
        }}>取消</Button>
      </div>
    </Card>}
  </div>
}

function DefaultTargets({ preferences }: { preferences: NoteProcessingPreferences }) {
  return <div className="space-y-1">
    <p className="font-medium text-text-1">账号默认处理目标：</p>
    {preferences.defaultTargets.length > 0
      ? <ul className="list-disc space-y-1 pl-5">
        {preferences.defaultTargets.map((target) => <li key={target} className="break-words">{target}</li>)}
      </ul>
      : <p className="text-text-3">账号默认模型尚未配置；自动处理会保持待发起。</p>}
  </div>
}
