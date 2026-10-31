// @vitest-environment jsdom
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { RouteConfigEditor } from './route-config-editor'
import type { RouteConfigView } from './types'

const config: RouteConfigView = {
  revision: 'route-revision-1',
  defaultRouteId: 'local',
  localModelId: 'local-model-1',
  localModelRevision: 'local-rev-1',
  cloudConfigured: false,
  cloudModelId: null,
  consentVersion: 'consent-v1',
  updatedAtLabel: '2026-10-31 09:00',
}

const localModels = [{ id: 'local-model-1', label: '本机 qwen-asr', revision: 'local-rev-1', enabled: true }]
const cloudModels = [{ id: 'cloud-model-1', label: 'MiMo Flash', revision: 'cloud-rev-1', enabled: true, cloudConfigured: true }]

afterEach(cleanup)
beforeAll(() => {
  if (!HTMLElement.prototype.hasPointerCapture) {
    HTMLElement.prototype.hasPointerCapture = () => false
  }
  if (!HTMLElement.prototype.releasePointerCapture) {
    HTMLElement.prototype.releasePointerCapture = () => {}
  }
  if (!HTMLElement.prototype.scrollIntoView) {
    HTMLElement.prototype.scrollIntoView = () => {}
  }
})
async function choose(name: string, option: string) {
  const trigger = screen.getByRole('combobox', { name })
  fireEvent.pointerDown(trigger, { button: 0, pointerType: 'mouse' })
  const listbox = await screen.findByRole('listbox')
  fireEvent.click(within(listbox).getByRole('option', { name: option }))
}

