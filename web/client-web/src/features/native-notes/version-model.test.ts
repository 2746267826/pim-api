// @vitest-environment node
import { describe, expect, it } from 'vitest'
import type { NoteMaterialUpload, NoteMaterialVersion } from './material-model'
import {
  buildNoteVersionViews,
  noteVersionKind,
  safeVersionSummary,
  versionShortId,
} from './version-model'

const noteId = '11111111-1111-4111-8111-111111111111'
const currentVersionId = '22222222-2222-4222-8222-222222222222'
const historyVersionId = '33333333-3333-4333-8333-333333333333'
const conflictVersionId = '44444444-4444-4444-8444-444444444444'

function upload(overrides: Partial<NoteMaterialUpload> = {}): NoteMaterialUpload {
  return {
    versionId: currentVersionId,
    noteId,
    baseVersionId: null,
    currentVersionId,
    phase: 'Uploaded',
    createdAt: '2026-11-01T10:00:00Z',
    expiresAt: '2026-11-01T11:00:00Z',
    isCurrent: true,
    hasConflict: false,
    limits: {
      partSizeBytes: 1024,
      maxMetadataCharacters: 8_388_608,
      maxVersionBytes: 64 * 1024 * 1024 * 1024,
      maxUserReservedBytes: 64 * 1024 * 1024 * 1024,
      maxFiles: 100_000,
      maxPendingVersions: 10,
      maxVersionsPerUser: 1000,
      uploadExpiryHours: 1,
    },
    files: [],
    ...overrides,
  }
}

function material(versionId: string, text: string, title = '课堂笔记'): NoteMaterialVersion {
  const inputJson = JSON.stringify({
    schemaVersion: 1,
    id: versionId,
    note: {
      schemaVersion: 1,
      id: noteId,
      revision: 3,
      title,
      createdAtMillis: 1,
      blocks: [{ id: '55555555-5555-4555-8555-555555555555', kind: 'markdown', text }],
    },
    recordings: [],
  })
  return {
    inputJson,
    manifestJson: JSON.stringify({
      schemaVersion: 1,
      versionId,
      noteId,
      revision: 3,
      files: [],
    }),
    upload: upload({ versionId, currentVersionId }),
  }
}

describe('native note version model', () => {
  it('labels current, history, conflict and unfinished versions without guessing success', () => {
    const versions = [
      upload(),
      upload({ versionId: historyVersionId, isCurrent: false, phase: 'Uploaded' }),
      upload({ versionId: conflictVersionId, isCurrent: false, hasConflict: true }),
      upload({ versionId: '66666666-6666-4666-8666-666666666666', isCurrent: false, phase: 'Uploading' }),
    ]

    expect(versions.map(noteVersionKind)).toEqual(['current', 'history', 'conflict', 'uploading'])
    const views = buildNoteVersionViews(versions)
    expect(views.map((view) => view.kindLabel)).toEqual(['当前版', '历史版', '冲突候选', '未完成上传'])
    expect(views.map((view) => view.canOpen)).toEqual([true, true, true, false])
    expect(views.map((view) => view.canPromote)).toEqual([false, true, true, false])
    expect(versionShortId(currentVersionId)).toBe('22222222')
  })

  it('never labels the current version as a conflict candidate', () => {
    const views = buildNoteVersionViews([
      upload({ hasConflict: true, phase: 'Conflict' }),
      upload({ versionId: conflictVersionId, isCurrent: false, hasConflict: true }),
    ])

    expect(views.map((view) => view.kind)).toEqual(['current', 'conflict'])
    expect(views[0]).toMatchObject({ kindLabel: '当前版', canPromote: false })
    expect(views[1]).toMatchObject({ kindLabel: '冲突候选', canPromote: true })
  })

  it('builds a safe human summary from validated material', () => {
    const summary = safeVersionSummary(material(currentVersionId, [
      '## 学习率',
      '',
      '比较 **固定学习率** 与 `衰减学习率`。',
    ].join('\n')))

    expect(summary).toMatchObject({
      title: '课堂笔记',
      revision: 3,
      markdownBlocks: 1,
      photoBlocks: 0,
      inkBlocks: 0,
      recordings: 0,
    })
    expect(summary?.excerpt).toContain('学习率')
    expect(summary?.excerpt).not.toContain('##')
    expect(summary?.excerpt).not.toContain('**')
    expect(summary?.excerpt).not.toContain('`')
  })

  it('returns no summary for mismatched or malformed material instead of echoing raw JSON', () => {
    const broken = material(currentVersionId, '正文')
    broken.manifestJson = JSON.stringify({ schemaVersion: 1, versionId: historyVersionId, noteId, revision: 3, files: [] })

    expect(safeVersionSummary(broken)).toBeNull()
    expect(safeVersionSummary(undefined)).toBeNull()
  })

  it('omits engineering metadata from a conflict summary', () => {
    const withMetadata = material(currentVersionId, '可展示的正文')
    const input = JSON.parse(withMetadata.inputJson) as {
      note: Record<string, unknown>
      recordings: unknown[]
    }
    Object.assign(input, {
      token: 'synthetic-token',
      changeKey: 'synthetic-change-key',
      metadata: {
        graphEventId: 'synthetic-graph-event-id',
        taskId: 'synthetic-internal-task-id',
      },
    })
    withMetadata.inputJson = JSON.stringify(input)

    const summary = safeVersionSummary(withMetadata)
    const serialized = JSON.stringify(summary)

    expect(summary?.excerpt).toBe('可展示的正文')
    expect(serialized).not.toContain('synthetic-token')
    expect(serialized).not.toContain('synthetic-change-key')
    expect(serialized).not.toContain('synthetic-graph-event-id')
    expect(serialized).not.toContain('synthetic-internal-task-id')
  })
})
