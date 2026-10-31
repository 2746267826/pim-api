import type { NoteAsrReprocessRequest } from './api'

const prefix = 'pim.note-asr.reprocess-intent.v2'
export const noteAsrReprocessIntentScope = (userId: string, serverBinding: string, sourceTaskId: string) =>
  `${prefix}/${encodeURIComponent(userId)}/${encodeURIComponent(serverBinding)}/${encodeURIComponent(sourceTaskId)}`

export type NoteAsrReprocessIntentState =
  | { kind: 'none' }
  | { kind: 'ready'; value: NoteAsrReprocessRequest }
  | { kind: 'blocked'; message: string }

export function loadNoteAsrReprocessIntent(userId: string, serverBinding: string, sourceTaskId: string): NoteAsrReprocessIntentState {
  try {
    const raw = window.localStorage.getItem(noteAsrReprocessIntentScope(userId, serverBinding, sourceTaskId))
    if (!raw) return { kind: 'none' }
    const value = JSON.parse(raw) as NoteAsrReprocessRequest
    if (value?.clientTaskId && value.expectedUserId === userId) return { kind: 'ready', value }
    return { kind: 'blocked', message: '本机重处理提交意图与当前账号不匹配。为避免新付费任务，本入口已锁定；请先核对本机记录。' }
  } catch {
    return { kind: 'blocked', message: '本机重处理提交意图无法读取。为避免用新任务覆盖未知提交，本入口已锁定；请先核对本机记录。' }
  }
}

export function saveNoteAsrReprocessIntent(userId: string, serverBinding: string, sourceTaskId: string, intent: NoteAsrReprocessRequest): string | null {
  try {
    window.localStorage.setItem(noteAsrReprocessIntentScope(userId, serverBinding, sourceTaskId), JSON.stringify(intent))
    return null
  } catch {
    return '本机无法保存冻结的提交标识，已停止重处理且没有发送请求。'
  }
}

export function clearNoteAsrReprocessIntent(userId: string, serverBinding: string, sourceTaskId: string, clientTaskId: string) {
  try {
    const current = loadNoteAsrReprocessIntent(userId, serverBinding, sourceTaskId)
    if (current.kind === 'ready' && current.value.clientTaskId === clientTaskId) {
      window.localStorage.removeItem(noteAsrReprocessIntentScope(userId, serverBinding, sourceTaskId))
    }
  } catch {
    // Clearing is best-effort; the server response already owns the result.
  }
}
