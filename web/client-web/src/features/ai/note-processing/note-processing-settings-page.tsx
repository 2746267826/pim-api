import { useState } from 'react'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { RefreshCw } from 'lucide-react'
import { Button, Card, InlineAlert, PageHeader, Skeleton, Switch } from '@/components/ui'
import { useAuth } from '@/features/auth/auth-context'
import { notifyError, notifySuccess } from '@/lib/notify'
import { noteProcessingApi } from './api'

export function NoteProcessingSettingsPage() {
  const { user } = useAuth()
  const queryClient = useQueryClient()
  const [busy, setBusy] = useState(false)
  const queryKey = ['note-processing-preferences', user?.id ?? '']
  const preferences = useQuery({
    queryKey,
    queryFn: ({ signal }) => noteProcessingApi.preferences(signal),
    enabled: Boolean(user),
  })

  async function save(autoProcessingEnabled: boolean) {
    if (!preferences.data) return
    setBusy(true)
    try {
      const saved = await noteProcessingApi.savePreferences({
        expectedRevision: preferences.data.revision,
        autoProcessingEnabled,
      })
      queryClient.setQueryData(queryKey, saved)
      notifySuccess('笔记处理设置已保存')
    } catch (error) {
      notifyError(error instanceof Error ? error.message : '笔记处理设置保存失败，请刷新核对')
      await preferences.refetch()
    } finally {
      setBusy(false)
    }
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
            开启后，当前笔记的完整材料收到服务器回执时自动创建录音转写和多模态整理两条任务。关闭时保留“待发起处理”，由本篇状态页手动发起。
          </p>
        </div>
        <Switch
          id="note-processing-auto"
          checked={preferences.data.autoProcessingEnabled}
          disabled={busy}
          onCheckedChange={(checked) => void save(checked)}
          aria-label="结束后自动处理笔记"
        />
      </div>
      <p className="text-xs leading-5 text-text-3">
        当前状态：{preferences.data.autoProcessingEnabled ? '已开启' : '已关闭'}。默认模型和转写路由仍由管理员与本账号设置分别管理。
      </p>
    </Card>}
  </div>
}
