import * as CheckboxPrimitive from '@radix-ui/react-checkbox'
import { Check, Minus } from 'lucide-react'
import { cn } from '@/lib/utils'

export interface CheckboxProps {
  checked: boolean | 'indeterminate'
  onCheckedChange: (checked: boolean) => void
  disabled?: boolean
  id?: string
  className?: string
  ariaLabel?: string
}

export function Checkbox({
  checked,
  onCheckedChange,
  disabled,
  id,
  className,
  ariaLabel,
}: CheckboxProps) {
  return (
    <CheckboxPrimitive.Root
      id={id}
      aria-label={ariaLabel}
      checked={checked}
      onCheckedChange={(v) => onCheckedChange(v === true)}
      disabled={disabled}
      className={cn(
        'flex size-4 shrink-0 items-center justify-center rounded-[4px] border transition-colors duration-150 outline-none disabled:opacity-50',
        checked === false
          ? 'border-border-strong bg-bg hover:border-primary'
          : 'border-primary bg-primary text-primary-fg',
        className,
      )}
    >
      <CheckboxPrimitive.Indicator>
        {checked === 'indeterminate' ? (
          <Minus className="size-3" aria-hidden />
        ) : (
          <Check className="size-3" strokeWidth={3} aria-hidden />
        )}
      </CheckboxPrimitive.Indicator>
    </CheckboxPrimitive.Root>
  )
}
