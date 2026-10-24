import { useState } from 'react'
import { useNavigate } from 'react-router'
import * as DropdownMenu from '@radix-ui/react-dropdown-menu'
import { Check, MoreHorizontal, Plus, X } from 'lucide-react'
import { useCalendarVisibility } from '../calendar-visibility'
import { calendarsApi } from '../api'
import {
  useCalendars,
  useCreateCalendar,
  useDeleteCalendar,
  useRenameCalendar,
  useSetCalendarMutePolicy,
} from '../queries'
import type { CalendarBook } from '../types'
import { Button, ConfirmDialog, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader } from '@/components/ui'
import { notifyError, notifySuccess } from '@/lib/notify'
import { cn } from '@/lib/utils'

function InlineInput({
  defaultValue,
  placeholder,
  onCommit,
  onCancel,
}: {
  defaultValue: string
  placeholder: string
  onCommit: (value: string) => void
  onCancel: () => void
}) {
  const [value, setValue] = useState(defaultValue)
  return (
    <input
      autoFocus
      value={value}
      placeholder={placeholder}
      onChange={(e) => setValue(e.target.value)}
      onBlur={() => (value.trim() ? onCommit(value.trim()) : onCancel())}
      onKeyDown={(e) => {
        if (e.key === 'Enter') (e.target as HTMLInputElement).blur()
        if (e.key === 'Escape') onCancel()
      }}
      className="h-7 w-full rounded-ctl border border-primary bg-bg px-2 text-[13px] text-text-1 outline-none"
    />
  )
}

function BookRow({
  book,
  onNavigate,
  onDelete,
  onMutePolicy,
}: {
  book: CalendarBook
  onNavigate: () => void
  onDelete: (book: CalendarBook) => void
  onMutePolicy: (book: CalendarBook) => void
}) {
  const navigate = useNavigate()
  const { calendarVisibility, toggleCalendar } = useCalendarVisibility()
  const rename = useRenameCalendar()
  const [renaming, setRenaming] = useState(false)

  const visible = calendarVisibility[book.id] ?? true
  const isTask = book.kind === 'task'

  if (renaming) {
    return (
      <div className="px-1 py-0.5">
        <InlineInput
          defaultValue={book.name}
          placeholder="日历本名称"
          onCommit={(name) => {
            setRenaming(false)
            if (name !== book.name) rename.mutate({ id: book.id, body: { name } })
          }}
          onCancel={() => setRenaming(false)}
        />
      </div>
    )
  }

  return (
    <div className="group flex h-7 items-center gap-1.5 rounded-ctl px-1.5 transition-colors hover:bg-surface-2">
      <button
        type="button"
        className="flex min-w-0 flex-1 items-center gap-2 text-left outline-none"
        onClick={() => {
          onNavigate()
          navigate(isTask ? `/tasks?taskBookId=${book.id}` : `/calendar?calendarId=${book.id}`)
        }}
        onDoubleClick={() => setRenaming(true)}
        title={`${book.name}（双击重命名）`}
      >
        <span
          className="size-2 shrink-0 rounded-full"
          style={{ backgroundColor: book.color }}
          aria-hidden
        />
        <span
          className={cn(
            'truncate text-[13px]',
            visible ? 'text-text-2' : 'text-text-4 line-through',
          )}
        >
          {book.name}
        </span>
        {isTask && (
          <span className="tnum ml-auto text-[11px] text-text-4">{book.eventCount}</span>
        )}
      </button>
      <button
        type="button"
        title={visible ? '在日历中隐藏' : '在日历中显示'}
        aria-pressed={visible}
        onClick={() => !isTask && toggleCalendar(book.id)}
        className={cn(
          'shrink-0 rounded-ctl p-0.5 outline-none transition-opacity',
          isTask
            ? 'invisible'
            : cn('text-text-3 hover:text-text-1', visible ? 'opacity-100' : 'opacity-40'),
        )}
        disabled={isTask}
      >
        {visible ? <Check className="size-3.5" aria-hidden /> : <X className="size-3.5" aria-hidden />}
      </button>
      <DropdownMenu.Root>
        <DropdownMenu.Trigger asChild>
          <button
            type="button"
            aria-label={`${book.name} 更多操作`}
            className="shrink-0 rounded-ctl p-0.5 text-text-4 opacity-0 transition-opacity outline-none group-hover:opacity-100 hover:text-text-1 data-[state=open]:opacity-100"
          >
            <MoreHorizontal className="size-3.5" aria-hidden />
          </button>
        </DropdownMenu.Trigger>
        <DropdownMenu.Portal>
          <DropdownMenu.Content
            sideOffset={4}
            align="end"
            className="z-50 min-w-28 rounded-ctl border border-border bg-bg p-1 shadow-overlay animate-[pim-pop-in_150ms_ease-out]"
          >
            <DropdownMenu.Item
              className="cursor-default rounded-ctl px-2.5 py-1.5 text-[13px] text-text-1 outline-none data-[highlighted]:bg-surface"
              onSelect={() => setRenaming(true)}
            >
              重命名
            </DropdownMenu.Item>
            <DropdownMenu.Item
              className="cursor-default rounded-ctl px-2.5 py-1.5 text-[13px] text-text-1 outline-none data-[highlighted]:bg-surface"
              onSelect={() => onMutePolicy(book)}
            >
              静音设置…
            </DropdownMenu.Item>
            <DropdownMenu.Item
              className="cursor-default rounded-ctl px-2.5 py-1.5 text-[13px] text-crit outline-none data-[highlighted]:bg-crit-soft"
              onSelect={() => onDelete(book)}
            >
              删除…
            </DropdownMenu.Item>
          </DropdownMenu.Content>
        </DropdownMenu.Portal>
      </DropdownMenu.Root>
    </div>
  )
}

