import { useQuery } from '@tanstack/react-query'
import { Button, Skeleton, StatusBadge } from '@/components/ui'
import { formatTime } from '@/lib/datetime'
import { aiApi, type NoteAsrTask } from './api'
import { TaskDetail } from './note-asr-tasks'
import { NoteAsrOwnerBatches } from './note-asr-owner-batches'

const phaseNames: Record<string, string> = {
  Queued: '等待处理', Running: '转写中', Succeeded: '转写完成', NeedsAttention: '有分片需处理',
  Stopped: '已停止', Failed: '失败', Blocked: '未发送', Cancelled: '已取消', Unknown: '处理状态需核对',
}

function stateTone(phase: string) {
  if (phase === 'Succeeded') return 'ok' as const
  if (phase === 'Queued' || phase === 'Running') return 'info' as const
  return 'warn' as const
}

/** Ordinary-owner ASR task list and detail. It uses only public note ASR APIs. */
export function NoteAsrOwnerTasks({ userId, serverBinding, versionId, selectedTaskId, onSelect }: {
  userId: string
  serverBinding: string
  versionId: string
  selectedTaskId: string
  onSelect: (taskId: string) => void
}) {
  const prefix = ['ai', userId, serverBinding, 'note-asr-owner']
  const tasks = useQuery({
    queryKey: [...prefix, 'tasks'],
    queryFn: ({ signal }) => aiApi.noteAsrTasks(signal),
    refetchInterval: 5000,
  })
  const versionTasks = (tasks.data ?? []).filter((task) => task.versionId === versionId)
  const selected = selectedTaskId || versionTasks[0]?.taskId || ''
  const selectedTask = versionTasks.find((task) => task.taskId === selected) ?? versionTasks[0]

  return <div className="space-y-3">
    {selectedTask && <div role="status" aria-label="当前转写任务摘要" className="rounded-ctl border border-border bg-bg p-3 text-xs text-text-2">
      <span className="font-medium">转写进度：{phaseNames[selectedTask.phase] ?? selectedTask.phase}</span>
      <span className="ml-2 text-text-3">{selectedTask.completedSegments}/{selectedTask.totalSegments} 片 · 失败 {selectedTask.failedSegments}</span>
    </div>}
    <details className="rounded-card border border-border bg-bg px-4 py-3 shadow-card">
    <summary className="cursor-pointer text-sm font-medium text-text-2">更多：转写任务与恢复{versionTasks.length ? ` · ${versionTasks.length} 项` : ''}</summary>
    <div className="mt-3 min-w-0 space-y-3 p-1">
      <div>
        <h3 className="text-sm font-semibold text-text-1">本版本录音转写任务</h3>
        <p className="mt-1 break-words text-xs text-text-3">每个任务保留独立错误、分片结果和重处理历史；旧结果不会被新任务覆盖。</p>
      </div>
    {tasks.isPending ? <Skeleton className="h-16" />
      : tasks.isError ? <p role="alert" className="text-sm text-text-2">任务读取失败，请刷新核对；页面不会自动重发。</p>
        : !versionTasks.length ? <p className="text-sm text-text-3">该材料版本还没有转写任务。</p>
          : <div className="space-y-2">{versionTasks.map((task) => <TaskRow key={task.taskId} task={task} selected={task.taskId === selected} onSelect={onSelect} />)}</div>}
    {selected && <TaskDetail key={`${serverBinding}/${selected}`} id={selected} userId={userId} serverBinding={serverBinding}
      prefix={prefix} />}
    {selectedTask && <NoteAsrOwnerBatches key={`batches/${serverBinding}/${selectedTask.taskId}`} task={selectedTask}
      userId={userId} serverBinding={serverBinding} onCreated={(task) => onSelect(task.taskId)} />}
    {tasks.isError && <Button size="sm" onClick={() => void tasks.refetch()}>刷新任务</Button>}
    </div>
    </details>
  </div>
}

function TaskRow({ task, selected, onSelect }: { task: NoteAsrTask; selected: boolean; onSelect: (id: string) => void }) {
  return <button type="button" onClick={() => onSelect(task.taskId)}
    className={`w-full min-w-0 rounded-ctl border p-3 text-left text-xs ${selected ? 'border-primary bg-primary/5' : 'border-border hover:bg-surface'}`}>
    <span className="flex flex-wrap items-center justify-between gap-2">
      <span className="break-words text-text-2">{formatTime(task.createdAt)} · {task.completedSegments}/{task.totalSegments} 片 · 失败 {task.failedSegments}</span>
      <StatusBadge size="xs" dot={false} tone={stateTone(task.phase)}>{phaseNames[task.phase] ?? task.phase}</StatusBadge>
    </span>
    <span className="mt-1 block text-text-3">{task.destination ? '云端转写' : '本机排队转写'}</span>
    {task.stopRequested && <span className="mt-1 block text-warn">已请求停止；未知结果不会自动重试。</span>}
  </button>
}
