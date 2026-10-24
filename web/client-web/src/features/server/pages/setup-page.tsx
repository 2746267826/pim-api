import { ServerForm } from '../server-form'

/** 首启向导（壳内未配置 API 地址时强制进入）：输入地址 → 测试 → 保存并去登录 */
export function SetupPage() {
  return (
    <div className="grid min-h-dvh place-items-center bg-surface px-4">
      <div className="w-full max-w-[460px]">
        <div className="mb-6 flex flex-col items-center gap-2">
          <span className="size-3 rounded-full bg-primary" aria-hidden />
          <h1 className="text-xl font-semibold text-text-1">连接到你的 PIM</h1>
          <p className="text-[13px] text-text-3">首次使用请填写 PIM API 服务器地址</p>
        </div>
        <div className="rounded-card border border-border bg-bg p-6 shadow-card">
          <ServerForm mode="setup" />
        </div>
      </div>
    </div>
  )
}