/** 侧边栏日历本管理器（规格 01 §3：新建/双击重命名/显隐/删除影响预览+输入确认） */
export function CalendarBooksManager({ onNavigate }: { onNavigate?: () => void }) {
  const { data: books = [] } = useCalendars()
  const create = useCreateCalendar()
  const remove = useDeleteCalendar()

  const calendarBooks = books.filter((b) => b.kind === 'calendar')
  const taskBooks = books.filter((b) => b.kind === 'task')

  const setMutePolicy = useSetCalendarMutePolicy()
  const [adding, setAdding] = useState(false)
  const [muting, setMuting] = useState<CalendarBook | null>(null)
  const [deleting, setDeleting] = useState<CalendarBook | null>(null)
  const [preview, setPreview] = useState<{ summary: string; samples: string[] } | null>(null)
  const [previewLoading, setPreviewLoading] = useState(false)

  async function openDelete(book: CalendarBook) {
    setDeleting(book)
    setPreviewLoading(true)
    setPreview(null)
    try {
      const p = await calendarsApi.deletePreview(book.id)
      setPreview({
        summary: p.summary,
        samples: p.samples.map((s) => `${s.title}${s.start ? ` · ${s.start.slice(0, 16).replace('T', ' ')}` : ''}`),
      })
    } catch {
      setPreview({ summary: `删除「${book.name}」及其全部内容。`, samples: [] })
    } finally {
      setPreviewLoading(false)
    }
  }

  /** 一次调用改该本默认 + 该本既有日程；返回的受影响条数用可见提示说清，不静默成功。 */
  async function applyMutePolicy(isMuted: boolean) {
    if (!muting) return
    try {
      const result = await setMutePolicy.mutateAsync({ id: muting.id, isMuted })
      notifySuccess(
        `「${muting.name}」已统一设为${result.defaultMuted ? '静音' : '不静音'}`,
        `同时更新了该本下 ${result.affectedEventCount} 个日程的设定`,
      )
      setMuting(null)
    } catch (err) {
      notifyError(err instanceof Error ? err.message : '设置失败，请稍后重试')
    }
  }

  return (
    <div>
      {/* 日历本 */}
      <div className="flex items-center justify-between px-2.5 pt-3 pb-1">
        <span className="text-xs font-medium text-text-3">日历本 ({calendarBooks.length})</span>
        <button
          type="button"
          aria-label="新建日历本"
          onClick={() => setAdding(true)}
          className="rounded-ctl p-0.5 text-text-4 transition-colors hover:text-text-1 outline-none"
        >
          <Plus className="size-3.5" aria-hidden />
        </button>
      </div>
      <div className="px-2">
        {adding && (
          <div className="py-0.5">
            <InlineInput
              defaultValue=""
              placeholder="新日历本名称，回车创建"
              onCommit={(name) => {
                setAdding(false)
                create.mutate({ name })
              }}
              onCancel={() => setAdding(false)}
            />
          </div>
        )}
        {calendarBooks.map((b) => (
          <BookRow
            key={b.id}
            book={b}
            onNavigate={() => onNavigate?.()}
            onDelete={(bk) => void openDelete(bk)}
            onMutePolicy={setMuting}
          />
        ))}
        {calendarBooks.length === 0 && !adding && (
          <div className="px-1.5 py-1 text-xs text-text-4">暂无日历本</div>
        )}
      </div>

      {/* 任务本（只读列表，点击跳任务页） */}
      {taskBooks.length > 0 && (
        <>
          <div className="px-2.5 pt-3 pb-1 text-xs font-medium text-text-3">
            任务本 ({taskBooks.length})
          </div>
          <div className="px-2">
            {taskBooks.map((b) => (
              <BookRow
                key={b.id}
                book={b}
                onNavigate={() => onNavigate?.()}
                onDelete={(bk) => void openDelete(bk)}
                onMutePolicy={setMuting}
              />
            ))}
          </div>
        </>
      )}

      {/* 工单 04 E1.3：日历本静音设置 —— 显示当前状态 + 一次把该本下的日程统一为开/关 */}
      <Dialog open={muting != null} onOpenChange={(o) => !o && setMuting(null)}>
        <DialogContent className="max-w-[460px]">
          <DialogHeader
            title={`「${muting?.name ?? ''}」的静音设置`}
            description="设为静音后，该日历本下的日程在开始时间自动静音、结束时间恢复；之后从 Outlook 同步进来的日程也会套用这个设定。注意：这会把该本下已有日程的静音设定统一改写（含之前单独设过的那些）。"
          />
          <DialogBody className="space-y-3">
            <div className="rounded-ctl border border-border bg-surface p-3 text-[13px]" data-testid="calendar-mute-current">
              当前默认：
              <span className="font-medium text-text-1">
                {muting?.defaultMuted ? '静音' : '不静音'}
              </span>
              <span className="ml-1 text-text-4">（该本下「未指定」的日程按它生效）</span>
            </div>
            <div className="flex gap-2">
              <Button
                type="button"
                variant={muting?.defaultMuted ? 'primary' : 'secondary'}
                data-testid="calendar-mute-on"
                disabled={setMutePolicy.isPending}
                onClick={() => void applyMutePolicy(true)}
              >
                统一设为静音
              </Button>
              <Button
                type="button"
                variant={muting?.defaultMuted ? 'secondary' : 'primary'}
                data-testid="calendar-mute-off"
                disabled={setMutePolicy.isPending}
                onClick={() => void applyMutePolicy(false)}
              >
                统一设为不静音
              </Button>
            </div>
          </DialogBody>
          <DialogFooter>
            <Button type="button" variant="ghost" onClick={() => setMuting(null)}>
              关闭
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <ConfirmDialog
        open={deleting != null}
        onOpenChange={(o) => !o && setDeleting(null)}
        tone="danger"
        title={`删除日历本「${deleting?.name ?? ''}」？`}
        description="该操作将进入回收站。"
        impact={preview ?? { summary: previewLoading ? '正在获取影响预览…' : '' }}
        requireText={deleting?.name}
        confirmLabel="删除日历本"
        loading={remove.isPending || previewLoading}
        onConfirm={async () => {
          if (deleting) await remove.mutateAsync({ id: deleting.id })
        }}
      />
    </div>
  )
}
