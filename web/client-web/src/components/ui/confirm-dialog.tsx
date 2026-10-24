import { useEffect, useState } from 'react'
import type { ReactNode } from 'react'
import { cn } from '@/lib/utils'
import { Button } from './button'
import { Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader } from './dialog'
import { Input } from './input'

export interface ImpactPreview {
  /** 影响摘要，如"将连带删除 128 条日程与任务。" */
  summary: ReactNode
  /** 受影响样例（最多展示 5 行） */
  samples?: string[]
}

export interface ConfirmDialogProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  title: ReactNode
  description?: ReactNode
  /** danger 时确认按钮与标题使用危险语义 */
  tone?: 'default' | 'danger'
  /** 删除影响预览（规格"删除影响预览"结构：影响数+样例） */
  impact?: ImpactPreview
  /** 输入确认：需键入该文本才能点击确认（日历本删除等高危操作） */
  requireText?: string
  confirmLabel?: string
  cancelLabel?: string
  loading?: boolean
  onConfirm: () => void | Promise<void>
}

/**
 * 统一破坏性操作确认对话框（01 §7）：
 * 普通 / 影响预览 / 输入确认 三档由 props 组合表达。
 */
export function ConfirmDialog({
  open,
  onOpenChange,
  title,
  description,
  tone = 'default',
  impact,
  requireText,
  confirmLabel = '确认',
  cancelLabel = '取消',
  loading = false,
  onConfirm,
}: ConfirmDialogProps) {
  const [input, setInput] = useState('')

  useEffect(() => {
    if (open) setInput('')
  }, [open])

  const armed = !requireText || input === requireText

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-[440px]">
        <DialogHeader
          title={<span className={cn(tone === 'danger' && 'text-crit')}>{title}</span>}
          description={description}
        />
        <DialogBody className="space-y-3">
          {impact && (
            <div className="rounded-ctl border border-border bg-surface px-3 py-2.5 text-[13px] text-text-2">
              <div>{impact.summary}</div>
              {impact.samples && impact.samples.length > 0 && (
                <ul className="mt-1.5 space-y-0.5 text-xs text-text-3">
                  {impact.samples.slice(0, 5).map((s, i) => (
                    <li key={i} className="truncate">
                      · {s}
                    </li>
                  ))}
                  {impact.samples.length > 5 && <li>… 共 {impact.samples.length} 条</li>}
                </ul>
              )}
            </div>
          )}
          {requireText && (
            <div>
              <div className="mb-1.5 text-xs text-text-3">
                请输入 <span className="font-medium text-text-1">{requireText}</span> 以确认执行
              </div>
              <Input
                value={input}
                onChange={(e) => setInput(e.target.value)}
                placeholder={requireText}
                autoFocus
              />
            </div>
          )}
        </DialogBody>
        <DialogFooter>
          <Button variant="secondary" onClick={() => onOpenChange(false)} disabled={loading}>
            {cancelLabel}
          </Button>
          <Button
            variant={tone === 'danger' ? 'danger' : 'primary'}
            disabled={!armed}
            loading={loading}
            onClick={async () => {
              await onConfirm()
              onOpenChange(false)
            }}
          >
            {confirmLabel}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
