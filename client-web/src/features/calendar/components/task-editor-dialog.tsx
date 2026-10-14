import { useEffect, useState } from 'react'
import { useForm } from 'react-hook-form'
import { zodResolver } from '@hookform/resolvers/zod'
import { z } from 'zod'
import { Check, Plus, Trash2 } from 'lucide-react'
import { ApiError } from '@/api/client'
import type { components } from '@/api/generated'
import { useChecklistMutations, useDeleteTask, useSaveTask } from '../queries'
import { TASK_STATUS, TASK_STATUS_OPTIONS, TASK_STATUS_VALUES, isKnownTaskStatus, normalizeTaskStatus } from '../types'
import type { TaskBook, TaskResponse } from '../types'
import { fromLocalInputValue, toLocalInputValue } from '@/lib/datetime'
import { InlineAlert, Button, Dialog, DialogBody, DialogContent, DialogFooter, DialogHeader, Input, Label, Select, Textarea } from '@/components/ui'

/** 与后端 CreateTaskRequest/UpdateTaskRequest 对齐的全部可写字段（05/calendar.md §任务） */
const schema = z
  .object({
    title: z.string().min(1, '请输入标题').max(255, '最多 255 字符'),
    taskBookId: z.string(),
    priority: z.string(),
    estimatedDuration: z.string(),
    minimumSegment: z.string(),
    due: z.string(),
    dtStart: z.string(),
    plannedEnd: z.string(),
    // 取值来自 types.ts 的唯一真值来源（与契约 enum / 服务端白名单同源，工单 03 D1）
    status: z.enum(TASK_STATUS_VALUES),
    percentComplete: z.string(),
    description: z.string(),
  })
  .refine((v) => !v.dtStart || !v.plannedEnd || v.plannedEnd > v.dtStart, {
    message: '计划结束须晚于计划开始',
    path: ['plannedEnd'],
  })

const PRIORITY_OPTIONS = [
  { value: '1', label: '低（1）' },
  { value: '5', label: '中（5）' },
  { value: '9', label: '高（9）' },
]

/** 时长输入提示（hh:mm:ss；后端亦接受 ISO8601 PT1H30M） */
const DURATION_HINT = 'hh:mm:ss，如 01:30:00'

export interface TaskEditorProps {
  open: boolean
  onOpenChange: (open: boolean) => void
  taskBooks: TaskBook[]
  task?: TaskResponse | null
  /** 新建时的预填计划开始（拖放场景） */
  initialPlannedStart?: Date | null
}

