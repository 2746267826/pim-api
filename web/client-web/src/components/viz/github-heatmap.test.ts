import { describe, expect, it } from 'vitest'
import { levelFor, quantileThresholds, HEAT_RAMP_BLUE } from './github-heatmap'

describe('quantileThresholds（色阶按非零值四分位，与 GitHub 一致）', () => {
  it('按非零值排序取四分位', () => {
    // 非零值 [10, 20, 30, 40] → q25=20, q50=30, q75=40
    expect(quantileThresholds([40, 0, 10, 30, 20])).toEqual([20, 30, 40])
  })

  it('全是 0 或空 → null', () => {
    expect(quantileThresholds([0, 0])).toBeNull()
    expect(quantileThresholds([])).toBeNull()
  })
})

describe('levelFor（0 档 = 无记录；非零按四分位落 1–4 档）', () => {
  const th = quantileThresholds([1000, 5000, 10000, 20000, 30000])!
  // 非零排序 [1000,5000,10000,20000,30000] → q25=5000, q50=10000, q75=20000

  it('0 → 空档', () => {
    expect(levelFor(0, th)).toBe(0)
    expect(levelFor(-1, th)).toBe(0)
  })

  it('小值也能落到 1 档（不再被最大值压成最浅色）', () => {
    expect(levelFor(1000, th)).toBe(1)
    expect(levelFor(5000, th)).toBe(1)
    expect(levelFor(5001, th)).toBe(2)
    expect(levelFor(10000, th)).toBe(2)
    expect(levelFor(10001, th)).toBe(3)
    expect(levelFor(20000, th)).toBe(3)
    expect(levelFor(30000, th)).toBe(4)
  })

  it('阈值不存在（全零）时非零值落 1 档', () => {
    expect(levelFor(5, null)).toBe(1)
  })

  it('色阶为 5 档（含空格）', () => {
    expect(HEAT_RAMP_BLUE).toHaveLength(5)
  })

  it('极端分布下各档都有样本（四分位的意义）', () => {
    // 9 个非零值均匀分布 → 每档约 2-3 个
    const values = [100, 200, 300, 1000, 2000, 3000, 10000, 20000, 30000]
    const th = quantileThresholds(values)!
    const levels = values.map((v) => levelFor(v, th))
    expect(new Set(levels).size).toBeGreaterThanOrEqual(3)
  })
})
