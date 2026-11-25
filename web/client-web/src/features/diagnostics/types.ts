import type { components } from '@/api/generated'

export type DiagnosticEventUploadRequest =
  components['schemas']['DiagnosticEventUploadRequest']
export type DiagnosticEventUploadItem =
  components['schemas']['DiagnosticEventUploadItem']
export type DiagnosticUploadResult =
  components['schemas']['DiagnosticUploadResult']
export type DiagnosticOverview =
  components['schemas']['DiagnosticOverviewDto']
export type DiagnosticEvent = components['schemas']['DiagnosticEventDto']
export type DiagnosticDeviceSummary =
  components['schemas']['DiagnosticDeviceSummaryDto']

export const DIAGNOSTIC_PLATFORMS = ['android', 'windows', 'web'] as const
export const DIAGNOSTIC_KINDS = ['crash', 'error', 'warning', 'manual'] as const
export const DIAGNOSTIC_SEVERITIES = ['warning', 'error', 'fatal'] as const

export type DiagnosticPlatform = (typeof DIAGNOSTIC_PLATFORMS)[number]
export type DiagnosticKind = (typeof DIAGNOSTIC_KINDS)[number]
export type DiagnosticSeverity = (typeof DIAGNOSTIC_SEVERITIES)[number]
