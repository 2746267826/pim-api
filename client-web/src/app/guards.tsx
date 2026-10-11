import { Navigate, useLocation } from 'react-router'
import { LazyOutlet } from './lazy-outlet'
import { useAuth } from '@/features/auth/auth-context'
import { needsServerSetup } from '@/lib/apiBase'
import { Button, EmptyState, Spinner } from '@/components/ui'
import { ShieldAlert } from 'lucide-react'

/** 壳内未配置 API 地址 → 强制进入首启向导 */
export function RequireSetup() {
  if (needsServerSetup()) return <Navigate to="/setup" replace />
  return <LazyOutlet />
}

/** 认证守卫：未登录重定向 /login（成功后回跳原 URL）；host 通道改用宿主说明页 */
export function RequireAuth() {
  const { user, booting, bootError, retryBoot, hostChannel, hostStatus, hostAuthenticated } =
    useAuth()
  const location = useLocation()

  if (booting) {
    return (
      <div className="grid min-h-dvh place-items-center">
        <Spinner className="size-6" />
      </div>
    )
  }

  /*
   * host 通道（规格 §1/§6.1）：账密表单在宿主里既不可用也不该出现——
   * 登录由桌面客户端的登录窗口完成。500ms 没有合法 host.state、me 的非 401 失败、
   * 以及宿主未登录，都在这里给同一张可重试说明；**不**跳 /login，因为那会把
   * 真实 LoginPage 的账密表单渲染出来（等于退回浏览器登录）。
   */
  if (hostChannel === 'host') {
    if (bootError || hostStatus === 'unavailable') {
      return (
        <div className="grid min-h-dvh place-items-center px-4">
          <EmptyState
            title="宿主会话未就绪"
            description="没有收到桌面宿主的会话信息，或启动时无法连接服务。请在桌面客户端里完成登录，然后重试。"
            action={
              <Button variant="primary" onClick={retryBoot}>
                重试
              </Button>
            }
          />
        </div>
      )
    }
    if (!user) {
      return (
        <div className="grid min-h-dvh place-items-center px-4">
          <EmptyState
            title={hostAuthenticated ? '正在恢复宿主会话' : '尚未登录'}
            description="嵌入页面不提供账号密码登录。请使用桌面客户端的登录窗口登录，然后点重试。"
            action={
              <Button variant="primary" onClick={retryBoot}>
                重试
              </Button>
            }
          />
        </div>
      )
    }
    return <LazyOutlet />
  }

  /*
   * 引导失败（5xx/网络）≠ 未登录：令牌仍在，不能踢去登录页。
   * 展示可重试的错误页；401 场景 client 已清令牌，bootError 不会置位。
   */
  if (bootError) {
    return (
      <div className="grid min-h-dvh place-items-center px-4">
        <EmptyState
          title="会话恢复失败"
          description="启动时无法连接服务器或服务暂不可用。令牌仍然有效，可稍后重试。"
          action={
            <Button variant="primary" onClick={retryBoot}>
              重试
            </Button>
          }
        />
      </div>
    )
  }
  if (!user) {
    return (
      <Navigate
        to="/login"
        replace
        state={{ from: location.pathname + location.search }}
      />
    )
  }
  return <LazyOutlet />
}

/**
 * 管理员守卫：后端 ai 组与 admin 组整体 Roles="admin"（auth-admin-ai.md:3,85,138），
 * 普通用户直接访问会得到 403。此处提前拦截，避免页面刷出一堆无意义的错误请求。
 */
export function RequireAdmin() {
  const { user, booting } = useAuth()

  if (booting) {
    return (
      <div className="grid min-h-dvh place-items-center">
        <Spinner className="size-6" />
      </div>
    )
  }
  if (user && user.role !== 'admin') {
    return (
      <EmptyState
        icon={ShieldAlert}
        title="需要管理员权限"
        description={`当前账户「${user.displayName || user.username}」为普通用户，无法访问该页面的接口。请使用管理员账户登录。`}
      />
    )
  }
  return <LazyOutlet />
}
