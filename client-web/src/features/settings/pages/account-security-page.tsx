import { useState } from 'react'
import type { FormEvent } from 'react'
import { ApiError, apiPost, getRefreshToken } from '@/api/client'
import type { components } from '@/api/generated'
import { useAuth } from '@/features/auth/auth-context'
import { notifyError, notifySuccess } from '@/lib/notify'
import { Button, Card, EmptyState, InlineAlert, Input, Label, PageHeader } from '@/components/ui'

/*
 * REQ-14：设置中心「账号安全」—— 修改登录密码。
 *
 * 逐字文案依据 references/界面文案与交互要点.md §4；请求形状依据决策记录 §3.2：
 *   POST /api/v1/auth/password  { currentPassword, newPassword, refreshToken }
 *
 * §4.1（最容易做错的一条）：`refreshToken` 必须**在提交时**从既有令牌存储现取。
 * 401 自动刷新会轮换令牌并写回存储，挂载时缓存进 state 的旧值在服务端已被撤销，
 * 提交它会让服务端按 AC-5.9 返回 40065（本可避免的失败）。
 */

/** 本地校验提示（逐字，不得改写同义句） */
const MSG_TOO_SHORT = '新密码至少 8 位'
const MSG_MISMATCH = '两次输入的新密码不一致'
const MSG_SAME_AS_CURRENT = '新密码不能与当前密码相同'

export function AccountSecurityPage() {
  const { hostChannel } = useAuth()
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [localError, setLocalError] = useState<string | null>(null)
  const [serverError, setServerError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  /*
   * host 通道（桌面壳嵌入）下页面不持有令牌（client.ts：host 通道不读 storage、
   * 不带 Authorization），因此按 AC-14.6 / AC-5.8 明确提示走桌面客户端，
   * 而不是提交一个空的 refreshToken 去换 40065。
   */
  if (hostChannel === 'host') {
    return (
      <div>
        <PageHeader title="账号安全" subtitle="修改登录密码" />
        <EmptyState
          title="请在桌面客户端修改密码"
          description="嵌入页面不持有登录令牌，修改密码请在桌面客户端完成。"
        />
      </div>
    )
  }

  /** 本地校验：命中即就地提示并**不发出请求** */
  function validate(): string | null {
    if (!currentPassword || !newPassword || !confirmPassword) return '请填写当前密码、新密码与确认新密码'
    if (newPassword.length < 8) return MSG_TOO_SHORT
    if (newPassword !== confirmPassword) return MSG_MISMATCH
    if (newPassword === currentPassword) return MSG_SAME_AS_CURRENT
    return null
  }

  async function onSubmit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault()
    setServerError(null)

    const invalid = validate()
    if (invalid) {
      setLocalError(invalid)
      return
    }
    setLocalError(null)
    setSubmitting(true)
    try {
      // 请求体形状取自契约类型（AC-15.2）：字段名写错会被 tsc 拦下
      const body: components['schemas']['ChangePasswordRequest'] = {
        currentPassword,
        newPassword,
        // §4.1：现取，不用挂载时的缓存值
        refreshToken: getRefreshToken() ?? '',
      }
      await apiPost<string>('/api/v1/auth/password', body)
      notifySuccess('密码已修改')
      // 成功：清空表单，当前设备保持登录（不跳登录页、不清令牌）
      setCurrentPassword('')
      setNewPassword('')
      setConfirmPassword('')
    } catch (err) {
      // 失败：显示服务端 message 原文，停留原页，已填内容不清空。
      // 只认 ApiError：`TypeError: Failed to fetch` 之类也是 Error，会把英文原文贴给用户（R-3）
      const message = err instanceof ApiError ? err.message || '修改密码失败，请稍后重试' : '修改密码失败，请稍后重试'
      setServerError(message)
      notifyError(message)
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div>
      <PageHeader title="账号安全" subtitle="修改登录密码" />
      <Card className="max-w-[520px] p-5">
        {(localError || serverError) && (
          <InlineAlert tone="crit" className="mb-4">
            {localError ?? serverError}
          </InlineAlert>
        )}
        <form className="space-y-4" onSubmit={onSubmit}>
          <div>
            <Label htmlFor="pwd-current">当前密码</Label>
            <Input
              id="pwd-current"
              type="password"
              autoComplete="current-password"
              value={currentPassword}
              onChange={(e) => setCurrentPassword(e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="pwd-new">新密码</Label>
            <Input
              id="pwd-new"
              type="password"
              autoComplete="new-password"
              placeholder="至少 8 位"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
            />
          </div>
          <div>
            <Label htmlFor="pwd-confirm">确认新密码</Label>
            <Input
              id="pwd-confirm"
              type="password"
              autoComplete="new-password"
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
            />
          </div>
          <Button type="submit" variant="primary" className="w-full" loading={submitting}>
            修改密码
          </Button>
        </form>
      </Card>
    </div>
  )
}
