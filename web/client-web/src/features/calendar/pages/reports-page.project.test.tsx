// @vitest-environment jsdom
/*
 * api#462 项目报告旅程（api#495 指标呈现一并覆盖）：
 * 真实 ReportsPage + QueryClientProvider，只替换 API 边界（./api 与业务 hooks 的
 * 网络端点），断言请求体带 projectId、错误/空态可读、切换不串报告。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'

const { generateMock, listMock, projectsListMock, notifyErrorMock, notifySuccessMock } = vi.hoisted(() => ({
  generateMock: vi.fn(),
  listMock: vi.fn(),
  projectsListMock: vi.fn(),
  notifyErrorMock: vi.fn(),
  notifySuccessMock: vi.fn(),
}))

vi.mock('../api', async (importOriginal) => ({
  ...(await importOriginal<typeof import('../api')>()),
  reportsApi: {
    list: listMock,
    generate: generateMock,
    requestSuggestionAction: vi.fn(),
  },
  domainProjectsApi: {
    list: projectsListMock,
  },
}))

vi.mock('@/lib/businessDay', async (importOriginal) => ({
  ...(await importOriginal<typeof import('@/lib/businessDay')>()),
  todayBusinessDay: () => '2026-10-24',
}))

vi.mock('@/lib/notify', () => ({
  notifyError: notifyErrorMock,
  notifySuccess: notifySuccessMock,
}))

// ECharts 的 canvas 在 jsdom 里不可用；图表 option 的真实值由组件输入断言，渲染归浏览器 QA
vi.mock('@/components/viz/echarts-box', () => ({
  EChartsBox: () => <div data-testid="echarts-box" />,
}))

import { ReportsPage } from './reports-page'
import type { DomainProject, ReportArtifact } from '../types'

const PROJECT_A: DomainProject = { id: 'a1a1a1a1-a1a1-4a1a-8a1a-a1a1a1a1a1a1', name: 'PIM 重构', status: 'Active', description: null }
const PROJECT_B: DomainProject = { id: 'b2b2b2b2-b2b2-4b2b-8b2b-b2b2b2b2b2b2', name: '毕业论文', status: 'Active', description: null }

let projectsFixture: DomainProject[]
let reportsFixture: ReportArtifact[]

type ReportKind = ReportArtifact['kind']

function report(overrides: Partial<ReportArtifact> = {}): ReportArtifact {
  return {
    id: 'rep-project-1',
    kind: 'Project',
    projectId: PROJECT_A.id,
    riskLevel: 'L0AutomaticArtifact',
    contentMarkdown: '# 项目报告\n\n## 结论\n- 计划：任务预估 1.5 小时，已排入日程 1 小时',
    metricsJson: JSON.stringify({
      tasks: 3, completedTasks: 1, plannedEstimatedMinutes: 90, plannedScheduledMinutes: 60,
      actualExecutedMinutes: 30, calendarOccupiedMinutes: 0, calendarActiveDays: 0,
      tasksCreated: 1, tasksCompleted: 1, tasksOverdue: 0,
      mobileUsageEvents: 10, mobileLocationPoints: '本周期无数据', pcEvents: 0, pcMissingHours: 2,
    }),
    generatedAt: '2026-10-24T10:00:00Z',
    status: 'Active',
    ...overrides,
  }
}

beforeEach(() => {
  Element.prototype.hasPointerCapture = () => false
  Element.prototype.setPointerCapture = () => {}
  Element.prototype.releasePointerCapture = () => {}
  Element.prototype.scrollIntoView = () => {}
  vi.stubGlobal('matchMedia', vi.fn().mockImplementation((query: string) => ({
    matches: false,
    media: query,
    onchange: null,
    addListener: vi.fn(),
    removeListener: vi.fn(),
    addEventListener: vi.fn(),
    removeEventListener: vi.fn(),
    dispatchEvent: vi.fn(),
  })))
  vi.stubGlobal('ResizeObserver', class { observe() {} unobserve() {} disconnect() {} })
  projectsFixture = [PROJECT_A, PROJECT_B]
  reportsFixture = []
  projectsListMock.mockImplementation(async () => projectsFixture.map((p) => ({ ...p })))
  listMock.mockImplementation(async () => reportsFixture.map((r) => ({ ...r })))
  notifyErrorMock.mockClear()
  notifySuccessMock.mockClear()
  generateMock.mockImplementation(async (body: { kind: ReportKind; projectId?: string }) => {
    const created = report({ id: `rep-${Date.now()}`, kind: body.kind, projectId: body.projectId ?? null })
    reportsFixture = [created, ...reportsFixture]
    return created
  })
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
  vi.clearAllMocks()
})

function renderPage() {
  const client = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  return render(
    <QueryClientProvider client={client}>
      <ReportsPage />
    </QueryClientProvider>,
  )
}

async function openProjectKind() {
  renderPage()
  fireEvent.click(screen.getByRole('radio', { name: '项目报告' }))
  await screen.findByRole('combobox', { name: '选择项目' })
}

async function pickProject(name: string) {
  fireEvent.click(screen.getByRole('combobox', { name: '选择项目' }))
  fireEvent.click(await screen.findByRole('option', { name }))
}

describe('ReportsPage 项目报告（api#462）', () => {
  it('未选项目时按钮禁用；选中后生成请求带 projectId 并打开新报告', async () => {
    await openProjectKind()
    const button = screen.getByRole('button', { name: /生成报告/ })
    expect((button as HTMLButtonElement).disabled).toBe(true)

    await pickProject('毕业论文')
    expect((button as HTMLButtonElement).disabled).toBe(false)
    fireEvent.click(button)

    await waitFor(() => expect(generateMock).toHaveBeenCalledTimes(1))
    expect(generateMock.mock.calls[0]![0]).toEqual({ kind: 'Project', date: '2026-10-24', projectId: PROJECT_B.id })
    await screen.findByText('计划 vs 实际')
    expect(listMock.mock.calls.length).toBeGreaterThanOrEqual(2)
  })

  it('生成失败按后端 message 明确报错，不弹成功', async () => {
    await openProjectKind()
    await pickProject('PIM 重构')
    generateMock.mockRejectedValueOnce(new Error('项目下没有任何任务，无法推导项目周期。'))
    fireEvent.click(screen.getByRole('button', { name: /生成报告/ }))
    await waitFor(() => expect(notifyErrorMock).toHaveBeenCalledWith('项目下没有任何任务，无法推导项目周期。'))
    expect(notifySuccessMock).not.toHaveBeenCalled()
  })

  it('无项目时显示创建引导空态；项目列表加载失败显示明确错误', async () => {
    projectsFixture = []
    renderPage()
    fireEvent.click(screen.getByRole('radio', { name: '项目报告' }))
    await screen.findByText('还没有项目')

    cleanup()
    listMock.mockRejectedValue(new Error('项目列表读取失败'))
    projectsListMock.mockRejectedValue(new Error('项目列表读取失败'))
    renderPage()
    fireEvent.click(screen.getByRole('radio', { name: '项目报告' }))
    await screen.findByText('项目列表加载失败')
    const button = screen.getByRole('button', { name: /生成报告/ })
    expect((button as HTMLButtonElement).disabled).toBe(true)
  })

  it('切换项目后列表只显示该项目的报告，不再串上一个项目的报告', async () => {
    const repA = report({ id: 'rep-a' })
    reportsFixture = [repA, report({ id: 'rep-b', projectId: PROJECT_B.id, generatedAt: '2026-10-23T10:00:00Z' })]
    await openProjectKind()
    await pickProject('PIM 重构')
    await screen.findByText('计划 vs 实际')
    expect(screen.getByRole('button', { name: /PIM 重构/ })).toBeTruthy()
    expect(screen.queryByRole('button', { name: /毕业论文/ })).toBeNull()
  })
})

describe('ReportsPage 关键指标（api#495）', () => {
  it('指标完整分组展示：中文标签、单位、0 与「本周期无数据」均可见', async () => {
    reportsFixture = [report()]
    await openProjectKind()
    await pickProject('PIM 重构')
    await screen.findByText('计划 vs 实际')
    expect(screen.getByText('已排期任务')).toBeTruthy()
    expect(screen.getByText('其中已完成')).toBeTruthy()
    expect(screen.getByText('任务进展')).toBeTruthy()
    expect(screen.getByText('收集质量')).toBeTruthy()
    expect(screen.getByText('本周期无数据')).toBeTruthy()
    expect(screen.getByText('PC 缺数小时')).toBeTruthy()
    expect(screen.queryByText('plannedEstimatedMinutes')).toBeNull()
  })

  it('POST 成功但列表随后加载失败时，实际返回的报告仍能打开（缓存 upsert）', async () => {
    reportsFixture = []
    await openProjectKind()
    await pickProject('PIM 重构')
    fireEvent.click(screen.getByRole('button', { name: /生成报告/ }))
    await waitFor(() => expect(generateMock).toHaveBeenCalledTimes(1))

    // 生成成功后的 invalidate 触发 refetch；让它失败，页面仍显示 setQueryData 写入的真实返回
    listMock.mockRejectedValue(new Error('列表刷新失败'))
    await screen.findByText('计划 vs 实际')
    expect(screen.getByText(/计划 vs 实际/)).toBeTruthy()
    expect(screen.queryByText('报告列表加载失败')).toBeNull()
  })

  it('生成执行中锁定类型与项目选择，避免结果写回旧上下文', async () => {
    await openProjectKind()
    await pickProject('PIM 重构')
    const kindToggle = screen.getByRole('radio', { name: '项目报告' }) as HTMLButtonElement
    expect(kindToggle.disabled).toBe(false)

    // 让生成请求挂起，期间选择被禁用
    let release!: (value: unknown) => void
    generateMock.mockReturnValueOnce(new Promise((r) => { release = r }))
    fireEvent.click(screen.getByRole('button', { name: /生成报告/ }))
    await waitFor(() => expect((screen.getByRole('radio', { name: '项目报告' }) as HTMLButtonElement).disabled).toBe(true))
    expect((screen.getByRole('combobox', { name: '选择项目' }) as HTMLButtonElement).disabled).toBe(true)
    release(undefined)
  })

  it('状态筛选在已归档时生成成功自动切回全部，新报告立即可见', async () => {
    reportsFixture = []
    await openProjectKind()
    await pickProject('PIM 重构')
    fireEvent.click(screen.getByRole('button', { name: '已归档' }))
    fireEvent.click(screen.getByRole('button', { name: /生成报告/ }))
    await waitFor(() => expect(generateMock).toHaveBeenCalledTimes(1))
    await screen.findByText('计划 vs 实际')
    const badges = screen.getAllByText('已发布')
    expect(badges.length).toBeGreaterThan(0)
  })
})
