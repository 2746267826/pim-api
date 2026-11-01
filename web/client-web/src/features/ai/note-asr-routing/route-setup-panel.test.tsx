// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { RouteSetupPanel } from './route-setup-panel'
import type { RouteCreateIntent, RouteOptionView } from './types'

const localRoute: RouteOptionView = {
  id: 'local',
  label: '本机执行',
  kind: 'local',
  available: true,
  isDefault: true,
  destinationLabel: '本机模型 qwen-asr · 已就绪',
}

const cloudRoute: RouteOptionView = {
  id: 'cloud',
  label: '云端多模态',
  kind: 'cloud',
  available: true,
  destinationLabel: 'MiMo Flash · https://cloud.synthetic.invalid/v1/chat/completions',
  costNotice: '音频会离开本机，任务可能长时间等待并按成功请求计费。',
}

function RouteHarness({ routes, cloudKeyReady = true, onCreate }: {
  routes: RouteOptionView[]
  cloudKeyReady?: boolean
  onCreate: (intent: RouteCreateIntent) => void | Promise<void>
}) {
  const [selected, setSelected] = useState(routes[0]?.id ?? '')
  return <RouteSetupPanel routes={routes} selectedRouteId={selected} onSelectRoute={setSelected}
    cloudKeyReady={cloudKeyReady} onCreate={onCreate} />
}

afterEach(cleanup)

describe('RouteSetupPanel', () => {
  it('本机执行不要求云密钥，未确认时不产生任何创建副作用', async () => {
    const create = vi.fn()
    render(<RouteHarness routes={[localRoute, cloudRoute]} cloudKeyReady={false} onCreate={create} />)

    expect(screen.queryByText('云端密钥未配置，不能创建云端任务。')).toBeNull()
    expect(screen.getByText(/本机转写不要求云端 API Key/)).toBeTruthy()
    const button = screen.getByRole('button', { name: '创建本机任务' }) as HTMLButtonElement
    expect(button.disabled).toBe(true)
    expect(create).not.toHaveBeenCalled()

    await userEvent.click(screen.getByRole('checkbox', { name: '确认使用本机执行本次转写' }))
    await userEvent.click(button)
    expect(create).toHaveBeenCalledOnce()
    expect(create.mock.calls[0]?.[0]).toEqual({
      routeId: 'local',
      mode: 'start',
      audioLeavesDevice: false,
      possibleCharge: false,
      confirmed: true,
    })
  })

  it('云端必须显示出域/费用确认，缺密钥时按钮禁用且未确认0调用', async () => {
    const create = vi.fn()
    render(<RouteHarness routes={[cloudRoute, localRoute]} cloudKeyReady={false} onCreate={create} />)

    expect(screen.getByText('云端音频出域与费用')).toBeTruthy()
    expect(screen.getByText(/音频会离开本机/)).toBeTruthy()
    expect(screen.getByText('云端密钥未配置，不能创建云端任务。')).toBeTruthy()
    const checkbox = screen.getByRole('checkbox', { name: /确认本次音频发送至云端/ }) as HTMLButtonElement
    expect(checkbox.disabled).toBe(true)
    expect((screen.getByRole('button', { name: '确认并创建云端任务' }) as HTMLButtonElement).disabled).toBe(true)
    expect(create).not.toHaveBeenCalled()
  })

  it('本机不可用时只保存排队任务，不自动切换云端或产生费用', async () => {
    const create = vi.fn()
    const unavailable = { ...localRoute, available: false, availabilityLabel: '本机模型未启动' }
    render(<RouteHarness routes={[unavailable, cloudRoute]} onCreate={create} />)

    expect(screen.getByText('本机当前不可用')).toBeTruthy()
    expect(screen.getByText(/不会自动切换云端/)).toBeTruthy()
    await userEvent.click(screen.getByRole('checkbox', { name: /确认保存为排队任务/ }))
    await userEvent.click(screen.getByRole('button', { name: '保存排队任务' }))
    expect(create).toHaveBeenCalledWith({
      routeId: 'local',
      mode: 'queue',
      audioLeavesDevice: false,
      possibleCharge: false,
      confirmed: true,
    })
  })

  it('创建失败显示错误但不声称自动重试', async () => {
    const create = vi.fn().mockRejectedValue(new Error('连接中断'))
    render(<RouteHarness routes={[localRoute]} onCreate={create} />)
    await userEvent.click(screen.getByRole('checkbox', { name: '确认使用本机执行本次转写' }))
    await userEvent.click(screen.getByRole('button', { name: '创建本机任务' }))
    expect(await screen.findByText('提交未确认')).toBeTruthy()
    expect(screen.getByText(/原有任务与结果仍保留/)).toBeTruthy()
    expect(create).toHaveBeenCalledOnce()
  })
})
