import { useState } from 'react'
import { Circle, ExternalLink, RefreshCw, ShieldAlert, Star, Unplug } from 'lucide-react'
import { useQueryClient } from '@tanstack/react-query'
import { filesApi } from '@/features/files/api'
import { useProviders, useSyncStatus } from '@/features/files/queries'
import type { FileProvider } from '@/features/files/types'
import { formatTime } from '@/lib/datetime'
import { notifyError, notifySuccess } from '@/lib/notify'
import { Button, Card, CardTitle, Chip, EmptyState, PageHeader, Skeleton, StatusBadge } from '@/components/ui'

/**
 * Google Drive 账户页（工单 05 / F4.4）。
 *
 * 授权形态与 OneDrive 的设备码不同：**浏览器整页跳转**，且必须由人在 Google 页面上
 * 点「同意」——因此这里只能「发起跳转」，无法在页面内自动完成。
 * 回调由后端 `/api/v1/files/google/callback` 处理，成功后回到本页即看到账号。
 *
 * 另：Google OAuth 应用若处于 Testing 发布状态，restricted scope 的 refresh token
 * **7 天后过期**。此时账号会显示为「授权已失效」，需要点「重新授权」——
 * 这与「同步游标失效」（会自动全量重扫）是两回事，界面文案也分开。
 */
export function GoogleDrivePage() {
  const qc = useQueryClient()
  const providers = useProviders()
  const [busy, setBusy] = useState(false)

  const accounts = (providers.data ?? []).filter((p) => p.provider === 'google')
  const connected = accounts.filter((a) => a.status === 'connected')

  const invalidate = () => void qc.invalidateQueries({ queryKey: ['files'] })

  async function connect() {
    setBusy(true)
    try {
      const start = await filesApi.authorizeGoogle()
      if (!start.authorizationUrl) {
        throw new Error('服务端未返回授权地址，请检查 Google 凭据配置')
      }
      // 整页跳转：必须是浏览器导航（不是 fetch），用户要在 Google 页面上点「同意」
      window.location.assign(start.authorizationUrl)
    } catch (err) {
      notifyError(err instanceof Error ? err.message : '发起授权失败')
      setBusy(false)
    }
  }

  return (
    <div>
      <PageHeader
        title="Google Drive"
        subtitle="浏览器跳转授权；授权后文件页可与其他网盘账号并存并切换"
      />

      <Card className="mb-4 p-4">
        <div className="flex flex-wrap items-center gap-2">
          <CardTitle>连接 Google Drive</CardTitle>
          <Chip className="ml-auto">{connected.length} 个已连接账号</Chip>
        </div>
        <p className="mt-2 text-[13px] text-text-3">
          点击后会在浏览器打开 Google 授权页，需要你**在 Google 页面上点击「同意」**。
          授权范围是「整个 Google 网盘读写」，个人自用不需要上架审核。
        </p>
        <div className="mt-3 flex flex-wrap items-center gap-2">
          <Button variant="primary" size="sm" loading={busy} onClick={() => void connect()}>
            <ExternalLink className="size-3.5" aria-hidden /> 连接 Google Drive
          </Button>
          <Button variant="ghost" size="sm" onClick={invalidate}>
            刷新状态
          </Button>
        </div>
      </Card>

      {providers.isLoading ? (
        <Skeleton className="h-24 w-full" />
      ) : accounts.length === 0 ? (
        <Card className="p-6">
          <EmptyState
            icon={Circle}
            title="还没有 Google Drive 账号"
            description="若按钮点了没有反应或提示缺少配置，请先由维护者配置 PIM_FILES_GOOGLE_CLIENT_ID / PIM_FILES_GOOGLE_CLIENT_SECRET，并确认回调地址已登记到 Google Cloud Console。"
          />
        </Card>
      ) : (
        <div className="space-y-3">
          {accounts.map((account) => (
            <GoogleAccountCard key={account.id} account={account} onChanged={invalidate} />
          ))}
        </div>
      )}
    </div>
  )
}

