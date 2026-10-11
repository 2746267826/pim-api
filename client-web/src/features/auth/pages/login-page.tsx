import { useState } from 'react'
import { Navigate, Link, useLocation, useNavigate } from 'react-router'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { ApiError, formatRetryAfter } from '@/api/client'
import { useAuth } from '@/features/auth/auth-context'
import { Button, EmptyState, Input, InlineAlert, Label, Segmented } from '@/components/ui'

const loginSchema = z.object({
  username: z.string().min(1, '请输入用户名或邮箱'),
  password: z.string().min(1, '请输入密码'),
})

const registerSchema = z.object({
  username: z.string().min(1, '请输入用户名').max(50, '用户名最多 50 字符'),
  email: z.string().email('邮箱格式不正确'),
  password: z.string().min(8, '密码至少 8 位').max(100, '密码最多 100 字符'),
  displayName: z.string().max(100, '显示名最多 100 字符').optional(),
})

type Mode = 'login' | 'register'

export function LoginPage() {
  const { user, login, register, hostChannel, retryBoot } = useAuth()
  const navigate = useNavigate()
  const location = useLocation()
  const [mode, setMode] = useState<Mode>('login')
  const [formError, setFormError] = useState<string | null>(null)

  const from = (location.state as { from?: string } | null)?.from ?? '/today'

  const loginForm = useForm<z.infer<typeof loginSchema>>({
    resolver: zodResolver(loginSchema),
    defaultValues: { username: '', password: '' },
  })
  const registerForm = useForm<z.infer<typeof registerSchema>>({
    resolver: zodResolver(registerSchema),
    defaultValues: { username: '', email: '', password: '', displayName: '' },
  })

  if (user) return <Navigate to={from} replace />

  /*
   * host 通道（规格 §6.1）：直接打开 /login 或注册页也显示同一张非账密说明。
   * 宿主里没有网页账密表单——登录/注册由桌面客户端的登录窗口完成。
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
          <h1 className="text-xl font-semibold text-text-1">
            {mode === 'login' ? '登录 PIM' : '注册 PIM 账户'}
          </h1>
          <p className="text-[13px] text-text-3">个人信息管理工作台</p>
        </div>

        <div className="rounded-card border border-border bg-bg p-6 shadow-card">
          <Segmented
            className="mb-5 w-full [&>button]:flex-1"
            value={mode}
            onValueChange={(v) => {
              setMode(v)
              setFormError(null)
            }}
            options={[
              { value: 'login', label: '登录' },
              { value: 'register', label: '注册' },
            ]}
          />

          {formError && (
            <InlineAlert tone="crit" className="mb-4">
              {formError}
            </InlineAlert>
          )}

          {mode === 'login' ? (
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
          ) : (
            <form
              className="space-y-4"
              onSubmit={registerForm.handleSubmit(async (values) => {
                setFormError(null)
                try {
                  await register({
                    username: values.username,
                    email: values.email,
                    password: values.password,
                    displayName: values.displayName || undefined,
                  })
                  navigate(from, { replace: true })
                } catch (err) {
                  setFormError(toErrorMessage(err))
                }
              })}
            >
              <div>
                <Label htmlFor="reg-username">用户名</Label>
                <Input id="reg-username" autoComplete="username" {...registerForm.register('username')} />
                {registerForm.formState.errors.username && (
                  <p className="mt-1 text-xs text-crit">
                    {registerForm.formState.errors.username.message}
                  </p>
                )}
              </div>
              <div>
                <Label htmlFor="reg-email">邮箱</Label>
                <Input id="reg-email" type="email" autoComplete="email" {...registerForm.register('email')} />
                {registerForm.formState.errors.email && (
                  <p className="mt-1 text-xs text-crit">
                    {registerForm.formState.errors.email.message}
                  </p>
                )}
              </div>
              <div>
                <Label htmlFor="reg-password">密码（至少 8 位）</Label>
                <Input
                  id="reg-password"
                  type="password"
                  autoComplete="new-password"
                  {...registerForm.register('password')}
                />
                {registerForm.formState.errors.password && (
                  <p className="mt-1 text-xs text-crit">
                    {registerForm.formState.errors.password.message}
                  </p>
                )}
              </div>
              <div>
                <Label htmlFor="reg-display">显示名（可选）</Label>
                <Input id="reg-display" {...registerForm.register('displayName')} />
              </div>
              <Button
                type="submit"
                variant="primary"
                className="w-full"
                loading={registerForm.formState.isSubmitting}
              >
                注册并登录
              </Button>
            </form>
          )}
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
