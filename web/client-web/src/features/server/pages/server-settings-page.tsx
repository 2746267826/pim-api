import { InlineAlert, PageHeader } from '@/components/ui'
import { ServerForm } from '../server-form'

/** 设置 → 服务器连接：API 地址配置、连接测试、最近连接历史 */
export function ServerSettingsPage() {
  return (
    <div>
      <PageHeader
        title="服务器连接"
        subtitle="配置 PIM API 地址；切换后令牌与本地缓存将被清除并要求重新登录"
      />
      <div className="max-w-[560px] space-y-4">
        <InlineAlert tone="info">
          切换服务器不会影响 PIM 数据本身——所有数据都保存在对应服务器的数据库中。
        </InlineAlert>
        <div className="rounded-card border border-border bg-bg p-5 shadow-card">
          <ServerForm mode="settings" />
        </div>
      </div>
    </div>
  )
}
