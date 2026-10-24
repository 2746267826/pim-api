import { useEffect, type Ref } from 'react'

/*
 * 弹层退场兜底（Radix Presence 配套）。
 *
 * Radix 通过监听 data-state="closed" 元素上的 animationend 决定卸载时机；
 * 但 CSS 动画时钟在「后台标签 / 最小化窗口 / 移动端 WebView 后台」可能冻结
 * （实测 getAnimations() 的 currentTime 停在 0、playState=running 不前进），
 * animationend 永不触发 → 弹层以 opacity:0 的形态滞留并遮挡整屏点击。
 *
 * 此 hook 在节点进入 closed 态后启动 300ms 定时器：若届时仍未卸载，
 * 手动补发 animationend（与 Radix 的校验字段 animationName 一致）。
 * 动画正常完成时事件天然先到，节点已断连则定时器空转，无副作用。
 */
export function useAnimationEndFallback(node: HTMLElement | null): void {
  useEffect(() => {
    if (!node) return
    let timer: number | undefined
    const schedule = () => {
      window.clearTimeout(timer)
      timer = window.setTimeout(() => {
        if (node.isConnected && node.getAttribute('data-state') === 'closed') {
          node.dispatchEvent(
            new AnimationEvent('animationend', {
              animationName: getComputedStyle(node).animationName,
              bubbles: true,
            }),
          )
        }
      }, 300)
    }
    // 已处于 closed 态（如复用节点）也要兜底；否则仅监听状态切换
    if (node.getAttribute('data-state') === 'closed') schedule()
    const observer = new MutationObserver(() => {
      if (node.getAttribute('data-state') === 'closed') schedule()
      else window.clearTimeout(timer)
    })
    observer.observe(node, { attributes: true, attributeFilter: ['data-state'] })
    return () => {
      window.clearTimeout(timer)
      observer.disconnect()
    }
  }, [node])
}

/** 回调 ref 便捷封装：把 DOM 节点交给 useAnimationEndFallback */
export function exitGuardRef(onNode: (el: HTMLElement | null) => void): Ref<HTMLElement> {
  return onNode as Ref<HTMLElement>
}
