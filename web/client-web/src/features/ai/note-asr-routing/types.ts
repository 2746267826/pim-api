import type { Tone } from '@/components/ui'

export type RouteKind = 'local' | 'cloud'

export interface RouteOptionView {
  id: string
  label: string
  kind: RouteKind
  available: boolean
  isDefault?: boolean
  destinationLabel?: string
  availabilityLabel?: string
  costNotice?: string
}

export interface RouteCreateIntent {
  routeId: string
  mode: 'start' | 'queue'
  audioLeavesDevice: boolean
  possibleCharge: boolean
  confirmed: true
}

export interface TaskHistoryView {
  id: string
  title: string
  createdAtLabel: string
  routeLabel: string
  destinationLabel: string
  statusLabel: string
  statusTone?: Tone
  summary?: string
  attemptLabel?: string
  isCurrent?: boolean
}

export type RecoveryItemStatus = 'succeeded' | 'failed' | 'unknown' | 'queued' | 'running'

export interface RecoveryItemView {
  id: string
  label: string
  status: RecoveryItemStatus
  resultPreview?: string
  failureLabel?: string
}

export interface RecoveryRequestView {
  itemIds: string[]
  reason: string
  possibleDuplicateCharge: boolean
  confirmed: true
}

export type BatchStatusView =
  | 'validating'
  | 'waiting'
  | 'running'
  | 'partial'
  | 'unknown'
  | 'cancelling'
  | 'cancelled'
  | 'completed'
  | 'failed'
  | 'expired'

export interface BatchJobView {
  id: string
  title: string
  status: BatchStatusView
  createdAtLabel: string
  destinationLabel: string
  completionWindowLabel: string
  cashEstimateLabel: string
  progressLabel: string
  totalItems?: number | null
  completedItems?: number | null
  failedItems?: number | null
  providerRequestId?: string | null
  inputExpiresAtLabel?: string
  batchExpiresAtLabel?: string
  outputAvailable?: boolean
  errorLabel?: string
}

export interface RouteConfigView {
  revision: string
  defaultRouteId: string
  localModelId: string | null
  localModelRevision: string | null
  cloudConfigured: boolean
  cloudModelId: string | null
  consentVersion: string
  updatedAtLabel: string
}

export interface RouteConfigModelOption {
  id: string
  label: string
  revision: string | null
  enabled: boolean
  cloudConfigured?: boolean
}

/** undefined keeps the persisted value; null explicitly clears it. */
export interface RouteConfigSaveRequest {
  accountScope: string
  expectedRevision: string
  defaultRouteId: string
  localModelId?: string | null
  localModelRevision?: string | null
  cloudModelId?: string | null
  consentVersion: string
  confirmed: true
}
