import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import { authApi } from '@/api/auth'
import type { RegisterPayload } from '@/api/auth'
import { ApiError, clearTokens, getAccessToken, setTokens } from '@/api/client'
import type { UserInfo } from '@/api/types'
import { detectHostChannel, type HostChannel } from '@/lib/host-channel'
import { getHostSession, type HostHandshake, type HostStatus } from '@/lib/host-session'
import { notifyError } from '@/lib/notify'

interface AuthContextValue {
  user: UserInfo | null
  /**
   * 会话通道（host 嵌入页 / 普通浏览器）。
   * 见 `@/lib/host-channel` 与 `remediation/host-session/reviewed-spec.md`。
   */
  hostChannel: HostChannel
  /**
   * host 通道的握手状态（规格 §1）：
   * `pending`（已发 page.ready，等 host.state）/ `ready` / `unavailable`（500ms 超时，可重试）。
   * browser 通道恒为 `pending`，不参与判定。
   */
  hostStatus: HostStatus
  /** 上一次合法 `host.state` 的 `authenticated`（只决定要不要调 `/auth/me`） */
  hostAuthenticated: boolean
  /** host 通道退出超时（2 秒没有匹配的 `host.session.ended`）：可重试，会话保持 */
  hostLogoutError: boolean
  /** 启动期会话恢复中（GET /auth/me 进行时） */
  booting: boolean
  /**
   * 启动期会话恢复失败（非 401 的 5xx/网络错误）。
   * 与「未登录」区分：未登录跳登录页；bootError 展示可重试的错误页，
   * 否则一次瞬时 500 就会把已登录用户误踢到登录页。
   */
  bootError: boolean
  retryBoot: () => void
  login: (username: string, password: string) => Promise<void>
  register: (payload: RegisterPayload) => Promise<void>
  /** 纯客户端清令牌（规格：登出无服务端调用）；host 通道改为 `page.logout` */
  logout: () => void
}