function GoogleAccountCard({ account, onChanged }: { account: FileProvider; onChanged: () => void }) {
  const syncStatus = useSyncStatus(account.status === 'connected' ? account.id : undefined)
  const [busy, setBusy] = useState(false)

  async function run(action: () => Promise<unknown>, success: string, failure: string) {
    setBusy(true)
    try {
      await action()
      notifySuccess(success)
      onChanged()
    } catch (err) {
      notifyError(err instanceof Error ? err.message : failure)
    } finally {
      setBusy(false)
    }
  }

  const expired = account.status === 'expired'

  return (
    <Card className="p-4">
      <div className="flex flex-wrap items-center gap-2">
        <span className="font-medium text-text-1">{account.accountName ?? 'Google Drive 账号'}</span>
        {account.isDefault && (
          <Chip>
            <Star className="mr-1 inline size-3" aria-hidden /> 默认落点
          </Chip>
        )}
        <StatusBadge
          tone={account.status === 'connected' ? 'ok' : expired ? 'warn' : 'neutral'}
          className="ml-auto"
        >
          {account.status === 'connected' ? '已连接' : expired ? '授权已失效' : account.status === 'denied' ? '已拒绝' : '待授权'}
        </StatusBadge>
      </div>

      <div className="mono mt-2 space-y-0.5 text-[11px] text-text-4">
        <div className="break-all">accountId: {account.accountId ?? '—'}</div>
        <div>本次同步应用: {account.syncedItemCount.toLocaleString()}（0 不代表失败）</div>
        <div>
          已索引总量: {account.totalIndexedCount != null ? account.totalIndexedCount.toLocaleString() : '未知'}
        </div>
        {account.lastSyncAt && <div>最近同步: {formatTime(account.lastSyncAt)}</div>}
        {account.lastError && <div className="text-crit">最近错误: {account.lastError}</div>}
      </div>

      {expired && (
        <p className="mt-2 flex items-start gap-1.5 text-xs text-warn">
          <ShieldAlert className="mt-0.5 size-3.5 shrink-0" aria-hidden />
          Google 授权已失效（refresh token 过期或被撤销）。这**不是**同步游标失效：
          游标失效会自动全量重扫，授权失效必须重新授权。
        </p>
      )}

      <div className="mt-3 flex flex-wrap gap-2">
        {account.status === 'connected' && (
          <>
            <Button
              variant="secondary"
              size="sm"
              loading={busy || syncStatus.data?.syncStatus === 'syncing'}
              onClick={() => void run(() => filesApi.startSync(account.id), '已开始同步', '同步失败')}
            >
              <RefreshCw className="size-3.5" aria-hidden />
              {syncStatus.data?.syncStatus === 'syncing' ? '同步中…' : '手动同步'}
            </Button>
            {!account.isDefault && (
              <Button
                variant="secondary"
                size="sm"
                loading={busy}
                onClick={() =>
                  void run(() => filesApi.setDefault(account.id), '已设为默认落点账号', '设置失败')
                }
              >
                <Star className="size-3.5" aria-hidden /> 设为默认
              </Button>
            )}
          </>
        )}
        {expired && (
          <Button variant="primary" size="sm" loading={busy} onClick={() => void connectAgain()}>
            <ExternalLink className="size-3.5" aria-hidden /> 重新授权
          </Button>
        )}
        <Button
          variant="danger-soft"
          size="sm"
          loading={busy}
          onClick={() => {
            if (!window.confirm('断开该 Google Drive 账号？本地文件元数据会被清除，云端文件不受影响。')) return
            void run(() => filesApi.unbind(account.id), '已断开', '断开失败')
          }}
        >
          <Unplug className="size-3.5" aria-hidden /> 断开
        </Button>
      </div>
    </Card>
  )

  /** 重新授权 = 再走一次浏览器跳转（授权失效后唯一可行的恢复路径）。 */
  async function connectAgain() {
    const start = await filesApi.authorizeGoogle()
    if (!start.authorizationUrl) {
      throw new Error('服务端未返回授权地址')
    }
    window.location.assign(start.authorizationUrl)
  }
}
