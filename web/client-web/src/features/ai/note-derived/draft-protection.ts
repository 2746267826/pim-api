/**
 * Local-only draft protection. Network results are deliberately classified here
 * before any component mutates the visible text or the selected artifact version.
 */
export interface TextDraftState {
  readonly text: string
  readonly savedText: string
  readonly baseToken: string
  readonly editRevision: number
  readonly generationSequence: number
  readonly saveSequence: number
}

export interface GenerationRequestSnapshot {
  readonly sequence: number
  readonly editRevision: number
  readonly baseToken: string
  readonly text: string
}

export interface ManualSaveSnapshot {
  readonly sequence: number
  readonly editRevision: number
  readonly baseToken: string
  readonly text: string
}

export type GenerationArrival =
  | { readonly status: 'superseded'; readonly reason: 'newer-request' }
  | { readonly status: 'source-mismatch'; readonly reason: 'base-changed' }
  | { readonly status: 'candidate'; readonly reason: 'draft-clean'; readonly preserveDraft: false }
  | { readonly status: 'candidate'; readonly reason: 'draft-changed'; readonly preserveDraft: true }

export type ManualSaveOutcome =
  | { readonly kind: 'saved'; readonly headToken: string }
  | { readonly kind: 'conflict'; readonly candidatePreserved: boolean }

export type ManualSaveResolution =
  | { readonly status: 'superseded'; readonly state: TextDraftState }
  | { readonly status: 'edited-during-save'; readonly state: TextDraftState }
  | { readonly status: 'conflict-preserved'; readonly state: TextDraftState }
  | { readonly status: 'saved'; readonly state: TextDraftState }

export function createTextDraft(text = '', baseToken = ''): TextDraftState {
  return {
    text,
    savedText: text,
    baseToken,
    editRevision: 0,
    generationSequence: 0,
    saveSequence: 0,
  }
}

export function isTextDraftDirty(state: TextDraftState): boolean {
  return state.text !== state.savedText
}

/** A mutation became the current head only when the returned head points at its version. */
export function isVersionHead(headVersionId: string | null | undefined, candidateVersionId: string): boolean {
  return Boolean(candidateVersionId) && headVersionId === candidateVersionId
}

export function editTextDraft(state: TextDraftState, text: string): TextDraftState {
  if (text === state.text) return state
  return { ...state, text, editRevision: state.editRevision + 1 }
}

export function beginGeneration(state: TextDraftState): {
  readonly state: TextDraftState
  readonly snapshot: GenerationRequestSnapshot
} {
  const sequence = state.generationSequence + 1
  return {
    state: { ...state, generationSequence: sequence },
    snapshot: {
      sequence,
      editRevision: state.editRevision,
      baseToken: state.baseToken,
      text: state.text,
    },
  }
}

/**
 * Classifies a returned generation result without changing the draft or head.
 * A changed local draft is always retained; even a clean candidate requires an
 * explicit user selection before it can become the selected text.
 */
export function classifyGenerationArrival(
  state: TextDraftState,
  request: GenerationRequestSnapshot,
): GenerationArrival {
  if (request.sequence !== state.generationSequence) {
    return { status: 'superseded', reason: 'newer-request' }
  }
  if (request.baseToken !== state.baseToken) {
    return { status: 'source-mismatch', reason: 'base-changed' }
  }
  if (request.editRevision !== state.editRevision || request.text !== state.text) {
    return { status: 'candidate', reason: 'draft-changed', preserveDraft: true }
  }
  return { status: 'candidate', reason: 'draft-clean', preserveDraft: false }
}

/**
 * Explicitly selecting a candidate/history version is the only local operation
 * that replaces the visible draft and its saved baseline.
 */
export function selectTextVersion(
  state: TextDraftState,
  text: string,
  baseToken: string,
): TextDraftState {
  if (text === state.text && baseToken === state.baseToken && !isTextDraftDirty(state)) {
    return state
  }
  return {
    ...state,
    text,
    savedText: text,
    baseToken,
    editRevision: state.editRevision + 1,
  }
}

export function beginManualSave(state: TextDraftState): {
  readonly state: TextDraftState
  readonly snapshot: ManualSaveSnapshot
} {
  const sequence = state.saveSequence + 1
  return {
    state: { ...state, saveSequence: sequence },
    snapshot: {
      sequence,
      editRevision: state.editRevision,
      baseToken: state.baseToken,
      text: state.text,
    },
  }
}

/**
 * Resolves a manual-save receipt without allowing an older save or a conflict
 * to erase newer edits. Conflict receipts always retain the local draft.
 */
export function resolveManualSave(
  state: TextDraftState,
  request: ManualSaveSnapshot,
  outcome: ManualSaveOutcome,
): ManualSaveResolution {
  if (request.sequence !== state.saveSequence) {
    return { status: 'superseded', state }
  }
  if (request.editRevision !== state.editRevision || request.text !== state.text) {
    return { status: 'edited-during-save', state }
  }
  if (outcome.kind === 'conflict') {
    return { status: 'conflict-preserved', state }
  }
  return {
    status: 'saved',
    state: {
      ...state,
      savedText: state.text,
      baseToken: outcome.headToken,
    },
  }
}