describe('RouteConfigEditor', () => {
  it('local 默认路由不要求云端模型或云端密钥即可保存', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    render(<RouteConfigEditor config={config} accountScope="user-1" loading={false} loadError={null} onReload={() => {}}
      localModels={localModels} cloudModels={cloudModels} onSave={save} />)

    expect(screen.getByText(/本机与云端配置彼此独立保存/)).toBeTruthy()
    expect(screen.queryByText('云端尚未配置')).toBeNull()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))

    await waitFor(() => expect(save).toHaveBeenCalledOnce())
    expect(save.mock.calls[0]?.[0]).toEqual({
      accountScope: 'user-1',
      expectedRevision: 'route-revision-1',
      defaultRouteId: 'local',
      localModelId: 'local-model-1',
      localModelRevision: 'local-rev-1',
      consentVersion: 'consent-v1',
      confirmed: true,
    })
  })

  it('首次配置可从可用云端 profile 设置云端并独立保留本机双配置', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    render(<RouteConfigEditor config={{ ...config, cloudConfigured: false, cloudModelId: null }}
      accountScope="user-1" loading={false} loadError={null} onReload={() => {}}
      localModels={localModels} cloudModels={cloudModels} onSave={save} />)

    await choose('本机模型', '本机 qwen-asr · local-rev-1')
    const cloud = screen.getByRole('combobox', { name: '云端模型' })
    expect((cloud as HTMLButtonElement).disabled).toBe(false)
    await choose('云端模型', 'MiMo Flash · cloud-rev-1')
    await choose('默认转写路由', '默认云端执行')
    expect(screen.queryByText('云端尚未配置')).toBeNull()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))

    await waitFor(() => expect(save).toHaveBeenCalledOnce())
    expect(save.mock.calls[0]?.[0]).toMatchObject({
      accountScope: 'user-1', expectedRevision: 'route-revision-1',
      defaultRouteId: 'cloud', cloudModelId: 'cloud-model-1', confirmed: true,
    })
    expect(save.mock.calls[0]?.[0]).not.toHaveProperty('localModelId')
    expect(save.mock.calls[0]?.[0]).not.toHaveProperty('localModelRevision')
  })

  it('默认local保存时保留已配置云端，显式选择才会清空', async () => {
    const save = vi.fn().mockResolvedValue(undefined)
    const dual = { ...config, cloudConfigured: true, cloudModelId: 'cloud-model-1' }
    const { unmount } = render(<RouteConfigEditor config={dual} accountScope="user-1" loading={false}
      loadError={null} onReload={() => {}} localModels={localModels} cloudModels={cloudModels} onSave={save} />)
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    await waitFor(() => expect(save).toHaveBeenCalledOnce())
    expect(save.mock.calls[0]?.[0]).not.toHaveProperty('cloudModelId')
    unmount()
    cleanup()

    save.mockClear()
    render(<RouteConfigEditor config={dual} accountScope="user-1" loading={false} loadError={null}
      onReload={() => {}} localModels={localModels} cloudModels={cloudModels} onSave={save} />)
    await choose('云端模型', '未选择云端模型')
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    await waitFor(() => expect(save).toHaveBeenCalledOnce())
    expect(save.mock.calls[0]?.[0]).toMatchObject({ cloudModelId: null, defaultRouteId: 'local' })
  })

  it('本机 profile 不可用或未选择时默认local不能保存', async () => {
    const save = vi.fn()
    render(<RouteConfigEditor config={{ ...config, localModelId: null }} accountScope="user-1"
      loading={false} loadError={null} onReload={() => {}}
      localModels={localModels} cloudModels={cloudModels} onSave={save} />)
    expect(screen.getByText('本机默认路由缺少可用模型')).toBeTruthy()
    expect((screen.getByRole('button', { name: '保存路由设置' }) as HTMLButtonElement).disabled).toBe(true)
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    expect(save).not.toHaveBeenCalled()
  })

  it('没有可用云端 profile 时不能把默认路由改成云端，也不产生保存调用', async () => {
    const save = vi.fn()
    render(<RouteConfigEditor config={{ ...config, defaultRouteId: 'cloud' }} accountScope="user-1"
      loading={false} loadError={null}
      onReload={() => {}} localModels={localModels} cloudModels={cloudModels} onSave={save} />)
    expect(screen.getByText('云端尚未配置')).toBeTruthy()
    expect((screen.getByRole('button', { name: '保存路由设置' }) as HTMLButtonElement).disabled).toBe(true)
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    expect(save).not.toHaveBeenCalled()
  })

  it('选择云端会让未配置状态立即阻止保存', async () => {
    const save = vi.fn()
    render(<RouteConfigEditor config={config} accountScope="user-1" loading={false} loadError={null}
      onReload={() => {}} localModels={[]} cloudModels={[]} onSave={save} />)
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await choose('默认转写路由', '默认云端执行')

    expect(screen.getByText('云端尚未配置')).toBeTruthy()
    expect((screen.getByRole('button', { name: '保存路由设置' }) as HTMLButtonElement).disabled).toBe(true)
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    expect(save).not.toHaveBeenCalled()
  })

  it('保存失败保留用户选择，不自动重试，也不回滚成服务端值', async () => {
    const save = vi.fn().mockRejectedValue(new Error('配置版本已过期'))
    render(<RouteConfigEditor config={config} accountScope="user-1" loading={false} loadError={null} onReload={() => {}}
      localModels={localModels} cloudModels={cloudModels} onSave={save} />)

    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    await userEvent.click(screen.getByRole('button', { name: '保存路由设置' }))
    expect(await screen.findByText('保存未确认')).toBeTruthy()
    expect(screen.getByText(/配置版本已过期/)).toBeTruthy()
    expect(save).toHaveBeenCalledOnce()
    expect((screen.getByRole('combobox', { name: '本机模型' }) as HTMLButtonElement).textContent).toContain('本机 qwen-asr')
  })

  it('只读与加载失败时不提供保存路径', () => {
    const save = vi.fn()
    const { rerender } = render(<RouteConfigEditor config={config} accountScope="user-1" loading={false} loadError={null}
      onReload={() => {}} localModels={localModels} cloudModels={cloudModels} onSave={save} readOnly />)
    expect((screen.getByRole('button', { name: '保存路由设置' }) as HTMLButtonElement).disabled).toBe(true)
    expect(save).not.toHaveBeenCalled()

    rerender(<RouteConfigEditor config={null} accountScope="user-2" loading={false} loadError="网络中断" onReload={() => {}}
      localModels={localModels} cloudModels={cloudModels} onSave={save} />)
    expect(screen.getByRole('alert')).toBeTruthy()
    expect(screen.queryByRole('button', { name: '保存路由设置' })).toBeNull()
    expect(screen.getByRole('button', { name: '重新读取' })).toBeTruthy()
  })

  it('revision或账号变化撤销确认并阻止旧draft保存', async () => {
    const save = vi.fn()
    const { rerender } = render(<RouteConfigEditor config={config} accountScope="user-1" loading={false}
      loadError={null} onReload={() => {}} localModels={localModels} cloudModels={cloudModels} onSave={save} />)
    await choose('默认转写路由', '默认云端执行')
    expect(screen.getByText('云端尚未配置')).toBeTruthy()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    expect((screen.getByRole('button', { name: '保存路由设置' }) as HTMLButtonElement).disabled).toBe(true)
    expect(save).not.toHaveBeenCalled()

    rerender(<RouteConfigEditor config={{ ...config, revision: 'route-revision-2' }} accountScope="user-1"
      loading={false} loadError={null} onReload={() => {}} localModels={localModels}
      cloudModels={cloudModels} onSave={save} />)
    expect((screen.getByRole('checkbox', { name: '确认保存路由设置' }) as HTMLButtonElement).dataset.state).toBe('unchecked')
    expect(screen.getByText('默认本机执行')).toBeTruthy()
    expect(screen.queryByText('云端尚未配置')).toBeNull()
    await userEvent.click(screen.getByRole('checkbox', { name: '确认保存路由设置' }))
    expect((screen.getByRole('button', { name: '保存路由设置' }) as HTMLButtonElement).disabled).toBe(true)

    rerender(<RouteConfigEditor config={{ ...config, revision: 'route-revision-2' }} accountScope="user-2"
      loading={false} loadError={null} onReload={() => {}} localModels={localModels}
      cloudModels={cloudModels} onSave={save} />)
    expect((screen.getByRole('checkbox', { name: '确认保存路由设置' }) as HTMLButtonElement).dataset.state).toBe('unchecked')
    expect((screen.getByRole('button', { name: '保存路由设置' }) as HTMLButtonElement).disabled).toBe(true)
    expect(save).not.toHaveBeenCalled()
  })
})
