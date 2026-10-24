import { describe, expect, it } from 'vitest'
import { placeTooltip } from './floating-tooltip'

const VP = { width: 1280, height: 720 }

describe('placeTooltip（悬浮框视口收敛）', () => {
  it('锚点在中间：以锚点为中心', () => {
    const p = placeTooltip({ left: 600, top: 100, bottom: 130, width: 20 }, { width: 200, height: 28 }, VP)
    expect(p.left).toBe(510) // 中心 610 - 框宽/2 100
    expect(p.top).toBe(136) // bottom + 6
    expect(p.bottom).toBeNull()
  })

  it('锚点靠右：向左收敛，不出右边界', () => {
    // 中心在 1240，框宽 300 → 会超界，应收敛到 vw - 300 - 8 = 972
    const p = placeTooltip({ left: 1230, top: 100, bottom: 130, width: 20 }, { width: 300, height: 28 }, VP)
    expect(p.left).toBe(972)
    expect(p.left + 300).toBeLessThanOrEqual(VP.width - 8)
  })

  it('锚点靠左：向右收敛，不出左边界', () => {
    const p = placeTooltip({ left: 0, top: 100, bottom: 130, width: 10 }, { width: 300, height: 28 }, VP)
    expect(p.left).toBe(8) // margin
  })

  it('框比视口还宽：优先保证左边界（右边界自然溢出由 max-w 兜住）', () => {
    const p = placeTooltip({ left: 100, top: 100, bottom: 130, width: 10 }, { width: 1400, height: 28 }, VP)
    expect(p.left).toBe(8)
  })

  it('锚点贴近底部：翻到上方', () => {
    const p = placeTooltip({ left: 600, top: 690, bottom: 718, width: 20 }, { width: 200, height: 60 }, VP)
    expect(p.top).toBeNull()
    expect(p.bottom).toBe(720 - 690 + 6)
  })

  it('下方空间充足：留在下方', () => {
    const p = placeTooltip({ left: 600, top: 300, bottom: 330, width: 20 }, { width: 200, height: 60 }, VP)
    expect(p.top).toBe(336)
    expect(p.bottom).toBeNull()
  })

  it('任意锚点位置都不产生负 left', () => {
    for (const left of [0, 5, 200, 640, 1000, 1270, 1280]) {
      const p = placeTooltip({ left, top: 100, bottom: 130, width: 20 }, { width: 240, height: 28 }, VP)
      expect(p.left).toBeGreaterThanOrEqual(0)
    }
  })
})
