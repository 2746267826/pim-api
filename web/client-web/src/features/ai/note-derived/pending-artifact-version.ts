export interface PendingArtifactVersion {
  readonly versionId: string
  readonly createdAt: string
  readonly isHead: boolean
}

export interface PendingArtifact {
  readonly headVersionId?: string | null
  readonly versions: readonly PendingArtifactVersion[]
}

/**
 * Returns the newest unadopted result that is provably newer than the current head.
 * Missing or invalid timestamps are excluded rather than guessed, so an old draft can
 * never be presented as a newer result.
 */
export function pendingArtifactVersion(artifact: PendingArtifact): PendingArtifactVersion | undefined {
  const head = artifact.versions.find((item) => item.isHead || item.versionId === artifact.headVersionId)
  const headCreatedAt = head ? Date.parse(head.createdAt) : Number.NaN
  return artifact.versions
    .filter((item) => !item.isHead && item.versionId !== artifact.headVersionId)
    .map((item) => ({ item, createdAt: Date.parse(item.createdAt) }))
    .filter((entry) => Number.isFinite(entry.createdAt))
    .filter((entry) => !head || (Number.isFinite(headCreatedAt) && entry.createdAt > headCreatedAt))
    .sort((left, right) => right.createdAt - left.createdAt)
    .map((entry) => entry.item)[0]
}
