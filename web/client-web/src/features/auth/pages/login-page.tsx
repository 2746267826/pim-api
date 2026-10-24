import { useState } from 'react'
import { Navigate, Link, useLocation, useNavigate } from 'react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { ApiError, formatRetryAfter } from '@/api/client'
import { useAuth } from '@/features/auth/auth-context'
import { Button, EmptyState, Input, InlineAlert, Label } from '@/components/ui'

/*
 * WO-AUTH-REGISTER-20261014 REQ-11：登录页只提供登录。
 *
 * 公开注册已关闭（后端 `/auth/register` 在库非空时返回 403/40304），
 * 因此这里**不再**有「登录 / 注册」模式切换、邮箱与显示名字段、注册提交；
 * 账号由管理员在「用户管理」里创建。
 * 全新部署（空库）的首个管理员引导见后端注册策略，不在前端提供入口。
 */

const loginSchema = z.object({
  username: z.string().min(1, '请输入用户名或邮箱'),
  password: z.string().min(1, '请输入密码'),
})

export function LoginPage() {
  const { user, login, hostChannel, retryBoot } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [formError, setFormError] = useState<string | null>(null)

  const from = (location.state as { from?: string } | null)?.from ?? '/today'

  const loginForm = useForm<z.infer<typeof loginSchema>>({
    resolver: zodResolver(loginSchema),
    defaultValues: { username: '', password: '' },
  })

  if (user) return <Navigate to={from} replace />

  /*
   * host 通道（规格 §6.1）：直接打开 /login 也显示同一张非账密说明。
   * 宿主里没有网页账密表单——登录由桌面客户端的登录窗口完成。
   * 这段文案是既有措辞，按 AC-11.6 必须保持不变。
   */
  if (hostChannel === 'host') {
    return (
      <div className="grid min-h-dvh place-items-center bg-surface px-4">
        <div className="w-full max-w-[380px]">
          <EmptyState
            title="请在桌面客户端登录"
            description="嵌入页面不提供账号密码登录或注册。请使用桌面客户端的登录窗口登录，然后点重试。"
            action={
              <Button variant="primary" onClick={retryBoot}>
                重试
              </Button>
            }
          />
        </div>
      </div>
    )
  }

  function toErrorMessage(err: unknown): string {
    if (err instanceof ApiError) {
      // 429：服务端给了真实等待时长（Retry-After），页面必须把它显示出来，
      // 不能再写死一个猜测值（PW-18）。
      if (err.status === 429) {
        return err.retryAfterMs != null
          ? `尝试次数过多，请 ${formatRetryAfter(err.retryAfterMs)}后再试`
          : '尝试次数过多，请稍后再试'
      }
      return err.message || '请求失败，请稍后重试'
    }
    return '无法连接服务器：请检查网络或服务器地址'
  }

  return (
    <div className="grid min-h-dvh place-items-center bg-surface px-4">
      <div className="w-full max-w-[380px]">
        <div className="mb-6 flex flex-col items-center gap-2">
          <span className="size-3 rounded-full bg-primary" aria-hidden />
          <h1 className="text-xl font-semibold text-text-1">登录 PIM</h1>
          <p className="text-[13px] text-text-3">个人信息管理工作台</p>
        </div>

        <div className="rounded-card border border-border bg-bg p-6 shadow-card">
          {formError && (
            <InlineAlert tone="crit" className="mb-4">
              {formError}
            </InlineAlert>
          )}

          <form
            className="space-y-4"
            onSubmit={loginForm.handleSubmit(async (values) => {
              setFormError(null)
              try {
                await login(values.username, values.password)
                navigate(from, { replace: true })
              } catch (err) {
                setFormError(toErrorMessage(err))
              }
            })}
          >
            <div>
              <Label htmlFor="login-username">用户名或邮箱</Label>
              <Input
                id="login-username"
                autoComplete="username"
                {...loginForm.register('username')}
              />
              {loginForm.formState.errors.username && (
                <p className="mt-1 text-xs text-crit">
                  {loginForm.formState.errors.username.message}
                </p>
              )}
            </div>
            <div>
              <Label htmlFor="login-password">密码</Label>
              <Input
                id="login-password"
                type="password"
                autoComplete="current-password"
                {...loginForm.register('password')}
              />
              {loginForm.formState.errors.password && (
                <p className="mt-1 text-xs text-crit">
                  {loginForm.formState.errors.password.message}
                </p>
              )}
            </div>
            <Button
              type="submit"
              variant="primary"
              className="w-full"
              loading={loginForm.formState.isSubmitting}
            >
              登录
            </Button>
          </form>
        </div>

        <div className="mt-4 text-center text-[13px]">
          <Link
            to={`/settings/server?redirect=/login`}
            className="text-text-3 underline-offset-2 transition-colors hover:text-primary hover:underline outline-none"
          >
            连接不上服务器？更换服务器地址
          </Link>
        </div>
      </div>
    </div>
  )
}
