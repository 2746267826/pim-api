import { describe, expect, it } from 'vitest'
import type { NoteAsrRouteConfig } from './api'
import { toRouteConfigSave } from './route-config-adapter'
import type { RouteConfigSaveRequest } from './note-asr-routing'

const config: NoteAsrRouteConfig = {
  revision: '00000000-0000-4000-8000-00000000c001',
  defaultRoute: 'cloud-multimodal',
  localModelId: 'local-model-1',
  localModelRevision: 'local-rev-1',
  cloudConfigured: true,
  cloudModelId: '00000000-0000-4000-8000-00000000b001',
  consentVersion: 'consent-v1',
  updatedAt: '2026-10-31T10:00:00Z',
}
const cloud = { id: config.cloudModelId!, presetId: 'mimo-multimodal' }
const local = { id: 'local-model-1', revision: 'local-rev-1' }

const intent = (overrides: Partial<RouteConfigSaveRequest>): RouteConfigSaveRequest => ({
  accountScope: 'user-1',
  expectedRevision: config.revision,
  defaultRouteId: 'local',
  consentVersion: config.consentVersion,
  confirmed: true,
  ...overrides,
})

describe('toRouteConfigSave', () => {
  it('默认本机且未触碰云端时完整保留双配置并回到local', () => {
    expect(toRouteConfigSave(intent({}), config, cloud, local)).toEqual({
      expectedRevision: config.revision,
      defaultRoute: 'local',
      localModelId: 'local-model-1',
      localModelRevision: 'local-rev-1',
      cloudModelId: config.cloudModelId,
      consentVersion: config.consentVersion,
    })
  })

  it('不放宽云端default保留cloud-multimodal，不放宽local时保留local', () => {
    expect(toRouteConfigSave(intent({ defaultRouteId: 'cloud' }), config, cloud, local).defaultRoute).toBe('cloud-multimodal')
    expect(toRouteConfigSave(intent({}), { ...config, defaultRoute: 'local' }, cloud, local).defaultRoute).toBe('local')
    expect(toRouteConfigSave(intent({ defaultRouteId: 'cloud' }), { ...config, defaultRoute: 'cloud-asr' }, cloud, local).defaultRoute)
      .toBe('cloud-asr')
  })

  it('未变化配置文件未知预设时不凭空改成cloud-asr', () => {
    expect(toRouteConfigSave(intent({ defaultRouteId: 'cloud' }), config, { id: config.cloudModelId! }, local).defaultRoute)
      .toBe('cloud-multimodal')
  })

  it('首次选择multimodal profile时默认路由必须是cloud-multimodal', () => {
    const empty = { ...config, defaultRoute: 'local', cloudModelId: null }
    const first = { id: 'cloud-new', presetId: 'mimo-multimodal' }
    expect(toRouteConfigSave(intent({ defaultRouteId: 'cloud', cloudModelId: 'cloud-new' }), empty, first, local))
      .toMatchObject({ defaultRoute: 'cloud-multimodal', localModelId: 'local-model-1', cloudModelId: 'cloud-new' })
  })

  it('普通ASR profile使用cloud-asr，绝不发送cloud', () => {
    const plain = { id: 'cloud-new', presetId: 'mimo-asr' }
    expect(toRouteConfigSave(intent({ defaultRouteId: 'cloud', cloudModelId: 'cloud-new' }), config, plain, local).defaultRoute)
      .toBe('cloud-asr')
  })

  it('显式null只清空目标字段，其他字段仍回填为完整值', () => {
    expect(toRouteConfigSave(intent({ cloudModelId: null }), config, cloud, local)).toMatchObject({
      localModelId: 'local-model-1', localModelRevision: 'local-rev-1', cloudModelId: null,
    })
    expect(toRouteConfigSave(intent({ defaultRouteId: 'cloud', localModelId: null, localModelRevision: null }), config, cloud, local))
      .toMatchObject({
        defaultRoute: 'cloud-multimodal', localModelId: null, localModelRevision: null, cloudModelId: config.cloudModelId,
      })
  })
})
