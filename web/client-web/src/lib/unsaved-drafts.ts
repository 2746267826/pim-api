import { useEffect, useSyncExternalStore } from 'react'

export type UnsavedDraftKind = 'note' | 'handwriting' | 'image' | 'file' | 'quick-note'

export interface UnsavedDraftDescriptor {
  /** 同一编辑对象的稳定标识；重复注册会覆盖旧描述。 */
  readonly id: string
  readonly kind: UnsavedDraftKind
  readonly title: string
  readonly detail: string
  readonly saveHint: string
  readonly saveHref?: string
}

const drafts = new Map<string, UnsavedDraftDescriptor>()
const listeners = new Set<() => void>()
let snapshot: readonly UnsavedDraftDescriptor[] = []

function rebuildSnapshot() {
  snapshot = [...drafts.values()].sort((left, right) => left.id.localeCompare(right.id))
}

function notify() {
  rebuildSnapshot()
  for (const listener of listeners) listener()
}

export function registerUnsavedDraft(descriptor: UnsavedDraftDescriptor): () => void {
  const normalized: UnsavedDraftDescriptor = {
    ...descriptor,
    id: descriptor.id.trim(),
    title: descriptor.title.trim() || '未命名草稿',
    detail: descriptor.detail.trim(),
    saveHint: descriptor.saveHint.trim(),
  }
  if (!normalized.id) throw new Error('未保存草稿缺少稳定标识')
  drafts.set(normalized.id, normalized)
  notify()
  return () => {
    if (drafts.get(normalized.id) !== normalized) return
    drafts.delete(normalized.id)
    notify()
  }
}

export function listUnsavedDrafts(): readonly UnsavedDraftDescriptor[] {
  return snapshot
}

function subscribe(listener: () => void): () => void {
  listeners.add(listener)
  return () => listeners.delete(listener)
}

function getSnapshot(): readonly UnsavedDraftDescriptor[] {
  return snapshot
}

export function useUnsavedDrafts(): readonly UnsavedDraftDescriptor[] {
  return useSyncExternalStore(subscribe, getSnapshot, getSnapshot)
}

/** 组件在存在未保存内容时注册保护项；内容保存或组件卸载后自动注销。 */
export function useUnsavedDraft(active: boolean, descriptor: UnsavedDraftDescriptor | null): void {
  const serialized = descriptor ? JSON.stringify(descriptor) : ''
  useEffect(() => {
    if (!active || !serialized) return
    return registerUnsavedDraft(JSON.parse(serialized) as UnsavedDraftDescriptor)
  }, [active, serialized])
}

export function resetUnsavedDraftsForTests(): void {
  drafts.clear()
  notify()
}
