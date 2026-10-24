import { useEffect, useState } from 'react'
import type { ButtonHTMLAttributes } from 'react'
import { cn } from '@/lib/utils'
import { Button } from './button'

export interface TwoStepButtonProps extends Omit<
  ButtonHTMLAttributes<HTMLButtonElement>,
  'onClick'
> {
  onConfirm: () => void | Promise<void>
  /** 武装态文案 */
  armLabel?: string
  /** 武装保持时长，超时自动解除 */
  timeoutMs?: number
  className?: string
}

/**
 * 两步武装确认按钮（规格"两步武装确认"，确认中心用）：
 * 第一击进入待执行态（危险色 + 提示文案），第二击执行；超时自动复位。
 */
export function TwoStepButton({
  onConfirm,
  armLabel = '再次点击执行',
  timeoutMs = 10_000,
  className,
  children,
  disabled,
  ...props
}: TwoStepButtonProps) {
  const [armed, setArmed] = useState(false)

  useEffect(() => {
    if (!armed) return
    const timer = setTimeout(() => setArmed(false), timeoutMs)
    return () => clearTimeout(timer)
  }, [armed, timeoutMs])

  return (
    <Button
      variant={armed ? 'danger' : 'primary'}
      className={cn(armed && 'animate-[pim-fade-in_120ms_ease-out]', className)}
      disabled={disabled}
      {...props}
      onClick={async () => {
        if (!armed) {
          setArmed(true)
          return
        }
        setArmed(false)
        await onConfirm()
      }}
    >
      {armed ? armLabel : children}
    </Button>
  )
}
