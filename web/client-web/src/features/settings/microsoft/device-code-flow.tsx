import { useCallback, useEffect, useRef, useState } from 'react'
import { CheckCircle2, Copy, ExternalLink, XCircle } from 'lucide-react'
import { Button, InlineAlert, Label, Spinner } from '@/components/ui'
import { cn } from '@/lib/utils'

/*
 * 设备码授权流（Outlook 与 OneDrive 共用；两端点与轮询间隔参数化）：
 * - Outlook：createOutlookDeviceCode / pollOutlookDeviceCode，3 秒 setTimeout 链
 * - OneDrive：bindOneDrive → 5 秒轮询 binding-status（由调用方提供 poll 实现）
 * 状态：idle → waiting（显示码+验证链接）→ connected / failed / canceled
 */

export type DeviceCodePhase = 'idle' | 'starting' | 'waiting' | 'connected' | 'failed' | 'canceled'

export interface DeviceCodeSession {
  sessionId: string
  userCode: string | null
  verificationUri: string | null
  expiresAt: string | null
}

export interface DeviceCodeFlowProps {
  /** 页面上显示的说明（如"在浏览器中打开验证页并输入代码"） */
  hint: string
  title: string
  /** 开始授权：返回会话（含 userCode/verificationUri/expiresAt） */
  start: () => Promise<DeviceCodeSession>
  /** 单次轮询：返回最新状态（connected 后由组件停止轮询） */
  poll: (sessionId: string) => Promise<{ status: string; errorMessage?: string | null; accountName?: string | null }>
  /** 取消授权（可选） */
  cancel?: (sessionId: string) => Promise<void>
  /** 轮询间隔（ms）：Outlook 3000，OneDrive 5000 */
  pollIntervalMs: number
  onConnected?: () => void
}

export function DeviceCodeFlow({ title, hint, start, poll, cancel, pollIntervalMs, onConnected }: DeviceCodeFlowProps) {
  const [phase, setPhase] = useState<DeviceCodePhase>('idle')
  const [session, setSession] = useState<DeviceCodeSession | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [remaining, setRemaining] = useState<number | null>(null)
  const timerRef = useRef<number | null>(null)
  const tickRef = useRef<number | null>(null)

  const stop = useCallback(() => {
    if (timerRef.current != null) window.clearTimeout(timerRef.current)
    if (tickRef.current != null) window.clearInterval(tickRef.current)
    timerRef.current = null
    tickRef.current = null
  }, [])

  useEffect(() => stop, [stop])

  const scheduleNext = useCallback(
    (sessionId: string) => {
      timerRef.current = window.setTimeout(async () => {
        try {
          const res = await poll(sessionId)
          if (res.status === 'connected') {
            stop()
            setPhase('connected')
            setRemaining(null)
            onConnected?.()
            return
          }
          if (res.status === 'failed' || res.status === 'expired' || res.status === 'denied') {
            stop()
            setPhase('failed')
            setError(res.errorMessage ?? '授权失败或被拒绝，请重试')
            return
          }
          scheduleNext(sessionId)
        } catch (err) {
          stop()
          setPhase('failed')
          setError(err instanceof Error ? err.message : '轮询失败')
        }
      }, pollIntervalMs)
    },
    [poll, pollIntervalMs, stop, onConnected],
  )

  async function begin() {
    setPhase('starting')
    setError(null)
    try {
      const s = await start()
      setSession(s)
      setPhase('waiting')
      if (s.expiresAt) {
        const expiry = new Date(s.expiresAt).getTime()
        const update = () => setRemaining(Math.max(0, Math.round((expiry - Date.now()) / 1000)))
        update()
        tickRef.current = window.setInterval(update, 1000)
      }
      scheduleNext(s.sessionId)
    } catch (err) {
      setPhase('failed')
      setError(err instanceof Error ? err.message : '无法发起授权')
    }
  }

  async function abort() {
    stop()
    if (session && cancel) await cancel(session.sessionId).catch(() => {})
    setPhase('idle')
    setSession(null)
    setRemaining(null)
  }

  return (
    <div className="rounded-card border border-border p-4">
      <div className="flex items-center gap-2">
        <span className="text-[13px] font-semibold text-text-1">{title}</span>
        {phase === 'waiting' && <Spinner className="size-3.5" />}
        {phase === 'connected' && <CheckCircle2 className="size-4 text-ok" aria-hidden />}
      </div>
      <p className="mt-1 text-xs text-text-3">{hint}</p>

      {error && <InlineAlert tone="crit" className="mt-3">{error}</InlineAlert>}

      {phase === 'idle' && (
        <Button variant="primary" size="sm" className="mt-3" onClick={() => void begin()}>
          获取设备码
        </Button>
      )}

      {phase === 'starting' && (
        <Button variant="primary" size="sm" className="mt-3" loading>
          正在获取…
        </Button>
      )}

      {phase === 'waiting' && session && (
        <div className="mt-3 space-y-3">
          <div className="flex flex-wrap items-center gap-3">
            <div className="rounded-ctl border border-border bg-surface px-4 py-2.5">
              <Label className="mb-0.5">设备码</Label>
              <div className="mono text-xl font-semibold tracking-wider text-text-1">{session.userCode ?? '—'}</div>
            </div>
            <div className="space-y-1.5">
              <Button
                variant="secondary"
                size="sm"
                onClick={() => void navigator.clipboard.writeText(session.userCode ?? '')}
              >
                <Copy className="size-3.5" aria-hidden /> 复制代码
              </Button>
              {session.verificationUri && (
                <Button variant="primary" size="sm" onClick={() => window.open(session.verificationUri!, '_blank', 'noopener')}>
                  <ExternalLink className="size-3.5" aria-hidden /> 打开验证页
                </Button>
              )}
            </div>
            {remaining != null && (
              <span className="tnum ml-auto text-xs text-text-3">
                {remaining > 0 ? `${Math.floor(remaining / 60)}:${String(remaining % 60).padStart(2, '0')} 后过期` : '已过期'}
              </span>
            )}
          </div>
          <p className="text-xs text-text-4">
            已开始每 {pollIntervalMs / 1000} 秒自动检查授权状态…
          </p>
          <Button variant="ghost" size="sm" onClick={() => void abort()}>
            <XCircle className="size-3.5" aria-hidden /> 取消授权
          </Button>
        </div>
      )}

      {phase === 'connected' && (
        <InlineAlert tone="ok" className="mt-3">已连接成功。</InlineAlert>
      )}

      {phase === 'failed' && (
        <Button variant="secondary" size="sm" className="mt-3" onClick={() => void begin()}>
          重新发起授权
        </Button>
      )}

      <span className={cn('hidden', phase === 'canceled' && 'block')} />
    </div>
  )
}
