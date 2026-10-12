import { useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router'
import { useQueryClient } from '@tanstack/react-query'
import { ApiError, clearTokens } from '@/api/client'
import { Button, Chip, InlineAlert, Input, Label } from '@/components/ui'
import { detectHostChannel } from '@/lib/host-channel'
import { candidateUrl, getApiBase, getApiBaseHistory, setApiBase } from '@/lib/apiBase'
import { notifySuccess } from '@/lib/notify'

type TestResult =
  | { ok: true; version: string; capabilities: string[] }
  | { ok: false; message: string }

/**
 * 连接失败 → 可操作的提示。
 * fetch 抛 TypeError 且目标是跨域绝对地址时，最常见原因是服务器未返回
 * CORS 允许头（实测本测试服务器未返回）：浏览器直连被拦，但同源部署与
 * 原生壳（Capacitor/Tauri）不受限。
 */
function failureMessage(err: unknown, url: string): string {
  if (err instanceof DOMException && err.name === 'AbortError') {
    return '连接超时（8 秒）：请检查地址与网络'
  }
  const detail = err instanceof Error ? err.message : String(err)
  const crossOrigin =
    err instanceof TypeError &&
    typeof window !== 'undefined' &&
    /^https?:\/\//i.test(url) &&
    !url.startsWith(window.location.origin)
  if (crossOrigin) {
    return `浏览器直连被拦截（${detail}）。该源不在服务器的 CORS 白名单里：`
      + '同源部署请留空地址（走当前部署源）；App/桌面壳不受此限制；'
      + `浏览器跨域使用需服务端把本源（${window.location.origin}）加入 CORS 白名单`
      + '（测试 API 已放行 http://127.0.0.1:3000）。'
  }
  return `无法连接：${detail}`
}

/**
 * 服务器连接表单（/setup 与 /settings/server 共用）：
 * 测试连接走匿名端点 GET /api/version；保存后清令牌与查询缓存并重新登录。
 */
export function ServerForm({ mode }: { mode: 'setup' | 'settings' }) {
  const [value, setValue] = useState(getApiBase())
  const [testing, setTesting] = useState(false)
  const [saving, setSaving] = useState(false)
  const [result, setResult] = useState<TestResult | null>(null)
  const queryClient = useQueryClient()
  const navigate = useNavigate()
  const [params] = useSearchParams()
  const redirect = params.get('redirect') ?? (mode === 'setup' ? '/login' : undefined)
  const history = getApiBaseHistory()

  async function testConnection() {
    setTesting(true)
    setResult(null)
    const controller = new AbortController()
    const timer = setTimeout(() => controller.abort(), 8_000)
    try {
      const res = await fetch(candidateUrl(value, '/api/version'), {
        signal: controller.signal,
      })
      if (!res.ok) {
        throw new ApiError(
          res.status === 404
            ? 'HTTP 404——该地址下没有 /api/version，请确认这是 PIM API 服务器'
            : `HTTP ${res.status}`,
          -1,
          res.status,
        )
      }
      const info = (await res.json()) as { version?: string; capabilities?: string[] }
      setResult({
        ok: true,
        version: info.version ?? '未知版本',
        capabilities: info.capabilities ?? [],
      })
    } catch (err) {
      setResult({ ok: false, message: failureMessage(err, candidateUrl(value, '/api/version')) })
    } finally {
      clearTimeout(timer)
      setTesting(false)
    }
  }

  async function save() {
    /*
     * host 通道（规格 §6.1）：嵌入页的服务器地址由桌面宿主决定，页面里既不能保存，
     * 也不能因为「切换地址」去 clearTokens（页面根本不该碰令牌 storage）。
     */
    if (detectHostChannel() === 'host') {
      setResult({
        ok: false,
        message: '当前是桌面宿主嵌入页：服务器地址由桌面客户端决定，页面里不能修改。',
      })
      return
    }
    setSaving(true)
    try {
      setApiBase(value)
    } catch (err) {
      setResult({ ok: false, message: err instanceof Error ? err.message : String(err) })
      setSaving(false)
      return
    }
    // 令牌按用户+服务器绑定：切换地址后必须重新登录
    clearTokens()
    queryClient.clear()
    notifySuccess('服务器地址已保存')
    if (redirect) navigate(redirect, { replace: true })
    else navigate('/settings', { replace: true })
  }

  return (
    <div className="space-y-4">
      <div>
        <Label htmlFor="api-base">API 地址</Label>
        <Input
          id="api-base"
          className="mono"
          placeholder="http://192.168.1.10:5858"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === 'Enter') void testConnection()
          }}
        />
        <p className="mt-1.5 text-xs text-text-3">
          留空 = 使用当前部署源（浏览器同源场景默认）。填服务器源地址（协议://主机:端口，
          例如<span className="mono mx-1">http://192.168.1.10:5858</span>；误带
          <span className="mono mx-1">/api/v1</span>后缀会自动去掉。
        </p>
      </div>

      {history.length > 0 && (
        <div className="flex flex-wrap items-center gap-1.5">
          <span className="text-xs text-text-4">最近连接：</span>
          {history.map((h) => (
            <Chip key={h} className="mono h-6 px-2.5 text-xs" onClick={() => setValue(h)}>
              {h}
            </Chip>
          ))}
        </div>
      )}

      {result && (
        <InlineAlert tone={result.ok ? 'ok' : 'crit'}>
          {result.ok ? (
            <>
              已连接 · PIM API <span className="tnum">{result.version}</span>
              {result.capabilities.length > 0 && (
                <span className="text-text-3"> · 能力：{result.capabilities.join('、')}</span>
              )}
            </>
          ) : (
            result.message
          )}
        </InlineAlert>
      )}

      <div className="flex gap-2">
        <Button variant="secondary" loading={testing} onClick={() => void testConnection()}>
          测试连接
        </Button>
        <Button variant="primary" loading={saving} onClick={() => void save()}>
          保存并重新登录
        </Button>
      </div>
    </div>
  )
}
