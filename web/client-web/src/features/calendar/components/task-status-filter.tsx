import { Chip } from '@/components/ui'
import { TASK_STATUS_OPTIONS, type TaskStatus } from '../types'

export function toggleTaskStatus(selected: readonly TaskStatus[], status: TaskStatus): TaskStatus[] {
  return selected.includes(status)
    ? selected.filter((value) => value !== status)
    : [...selected, status]
}

export interface TaskStatusFilterProps {
  selected: readonly TaskStatus[]
  onChange: (statuses: TaskStatus[]) => void
}

/** R04：独立的任务状态多选；空选择表示全部状态。 */
export function TaskStatusFilter({ selected, onChange }: TaskStatusFilterProps) {
  return (
    <div className="flex flex-wrap items-center gap-1.5" role="group" aria-label="任务状态筛选">
      <span className="mr-1 text-[12px] text-text-4">状态</span>
      <Chip
        active={selected.length === 0}
        aria-pressed={selected.length === 0}
        onClick={() => onChange([])}
      >
        全部状态
      </Chip>
      {TASK_STATUS_OPTIONS.map((option) => {
        const active = selected.includes(option.value)
        return (
          <Chip
            key={option.value}
            active={active}
            aria-pressed={active}
            onClick={() => onChange(toggleTaskStatus(selected, option.value))}
          >
            {option.label}
          </Chip>
        )
      })}
      {selected.length > 0 && (
        <span className="ml-1 text-[12px] text-text-4">已选 {selected.length} 个状态</span>
      )}
    </div>
  )
}
