import { apiGet, apiPost } from '@/api/client'
import type { components } from '@/api/generated'

export type NoteDerivedTask = components['schemas']['NoteDerivedTaskDto']
export type NoteDerivedSteps = components['schemas']['NoteDerivedStepsDto']
export type NoteDerivedSource = components['schemas']['NoteAsrSourceDto']
export type NoteDerivedSources = components['schemas']['NoteAsrSourcesDto']
export type NoteDerivedDestination = components['schemas']['NoteDerivedDestinationDto']
export type NoteDerivedDestinations = components['schemas']['NoteDerivedDestinationsDto']
export type NoteDerivedLimits = components['schemas']['NoteDerivedLimitsDto']
export type NoteDerivedArtifact = components['schemas']['NoteDerivedArtifactDto']
export type NoteDerivedVersion = components['schemas']['NoteDerivedVersionDto']
export type NoteDerivedMutation = components['schemas']['NoteDerivedMutationDto']
export type StartNoteDerived = components['schemas']['StartNoteDerivedTaskRequest']
export type RetryNoteDerived = components['schemas']['RetryNoteDerivedTaskRequest']
export type SaveNoteDerivedManual = components['schemas']['SaveNoteDerivedManualRevisionRequest']
export type SelectNoteDerivedVersion = components['schemas']['SelectNoteDerivedVersionRequest']

export const noteDerivedApi = {
  limits: (signal?: AbortSignal) => apiGet<NoteDerivedLimits>('/api/v1/native-notes/derived-tasks/limits', { signal }),
  destinations: (signal?: AbortSignal) => apiGet<NoteDerivedDestinations>('/api/v1/native-notes/derived-tasks/destinations', { signal }),
  sources: (offset: number, signal?: AbortSignal) => apiGet<NoteDerivedSources>(`/api/v1/native-notes/derived-tasks/sources?offset=${offset}&limit=20`, { signal }),
  tasks: (noteId: string | null, signal?: AbortSignal) => {
    const query = noteId ? `?noteId=${encodeURIComponent(noteId)}` : ''
    return apiGet<NoteDerivedTask[]>(`/api/v1/native-notes/derived-tasks${query}`, { signal })
  },
  task: (id: string, signal?: AbortSignal) => apiGet<NoteDerivedTask>(`/api/v1/native-notes/derived-tasks/${encodeURIComponent(id)}`, { signal }),
  steps: (id: string, offset: number, signal?: AbortSignal) => apiGet<NoteDerivedSteps>(`/api/v1/native-notes/derived-tasks/${encodeURIComponent(id)}/steps?offset=${offset}&limit=20`, { signal }),
  start: (body: StartNoteDerived) => apiPost<NoteDerivedTask>('/api/v1/native-notes/derived-tasks', body),
  stop: (id: string) => apiPost<NoteDerivedTask>(`/api/v1/native-notes/derived-tasks/${encodeURIComponent(id)}/stop`, {}),
  retry: (id: string, body: RetryNoteDerived) => apiPost<NoteDerivedTask>(`/api/v1/native-notes/derived-tasks/${encodeURIComponent(id)}/retry`, body),
  artifacts: (noteId: string, signal?: AbortSignal) => apiGet<NoteDerivedArtifact[]>(`/api/v1/native-notes/derived-artifacts?noteId=${encodeURIComponent(noteId)}`, { signal }),
  artifact: (id: string, signal?: AbortSignal) => apiGet<NoteDerivedArtifact>(`/api/v1/native-notes/derived-artifacts/${encodeURIComponent(id)}`, { signal }),
  version: (artifactId: string, versionId: string, signal?: AbortSignal) => apiGet<NoteDerivedVersion>(`/api/v1/native-notes/derived-artifacts/${encodeURIComponent(artifactId)}/versions/${encodeURIComponent(versionId)}`, { signal }),
  saveManual: (artifactId: string, body: SaveNoteDerivedManual) => apiPost<NoteDerivedMutation>(`/api/v1/native-notes/derived-artifacts/${encodeURIComponent(artifactId)}/manual-revisions`, body),
  select: (artifactId: string, body: SelectNoteDerivedVersion) => apiPost<NoteDerivedArtifact>(`/api/v1/native-notes/derived-artifacts/${encodeURIComponent(artifactId)}/select`, body),
}
