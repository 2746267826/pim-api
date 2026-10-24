/*
 * api#495 指标格式化：真实 ReportService.BuildMetrics 形状（14 个扁平 key，
 * number | '本周期无数据'）的解析、中文标签/单位、0 展示、未知 key 兜底。
 */
import { describe, expect, it } from 'vitest'
import { describeUnknownKey, parseReportMetrics, REPORT_METRIC_INVALID, REPORT_NO_DATA } from './report-metrics'

/** 后端 BuildMetrics 全量形状：10 基础 + mobile/pc 各 2 项（0 或无数据混合） */
const backendSample = {
  tasks: 4,
  completedTasks: 1,
  plannedEstimatedMinutes: 120,
  plannedScheduledMinutes: 90,
  actualExecutedMinutes: 30,
  calendarOccupiedMinutes: 0,
  calendarActiveDays: 2,
  tasksCreated: 2,
  tasksCompleted: 1,
  tasksOverdue: 0,
  mobileUsageEvents: 240,
  mobileLocationPoints: '本周期无数据',
  pcEvents: 0,
  pcMissingHours: 1.5,
}

describe('parseReportMetrics', () => {
  it('解析后端全量形状：14 项全部保留、数字带单位、0 不当空', () => {
    const metrics = parseReportMetrics(JSON.stringify(backendSample))!
    expect(metrics).toHaveLength(14)
    const byKey = Object.fromEntries(metrics.map((m) => [m.key, m]))
    expect(byKey.tasks).toMatchObject({ label: '已排期任务', unit: '项', value: 4, display: '4' })
    expect(byKey.plannedEstimatedMinutes).toMatchObject({ label: '任务预估时长', unit: '分钟', value: 120 })
    expect(byKey.pcMissingHours).toMatchObject({ label: 'PC 缺数小时', unit: '小时', value: 1.5, display: '1.5' })
    expect(byKey.calendarOccupiedMinutes).toMatchObject({ value: 0, display: '0' })
    expect(byKey.tasksOverdue).toMatchObject({ value: 0, display: '0' })
    expect(byKey.pcEvents).toMatchObject({ value: 0, display: '0' })
  })

  it('「本周期无数据」按结论展示，不伪装成 0，unit 为空', () => {
    const metrics = parseReportMetrics(JSON.stringify(backendSample))!
    const mobilePoints = metrics.find((m) => m.key === 'mobileLocationPoints')!
    expect(mobilePoints.value).toBeNull()
    expect(mobilePoints.display).toBe(REPORT_NO_DATA)
    expect(mobilePoints.unit).toBeNull()
  })

  it('按计划/任务进展/日历/收集质量分组（页面渲染顺序）', () => {
    const metrics = parseReportMetrics(JSON.stringify(backendSample))!
    const groups = new Set(metrics.map((m) => m.group))
    expect(groups).toEqual(new Set(['plan-vs-actual', 'task-progress', 'calendar', 'collection']))
    expect(metrics.filter((m) => m.group === 'collection').map((m) => m.key)).toEqual([
      'mobileUsageEvents', 'mobileLocationPoints', 'pcEvents', 'pcMissingHours',
    ])
  })

  it('未知 key 走驼峰拆词兜底并按内容归组，未知字符串值按字面展示', () => {
    const metrics = parseReportMetrics(JSON.stringify({ mobileFutureFigure: 7, weirdNote: '服务端说明文字' }))!
    expect(metrics).toHaveLength(2)
    expect(metrics[0]).toMatchObject({ key: 'mobileFutureFigure', label: 'mobile Future Figure', value: 7 })
    expect(metrics[0].group).toBe('collection')
    expect(metrics[1]).toMatchObject({ key: 'weirdNote', value: null, display: '服务端说明文字' })
  })

  it('损坏值形状（null/boolean/object/NaN）显示指标格式异常，不伪装无数据', () => {
    const metrics = parseReportMetrics(
      JSON.stringify({ tasks: null, completedTasks: true, calendarActiveDays: {}, actualExecutedMinutes: 'NaN' }),
    )!
    const byKey = Object.fromEntries(metrics.map((m) => [m.key, m]))
    expect(byKey.tasks.display).toBe(REPORT_METRIC_INVALID)
    expect(byKey.completedTasks.display).toBe(REPORT_METRIC_INVALID)
    expect(byKey.calendarActiveDays.display).toBe(REPORT_METRIC_INVALID)
    expect(byKey.actualExecutedMinutes.display).toBe('NaN')
  })

  it('解析失败/非对象/空对象返回 null，页面据此显示明确失败态', () => {
    expect(parseReportMetrics('not-json{')).toBeNull()
    expect(parseReportMetrics(JSON.stringify([1, 2]))).toBeNull()
    expect(parseReportMetrics(JSON.stringify({}))).toBeNull()
    expect(parseReportMetrics('null')).toBeNull()
  })
})

describe('describeUnknownKey', () => {
  it('分钟结尾的未知 key 附加（分钟）提示', () => {
    expect(describeUnknownKey('focusMinutes')).toBe('focus Minutes（分钟）')
    expect(describeUnknownKey('customCount')).toBe('custom Count')
  })
})