const AuthContext = createContext<AuthContextValue | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  /*
   * 启动时读一次宿主通道标记（规格 §1）。
   * 标记合法 → host 通道：握手、/auth/me 守卫、单飞刷新、退出全部走宿主；
   * 没有标记（含 Capacitor/Tauri/第二个 Windows 壳）→ browser，今天的全部行为不变。
   */
  const [hostChannel] = useState<HostChannel>(() => detectHostChannel())
  const [user, setUser] = useState<UserInfo | null>(null)
  const [booting, setBooting] = useState(true)
  const [bootNonce, setBootNonce] = useState(0)

  const [bootError, setBootError] = useState(false)
  const [hostStatus, setHostStatus] = useState<HostStatus>('pending')
  const [hostAuthenticated, setHostAuthenticated] = useState(false)
  const [hostLogoutError, setHostLogoutError] = useState(false)

  /* ── browser 通道：今天的会话恢复（不改行为） ─────────────── */
  useEffect(() => {
    if (hostChannel !== 'browser') return
    let cancelled = false
    async function boot() {
      setBootError(false)
      setBooting(true)
      if (!getAccessToken()) {
        setBooting(false)
        return
      }
      try {
        const me = await authApi.me()
        if (!cancelled) setUser(me)
      } catch (err) {
        // 401 时 client 已清令牌并广播 → 未登录；
        // 其余（5xx/网络）不能当成未登录：保留令牌并标记引导失败，等待重试。
        const status = err instanceof ApiError ? err.status : -1
        if (!cancelled) {
          if (status === 401) setUser(null)
          else setBootError(true)
        }
      } finally {
        if (!cancelled) setBooting(false)
      }
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps -- bootNonce 变化重放引导
    void boot()
    const onUnauthorized = () => setUser(null)
    window.addEventListener('pim:unauthorized', onUnauthorized)
    return () => {
      cancelled = true
      window.removeEventListener('pim:unauthorized', onUnauthorized)
    }
  }, [bootNonce, hostChannel])

  /* ── host 通道：握手 → /auth/me 守卫（规格 §1/§6.1） ──────── */
  useEffect(() => {
    if (hostChannel !== 'host') return
    const session = getHostSession()
    if (!session) {
      // 有标记但没有页面可见的桥面：等同 500ms 没对上，保持 host 的可重试错误页
      setHostStatus('unavailable')
      setBooting(false)
      return
    }
    let cancelled = false
    /** 本次调用序：只有最后一次 me 的结果可以落地（重试/新代次会作废旧结果） */
    let meRun = 0
    let currentHandshake: HostHandshake | null = null

    /**
     * 作废正在等待的 me（规格 §2「会话结束只由 host.session.ended 表示」）。
     *
     * 本代次已经结束（收到匹配的 `host.session.ended`）或**根本不再调用** me
     * （新代次 `authenticated:false`）时，在飞的旧 me 迟到返回的 200 不属于当前代次：
     * 它不得重新写入用户、也不得让 `RequireAuth` 再次放行受保护路由。
     * 只递增调用序；清用户/清 booting 由调用方按 §6.2 的时机做。
     */
    const invalidatePendingMe = (): void => {
      meRun += 1
    }

    const runMe = async (): Promise<void> => {
      const id = ++meRun
      setBootError(false)
      setBooting(true)
      try {
        const me = await authApi.me()
        if (!cancelled && id === meRun) setUser(me)
      } catch (err) {
        if (cancelled || id !== meRun) return
        const status = err instanceof ApiError ? err.status : -1
        // 401：client 侧已按规格 §6.2 走过一次宿主刷新并只重放一次；这里只保持未登录。
        // 其余错误（5xx/网络）走 bootError 可重试页，不退出宿主会话、不写 storage。
        if (status === 401) setUser(null)
        else setBootError(true)
      } finally {
        if (!cancelled && id === meRun) setBooting(false)
      }
    }

    const sync = (): void => {
      if (cancelled) return
      setHostStatus(session.status)
      const handshake = session.handshake
      setHostAuthenticated(handshake?.authenticated ?? false)
      if (handshake && handshake !== currentHandshake) {
        currentHandshake = handshake
        if (handshake.authenticated) {
          void runMe()
        } else {
          // authenticated:false → 不调用 /auth/me，用户保持 null
          // 本代次不会再调 me：作废可能仍在飞的旧 me（它的 200 不属于这一代）
          invalidatePendingMe()
          setUser(null)
          setBootError(false)
          setBooting(false)
        }
        return
      }
      if (!handshake) {
        // pending（等 host.state）：保持启动中；unavailable 交给可重试入口
        setBooting(session.status === 'pending')
      }
    }

    const offState = session.subscribe(sync)
    const offEnded = session.onEnded(() => {
      if (cancelled) return
      // 能走到这里说明 handshakeId 与当前代次匹配（判定在 HostSession 内）
      // 会话已结束：先作废在飞的 me —— 迟到的 200 不得把用户写回、不得让守卫放行
      invalidatePendingMe()
      setUser(null)
      setHostAuthenticated(false)
      setBootError(false)
      setBooting(false)
      setHostStatus(session.status)
    })
    // 监听器装好后发 page.ready；500ms 没有合法 host.state → host/unavailable
    session.start()
    sync()
    return () => {
      cancelled = true
      offState()
      offEnded()
    }
  }, [hostChannel, bootNonce])

  const retryBoot = useCallback(() => setBootNonce((n) => n + 1), [])

  const login = useCallback(
    async (username: string, password: string) => {
      // host 通道不提供网页账密表单：不调 setTokens，也不发登录请求（规格 §6.1）
      if (hostChannel === 'host') {
        throw new ApiError('宿主通道不提供网页登录：请在桌面客户端登录后点重试', -1, 0)
      }
      const res = await authApi.login(username, password)
      setTokens(res.accessToken, res.refreshToken)
      setUser(res.user)
    },
    [hostChannel],
  )

  const register = useCallback(
    async (payload: RegisterPayload) => {
      if (hostChannel === 'host') {
        throw new ApiError('宿主通道不提供网页注册：请在桌面客户端完成注册', -1, 0)
      }
      const res = await authApi.register(payload)
      setTokens(res.accessToken, res.refreshToken)
      setUser(res.user)
    },
    [hostChannel],
  )

  const logout = useCallback(() => {
    if (hostChannel === 'host') {
      /*
       * host 通道（规格 §6.2）：只发 `page.logout`，等匹配当前 handshakeId 的
       * `host.session.ended`（最长 2 秒）。用户只在收到该消息后才清（由 onEnded 做）。
       * 超时：用户与会话都保持，不 clearTokens，显示可重试错误。
       */
      setHostLogoutError(false)
      const session = getHostSession()
      if (!session) {
        setHostLogoutError(true)
        return
      }
      void session.logout().then((ok) => {
        if (ok) {
          setHostLogoutError(false)
          return
        }
        setHostLogoutError(true)
        notifyError('退出登录未完成：桌面宿主没有确认，请重试')
      })
      return
    }
    clearTokens()
    setUser(null)
  }, [hostChannel])

  const value = useMemo(
    () => ({
      user,
      hostChannel,
      hostStatus,
      hostAuthenticated,
      hostLogoutError,
      booting,
      bootError,
      retryBoot,
      login,
      register,
      logout,
    }),
    [
      user,
      hostChannel,
      hostStatus,
      hostAuthenticated,
      hostLogoutError,
      booting,
      bootError,
      retryBoot,
      login,
      register,
      logout,
    ],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthContextValue {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth 必须在 AuthProvider 内使用')
  return ctx
}
