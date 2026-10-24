/** 确认单（OperationConfirmationDto；riskLevel/status 后端输出数字枚举，消费时必须 normalize） */
export interface OperationConfirmation {
  id: string
  requestedByUserId: string | null
  operationType: string
  summary: string
  riskLevel: number | string
  source: string
  payloadJson: string
  previewJson: string
  status: number | string
  expiresAt: string
  createdAt: string
  confirmedAt: string | null
  executedAt: string | null
  resultJson: string | null
  correlationId: string | null
  changedFields: string[] | null
  allowedActions: string[] | null
  objectType: string | null
  objectId: string | null
  requiresSecondLevelConfirmation: boolean
  beforeJson: string | null
  afterJson: string | null
  requiresStrictConfirmation: boolean
  auditBatchId: string | null
  aiRecommendation: string | null
  externalEffect: string | null
  recoveryPath: string | null
}
