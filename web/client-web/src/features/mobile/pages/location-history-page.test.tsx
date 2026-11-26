// @vitest-environment jsdom
import type { ReactNode } from 'react'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { ApiError } from '@/api/client'

const actions = vi.hoisted(() => ({
  correct: { mutateAsync: vi.fn() },
  restore: { mutateAsync: vi.fn() },
  exclude: { mutateAsync: vi.fn() },
  remove: { mutateAsync: vi.fn() },
  fill: { mutateAsync: vi.fn() },
  merge: { mutateAsync: vi.fn() },
  createPlace: { mutateAsync: vi.fn() },
  updatePlace: { mutateAsync: vi.fn() },
}))

const state = vi.hoisted(() => ({
  points: {
    data: {
      items: [
        {
          id: '00000000-0000-0000-0000-000000000001',
          deviceId: 'pixel-8',
          recordedAtUtc: '2026-11-25T10:00:00Z',
          latitude: 31.230416,
          longitude: 121.473701,
          originalLatitude: null,
          originalLongitude: null,
          horizontalAccuracyMeters: 12,
          provider: 'gps',
          sourceKind: 'auto',
          quality: 'usable',
          isExcluded: false,
          isCorrected: false,
          revision: 4,
          excludedAtUtc: null,
          updatedAtUtc: null,
          deletedAtUtc: null,
        },
      ],
      nextCursor: null,
      hasMore: false,
    },
    isLoading: false,
    isFetching: false,
    refetch: vi.fn(),
  },
  tracks: {
    data: [
      {
        id: 'track-1',
        deviceId: 'pixel-8',
        startUtc: '2026-11-25T10:00:00Z',
        endUtc: '2026-11-25T10:10:00Z',
        distanceMeters: 560,
        durationSeconds: 600,
        pointCount: 2,
        segmentCount: 1,
        segments: [
          {
            id: 'segment-1',
            kind: 'move',
            startUtc: '2026-11-25T10:00:00Z',
            endUtc: '2026-11-25T10:10:00Z',
            localStart: '2026-11-25 18:00',
            localEnd: '2026-11-25 18:10',
            durationSeconds: 600,
            distanceMeters: 560,
            pointCount: 2,
            path: [
              {
                id: '00000000-0000-0000-0000-000000000001',
                recordedAtUtc: '2026-11-25T10:00:00Z',
                latitude: 31.230416,
                longitude: 121.473701,
                horizontalAccuracyMeters: 12,
              },
              {
                id: '00000000-0000-0000-0000-000000000002',
                recordedAtUtc: '2026-11-25T10:10:00Z',
                latitude: 31.235,
                longitude: 121.48,
                horizontalAccuracyMeters: 12,
              },
            ],
          },
        ],
      },
    ],
  },
  places: {
    data: {
      home: null,
      places: [
        {
          centerLatitude: 31.230416,
          centerLongitude: 121.473701,
          radiusMeters: 80,
          pointCount: 12,
          visitDayCount: 2,
          isHome: false,
          savedPlaceId: null,
          name: null,
          type: null,
          savedPlaceRevision: null,
        },
      ],
    },
  },
}))

vi.mock('../queries', () => ({
  useFrequentPlaces: () => state.places,
  useLocationOverview: () => ({
    data: {
      range: {
        rangeStartUtc: '2026-11-25T00:00:00Z',
        rangeEndUtc: '2026-11-26T00:00:00Z',
        timezone: 'Asia/Shanghai',
        localStartDate: '2026-11-25',
        localEndDate: '2026-11-25',
      },
      generatedAt: '2026-11-25T12:00:00Z',
      pointCount: 2,
      usablePointCount: 2,
      rejectedPointCount: 0,
      distanceMeters: 560,
      stayCount: 0,
      longestStaySeconds: 0,
      averageAccuracyMeters: 12,
    },
    isLoading: false,
  }),
  useLocationPointActions: () => actions,
  useManagedLocationPoints: (_params: Record<string, unknown>, enabled = true) => ({
    ...state.points,
    data: enabled ? state.points.data : { items: [], nextCursor: null, hasMore: false },
  }),
  useMovementStats: () => ({
    data: {
      homeCenter: null,
      outingCount: 0,
      outingSeconds: 0,
      distanceMeters: 560,
      maxSpeedMetersPerSecond: 1.2,
    },
  }),
  useTracks: () => state.tracks,
}))

vi.mock('@/lib/notify', () => ({
  notifyError: vi.fn(),
  notifySuccess: vi.fn(),
}))

vi.mock('react-leaflet', () => ({
  MapContainer: ({ children }: { children: ReactNode }) => <div data-testid="track-map">{children}</div>,
  Polyline: () => null,
  TileLayer: () => null,
  CircleMarker: () => null,
  useMap: () => ({ setView: vi.fn() }),
}))

import { LocationHistoryPage } from './location-history-page'

let client: QueryClient

