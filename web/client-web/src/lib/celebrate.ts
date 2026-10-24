/*
 * 庆祝时刻（canvas-confetti 封装）。
 * - sessionStorage 会话级去重：同一 sessionKey 只放一次礼花，避免高频任务完成时疲劳；
 * - prefers-reduced-motion / canvas-confetti 自带的 disableForReducedMotion 双重短路。
 */
import confetti from 'canvas-confetti'

const PREFIX = 'pim.celebrated.'

export function celebrateOnce(sessionKey: string): void {
  if (typeof window === 'undefined') return
  const key = PREFIX + sessionKey
  if (sessionStorage.getItem(key)) return
  if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) return
  sessionStorage.setItem(key, '1')
  confetti({
    particleCount: 90,
    spread: 70,
    startVelocity: 38,
    origin: { y: 0.72 },
    colors: ['#2563eb', '#16a34a', '#f59e0b', '#8b5cf6', '#ec4899'],
    disableForReducedMotion: true,
  })
}
