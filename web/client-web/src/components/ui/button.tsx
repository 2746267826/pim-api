import type { ButtonHTMLAttributes } from 'react'
import { cn } from '@/lib/utils'
import { Spinner } from './spinner'

export type ButtonVariant = 'primary' | 'secondary' | 'ghost' | 'danger' | 'danger-soft'
export type ButtonSize = 'sm' | 'md' | 'lg'

const variantClasses: Record<ButtonVariant, string> = {
  primary: 'bg-primary text-primary-fg shadow-card hover:bg-primary-hover',
  secondary:
    'border border-border bg-bg text-text-1 hover:border-border-strong hover:bg-surface',
  ghost: 'text-text-2 hover:bg-surface hover:text-text-1',
  danger: 'bg-crit text-white hover:brightness-95',
  'danger-soft': 'border border-crit-border bg-crit-soft text-crit hover:bg-crit/10',
}

const sizeClasses: Record<ButtonSize, string> = {
  sm: 'h-8 px-3 text-[13px]',
  md: 'h-9 px-3.5 text-sm',
  lg: 'h-10 px-4 text-sm',
}

export interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: ButtonVariant
  size?: ButtonSize
  loading?: boolean
}

export function Button({
  className,
  variant = 'secondary',
  size = 'md',
  loading = false,
  disabled,
  type = 'button',
  children,
  ...props
}: ButtonProps) {
  return (
    <button
      type={type}
      className={cn(
        'inline-flex items-center justify-center gap-1.5 rounded-ctl font-medium transition-colors duration-150 outline-none focus-visible:outline-2 focus-visible:outline-primary-ring disabled:pointer-events-none disabled:opacity-50',
        variantClasses[variant],
        sizeClasses[size],
        className,
      )}
      disabled={disabled || loading}
      {...props}
    >
      {loading && <Spinner className="size-4" />}
      {children}
    </button>
  )
}
