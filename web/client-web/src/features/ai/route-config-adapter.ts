import type { NoteAsrRouteConfig, NoteAsrRouteConfigSave } from './api'
import type { RouteConfigSaveRequest } from './note-asr-routing'

export const ActualNoteAsrRoutes = {
  local: 'local',
  cloudAsr: 'cloud-asr',
  cloudMultimodal: 'cloud-multimodal',
} as const

export const isActualCloudRoute = (route: string): boolean =>
  route === ActualNoteAsrRoutes.cloudAsr || route === ActualNoteAsrRoutes.cloudMultimodal

/** Resolve the real cloud route from the profile's ASR preset, never shorthand "cloud". */
export function actualCloudRoute(presetId: string | null | undefined): string {
  return presetId === 'mimo-multimodal'
    ? ActualNoteAsrRoutes.cloudMultimodal
    : ActualNoteAsrRoutes.cloudAsr
}

interface ProfileRef {
  id: string
  revision?: string | null
  presetId?: string | null
}

/**
 * The routing config PUT is a full replacement. The pure editor carries a
 * cloud category, so the real wire route comes from the selected cloud
 * profile. When it stays on the already-configured cloud profile, keep the
 * persisted actual route (cloud-asr or cloud-multimodal) to avoid guessing.
 */
export function toRouteConfigSave(
  intent: RouteConfigSaveRequest,
  currentConfig: NoteAsrRouteConfig,
  selectedCloud: ProfileRef | null,
  selectedLocal: ProfileRef | null,
): NoteAsrRouteConfigSave {
  const localFromIntent = intent.localModelId !== undefined
  const localModelId = localFromIntent ? intent.localModelId ?? null : currentConfig.localModelId ?? null
  const localModelRevision = localFromIntent
    ? intent.localModelRevision ?? null
    : currentConfig.localModelRevision ?? null
  const cloudModelId = intent.cloudModelId !== undefined ? intent.cloudModelId : currentConfig.cloudModelId ?? null

  let defaultRoute: string
  if (intent.defaultRouteId !== 'cloud') {
    defaultRoute = ActualNoteAsrRoutes.local
  } else if (cloudModelId && cloudModelId === currentConfig.cloudModelId && isActualCloudRoute(currentConfig.defaultRoute)) {
    defaultRoute = currentConfig.defaultRoute
  } else if (cloudModelId && selectedCloud?.id === cloudModelId) {
    defaultRoute = actualCloudRoute(selectedCloud.presetId)
  } else {
    defaultRoute = ActualNoteAsrRoutes.cloudAsr
  }

  const localMatchesSelection = Boolean(localModelId && selectedLocal?.id === localModelId)
  return {
    expectedRevision: intent.expectedRevision,
    defaultRoute,
    localModelId,
    localModelRevision: localModelId
      ? localMatchesSelection ? selectedLocal?.revision ?? null : localModelRevision
      : null,
    cloudModelId,
    consentVersion: intent.consentVersion,
  }
}
