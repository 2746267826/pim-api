import { describe, expect, it } from 'vitest'
import { pendingArtifactVersion } from './pending-artifact-version'

const head = { versionId: 'head', createdAt: '2026-11-25T10:00:00Z', isHead: true }
const newer = { versionId: 'newer', createdAt: '2026-11-25T11:00:00Z', isHead: false }
const older = { versionId: 'older', createdAt: '2026-11-25T09:00:00Z', isHead: false }
const invalid = { versionId: 'invalid', createdAt: 'not-a-time', isHead: false }

describe('pendingArtifactVersion', () => {
  it('returns only the newest candidate that is newer than the head', () => {
    expect(pendingArtifactVersion({ headVersionId: 'head', versions: [head, newer, older] }))
      .toBe(newer)
  })

  it('does not present an invalid timestamp as a newer result', () => {
    expect(pendingArtifactVersion({ headVersionId: 'head', versions: [head, invalid] }))
      .toBeUndefined()
  })

  it('does not guess when the head timestamp is invalid', () => {
    const invalidHead = { versionId: 'head', createdAt: 'not-a-time', isHead: true }
    expect(pendingArtifactVersion({ headVersionId: 'head', versions: [invalidHead, newer] }))
      .toBeUndefined()
  })
})
