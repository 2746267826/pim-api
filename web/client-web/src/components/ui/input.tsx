import type { InputHTMLAttributes, Ref, TextareaHTMLAttributes } from 'react'
import { cn } from '@/lib/utils'

const baseField =
  'w-full rounded-ctl border border-border bg-bg text-sm text-text-1 transition-colors duration-150 placeholder:text-text-4 focus:border-primary focus:outline-none disabled:cursor-not-allowed disabled:opacity-50'

export interface InputProps extends InputHTMLAttributes<HTMLInputElement> {
  ref?: Ref<HTMLInputElement>
}

export function Input({ className, type = 'text', ...props }: InputProps) {
  return (
    <input
      type={type}
      className={cn(baseField, 'h-9 px-3', className)}
      {...props}
    />
  )
}

export interface TextareaProps extends TextareaHTMLAttributes<HTMLTextAreaElement> {
  ref?: Ref<HTMLTextAreaElement>
}

export function Textarea({ className, ...props }: TextareaProps) {
  return <textarea className={cn(baseField, 'min-h-20 px-3 py-2', className)} {...props} />
}