function renderPage() {
  client = new QueryClient({
    defaultOptions: { queries: { retry: false, gcTime: 0 } },
  })
  return render(
    <QueryClientProvider client={client}>
      <LocationHistoryPage />
    </QueryClientProvider>,
  )
}

async function openPoint(): Promise<void> {
  const user = userEvent.setup()
  await user.click(await screen.findByRole('button', { name: '查看' }))
  await screen.findByRole('dialog')
}

beforeEach(() => {
  vi.clearAllMocks()
  state.points.refetch.mockReset().mockResolvedValue(undefined)
  actions.correct.mutateAsync.mockResolvedValue({})
  actions.restore.mutateAsync.mockResolvedValue({})
  actions.exclude.mutateAsync.mockResolvedValue({ updatedCount: 1 })
  actions.remove.mutateAsync.mockResolvedValue({})
  actions.fill.mutateAsync.mockResolvedValue({})
  actions.merge.mutateAsync.mockResolvedValue({})
  actions.createPlace.mutateAsync.mockResolvedValue({})
  actions.updatePlace.mutateAsync.mockResolvedValue({})
})

afterEach(() => {
  cleanup()
  client?.clear()
})

describe('历史位置手动纠正（真实页面，API 边界为隔离 fixture）', () => {
  it('修改经纬度后按当前 revision 保存并重新查询', async () => {
    const user = userEvent.setup()
    renderPage()
    await openPoint()

    const latitude = screen.getByLabelText('纬度')
    const longitude = screen.getByLabelText('经度')
    await user.clear(latitude)
    await user.type(latitude, '31.2401234')
    await user.clear(longitude)
    await user.type(longitude, '121.4805678')
    await user.click(screen.getByRole('button', { name: '保存修改' }))

    await waitFor(() => expect(actions.correct.mutateAsync).toHaveBeenCalledWith({
      pointId: '00000000-0000-0000-0000-000000000001',
      body: {
        latitude: 31.2401234,
        longitude: 121.4805678,
        revision: 4,
        excluded: false,
        reason: null,
      },
    }))
  })

  it('非法经纬度在前端阻止请求并显示明确错误', async () => {
    const user = userEvent.setup()
    renderPage()
    await openPoint()

    const latitude = screen.getByLabelText('纬度')
    await user.clear(latitude)
    await user.type(latitude, '91')
    await user.click(screen.getByRole('button', { name: '保存修改' }))

    expect(await screen.findByText('纬度必须在 -90 到 90 之间。')).toBeTruthy()
    expect(actions.correct.mutateAsync).not.toHaveBeenCalled()
  })

  it('409 revision 冲突显示刷新提示并重新读取当前页', async () => {
    actions.correct.mutateAsync
      .mockRejectedValueOnce(
        new ApiError('这个定位点已被其他修改更新，请刷新后重新选择。', 6204, 409),
      )
      .mockResolvedValueOnce({})
    state.points.refetch.mockResolvedValue({
      data: {
        ...state.points.data,
        items: [{
          ...state.points.data.items[0],
          revision: 5,
          isExcluded: true,
          excludedAtUtc: '2026-11-25T10:30:00Z',
        }],
      },
    })
    const user = userEvent.setup()
    renderPage()
    await openPoint()

    const latitude = screen.getByLabelText('纬度')
    await user.clear(latitude)
    await user.type(latitude, '31.25')
    await user.click(screen.getByRole('button', { name: '保存修改' }))

    expect(await screen.findByText(
      '这个定位点已被其他修改更新。已刷新到最新修订，可直接重试。',
    )).toBeTruthy()
    await waitFor(() => expect(state.points.refetch).toHaveBeenCalled())
    await waitFor(() => expect((screen.getByRole('checkbox', {
      name: '标记为误报，从轨迹和统计中排除',
    }) as HTMLInputElement).checked).toBe(true))

    await user.click(screen.getByRole('button', { name: '保存修改' }))
    await waitFor(() => expect(actions.correct.mutateAsync).toHaveBeenLastCalledWith({
      pointId: '00000000-0000-0000-0000-000000000001',
      body: {
        latitude: 31.25,
        longitude: 121.473701,
        revision: 5,
        excluded: true,
        reason: null,
      },
    }))
  })

  it('409 后服务端已永久删除该点时关闭编辑，不允许用旧状态再次提交', async () => {
    actions.correct.mutateAsync.mockRejectedValueOnce(
      new ApiError('这个定位点已被其他修改更新，请刷新后重新选择。', 6204, 409),
    )
    state.points.refetch.mockResolvedValue({
      data: {
        ...state.points.data,
        items: [{
          ...state.points.data.items[0],
          revision: 5,
          deletedAtUtc: '2026-11-25T11:00:00Z',
        }],
      },
    })
    const user = userEvent.setup()
    renderPage()
    await openPoint()

    const latitude = screen.getByLabelText('纬度')
    await user.clear(latitude)
    await user.type(latitude, '31.25')
    await user.click(screen.getByRole('button', { name: '保存修改' }))

    await waitFor(() => expect(state.points.refetch).toHaveBeenCalled())
    await waitFor(() => expect(screen.queryByRole('dialog')).toBeNull())
    expect(actions.correct.mutateAsync).toHaveBeenCalledTimes(1)
  })

  it('409 坐标重复冲突显示服务器原因，不误报 revision 冲突', async () => {
    actions.correct.mutateAsync.mockRejectedValue(
      new ApiError('修改后的坐标与同一时刻的另一个定位点重复。', 6205, 409),
    )
    const user = userEvent.setup()
    renderPage()
    await openPoint()

    const latitude = screen.getByLabelText('纬度')
    await user.clear(latitude)
    await user.type(latitude, '31.25')
    await user.click(screen.getByRole('button', { name: '保存修改' }))

    expect(await screen.findByText(
      '修改后的坐标与同一时刻的另一个定位点重复。',
    )).toBeTruthy()
    expect(screen.queryByText(
      '这个定位点已被其他修改更新。已刷新到最新修订，可直接重试。',
    )).toBeNull()
    expect(state.points.refetch).not.toHaveBeenCalled()
  })

  it('网络失败显示可重试的中文错误并保留编辑内容', async () => {
    actions.correct.mutateAsync.mockRejectedValue(new TypeError('Failed to fetch'))
    const user = userEvent.setup()
    renderPage()
    await openPoint()

    const latitude = screen.getByLabelText('纬度')
    await user.clear(latitude)
    await user.type(latitude, '31.25')
    await user.click(screen.getByRole('button', { name: '保存修改' }))

    expect(await screen.findByText('网络连接失败，请检查网络后重试。')).toBeTruthy()
    expect((screen.getByLabelText('纬度') as HTMLInputElement).value).toBe('31.25')
    expect(state.points.refetch).not.toHaveBeenCalled()
  })

  it('全部点被排除时仍可显示被排除点并恢复', async () => {
    const originalTracks = state.tracks.data
    const originalPoints = state.points.data
    state.tracks.data = []
    state.points.data = {
      ...originalPoints,
      items: originalPoints.items.map((point) => ({ ...point, isExcluded: true, revision: 5 })),
    }
    try {
      const user = userEvent.setup()
      renderPage()

      expect(screen.queryByText('已排除')).toBeNull()
      await user.click(await screen.findByRole('button', { name: '显示被排除点' }))
      expect(await screen.findByText('已排除')).toBeTruthy()

      await openPoint()
      const excluded = screen.getByRole('checkbox', {
        name: '标记为误报，从轨迹和统计中排除',
      }) as HTMLInputElement
      expect(excluded.checked).toBe(true)
      await user.click(excluded)
      await user.click(screen.getByRole('button', { name: '保存修改' }))

      await waitFor(() => expect(actions.correct.mutateAsync).toHaveBeenCalledWith({
        pointId: '00000000-0000-0000-0000-000000000001',
        body: {
          latitude: 31.230416,
          longitude: 121.473701,
          revision: 5,
          excluded: false,
          reason: null,
        },
      }))
    } finally {
      state.tracks.data = originalTracks
      state.points.data = originalPoints
    }
  })

  it('批量排除会提交每个点的 revision 并清空选择', async () => {
    const user = userEvent.setup()
    renderPage()
    await user.click(await screen.findByRole('checkbox', { name: '选择当前页全部定位点' }))
    await user.click(screen.getByRole('button', { name: '批量排除' }))

    await waitFor(() => expect(actions.exclude.mutateAsync).toHaveBeenCalledWith({
      items: [{
        pointId: '00000000-0000-0000-0000-000000000001',
        revision: 4,
      }],
      excluded: true,
    }))
    expect(screen.queryByText('已选 1 个')).toBeNull()
  })

  it('永久删除必须经过独立二次确认后才提交', async () => {
    const user = userEvent.setup()
    renderPage()
    await openPoint()

    await user.click(screen.getByRole('button', { name: '永久删除' }))
    expect(await screen.findByText('永久删除定位点？')).toBeTruthy()
    expect(actions.remove.mutateAsync).not.toHaveBeenCalled()

    const dialogs = screen.getAllByRole('dialog')
    await user.click(within(dialogs.at(-1)!).getByRole('button', { name: '永久删除' }))

    await waitFor(() => expect(actions.remove.mutateAsync).toHaveBeenCalledWith({
      pointId: '00000000-0000-0000-0000-000000000001',
      revision: 4,
    }))
  })

  it('常去地点命名通过稳定地点接口保存', async () => {
    const user = userEvent.setup()
    renderPage()

    await user.click(await screen.findByRole('button', { name: '未命名地点编辑名称' }))
    const name = screen.getByRole('textbox', { name: '地点名称' })
    await user.type(name, '家')
    await user.click(screen.getByRole('button', { name: '保存名称' }))

    await waitFor(() => expect(actions.createPlace.mutateAsync).toHaveBeenCalledWith({
      name: '家',
      type: 'other',
      centerLatitude: 31.230416,
      centerLongitude: 121.473701,
      radiusMeters: 100,
    }))
  })
})