/** 任务编辑弹窗：属性 + 清单（时间段在独立面板 TaskSegmentsDialog） */
export function TaskEditorDialog({ open, onOpenChange, taskBooks, task, initialPlannedStart }: TaskEditorProps) {
  const isEdit = task != null
  const save = useSaveTask()
  const remove = useDeleteTask()
  const checklist = useChecklistMutations()
  const [error, setError] = useState<string | null>(null)
  const [newItem, setNewItem] = useState('')

  // 值域外的历史状态（工单 03 D1.3 不迁移旧值）：显式提示，不静默改写
  const legacyStatus = task && !isKnownTaskStatus(task.status) ? task.status : null

  const form = useForm<z.infer<typeof schema>>({
    resolver: zodResolver(schema),
    defaultValues: {
      title: '', taskBookId: '', priority: '5', estimatedDuration: '', minimumSegment: '',
      due: '', dtStart: '', plannedEnd: '', status: TASK_STATUS.needsAction, percentComplete: '0', description: '',
    },
  })

  useEffect(() => {
    if (!open) return
    setError(null)
    setNewItem('')
    if (task) {
      form.reset({
        title: task.title,
        taskBookId: task.taskBookId ?? '',
        priority: String(task.priority),
        estimatedDuration: task.estimatedDuration ?? '',
        minimumSegment: task.minimumSegment ?? '',
        due: toLocalInputValue(task.due),
        dtStart: toLocalInputValue(task.dtStart),
        plannedEnd: toLocalInputValue(task.plannedEnd),
        // 四值全部原样回显（工单 03 之前这里会把 IN-PROCESS 吞成 NEEDS-ACTION）；
        // 只有**值域外的历史值**（收紧前写进库的自由字符串）才归一到「待办」，
        // 并在下方给出显式提示，不静默改写。
        status: normalizeTaskStatus(task.status),
        percentComplete: String(task.percentComplete ?? 0),
        description: task.description ?? '',
      })
    } else {
      form.reset({
        title: '', taskBookId: '', priority: '5', estimatedDuration: '', minimumSegment: '',
        due: '', dtStart: initialPlannedStart ? toLocalInputValue(initialPlannedStart.toISOString()) : '',
        plannedEnd: '', status: TASK_STATUS.needsAction, percentComplete: '0', description: '',
      })
    }
  }, [open, task, initialPlannedStart, form])

  async function submit(values: z.infer<typeof schema>) {
    setError(null)
    const due = fromLocalInputValue(values.due)
    const dtStart = fromLocalInputValue(values.dtStart)
    const plannedEnd = fromLocalInputValue(values.plannedEnd)
    /* 契约类型（而非 Record<string, unknown>）：POST 与 PUT 的必填集由编译器校验 */
    const body: components['schemas']['CreateTaskRequest'] | components['schemas']['UpdateTaskRequest'] = {
      title: values.title,
      description: values.description || null,
      priority: Number(values.priority),
      estimatedDuration: values.estimatedDuration || null,
      minimumSegment: values.minimumSegment || null,
      due,
      dtStart,
      plannedEnd,
      status: values.status,
      percentComplete: Number(values.percentComplete) || 0,
      taskBookId: values.taskBookId || null,
    }
    try {
      await save.mutateAsync({ id: task?.id, body })
      onOpenChange(false)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '保存失败，请稍后重试')
    }
  }

  const values = form.watch()
  const checklistItems = task?.checklistItems ?? []

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-[560px]">
        <DialogHeader title={isEdit ? '编辑任务' : '新建任务'} />
        <DialogBody className="space-y-4">
          {error && <InlineAlert tone="crit">{error}</InlineAlert>}
          <div>
            <Label htmlFor="task-title">标题 *</Label>
            <Input id="task-title" {...form.register('title')} autoFocus />
            {form.formState.errors.title && (
              <p className="mt-1 text-xs text-crit">{form.formState.errors.title.message}</p>
            )}
          </div>

          <div className="grid grid-cols-3 gap-3">
            <div>
              <Label htmlFor="task-book">任务本</Label>
              <Select
                id="task-book"
                value={values.taskBookId || undefined}
                onValueChange={(v) => form.setValue('taskBookId', v)}
                options={taskBooks.map((b) => ({ value: b.id, label: b.name }))}
                placeholder="无（收集箱）"
              />
            </div>
            <div>
              <Label htmlFor="task-priority">优先级</Label>
              <Select
                id="task-priority"
                value={values.priority}
                onValueChange={(v) => form.setValue('priority', v)}
                options={PRIORITY_OPTIONS}
              />
            </div>
            <div>
              <Label htmlFor="task-status">状态</Label>
              <Select
                id="task-status"
                value={values.status}
                onValueChange={(v) => form.setValue('status', v as z.infer<typeof schema>['status'])}
                options={[...TASK_STATUS_OPTIONS]}
              />
              {legacyStatus && (
                <p className="mt-1 text-[11px] text-warn">
                  该任务是收紧状态值域之前写入的「{legacyStatus}」，不在当前可选状态内；保存后会变为「待办」。
                </p>
              )}
            </div>
          </div>

          <div className="grid grid-cols-2 gap-3">
            <div>
              <Label htmlFor="task-estimated">预计时长</Label>
              <Input id="task-estimated" className="mono" placeholder={DURATION_HINT} {...form.register('estimatedDuration')} />
            </div>
            <div>
              <Label htmlFor="task-minimum">最小可排段</Label>
              <Input id="task-minimum" className="mono" placeholder={DURATION_HINT} {...form.register('minimumSegment')} />
            </div>
          </div>

          <div className="grid grid-cols-3 gap-3">
            <div>
              <Label htmlFor="task-due">截止时间</Label>
              <Input id="task-due" type="datetime-local" {...form.register('due')} />
            </div>
            <div>
              <Label htmlFor="task-dtstart">计划开始</Label>
              <Input id="task-dtstart" type="datetime-local" {...form.register('dtStart')} />
            </div>
            <div>
              <Label htmlFor="task-plannedend">计划结束</Label>
              <Input id="task-plannedend" type="datetime-local" {...form.register('plannedEnd')} />
            </div>
          </div>
          {form.formState.errors.plannedEnd && (
            <p className="text-xs text-crit">{form.formState.errors.plannedEnd.message}</p>
          )}

          <div>
            <Label htmlFor="task-percent">完成百分比（{values.percentComplete}%）</Label>
            <input
              id="task-percent"
              type="range"
              min={0}
              max={100}
              step={5}
              value={values.percentComplete}
              onChange={(e) => form.setValue('percentComplete', e.target.value)}
              className="h-1.5 w-full cursor-pointer appearance-none rounded-full bg-surface-2 accent-primary outline-none"
            />
          </div>

          <div>
            <Label htmlFor="task-desc">描述</Label>
            <Textarea id="task-desc" rows={3} {...form.register('description')} />
          </div>

          {isEdit && (
            <>
              <div className="flex items-center gap-3 pt-1">
                <span className="text-xs font-medium text-text-3">检查清单</span>
                <span className="h-px flex-1 bg-divider" />
              </div>
              {checklistItems.length === 0 && (
                <p className="text-[11px] text-text-4">
                  后端 TaskResponse 不返回清单项、也没有清单读取端点（规格 calendar.md:353、495-501）：
                  此处仅可新增；已存条目需在后续版本由后端补齐读取端点后展示。
                </p>
              )}
              <div className="space-y-1.5">
                {checklistItems.map((item) => (
                  <div key={item.id} className="group flex items-center gap-2">
                    <button
                      type="button"
                      aria-label={item.isDone ? '取消完成' : '标记完成'}
                      onClick={() =>
                        checklist.update.mutate({ taskId: task.id, itemId: item.id, body: { isDone: !item.isDone } })
                      }
                      className={`grid size-4 shrink-0 place-items-center rounded-[4px] border transition-colors outline-none ${
                        item.isDone ? 'border-primary bg-primary text-primary-fg' : 'border-border-strong bg-bg'
                      }`}
                    >
                      {item.isDone && <Check className="size-3" strokeWidth={3} aria-hidden />}
                    </button>
                    <span className={`flex-1 text-[13px] ${item.isDone ? 'text-text-4 line-through' : 'text-text-1'}`}>
                      {item.title}
                    </span>
                    <button
                      type="button"
                      aria-label="删除清单项"
                      onClick={() => checklist.remove.mutate({ taskId: task.id, itemId: item.id })}
                      className="rounded-ctl p-0.5 text-text-4 opacity-0 transition-opacity outline-none group-hover:opacity-100 hover:text-crit"
                    >
                      <Trash2 className="size-3.5" aria-hidden />
                    </button>
                  </div>
                ))}
                <form
                  className="flex gap-2 pt-1"
                  onSubmit={(e) => {
                    e.preventDefault()
                    const title = newItem.trim()
                    if (!title) return
                    checklist.add.mutate({
                      taskId: task.id,
                      body: { title, sortOrder: checklistItems.length },
                    })
                    setNewItem('')
                  }}
                >
                  <Input
                    value={newItem}
                    onChange={(e) => setNewItem(e.target.value)}
                    placeholder="添加清单项，回车提交"
                    className="h-8"
                  />
                  <Button variant="secondary" size="sm" type="submit">
                    <Plus className="size-4" aria-hidden /> 添加
                  </Button>
                </form>
              </div>
            </>
          )}
        </DialogBody>
        <DialogFooter>
          {isEdit && (
            <Button
              variant="danger-soft"
              className="mr-auto"
              loading={remove.isPending}
              onClick={async () => {
                await remove.mutateAsync(task.id)
                onOpenChange(false)
              }}
            >
              <Trash2 className="size-4" aria-hidden /> 删除
            </Button>
          )}
          <Button variant="secondary" onClick={() => onOpenChange(false)}>取消</Button>
          <Button variant="primary" loading={save.isPending} onClick={() => void form.handleSubmit(submit)()}>
            保存
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
