import type { NoteAsrStart } from './api'

const prefix = 'pim.note-asr.start-intent.v2'
export const noteAsrStartIntentScope = (userId: string, serverBinding: string, versionId: string) =>
  `${prefix}/${encodeURIComponent(userId)}/${encodeURIComponent(serverBinding)}/${encodeURIComponent(versionId)}`

export type NoteAsrStartIntentState =
  | { kind: 'none' }
  | { kind: 'ready'; value: NoteAsrStart }
  | { kind: 'blocked'; message: string }

/** A body frozen before the first POST. It is retried by identity, never rebuilt. */
export function loadNoteAsrStartIntent(userId: string, serverBinding: string, versionId: string): NoteAsrStartIntentState {
  try {
    const raw = window.localStorage.getItem(noteAsrStartIntentScope(userId, serverBinding, versionId))
    if (!raw) return { kind: 'none' }
    const value = JSON.parse(raw) as NoteAsrStart
    if (value?.taskId && value.expectedUserId === userId && value.versionId === versionId) {
      return { kind: 'ready', value }
    }
    return { kind: 'blocked', message: '本机保存的转写提交意图与当前账号或材料版本不匹配。为避免新付费任务，本入口已锁定；请先联系维护者核对本机记录。' }
  } catch {
    return { kind: 'blocked', message: '本机保存的转写提交意图无法读取。为避免用新任务覆盖未知提交，本入口已锁定；请先联系维护者核对本机记录。' }
  }
}

export function saveNoteAsrStartIntent(userId: string, serverBinding: string, versionId: string, intent: NoteAsrStart): string | null {
  try {
    window.localStorage.setItem(noteAsrStartIntentScope(userId, serverBinding, versionId), JSON.stringify(intent))
    return null
  } catch {
    return '本机无法保存冻结的提交标识，已停止创建任务且没有发送请求。请检查本机存储后重试。'
  }
}

export function clearNoteAsrStartIntent(userId: string, serverBinding: string, versionId: string, taskId: string) {
  try {
    const current = loadNoteAsrStartIntent(userId, serverBinding, versionId)
    if (current.kind === 'ready' && current.value.taskId === taskId) {
      window.localStorage.removeItem(noteAsrStartIntentScope(userId, serverBinding, versionId))
    }
  } catch {
    // Clearing is best-effort; the caller still has the server-created task.
  }
}
