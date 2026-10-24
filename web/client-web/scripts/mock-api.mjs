/* P0/P1 视觉走查用 mock API：登录/会话/状态/版本 + 日历域 + 确认中心 + 今日分区 */
import { createServer } from 'node:http'

const json = (res, status, body) => {
  res.writeHead(status, { 'content-type': 'application/json' })
  res.end(JSON.stringify(body))
}
const ok = (res, data) => json(res, 200, { code: 0, message: 'ok', data, timestamp: new Date().toISOString() })
const paged = (res, items) =>
  ok(res, { items, totalCount: items.length, page: 1, pageSize: 100, totalPages: 1 })

const daysAgo = (n) => new Date(Date.now() - n * 86_400_000).toISOString()
const iso = (h, m = 0) => {
  const d = new Date()
  d.setHours(h, m, 0, 0)
  return d.toISOString()
}
const USER = { id: 'u1', username: 'demo', displayName: '演示用户', role: 'admin' }
const TOKENS = () => ({
  accessToken: 'mock-access-token',
  refreshToken: 'mock-refresh-token',
  expiresAt: new Date(Date.now() + 15 * 60_000).toISOString(),
  user: USER,
})

/* ── mock 数据 ─────────────────────────────────────────── */

let CAL_BOOKS = [
  { id: 'cal-1', name: '工作', color: '#3B82F6', kind: 'calendar', isDefault: true, eventCount: 3, source: 'manual', outlookCalendarBindingId: null, canEdit: true },
  { id: 'cal-2', name: '生活', color: '#22C55E', kind: 'calendar', isDefault: false, eventCount: 1, source: 'manual', outlookCalendarBindingId: null, canEdit: true },
]
let TASK_BOOKS = [
  { id: 'tb-1', domainProjectId: null, name: '收集箱', kind: 'task', status: 'Active', taskCount: 2 },
  { id: 'tb-2', domainProjectId: null, name: '本周', kind: 'task', status: 'Active', taskCount: 2 },
]

const at = (h, m = 0) => {
  const d = new Date()
  d.setHours(h, m, 0, 0)
  return d.toISOString()
}
const plusDays = (n) => {
  const d = new Date()
  d.setDate(d.getDate() + n)
  return d.toISOString()
}

let EVENTS = [
  { id: 'e1', calendarId: 'cal-1', uid: 'e1@pim', title: '晨会', description: null, location: '301', dtStart: at(9), dtEnd: at(9, 30), rrule: null, status: 'CONFIRMED', source: 'manual', originalEventId: null, isAllDay: false, timeZoneId: null, sourceTimeZoneId: null, sourceUid: null, recurrenceId: null, descriptionFormat: 'text', showAs: null, importance: 'high', sensitivity: null, categories: null, isReminderOn: null, reminderMinutesBeforeStart: null, organizer: null, attendees: null, isOnlineMeeting: null, onlineMeetingProvider: null, onlineMeetingUrl: null, externalLink: null, isSeriesMaster: false, isException: false, seriesMasterId: null, isCancelled: false, outlookCalendarBindingId: null, outlookEventId: null, outlookEtag: null, outlookEventType: null },
  { id: 'e2', calendarId: 'cal-1', uid: 'e2@pim', title: '方案评审', description: 'P1 里程碑', location: null, dtStart: at(11), dtEnd: at(12), rrule: null, status: 'CONFIRMED', source: 'manual', originalEventId: null, isAllDay: false, timeZoneId: null, sourceTimeZoneId: null, sourceUid: null, recurrenceId: null, descriptionFormat: 'text', showAs: null, importance: null, sensitivity: null, categories: null, isReminderOn: null, reminderMinutesBeforeStart: null, organizer: null, attendees: null, isOnlineMeeting: null, onlineMeetingProvider: null, onlineMeetingUrl: null, externalLink: null, isSeriesMaster: false, isException: false, seriesMasterId: null, isCancelled: false, outlookCalendarBindingId: null, outlookEventId: null, outlookEtag: null, outlookEventType: null },
  { id: 'e3', calendarId: 'cal-2', uid: 'e3@pim', title: '健身', description: null, location: null, dtStart: at(19), dtEnd: at(20), rrule: 'FREQ=DAILY', status: 'CONFIRMED', source: 'manual', originalEventId: null, isAllDay: false, timeZoneId: null, sourceTimeZoneId: null, sourceUid: null, recurrenceId: null, descriptionFormat: 'text', showAs: null, importance: null, sensitivity: null, categories: null, isReminderOn: null, reminderMinutesBeforeStart: null, organizer: null, attendees: null, isOnlineMeeting: null, onlineMeetingProvider: null, onlineMeetingUrl: null, externalLink: null, isSeriesMaster: false, isException: false, seriesMasterId: null, isCancelled: false, outlookCalendarBindingId: null, outlookEventId: null, outlookEtag: null, outlookEventType: null },
]

let TASKS = [
  { id: 't1', calendarId: null, uid: 't1@pim', title: '写 P1 周报', description: null, priority: 9, estimatedDuration: '00:30:00', minimumSegment: null, dtStart: null, due: at(18), status: 'NEEDS-ACTION', isInbox: true, sortOrder: 1, subTasks: [], plannedEnd: null, taskBookId: 'tb-1', percentComplete: 0 },
  { id: 't2', calendarId: null, uid: 't2@pim', title: '回复客户邮件', description: null, priority: 5, estimatedDuration: '00:15:00', minimumSegment: null, dtStart: null, due: plusDays(1), status: 'NEEDS-ACTION', isInbox: true, sortOrder: 2, subTasks: [], plannedEnd: null, taskBookId: 'tb-1', percentComplete: 0 },
  { id: 't3', calendarId: null, uid: 't3@pim', title: '深度工作块：日历组件', description: null, priority: 5, estimatedDuration: '02:00:00', minimumSegment: null, dtStart: at(14), due: null, status: 'NEEDS-ACTION', isInbox: false, sortOrder: 3, subTasks: [], plannedEnd: at(16), taskBookId: 'tb-2', percentComplete: 0 },
  { id: 't4', calendarId: null, uid: 't4@pim', title: '整理收藏夹', description: null, priority: 1, estimatedDuration: null, minimumSegment: null, dtStart: null, due: null, status: 'COMPLETED', isInbox: false, sortOrder: 4, subTasks: [], plannedEnd: null, taskBookId: 'tb-2', percentComplete: 100 },
]

const LAYERS = {
  start: at(0),
  end: plusDays(1),
  // 可变：排期（tasks/{id}/plan）会向此数组追加 task-segments 条目
  items: [
    { id: 'task-segment:t3', layer: 'task-segments', objectType: 'task-segment', objectId: 'seg-1', title: '深度工作块：日历组件', startsAt: at(14), endsAt: at(16), source: 'manual', status: 'planned', color: '#22C55E', requiresConfirmation: false },
    { id: 'habit:e3', layer: 'habits', objectType: 'habit-occurrence', objectId: 'h1', title: '健身', startsAt: at(19), endsAt: at(20), source: 'manual', status: 'Planned', color: '#A855F7', requiresConfirmation: false },
    { id: 'availability:a1', layer: 'availability', objectType: 'availability-window', objectId: 'a1', title: '空闲', startsAt: at(13), endsAt: at(14), source: 'manual', status: 'available', color: '#0EA5E9', requiresConfirmation: false },
  ],
}

/** 运行时可变排期结果（plan/segments 写入后，layers 查询立即可见） */
const PLANNED_SEGMENTS = []

const CONFIRMATION = {
  id: 'c1'.padEnd(36, '0'),
  requestedByUserId: null,
  operationType: 'calendar.ai_placeholder.confirm',
  summary: '将「深度工作块：任务编辑弹窗」排入明天 09:00–11:00',
  riskLevel: 12,
  source: 'workbench',
  payloadJson: '{"horizonDays":7}',
  previewJson: '{"slots":1}',
  status: 0,
  expiresAt: plusDays(0.5),
  createdAt: at(8),
  confirmedAt: null,
  executedAt: null,
  resultJson: null,
  correlationId: null,
  changedFields: ['plannedStart', 'plannedEnd'],
  allowedActions: ['confirm', 'reject'],
  objectType: 'ai-planning-placeholder',
  objectId: 'ap-1',
  requiresSecondLevelConfirmation: false,
  beforeJson: '{"plannedStart":null}',
  afterJson: `{"plannedStart":"${plusDays(1)}"}`,
  requiresStrictConfirmation: false,
  auditBatchId: null,
  aiRecommendation: '根据最近 7 天的专注时段，上午 9-11 点是你的高效区间。',
  externalEffect: null,
  recoveryPath: '可在数据中心将任务恢复到变更前版本。',
}

const todaySection = (id, kind, status, data) => ({ id, kind, status, generatedAt: new Date().toISOString(), data, links: [{ rel: 'self', href: `/api/v1/today/sections/${id}` }], error: null })

/* ── server ─────────────────────────────────────────────── */

let seq = 100

/*
 * 监听地址：MOCK_PORT / MOCK_HOST（缺省 = 由内核分配空闲端口，只绑 127.0.0.1）。
 *
 * 不要把 5858 当默认值：本机 5858 上可能已经有真实的 PIM API 在跑，
 * 静默把 mock 绑上去既会直接 EADDRINUSE，也会让「本地起了一个 API」的来源变得不可分辨。
 * 需要固定端口时显式 `MOCK_PORT=xxxx node scripts/mock-api.mjs`。
 */
const MOCK_PORT = Number(process.env.MOCK_PORT ?? 0)
const MOCK_HOST = process.env.MOCK_HOST ?? '127.0.0.1'
if (!Number.isInteger(MOCK_PORT) || MOCK_PORT < 0 || MOCK_PORT > 65535) {
  console.error(`MOCK_PORT 不是合法端口：${process.env.MOCK_PORT}`)
  process.exit(2)
}

const server = createServer((req, res) => {
  const url = new URL(req.url ?? '/', 'http://localhost')
  const path = url.pathname

  if (path === '/api/version') {
    json(res, 200, { version: '0.9.9-mock', capabilities: ['androidEmbedV1'], latestVersion: null, checkedAt: new Date().toISOString(), error: null })
    return
  }

  const readBody = (cb) => {
    let body = ''
    req.on('data', (c) => (body += c))
    req.on('end', () => cb(body ? JSON.parse(body) : {}))
  }

  if (path === '/api/v1/auth/login' || path === '/api/v1/auth/register') { readBody(() => ok(res, TOKENS())); return }
  if (path === '/api/v1/auth/refresh') { ok(res, TOKENS()); return }
  if (path === '/api/v1/auth/me') { ok(res, USER); return }
  if (path === '/api/v1/status/summary') { ok(res, { status: 1, label: '正常', message: '全部组件健康（mock）', checkedAt: new Date().toISOString() }); return }

  /* 日历域 */
  if (path === '/api/v1/calendar/calendars' && req.method === 'GET') { ok(res, CAL_BOOKS); return }
  if (path === '/api/v1/calendar/task-books' && req.method === 'GET') { ok(res, TASK_BOOKS); return }
  if (path === '/api/v1/calendar/events' && req.method === 'GET') {
    // 双态行为（规格 calendar.md:142-144）：search/calendarId/page/pageSize 任一出现 → PagedResult；否则旧版全量数组
    const hasPaging = ['page', 'pageSize', 'search', 'calendarId'].some((k) => url.searchParams.has(k))
    const search = url.searchParams.get('search')
    const calId = url.searchParams.get('calendarId')
    let list = EVENTS
    if (search) list = list.filter((e) => e.title.includes(search))
    if (calId) list = list.filter((e) => e.calendarId === calId)
    if (hasPaging) paged(res, list)
    else ok(res, list)
    return
  }
  if (path === '/api/v1/calendar/layers' && req.method === 'GET') {
    // 合并运行时数据：events 图层取当前 EVENTS（含新建日程），task-segments 取运行时排期结果
    const eventLayerItems = EVENTS.map((e) => ({
      id: 'event:' + e.id, layer: 'events', objectType: 'event', objectId: e.id,
      title: e.title, startsAt: e.dtStart, endsAt: e.dtEnd, source: e.source,
      status: e.status, color: (CAL_BOOKS.find((c) => c.id === e.calendarId) ?? {}).color ?? '#3B82F6',
      requiresConfirmation: false,
    }))
    const staticNonEvent = LAYERS.items.filter((i) => i.layer !== 'events')
    ok(res, { start: LAYERS.start, end: LAYERS.end, items: [...eventLayerItems, ...staticNonEvent, ...PLANNED_SEGMENTS] })
    return
  }
  if (path === '/api/v1/calendar/tasks' && req.method === 'GET') {
    // 应用运行时排期：把已排期任务从收件箱移除并带上计划时间
    for (const seg of PLANNED_SEGMENTS) {
      const task = TASKS.find((t) => t.id === seg.taskId)
      if (task) {
        task.isInbox = false
        task.dtStart = seg.startsAt
        task.plannedEnd = seg.endsAt
      }
    }
    if (url.searchParams.has('page') || url.searchParams.has('search') || url.searchParams.has('status') || url.searchParams.has('priority') || url.searchParams.has('inbox')) {
      let list = TASKS
      if (url.searchParams.get('inbox') === 'true') list = list.filter((t) => t.isInbox)
      if (url.searchParams.get('status') === 'COMPLETED') list = list.filter((t) => t.status === 'COMPLETED')
      if (url.searchParams.get('priority')) list = list.filter((t) => t.priority === Number(url.searchParams.get('priority')))
      paged(res, list)
    } else {
      ok(res, TASKS)
    }
    return
  }
  if (path.startsWith('/api/v1/calendar/tasks/') && path.endsWith('/segments') && req.method === 'GET') {
    const taskId = path.split('/')[5]
    ok(res, [{ id: 'seg-1', taskId, taskTitle: '深度工作块：日历组件', startsAt: at(14), endsAt: at(16), status: 'planned', source: 'manual', planningReason: null, confirmationId: null }])
    return
  }
  if ((path === '/api/v1/calendar/calendars/cal-1/delete-preview' || /delete-preview$/.test(path)) && req.method === 'POST') {
    ok(res, { targetType: 'calendar-book', targetId: 'cal-1', title: '工作', operationKind: 'calendar.delete', affectedCount: 4, samples: EVENTS.slice(0, 5).map((e) => ({ id: e.id, type: 'event', title: e.title, start: e.dtStart, end: e.dtEnd, bookName: '工作' })), summary: '删除「工作」及其 4 个活跃日程。', requiresStrictConfirmation: true })
    return
  }
  /* 确认中心 */
  if (path === '/api/v1/operations/confirmations/pending') { ok(res, [CONFIRMATION]); return }
  if (/^\/api\/v1\/operations\/confirmations\/[^/]+$/.test(path) && req.method === 'GET') {
    ok(res, { ...CONFIRMATION, status: url.pathname === path ? 0 : 1 })
    return
  }
  if (/^\/api\/v1\/operations\/confirmations\/[^/]+\/(confirm|confirm-second-level|confirm-strict|reject)$/.test(path)) {
    ok(res, { ...CONFIRMATION, status: path.endsWith('reject') ? 2 : 1 })
    return
  }

  /* 今日分区 */
  if (path === '/api/v1/today/sections') {
    ok(res, {
      date: url.searchParams.get('date') ?? new Date().toISOString().slice(0, 10),
      pcBusinessDate: new Date().toISOString().slice(0, 10),
      generatedAt: new Date().toISOString(),
      sections: [
        { id: 'calendar.schedule', kind: 'calendar.schedule', status: 'normal', links: [] },
        { id: 'calendar.tasks', kind: 'calendar.tasks', status: 'normal', links: [] },
        { id: 'pc.activity', kind: 'pc.activity', status: 'normal', links: [] },
        { id: 'pc.quality', kind: 'pc.quality', status: 'warning', links: [] },
        { id: 'operations.confirmations', kind: 'operations.confirmations', status: 'warning', links: [] },
        { id: 'operations.outlook-sync', kind: 'operations.outlook-sync', status: 'empty', links: [] },
        { id: 'operations.reminders', kind: 'operations.reminders', status: 'normal', links: [] },
        { id: 'operations.endpoints', kind: 'operations.endpoints', status: 'normal', links: [] },
      ],
    })
    return
  }
  if (path.startsWith('/api/v1/today/sections/')) {
    const id = decodeURIComponent(path.split('/')[5] ?? '')
    const byId = {
      'calendar.schedule': [ { id: 'e1', objectId: 'e1', title: '晨会', startsAt: at(9), endsAt: at(9, 30) }, { id: 'e2', objectId: 'e2', title: '方案评审', startsAt: at(11), endsAt: at(12) } ],
      'calendar.tasks': [ { id: 't3', objectId: 't3', title: '深度工作块：日历组件', startsAt: at(14), endsAt: at(16) }, { id: 't1', objectId: 't1', title: '写 P1 周报', startsAt: null, endsAt: null } ],
      'pc.activity': { items: [ { id: 'k1', title: '按键总数', value: '12,480' }, { id: 'k2', title: '活跃时长', value: '4.2 小时' } ] },
      'pc.quality': { items: [ { id: 'q1', title: '2 条记录缺少分类', message: '建议在标注队列中处理' } ] },
      'operations.confirmations': { items: [ { id: 'c1', title: '1 条待确认：排程建议', message: '来自工作台' } ] },
      'operations.outlook-sync': { items: [] },
      'operations.reminders': { items: [ { id: 'r1', title: '下午 3 点 · 提醒回复邮件', startsAt: at(15) } ] },
      'operations.endpoints': { items: [ { id: 'ep1', title: 'Windows 工作站 · 在线', message: '心跳 2 分钟前' }, { id: 'ep2', title: 'Pixel 手机 · 在线', message: '心跳 5 分钟前' } ] },
    }
    ok(res, todaySection(id, id, 'normal', byId[id] ?? { items: [] }))
    return
  }

  /* PC 域（工作台概览瓦片） */
  if (path === '/api/v1/pc/summary') {
    ok(res, {
      keystats: { keyPresses: 12480 },
      metrics: { activeInputDuration: '04:12:00', totalRecordedDuration: '08:30:00', switchFrequency: 3.2, appSwitchCount: 86 },
      categories: [
        { categoryName: '开发', share: 0.42, color: '#2563EB' },
        { categoryName: '学习', share: 0.25, color: '#8B5CF6' },
        { categoryName: '其他', share: 0.33, color: '#64748B' },
      ],
    })
    return
  }

  /* AI 排程建议 + Outlook 状态（工作台） */
  if (path === '/api/v1/calendar/ai-placeholders') {
    ok(res, url.searchParams.get('status') === 'Dismissed' ? [] : [
      { id: 'ap-1', title: '深度工作块：手机记录页', startsAt: at(10), endsAt: at(12), reason: '按最近 7 天专注时段推荐上午时段', status: 'Suggested', source: 'ai', confirmationId: null },
      { id: 'ap-2', title: '整理收件箱任务', startsAt: at(16), endsAt: at(17), reason: '收件箱积压 2 个任务', status: 'Suggested', source: 'rule-engine', confirmationId: null },
    ])
    return
  }
  if (/ai-placeholders\/[^/]+\/(confirm|dismiss)$/.test(path)) { readBody(() => ok(res, null)); return }
  if (path === '/api/v1/calendar/ai-placeholders/generate') {
    readBody(() => ok(res, { source: 'rule-engine', placeholders: [] }))
    return
  }
  if (path === '/api/v1/calendar/outlook/settings') {
    ok(res, { provider: 'outlook', clientId: null, status: 'not-connected', tokenHealth: 'missing', lastSyncedAt: null, lastError: null, uiStatus: 'not-configured' })
    return
  }

  /* 提醒 */
  if (path === '/api/v1/calendar/reminders/delivery-log') {
    ok(res, [
      { id: 'dl1', reminderId: 'r1', channel: 'Web', status: 'Executed', payloadJson: '{}', createdAt: at(9), respondedAt: at(9, 5) },
      { id: 'dl2', reminderId: 'r2', channel: '桌面', status: 'Created', payloadJson: '{}', createdAt: at(10), respondedAt: null },
    ])
    return
  }
  if (/reminders\/[^/]+\/(snooze|dismiss)$/.test(path) || /reminders\/[^/]+\/actions\//.test(path)) {
    ok(res, { id: 'r1', relatedObjectType: 'task', relatedObjectId: 't1', title: '回复客户邮件', body: '', triggerReason: '截止前 2 小时', riskLevel: 11, channels: ['Web'], doNotDisturbStart: null, doNotDisturbEnd: null, scheduledAt: at(16), status: 'Open' })
    return
  }
  if (path === '/api/v1/calendar/reminders') {
    ok(res, [
      { id: 'r1', relatedObjectType: 'task', relatedObjectId: 't1', title: '回复客户邮件', body: '客户等待报价回复', triggerReason: '截止前 2 小时', riskLevel: 11, channels: ['Web'], doNotDisturbStart: null, doNotDisturbEnd: null, scheduledAt: at(16), status: 'Open' },
      { id: 'r2', relatedObjectType: 'object', relatedObjectId: 'o2', title: '周报尚未完成', body: '', triggerReason: '每日 17:30 检查', riskLevel: 11, channels: ['桌面', 'Web'], doNotDisturbStart: null, doNotDisturbEnd: null, scheduledAt: at(17, 30), status: 'Open' },
    ])
    return
  }

  /* 报告 */
  if (path === '/api/v1/calendar/reports/generate') {
    readBody((b) => ok(res, {
      id: `rep-${Date.now()}`, kind: b.kind ?? 'Daily', projectId: null, riskLevel: 'L0AutomaticArtifact',
      contentMarkdown: '## 今日概览\n\n- 完成 2 个任务，创建 1 个日程\n- PC 活跃 4.2 小时，专注时段集中在上午\n- 手机使用 3.1 小时，夜间使用正常\n\n> 明日建议：优先处理「回复客户邮件」。',
      metricsJson: '{"tasks":4,"completedTasks":1,"events":3,"reminders":2,"habits":1}',
      generatedAt: new Date().toISOString(), status: 'Active',
    }))
    return
  }
  if (path === '/api/v1/calendar/reports') {
    ok(res, [{
      id: 'rep-1', kind: 'Daily', projectId: null, riskLevel: 'L0AutomaticArtifact',
      contentMarkdown: '## 今日概览\n\n- 完成 2 个任务，创建 1 个日程\n- PC 活跃 4.2 小时\n- 手机使用 3.1 小时\n\n> 明日建议：优先处理「回复客户邮件」。',
      metricsJson: '{"tasks":4,"completedTasks":1,"events":3,"reminders":2,"habits":1}',
      generatedAt: at(18), status: 'Active',
    }])
    return
  }

  /* 习惯 */
  if (path === '/api/v1/calendar/habits') {
    ok(res, [
      { id: 'h1', title: '每天阅读 30 分钟', cadence: 'Daily', source: 'manual', status: 'Active' },
      { id: 'h2', title: '每周跑步三次', cadence: 'Weekly', source: 'template', status: 'Active' },
    ])
    return
  }

  /* 快速记录 */
  if (path === '/api/v1/quick-notes/attachments') { ok(res, { id: `att-${Date.now()}`, fileName: 'file', contentType: 'application/octet-stream', sizeBytes: 1024, downloadUrl: '/api/v1/quick-notes/attachments/x/download', previewUrl: null, createdAt: new Date().toISOString() }); return }
  if (/quick-notes\/attachments\/[^/]+$/.test(path) && req.method === 'DELETE') { ok(res, '已删除'); return }
  if (/quick-notes\/attachments\/[^/]+\/download$/.test(path)) { json(res, 200, { code: 0, message: 'ok', data: null }); return }
  if (/quick-notes\/[^/]+$/.test(path) && req.method === 'GET') {
    ok(res, {
      id: 'qn-1', contentMarkdown: '#开发 日历组件：瀑布流布局完成，TipTap 编辑器接入中', status: 'inbox', source: 'web-page',
      attachments: [], metadataJson: '{}', contentPreview: '日历组件：瀑布流布局完成', attachmentCount: 0,
      createdAt: at(10, 20), updatedAt: at(10, 30), archivedAt: null,
    })
    return
  }
  if (path === '/api/v1/quick-notes') {
    if (req.method === 'POST') {
      readBody((b) => ok(res, {
        id: `qn-${Date.now()}`, contentMarkdown: b.contentMarkdown ?? '', status: 'inbox', source: b.source ?? 'web-page',
        attachments: [], metadataJson: '{}', contentPreview: (b.contentMarkdown ?? '').slice(0, 50), attachmentCount: 0,
        createdAt: new Date().toISOString(), updatedAt: new Date().toISOString(), archivedAt: null,
      }))
      return
    }
    ok(res, {
      items: [
        { id: 'qn-1', contentPreview: '#开发 日历组件：瀑布流布局完成，TipTap 编辑器接入中', status: 'inbox', source: 'web-page', attachmentCount: 1, attachments: [{ id: 'a1', fileName: '设计草图.png', contentType: 'image/png', sizeBytes: 20480, downloadUrl: '/x', previewUrl: null, createdAt: at(10) }], createdAt: at(10, 20), updatedAt: at(10, 30), archivedAt: null },
        { id: 'qn-2', contentPreview: '#灵感 日历事件卡可以加 hover 时的时段预览提示', status: 'inbox', source: 'web-floating', attachmentCount: 0, attachments: null, createdAt: at(9, 5), updatedAt: at(9, 5), archivedAt: null },
        { id: 'qn-3', contentPreview: '#运维 mock 服务记得在走查后关闭端口', status: 'processed', source: 'web-page', attachmentCount: 0, attachments: null, createdAt: at(8, 40), updatedAt: at(9), archivedAt: null },
        { id: 'qn-4', contentPreview: '#生活 周末把书房的灯换成暖色', status: 'archived', source: 'web-page', attachmentCount: 0, attachments: null, createdAt: plusDays(-1), updatedAt: plusDays(-1), archivedAt: plusDays(-1) },
      ],
      totalCount: 4, page: 1, pageSize: 50, totalPages: 1,
    })
    return
  }

  /* 数据中心 + 审计 */
  if (path === '/api/v1/calendar/data-center/query') {
    readBody(() => ok(res, {
      items: [
        { objectType: 'event', objectId: 'e1', title: '晨会', source: 'manual', status: 'CONFIRMED', startsAt: at(9), endsAt: at(9, 30), summary: '工作' },
        { objectType: 'task', objectId: 't1', title: '写 P1 周报', source: 'manual', status: 'NEEDS-ACTION', startsAt: null, endsAt: null, summary: '收集箱' },
        { objectType: 'audit-version', objectId: 'av-1', title: '审计版本 1', source: 'manual', status: 'Active', startsAt: null, endsAt: null, summary: '变更 plannedStart' },
      ],
      page: 1, pageSize: 50, totalCount: 3,
    }))
    return
  }
  if (path === '/api/v1/calendar/data-center/audit/export' || path === '/api/v1/operations/audit/export') {
    ok(res, { fileName: 'audit-export.json', contentType: 'application/json', content: '[]' })
    return
  }
  if (path === '/api/v1/calendar/data-center/restore/preview' || /operations\/audit\/[^/]+\/restore-preview$/.test(path)) {
    ok(res, {
      objectType: 'task', objectId: 't1', summary: 'Restore task t1 to audit version.', requiresConfirmation: true,
      changedFields: ['plannedStart'], beforeJson: '{"plannedStart":null}', afterJson: '{"plannedStart":"2026-09-25T20:00:00Z"}',
    })
    return
  }
  if (/operations\/audit\/[^/]+\/[^/]+$/.test(path) && req.method === 'GET') {
    const seg = path.split('/')
    ok(res, { items: [
      { id: 'av-1', objectType: seg[5] ?? 'task', objectId: seg[6] ?? 't1', confirmationId: null, source: 'manual', actor: 'system', beforeJson: '{"plannedStart":null}', afterJson: '{"plannedStart":"2026-09-25T20:00:00Z"}', changedFieldsJson: '["plannedStart"]', createdAt: at(10) },
    ] })
    return
  }

if (path === '/api/v1/mobile/analytics/charts') {
  const pts = (labels, vals) => labels.map((l, i) => ({ key: String(i), label: l, value: vals[i] }))
  ok(res, [
    { key: 'category-share', title: '分类占比', chartType: 'pie', unit: 'minutes', points: [{ key: '0', label: '聊天', value: 64, lifeCategory: '聊天' }, { key: '1', label: '视频', value: 38, lifeCategory: '视频' }, { key: '2', label: '学习', value: 26, lifeCategory: '学习' }, { key: '3', label: '其他', value: 22, lifeCategory: '其他' }] },
    { key: 'top-apps', title: 'Top App', chartType: 'bar', unit: 'minutes', points: pts(['微信', '抖音', 'B站', '知乎'], [48, 32, 25, 19]) },
    { key: 'daily-total', title: '每日趋势', chartType: 'line', unit: 'minutes', points: pts(Array.from({ length: 7 }, (_, i) => daysAgo(6 - i).slice(0, 10)), Array.from({ length: 7 }, () => Math.round(120 + Math.random() * 160))) },
    { key: 'hour-distribution', title: '小时分布', chartType: 'bar', unit: 'minutes', points: pts(Array.from({ length: 24 }, (_, i) => String(i) + '时'), Array.from({ length: 24 }, () => Math.round(Math.random() * 20))) },
  ])
  return
}
if (path === '/api/v1/mobile/analytics/timeline-blocks') {
  const items = []
  for (let i = 0; i < 8; i++) {
    const h = 21 - i * 2
    if (h < 7) break
    items.push({ id: 'blk-' + i, startUtc: iso(h), endUtc: iso(h + 1), localStart: String(h).padStart(2, '0') + ':00', localEnd: String(h + 1).padStart(2, '0') + ':00', lifeCategory: ['聊天', '视频'][i % 2], foregroundSeconds: 2400 + Math.round(Math.random() * 900), sessionCount: 3 + i, appCount: i % 3 === 0 ? 1 : 2, topApps: [{ packageName: 'com.tencent.mm', displayName: '微信', foregroundSeconds: 1800 }] })
  }
  ok(res, { items, hasMore: false, totalCount: items.length, nextCursor: null })
  return
}
if (/timeline-blocks\/[^/]+\/sessions$/.test(path)) {
  ok(res, [
    { id: 's1', packageName: 'com.tencent.mm', displayName: '微信', startUtc: iso(21), endUtc: iso(21, 40), durationSeconds: 2400, lifeCategory: '聊天' },
    { id: 's2', packageName: 'com.zhihu.android', displayName: '知乎', startUtc: iso(21, 45), endUtc: iso(21, 55), durationSeconds: 600, lifeCategory: '其他' },
  ])
  return
}
if (path === '/api/v1/mobile/devices/manage') {
  ok(res, [
    { deviceId: 'pixel-7', displayName: 'Pixel 7', brand: 'Google', model: 'Pixel 7', osVersion: 'Android 15', appVersion: '1.4.0', registeredAtUtc: daysAgo(30), lastSeenAtUtc: new Date().toISOString(), isOnline: true, sessionCount: 210, eventCount: 4321, locationCount: 1890, storageEstimateKb: 3210, syncStatus: 'normal', dataQuality: 'normal', storagePressure: 'normal' },
    { deviceId: 'mi-pad', displayName: '小米平板', brand: 'Xiaomi', model: 'Pad 6', osVersion: 'Android 14', appVersion: '1.3.2', registeredAtUtc: daysAgo(60), lastSeenAtUtc: daysAgo(0.2), isOnline: false, sessionCount: 88, eventCount: 1200, locationCount: 340, storageEstimateKb: 980, syncStatus: 'delayed', dataQuality: 'normal', storagePressure: 'pending' },
  ])
  return
}
if (/devices\/[^/]+\/delete-preview$/.test(path)) {
  ok(res, { deviceId: 'pixel-7', displayName: 'Pixel 7', sessionCount: 210, eventCount: 4321, locationCount: 1890, summaryCount: 84 })
  return
}
if (/devices\/[^/]+\/detail$/.test(path)) {
  ok(res, {
    device: { deviceId: 'pixel-7', displayName: 'Pixel 7', brand: 'Google', model: 'Pixel 7', osVersion: 'Android 15', appVersion: '1.4.0', registeredAtUtc: daysAgo(30), lastSeenAtUtc: new Date().toISOString() },
    stats: { sessionCount: 210, eventCount: 4321, locationCount: 1890, storageEstimateKb: 3210 },
    syncHistory: Array.from({ length: 5 }, (_, i) => ({ batchId: 'batch-' + i, createdAt: daysAgo(i), acceptedCount: 120 - i * 10, status: i === 2 ? 'completed-with-errors' : 'completed' })),
    healthTimeline: Array.from({ length: 7 }, (_, i) => daysAgo(6 - i).slice(0, 10) + (i >= 5 ? ':online' : ':offline')),
  })
  return
}
if (/devices\/[^/]+\/rename$/.test(path) || /devices\/merge/.test(path) || (req.method === 'DELETE' && /devices\/[^/]+$/.test(path))) {
  readBody(() => ok(res, null))
  return
}
if (path === '/api/v1/mobile/liveness/overview') {
  const mk = (id, name, kind, label, has) => ({ deviceId: id, displayName: name, deviceKind: kind, deviceKindLabel: label, hasData: has, conclusion: has ? '存活连续：区间内无大于等于30分钟静默。' : '无数据/未上报：该区间内没有任何存活证据。', coverageByHour: has ? 0.92 : null, longestSilenceMinutes: has ? 18 : 0, silences: [], causes: has ? [{ cause: 'reboot', label: '设备重启', count: 1, inference: null }] : [], lastEventAtUtc: has ? new Date().toISOString() : null })
  ok(res, { rangeStartUtc: daysAgo(7), rangeEndUtc: new Date().toISOString(), expectedHeartbeatIntervalMinutes: 15, phones: [mk('pixel-7', 'Pixel 7', 'phone', '手机', true)], tablets: [mk('mi-pad', '小米平板', 'tablet', '平板', false)], unclassified: [] })
  return
}
if (path === '/api/v1/mobile/location/analytics/overview') {
  ok(res, { range: { rangeStartUtc: '', rangeEndUtc: '', localStartDate: '', localEndDate: '' }, pointCount: 1890, usablePointCount: 1720, rejectedPointCount: 170, distanceMeters: 48200, stayCount: 12, longestStaySeconds: 14400, averageAccuracyMeters: 12.4 })
  return
}
if (path === '/api/v1/mobile/location/analytics/tracks') {
  const mkPath = (lat0, lng0, n) => Array.from({ length: n }, (_, i) => ({ id: 'p' + i, recordedAtUtc: new Date(Date.now() - (n - i) * 60000).toISOString(), latitude: lat0 + (Math.random() - 0.5) * 0.01, longitude: lng0 + (Math.random() - 0.5) * 0.01, horizontalAccuracyMeters: 10 }))
  ok(res, [{ id: 'trk-1', deviceId: 'pixel-7', startUtc: iso(9), endUtc: iso(12), distanceMeters: 12400, durationSeconds: 10800, pointCount: 120, segmentCount: 2, segments: [
    { id: 'seg-stay', kind: 'stay', startUtc: iso(9), endUtc: iso(11), localStart: '09:00', localEnd: '11:00', durationSeconds: 7200, distanceMeters: 0, pointCount: 40, path: [{ id: 's0', recordedAtUtc: iso(10), latitude: 31.2304, longitude: 121.4737, horizontalAccuracyMeters: 8 }] },
    { id: 'seg-move', kind: 'move', startUtc: iso(11), endUtc: iso(12), localStart: '11:00', localEnd: '12:00', durationSeconds: 3600, distanceMeters: 12400, pointCount: 80, path: mkPath(31.2304, 121.4737, 40) },
  ] }])
  return
}
if (path === '/api/v1/mobile/location/analytics/frequent-places') {
  ok(res, {
    home: { centerLatitude: 31.2304, centerLongitude: 121.4737, radiusMeters: 120, pointCount: 890, visitDayCount: 6, isHome: true },
    places: [
      { centerLatitude: 31.2304, centerLongitude: 121.4737, radiusMeters: 120, pointCount: 890, visitDayCount: 6, isHome: true },
      { centerLatitude: 31.2260, centerLongitude: 121.4700, radiusMeters: 80, pointCount: 210, visitDayCount: 4, isHome: false },
    ],
  })
  return
}
if (path === '/api/v1/mobile/location/analytics/movement-stats') {
  ok(res, { homeCenter: { latitude: 31.2304, longitude: 121.4737 }, outingCount: 5, outingSeconds: 32400, distanceMeters: 48200, maxSpeedMetersPerSecond: 8.4 })
  return
}
if (/segments\/[^/]+\/points$/.test(path)) {
  const items = Array.from({ length: 50 }, (_, i) => ({ id: 'pt-' + i, recordedAtUtc: new Date(Date.now() - i * 120000).toISOString(), latitude: 31.23 + (Math.random() - 0.5) * 0.01, longitude: 121.47 + (Math.random() - 0.5) * 0.01, horizontalAccuracyMeters: 10 }))
  ok(res, { items, nextCursor: null, hasMore: false })
  return
}
if (path === '/api/v1/status/' || path === '/api/v1/status') {
  ok(res, {
    summary: { status: 1, label: '正常', message: '全部组件健康（mock）', checkedAt: new Date().toISOString() },
    components: [
      { key: 'api', name: 'API 服务', kind: 0, status: 1, message: '正常', checkedAt: new Date().toISOString(), details: {} },
      { key: 'db', name: 'PostgreSQL', kind: 1, status: 1, message: '连接正常', checkedAt: new Date().toISOString(), details: {} },
      { key: 'storage', name: '对象存储', kind: 2, status: 2, message: 'OneDrive 令牌即将过期', checkedAt: new Date().toISOString(), details: {} },
      { key: 'jobs', name: '后台任务', kind: 7, status: 1, message: 'Hangfire 正常', checkedAt: new Date().toISOString(), details: {} },
    ],
    nextSteps: ['检查 OneDrive 授权状态（设置 → Microsoft 账户）'],
  })
  return
}
if (path === '/api/v1/pc/quality') {
  ok(res, { overallStatus: 1, label: 'PC 采集正常', message: '键鼠与窗口采集均正常', checkedAt: new Date().toISOString(), components: [], issues: [], nextSteps: [] })
  return
}
if (path === '/api/v1/pc/tracker/health/latest') {
  ok(res, { deviceId: 'ws-01', status: 'running', uptimeSeconds: 86400, hookActive: true, browserConnected: true, browserHeartbeatAgeSeconds: 45, siteConnected: false, siteEventsUploaded: 0, reportedAt: new Date().toISOString() })
  return
}
if (path === '/api/v1/daemon/heartbeats') {
  ok(res, [
    { deviceId: 'ws-01', daemonKind: 'windows', version: '1.4.0', lastSuccessfulUploadAt: new Date().toISOString(), uploadQueueCount: 0, activityWatchState: 'Available', keyStatsState: 'Available', collectionPaused: false, receivedAt: new Date().toISOString(), plannedOfflineAt: null },
  ])
  return
}
if (path === '/api/v1/mobile/quality') {
  ok(res, { overallStatus: 1, label: 'Android 采集正常', message: '心跳与使用上报正常', checkedAt: new Date().toISOString(), components: [], issues: [], nextSteps: [] })
  return
}

if (path === '/api/v1/pc/heatmap/grid') {
  const dimension = url.searchParams.get('dimension') ?? 'day'
  const grid = []
  const rows = dimension === 'day' ? 7 : 12
  for (let y = 0; y < rows; y++) {
    const row = []
    for (let x = 0; x < (dimension === 'hour' ? 24 : 7); x++) {
      const v = Math.random() > 0.35 ? Math.round(Math.random() * 8000) : 0
      row.push({ start: new Date().toISOString(), end: new Date().toISOString(), hour: dimension === 'hour' ? x : y, activeMinutes: 0, totalEvents: v, intensityScore: v })
    }
    grid.push(row)
  }
  ok(res, { grid, dimension, maxKeyCount: 8000 })
  return
}
if (path === '/api/v1/pc/activity-analysis') {
  const blocks = []
  for (let h = 7; h <= 22; h++) {
    blocks.push({
      start: iso(h), end: iso(h + 1), intensityScore: Math.round(Math.random() * 100),
      activeDurationSeconds: 3600, pendingClassificationCount: h === 10 || h === 15 ? 3 : 0,
      contextSwitchCount: Math.round(Math.random() * 20),
      categories: [{ categoryName: '开发', color: '#2563EB', durationSeconds: 2400 }, { categoryName: '学习', color: '#8B5CF6', durationSeconds: 1200 }],
    })
  }
  ok(res, { date: url.searchParams.get('date') ?? '', blockMinutes: 60, blocks })
  return
}
if (path === '/api/v1/pc/aggregation/app-usage') {
  ok(res, { items: [
    { appName: 'Code.exe', displayName: 'VS Code', totalMinutes: 214, percentage: 0.38 },
    { appName: 'chrome.exe', displayName: 'Chrome', totalMinutes: 128, percentage: 0.22 },
    { appName: 'WeChat.exe', displayName: '微信', totalMinutes: 62, percentage: 0.11 },
    { appName: 'WINWORD.EXE', displayName: 'Word', totalMinutes: 45, percentage: 0.08 },
  ], totalMinutes: 560 })
  return
}
if (path === '/api/v1/pc/aggregation/category-distribution') {
  ok(res, { items: [
    { categoryName: '开发', color: '#2563EB', minutes: 260, percentage: 0.46 },
    { categoryName: '学习', color: '#8B5CF6', minutes: 120, percentage: 0.21 },
    { categoryName: '其他', color: '#64748B', minutes: 180, percentage: 0.33 },
  ] })
  return
}
if (path === '/api/v1/pc/productivity/dashboard') {
  ok(res, {
    todayScore: 78, productiveHours: 4.4, distractingHours: 1.2, neutralHours: 1.8, targetHours: 5, goalMet: false,
    weeklyTrend: Array.from({ length: 7 }, (_, i) => ({ date: daysAgo(6 - i).slice(0, 10), productiveMinutes: 180 + Math.round(Math.random() * 180), neutralMinutes: 60 + Math.round(Math.random() * 60), distractingMinutes: Math.round(Math.random() * 80), totalMinutes: 360, productiveRatio: 0.6 })),
  })
  return
}
if (path === '/api/v1/pc/classification/queue') {
  ok(res, { items: [
    { targetType: 'app', target: 'Obsidian.exe', displayName: 'Obsidian', minutes: 84, sampleTitles: ['学习笔记 - 知识库'], currentCategory: null },
    { targetType: 'domain', target: 'github.com', displayName: 'github.com', minutes: 56, sampleTitles: ['pim/client-web Pull requests'], currentCategory: null },
  ] })
  return
}
if (path === '/api/v1/pc/categories/dictionary') {
  ok(res, [
    { id: 'c1', name: '开发', color: '#2563EB', icon: null },
    { id: 'c2', name: '学习', color: '#8B5CF6', icon: null },
    { id: 'c3', name: '文档', color: '#F59E0B', icon: null },
    { id: 'c4', name: '其他', color: '#64748B', icon: null },
  ])
  return
}
if (/classification\/label$/.test(path)) {
  readBody(() => ok(res, { ok: true, categoryName: '已标注', created: '标注成功' }))
  return
}
if (/app-knowledge\/suggestions\/[^/]+\/(preview|apply)$/.test(path)) {
  readBody(() => ok(res, {
    recommendation: { categoryName: '开发', categoryPath: '开发/工具' },
    preview: { affectedRecordCount: 42, affectedDurationSeconds: 5400, currentCategoryCounts: { 其他: 42 }, newCategoryCounts: { 开发: 42 }, requiresConfirmation: false, summary: '42 条记录将重分类为「开发」' },
  }))
  return
}
if (/suggestions\/[^/]+\/reject$/.test(path)) { ok(res, '已拒绝'); return }
if (path === '/api/v1/pc/classification/suggestions') {
  ok(res, [
    { id: 'sg-1', clusterKey: 'k1', sampleCount: 42, totalDurationSeconds: 5400, currentCategory: null, suggestedCategory: '开发', appDisplayName: 'Obsidian', status: 'pending' },
  ])
  return
}
if (path === '/api/v1/pc/browser-tt/summary') {
  ok(res, {
    from: daysAgo(6).slice(0, 10), to: new Date().toISOString().slice(0, 10),
    totalFocusMs: 21 * 3600_000, totalVisits: 312, totalRunMs: 26 * 3600_000, totalMediaMs: 2 * 3600_000, siteCount: 23,
    topHosts: [
      { host: 'github.com', alias: null, focusMs: 6 * 3600_000, visitCount: 96 },
      { host: 'docs.rs', alias: null, focusMs: 3.2 * 3600_000, visitCount: 54 },
      { host: 'stackoverflow.com', alias: null, focusMs: 2.4 * 3600_000, visitCount: 41 },
      { host: 'zhihu.com', alias: null, focusMs: 1.1 * 3600_000, visitCount: 38 },
    ],
  })
  return
}
if (path === '/api/v1/pc/browser-tt/daily') {
  ok(res, Array.from({ length: 7 }, (_, i) => ({ date: daysAgo(6 - i).slice(0, 10), host: 'all', focusMs: (2 + Math.random() * 3) * 3600_000, visitCount: 40 + Math.round(Math.random() * 30), runMs: 3 * 3600_000, mediaMs: 0 })))
  return
}
if (path === '/api/v1/pc/browser-tt/timeline') {
  const tl = []
  const hosts = [['github.com', 9], ['docs.rs', 11], ['stackoverflow.com', 14], ['zhihu.com', 20]]
  let ci = 0
  for (const [host, h] of hosts) {
    tl.push({ host, startMs: new Date(iso(h)).getTime(), durationMs: 45 * 60000 })
    tl.push({ host, startMs: new Date(iso(h + 1, 20)).getTime(), durationMs: 20 * 60000 })
    ci++
  }
  ok(res, tl)
  return
}
if (/browser-tt\/import$/.test(path)) {
  readBody(() => ok(res, { rows: 128, dates: 5, hosts: 12, skipped: 2, format: 'tt4b-markdown' }))
  return
}

/* 手机域 */
if (path === '/api/v1/mobile/analytics/overview') {
  ok(res, {
    range: { rangeStartUtc: daysAgo(7), rangeEndUtc: new Date().toISOString(), localStartDate: '', localEndDate: '' },
    generatedAt: new Date().toISOString(),
    totalForegroundSeconds: 3.1 * 3600, dailyAverageSeconds: 0.45 * 3600,
    highestUseLocalDate: new Date().toISOString().slice(0, 10), peakLocalHour: 21,
    appCount: 12, switchOrPickupCount: 84, completeness: 0.82,
    quality: { usageEventsCoverage: 0.82, fallbackShare: 0.18, missingMetadataAppCount: 1, systemNoiseShare: 0.05, failedOrPartialSyncBatchCount: 0, lastSyncAt: at(8) },
    goalProgress: null,
    anomalies: [{ code: 'night-use', severity: 'Warning', title: '夜间使用偏高', evidence: '22 点后仍有 18 分钟使用', drilldownTarget: 'heatmap' }],
    suggestions: [{ code: 'top-category-review', text: '「视频」分类近 7 天占 34%，建议复查是否需要限制', drilldownTarget: '' }],
  })
  return
}
if (path === '/api/v1/mobile/analytics/heatmap') {
  const buckets = []
  for (let d = 6; d >= 0; d--) {
    const date = daysAgo(d).slice(0, 10)
    for (let h = 0; h < 24; h++) {
      if ((h < 7 || h > 23) && Math.random() > 0.1) continue
      if (Math.random() > 0.55) continue
      buckets.push({ bucketStartUtc: '', bucketEndUtc: '', localDate: date, localHour: h, lifeCategory: ['聊天', '视频', '其他'][Math.floor(Math.random() * 3)], foregroundSeconds: Math.round(Math.random() * 1800) })
    }
  }
  ok(res, buckets)
  return
}

/* P4-MOCK-BLOCK */
if (path === '/api/v1/files/providers') {
  ok(res, [{ id: 'prov-1', provider: 'onedrive', status: 'connected', clientId: '11111111-2222-3333-4444-555555555555', driveId: 'drive-abc', accountId: 'acc-1', accountName: 'demo@outlook.com', syncStatus: 'idle', syncedItemCount: 1284, lastSyncAt: at(9), lastError: null, tokenExpiresAt: null }])
  return
}
if (path === '/api/v1/files/items') {
  const dir = url.searchParams.get('path') ?? '/'
  const type = url.searchParams.get('type')
  const mk = (id, name, itemType, size, mime, ipath) => ({ id, providerId: 'prov-1', externalFileId: 'ext-' + id, parentExternalFileId: null, path: ipath, name, itemType, mimeType: mime, size, etag: null, contentHash: null, createdAt: daysAgo(20), modifiedAt: daysAgo(1), syncedAt: new Date().toISOString(), indexStatus: 'not_indexed' })
  let items = dir === '/'
    ? [mk('f1', '文档', 'folder', null, null, '/文档'), mk('f2', '图片', 'folder', null, null, '/图片'), mk('f3', '项目', 'folder', null, null, '/项目')]
    : dir === '/文档'
      ? [mk('d1', '周报.md', 'file', 24576, 'text/markdown', '/文档/周报.md'), mk('d2', '设计说明.txt', 'file', 4096, 'text/plain', '/文档/设计说明.txt'), mk('d3', '预算.xlsx', 'file', 88234, 'application/vnd.ms-excel', '/文档/预算.xlsx')]
      : dir === '/图片'
        ? [mk('i1', '截图-日历.png', 'file', 204800, 'image/png', '/图片/截图-日历.png'), mk('i2', '架构图.png', 'file', 512000, 'image/png', '/图片/架构图.png')]
        : [mk('p1', 'README.md', 'file', 2048, 'text/markdown', dir + '/README.md')]
  if (type === 'folder') items = items.filter((i) => i.itemType === 'folder')
  ok(res, { result: { items, page: 1, pageSize: 100, totalCount: items.length, totalPages: 1 } })
  return
}
if (path === '/api/v1/files/search') {
  const kw = (url.searchParams.get('q') ?? '').toLowerCase()
  const all = [{ id: 'd1', name: '周报.md', path: '/文档/周报.md', itemType: 'file', mimeType: 'text/markdown', size: 24576, providerId: 'prov-1', externalFileId: 'ext-d1', parentExternalFileId: null, etag: null, contentHash: null, createdAt: daysAgo(20), modifiedAt: daysAgo(1), syncedAt: new Date().toISOString(), indexStatus: 'not_indexed' }]
  const items = all.filter((i) => i.name.toLowerCase().includes(kw))
  ok(res, { items, totalCount: items.length, totalPages: 1 })
  return
}
if (/[f]iles\/providers\/[^/]+\/sync-status$/.test(path)) { ok(res, { syncStatus: 'idle', lastError: null, lastSyncAt: at(9), syncedItemCount: 1284 }); return }
if (/[f]iles\/providers\/[^/]+\/sync$/.test(path)) { readBody(() => ok(res, { started: true, message: '已开始同步' })); return }
if (/[f]iles\/providers\/[^/]+\/binding-status$/.test(path)) { ok(res, { status: 'pending', driveId: null, accountId: null, accountName: null, userCode: 'ABCD-1234', verificationUri: 'https://microsoft.com/devicelogin', deviceCodeExpiresAt: plusDays(0.05) }); return }
if (/[f]iles\/items\/[^/]+\/text$/.test(path) && req.method === 'GET') {
  ok(res, { content: '# 周报\n\n## 本周完成\n- P3 分析驾驶舱\n- P4 文件与设置域\n\n## 下周计划\n- 双壳打包与打磨\n', mimeType: 'text/markdown', size: 120, truncated: false })
  return
}
if (/[f]iles\/items\/[^/]+\/snapshots$/.test(path)) {
  ok(res, [{ id: 'snap-1', path: '/文档/周报.md', name: '周报.md', content: '# 周报（旧版）...', byteSize: 110, reason: 'pre-edit', createdAt: at(9, 30) }])
  return
}
if (/[f]iles\/items\/[^/]+\/shares$/.test(path)) {
  ok(res, [{ itemId: 'd1', itemName: '周报.md', path: '/文档/周报.md', permissionType: 'view', permissionId: 'perm-1', webUrl: 'https://1drv.ms/xxxx', expiresAt: null, createdAt: at(10) }])
  return
}
if (path === '/api/v1/files/shares') {
  ok(res, [{ itemId: 'd1', itemName: '周报.md', path: '/文档/周报.md', permissionType: 'view', permissionId: 'perm-1', webUrl: 'https://1drv.ms/xxxx', expiresAt: null, createdAt: at(10) }])
  return
}
if (/[f]iles\/items\/[^/]+\/(download-url|preview-url|open-link|content|thumbnail)$/.test(path)) {
  const u = 'https://example.invalid/file'
  if (path.endsWith('thumbnail') || path.endsWith('content')) { res.writeHead(302, { location: u }); res.end(); return }
  ok(res, { url: u, mode: 'onedrive-web' })
  return
}
if (/[f]iles\/items\/[^/]+\/share$/.test(path)) {
  readBody(() => ok(res, { itemId: 'd1', itemName: '周报.md', path: '/文档/周报.md', permissionType: 'view', permissionId: 'perm-2', webUrl: 'https://1drv.ms/new', expiresAt: null, createdAt: new Date().toISOString() }))
  return
}
if (/[f]iles\/items\/upload-session$/.test(path)) {
  readBody((b) => ok(res, { uploadUrl: 'https://example.invalid/upload', expirationDateTime: null, path: b.path ?? '/', fileName: b.fileName ?? 'file' }))
  return
}
if (/[f]iles\/items\/upload-session\/complete$/.test(path)) {
  readBody((b) => ok(res, { id: 'new-1', providerId: 'prov-1', externalFileId: 'ext-new', parentExternalFileId: null, path: (b.path ?? '/') + '/' + (b.fileName ?? 'file'), name: b.fileName ?? 'file', itemType: 'file', mimeType: null, size: 1024, etag: null, contentHash: null, createdAt: new Date().toISOString(), modifiedAt: new Date().toISOString(), syncedAt: new Date().toISOString(), indexStatus: 'not_indexed' }))
  return
}
if (path === '/api/v1/data-reliability/inspection' || path === '/api/v1/data-reliability/inspection/refresh') {
  const mkRule = (code, name, group, groupLabel, status) => ({ code, key: code, name, group, groupLabel, status, statusLabel: status === 'red' ? '红灯' : status === 'yellow' ? '黄灯' : '绿灯', detail: name + '检查完成', currentValueLabel: status === 'green' ? '0 违规' : '3 违规', threshold: '小于等于 0', criterion: '当 ' + name + ' 超出阈值时判为违规', rationale: '该判据来自数据不变量定义', totalViolations: status === 'green' ? 0 : 3, newViolations: status === 'green' ? 0 : 1, historicalViolations: status === 'green' ? 0 : 2, samples: status === 'green' ? [] : ['样例 A', '样例 B'] })
  ok(res, { inspectedAtUtc: new Date().toISOString(), redCount: 1, yellowCount: 2, greenCount: 10, totalViolations: 9, newViolations: 3, message: '体检完成', rules: [mkRule('S1', '会话重叠', 'A', '会话与事件一致性', 'red'), mkRule('S2', '三态分布', 'A', '会话与事件一致性', 'yellow'), mkRule('S3', '空标题', 'B', '元数据完整性', 'green'), mkRule('S4', '时区缺失', 'B', '元数据完整性', 'yellow')] })
  return
}
if (/data-reliability\/rules\/[^/]+\/violations$/.test(path)) {
  ok(res, { ruleCode: 'S1', generatedAtUtc: new Date().toISOString(), totalCount: 3, truncated: false, items: [{ ruleCode: 'S1', id: 'v1', deviceId: 'ws-01', occurredAtUtc: at(10), fields: { app: 'Code.exe' } }] })
  return
}
if (path === '/api/v1/ai/status') { ok(res, { enabled: true, provider: 'litellm', baseUrl: 'http://127.0.0.1:4000', defaultModel: 'gpt-4o-mini', lastHealthCheckAt: at(10), lastError: null, recentSuccessfulCallAt: at(10, 5) }); return }
if (path === '/api/v1/ai/usage/summary') { ok(res, { requestCount: 128, successCount: 121, failureCount: 7, totalTokens: 486123, estimatedCost: 1.2345 }); return }
if (path === '/api/v1/ai/requests') {
  const items = Array.from({ length: 8 }, (_, i) => ({ id: 'log-' + i, startedAt: at(10 - (i % 3), i * 5), module: ['calendar', 'files', 'pc'][i % 3], purpose: ['suggest', 'classify', 'summarize'][i % 3], model: 'gpt-4o-mini', status: i === 3 ? 'Failed' : 'Succeeded', totalTokens: 1200 + i * 130, estimatedCost: 0.0032, durationMs: 800 + i * 120 }))
  ok(res, { items, totalCount: 128, page: 1, pageSize: 20, totalPages: 7 })
  return
}
if (/ai\/requests\/[^/]+$/.test(path)) { ok(res, { id: 'log-1', module: 'calendar', purpose: 'suggest', model: 'gpt-4o-mini', status: 'Succeeded', requestMessagesJson: '[]', responseText: 'ok', totalTokens: 1200 }); return }
if (/ai\/(test|health-check)$/.test(path)) { ok(res, { status: 'Succeeded', responseText: 'ok', userFacingError: null }); return }
if (path === '/api/v1/mcp/clients') {
  ok(res, [{ id: 'mc-1', name: 'Claude Code', status: 'active', tokenPrefix: 'pim_mcp_ab12', permissions: { read: {}, write: {} }, createdAt: daysAgo(5), revokedAt: null, lastSeenAt: new Date(Date.now() - 120000).toISOString(), callCount: 342, writeCallCount: 12, lastTool: 'get_calendar_layers', online: true, createdByUsername: 'demo' }])
  return
}
if (path === '/api/v1/mcp/activity') {
  ok(res, Array.from({ length: 6 }, (_, i) => ({ timestamp: new Date(Date.now() - i * 60000).toISOString(), clientName: 'Claude Code', toolName: 'get_today_sections', statusCode: i === 2 ? 403 : 200, durationMs: 40 + i * 15, argumentsSummary: '{"q":"x"}' })))
  return
}
if (path === '/api/v1/mcp/catalog') {
  const mkTools = (prefix, n) => Array.from({ length: n }, (_, i) => ({ name: prefix + '_tool_' + i, group: prefix, description: prefix + ' tool ' + i }))
  ok(res, { read: mkTools('calendar', 6).concat(mkTools('files', 4)), write: mkTools('quicknotes', 4) })
  return
}
if (/mcp\/clients\/[^/]+\/revoke$/.test(path)) { readBody(() => ok(res, null)); return }
if (/mcp\/clients/.test(path)) { readBody((b) => ok(res, { token: 'pim_mcp_newtoken0123456789abcdef', client: { id: 'mc-2', name: b.name ?? 'new' } })); return }
if (path === '/api/v1/admin/users') {
  ok(res, [{ id: 'u1', username: 'demo', email: 'demo@pim.dev', displayName: '演示用户', role: 'admin', isActive: true, createdAt: daysAgo(60) }, { id: 'u2', username: 'alice', email: 'alice@pim.dev', displayName: 'Alice', role: 'user', isActive: true, createdAt: daysAgo(10) }])
  return
}
if (/admin\/users\/[^/]+\/(role|status)$/.test(path)) { readBody(() => ok(res, { id: 'u2', role: 'user', isActive: true })); return }
if (path === '/api/v1/pc/app-knowledge/apps') {
  ok(res, [
    { id: 'kb-1', processName: 'Code.exe', displayName: 'VS Code', categoryPath: '开发/编辑器', productivity: 'productive', source: 'builtin', icon: 'V', contextCount: 3, pendingContextCount: 0 },
    { id: 'kb-2', processName: 'Obsidian.exe', displayName: 'Obsidian', categoryPath: null, productivity: 'neutral', source: 'learned', icon: 'O', contextCount: 1, pendingContextCount: 1 },
  ])
  return
}
if (/app-knowledge\/apps\/[^/]+\/contexts$/.test(path)) {
  ok(res, [{ id: 'ctx-1', processName: 'Code.exe', patternType: 'domain', patternValue: 'github.com', targetCategoryName: '开发', scopeSummary: 'github.com 全站', source: 'learned', enabled: true, affectedRecordCount: 42 }])
  return
}
if (/app-knowledge\/contexts\/[^/]+$/.test(path) && req.method === 'DELETE') { ok(res, '已删除。'); return }
if (path === '/api/v1/pc/app-signatures/') { readBody((b) => ok(res, { id: 'sig-new' })); return }
if (/pc\/app-signatures\/[^/]+$/.test(path) && req.method === 'DELETE') { ok(res, '已删除'); return }
if (path === '/api/v1/pc/categories/tree') {
  ok(res, [{ id: 'cat-1', parentId: null, name: '开发', color: '#2563EB', icon: '💻', productivity: 'productive', sortOrder: 1, isBuiltin: true, children: [{ id: 'cat-1-1', parentId: 'cat-1', name: '编辑器', color: '#3B82F6', icon: null, productivity: 'productive', sortOrder: 1, isBuiltin: false, children: [] }] }, { id: 'cat-2', parentId: null, name: '学习', color: '#8B5CF6', icon: '📚', productivity: 'productive', sortOrder: 2, isBuiltin: true, children: [] }, { id: 'cat-3', parentId: null, name: '娱乐', color: '#F97316', icon: '🎮', productivity: 'distracting', sortOrder: 3, isBuiltin: true, children: [] }])
  return
}
if (path === '/api/v1/pc/categories' || path === '/api/v1/pc/categories/') { readBody((b) => ok(res, { id: b.id ?? 'cat-new', parentId: b.parentId ?? null, name: b.name ?? '新分类', color: b.color ?? '#64748B', icon: b.icon ?? null, productivity: b.productivity ?? 'neutral', sortOrder: 9, isBuiltin: false, children: [] })); return }
if (/pc\/categories\/seed$/.test(path)) { ok(res, '种子数据已初始化'); return }
if (/pc\/categories\/[^/]+$/.test(path) && req.method === 'DELETE') { ok(res, '已删除'); return }
if (path === '/api/v1/endpoints') {
  ok(res, [{ deviceId: 'ws-01', platform: 'windows', appVersion: '1.4.0', uploadStatus: 'Healthy', collectionCacheCount: 0, onlineOnlyBlockedCount: 1, lastHeartbeatAt: new Date().toISOString() }])
  return
}
if (/endpoints\/[^/]+\/collection-quality$/.test(path)) { ok(res, { deviceId: 'ws-01', platform: 'windows', uploadStatus: 'Healthy', issueCount: 0, checkedAt: new Date().toISOString() }); return }
if (/endpoints\/[^/]+\/heartbeat$/.test(path)) { readBody(() => ok(res, { deviceId: 'ws-01', platform: 'windows', uploadStatus: 'Healthy', collectionCacheCount: 0, onlineOnlyBlockedCount: 0, lastHeartbeatAt: new Date().toISOString() })); return }
if (/endpoints\/[^/]+\/notification-actions$/.test(path)) { readBody((b) => ok(res, { result: String(b.riskLevel ?? '').startsWith('L4') ? 'OpenDetailRequired' : 'Executed', detailUrl: String(b.riskLevel ?? '').startsWith('L4') ? '/confirmations' : null, message: '已记录' })); return }

if (path === '/api/v1/mobile/summary') {
  ok(res, {
    date: url.searchParams.get('date') ?? new Date().toISOString().slice(0, 10),
    deviceId: null, generatedAt: new Date().toISOString(),
    totalForegroundSeconds: 11160, fallbackForegroundSeconds: 1200, appSwitchCount: 84, appsUsed: 12, completeness: 0.82,
    lastSyncAt: at(8),
    appRanking: [
      { packageName: 'com.tencent.mm', displayName: '微信', categoryName: '聊天', foregroundSeconds: 4080, sessionCount: 32, launchCount: 40, lastUsedAt: at(21), source: 'events', share: 0.37 },
      { packageName: 'com.ss.android.ugc.aweme', displayName: '抖音', categoryName: '视频', foregroundSeconds: 2700, sessionCount: 18, launchCount: 22, lastUsedAt: at(22), source: 'events', share: 0.24 },
      { packageName: 'tv.danmaku.bili', displayName: 'B站', categoryName: '视频', foregroundSeconds: 1980, sessionCount: 12, launchCount: 14, lastUsedAt: at(20), source: 'events', share: 0.18 },
      { packageName: 'com.zhihu.android', displayName: '知乎', categoryName: '学习', foregroundSeconds: 1440, sessionCount: 9, launchCount: 11, lastUsedAt: at(19), source: 'fallback', share: 0.13 },
      { packageName: 'com.tencent.mtt', displayName: 'QQ浏览器', categoryName: '其他', foregroundSeconds: 960, sessionCount: 7, launchCount: 8, lastUsedAt: at(18), source: 'events', share: 0.08 },
    ],
    syncBatches: [], qualityIssueCount: 0,
  })
  return
}

/* MOCK-GAP-FIX */
if (path === '/api/v1/calendar/recycle-bin') {
  const mkRb = (id, type, title, book, delAt) => ({ id, type, title, deletedAt: delAt, bookName: book, start: at(9), end: at(10), source: 'manual', deletedByOperationId: null, deletedByOperationKind: null })
  const items = [
    mkRb('rb-1', 'event', '晨会', '工作', daysAgo(1)),
    mkRb('rb-2', 'task', '写 P1 周报', '收集箱', daysAgo(2)),
    mkRb('rb-3', 'calendar', '旧日历本', null, daysAgo(3)),
    mkRb('rb-4', 'task-book', '归档任务本', null, daysAgo(5)),
  ]
  ok(res, { items, page: 1, pageSize: 50, totalCount: items.length, totalPages: 1 })
  return
}
if (/recycle-bin\/[^/]+\/[^/]+\/restore-preview$/.test(path)) {
  ok(res, { targetType: 'event', targetId: 'rb-1', title: '晨会', restoreCount: 1, samples: [{ id: 'rb-1', type: 'event', title: '晨会', start: at(9), end: at(10), bookName: '工作' }], conflicts: [], canRestoreWithoutConflict: true })
  return
}
if (/recycle-bin\/[^/]+\/[^/]+\/restore$/.test(path)) {
  readBody(() => ok(res, { operation: 'calendar.recycle_bin.restore', operationId: 'op-rb', affectedCount: 1, affectedIds: ['rb-1'], samples: [], message: '已恢复' }))
  return
}
if (path === '/api/v1/pc/detail') {
  const mk = (i, recordType, app, title, keys, clicks) => ({
    recordType, start: new Date(Date.now() - i * 600000).toISOString(), end: new Date(Date.now() - i * 600000 + 300000).toISOString(),
    durationSeconds: 300, deviceId: 'ws-01', appName: app, displayName: app, categoryName: '开发', title,
    keyPresses: keys, totalClicks: clicks, mouseDistance: 1200, scrollDistance: 400, keyCounts: { A: 12, Enter: 3 },
    raw: { source: 'aw' }, url: null, domain: null, path: null, isLocalFile: false, browserAppName: null, browserWindowTitle: null,
    audible: null, incognito: null, tabCount: null, absorbedShortEventsCount: 0, absorbedDurationSeconds: 0,
    sourceWebEventIds: null, sourceWindowEventIds: [1, 2], categoryColor: '#2563EB', projectTag: null,
    classificationConfidence: 0.9, classificationSource: 'rule', classificationExplanation: '进程名匹配', bucketType: null,
    recordKey: 'k' + i, recordKeyVersion: 'v1', recordKeyStability: 'stable', sourceBucketIds: null, sourceType: 'aw', interpretationVersion: 'raw-aw-v1',
  })
  const items = [
    mk(1, 'window', 'Code.exe', 'main.tsx — client-web', 486, 42),
    mk(2, 'web-page', 'chrome.exe', 'GitHub · Pull requests', 120, 18),
    mk(3, 'input-minute', null, null, 90, 12),
    mk(4, 'afk', null, null, 0, 0),
    mk(5, 'window', 'Obsidian.exe', 'PIM 笔记', 210, 8),
  ]
  const eventType = url.searchParams.get('eventType')
  const filtered = eventType ? items.filter((i) => i.recordType === eventType) : items
  ok(res, { items: filtered, page: 1, pageSize: 50, totalCount: filtered.length, totalPages: 1 })
  return
}
if (path === '/api/v1/calendar/outlook/sync/batches') {
  const mkBatch = (i, status) => ({
    id: 'batch-' + i, provider: 'outlook', status,
    readCount: 120 - i * 10, createdCount: 12 - i, updatedCount: 8 - i, conflictCount: i === 1 ? 1 : 0, confirmationCount: 0, failureCount: status === 'failed' ? 2 : 0,
    steps: [{ name: 'fetch', status: 'completed', detail: null, at: daysAgo(i) }], errorSummary: status === 'failed' ? '令牌过期，需重新授权' : null,
    startedAt: daysAgo(i), finishedAt: daysAgo(i - 0.01), mode: 'normal', requestedWindowStart: null, requestedWindowEnd: null,
    perCalendarJson: null, cancelRequested: false,
  })
  const items = [mkBatch(0, 'completed'), mkBatch(1, 'completed-with-errors'), mkBatch(2, 'failed'), mkBatch(3, 'completed')]
  ok(res, { items, total: items.length, page: 1, pageSize: 20 })
  return
}
if (/outlook\/sync\/[^/]+\/cancel$/.test(path)) { readBody(() => ok(res, '已取消')); return }
if (path === '/api/v1/calendar/outlook/sync') { readBody((b) => ok(res, { id: 'batch-new', provider: 'outlook', status: 'running', readCount: 0, createdCount: 0, updatedCount: 0, conflictCount: 0, confirmationCount: 0, failureCount: 0, steps: [], errorSummary: null, startedAt: new Date().toISOString(), finishedAt: null, mode: b.mode ?? 'normal', requestedWindowStart: b.rangeStart ?? null, requestedWindowEnd: b.rangeEnd ?? null, perCalendarJson: null, cancelRequested: false })); return }
if (path === '/api/v1/calendar/outlook/check') { ok(res, { provider: 'outlook', tenantId: 'common', clientId: null, scopes: 'Calendars.ReadWrite', status: 'not-connected', tokenHealth: 'missing', lastSyncedAt: null, lastError: null, uiStatus: 'not-configured', activeAuthorization: null }); return }
if (path === '/api/v1/calendar/outlook/device-code') { ok(res, { id: 'auth-1', status: 'starting', verificationUri: 'https://microsoft.com/devicelogin', userCode: 'ABCD-1234', expiresAt: plusDays(0.05), accountDisplayName: null, accountLoginHint: null, errorCode: null, errorMessage: null, recoveryAction: null }); return }
if (/outlook\/device-code\/poll$/.test(path)) { ok(res, { id: 'auth-1', status: 'waiting-for-user', verificationUri: 'https://microsoft.com/devicelogin', userCode: 'ABCD-1234', expiresAt: plusDays(0.05), accountDisplayName: null, accountLoginHint: null, errorCode: null, errorMessage: null, recoveryAction: null }); return }
if (/outlook\/device-code\/[^/]+\/cancel$/.test(path)) { readBody(() => ok(res, '已取消')); return }
if (path === '/api/v1/calendar/outlook/local-data/preview') { ok(res, { bindingCount: 2, calendarCount: 3, eventCount: 128 }); return }
if (path === '/api/v1/calendar/outlook/local-data' && req.method === 'DELETE') { ok(res, '已清理'); return }
if (path === '/api/v1/calendar/outlook/disconnect') { readBody(() => ok(res, '已断开')); return }
if (path === '/api/v1/calendar/outlook/calendars/discover' || path === '/api/v1/calendar/outlook/calendars') {
  ok(res, [{ id: 'ob-1', pimCalendarId: 'cal-1', graphCalendarId: 'gcal-1', groupId: 'grp-1', groupName: '我的日历', name: '日历', color: '#3B82F6', ownerName: 'Demo', ownerAddress: 'demo@outlook.com', isDefault: true, canEdit: true, isSelected: true, remoteState: 'active', lastSyncedAt: at(9), lastError: null }])
  return
}
if (path === '/api/v1/calendar/outlook/calendars/selection') { readBody(() => ok(res, [{ id: 'ob-1', pimCalendarId: 'cal-1', graphCalendarId: 'gcal-1', groupId: null, groupName: null, name: '日历', color: null, ownerName: null, ownerAddress: null, isDefault: true, canEdit: true, isSelected: true, remoteState: 'active', lastSyncedAt: null, lastError: null }])); return }
if (/pc\/app-signatures$/.test(path) && req.method === 'POST') { readBody(() => ok(res, { id: 'sig-new' })); return }

/* MOCK-PLAN-REAL */
if (/\/calendar\/tasks\/[^/]+\/plan$/.test(path) && req.method === 'POST') {
  readBody((b) => {
    const taskId = path.split('/')[5]
    const task = TASKS.find((t) => t.id === taskId)
    const start = b.plannedStart
    const end = b.plannedEnd ?? (() => {
      // 无 plannedEnd 时按时长估算（与前端一致）
      const m = /^(\d+):(\d{2})(?::\d{2})?$/.exec(b.estimatedDuration ?? task?.estimatedDuration ?? '01:00:00')
      const minutes = m ? Number(m[1]) * 60 + Number(m[2]) : 60
      return new Date(new Date(start).getTime() + minutes * 60000).toISOString()
    })()
    // 幂等：同任务重复排期覆盖旧段
    const idx = PLANNED_SEGMENTS.findIndex((s) => s.taskId === taskId)
    const seg = {
      id: 'task-segment:' + taskId,
      layer: 'task-segments',
      objectType: 'task-segment',
      objectId: 'seg-' + taskId,
      taskId,
      title: task?.title ?? '任务',
      startsAt: start,
      endsAt: end,
      source: 'manual',
      status: 'planned',
      color: '#22C55E',
      requiresConfirmation: false,
    }
    if (idx >= 0) PLANNED_SEGMENTS[idx] = seg
    else PLANNED_SEGMENTS.push(seg)
    if (task) {
      task.isInbox = false
      task.dtStart = start
      task.plannedEnd = end
    }
    ok(res, task ?? { id: taskId })
  })
  return
}


/* MOCK-CRUD-REAL */
if (path === '/api/v1/calendar/events' && req.method === 'POST') {
  readBody((b) => {
    const id = 'e' + (++seq)
    const ev = {
      id, calendarId: b.calendarId ?? 'cal-1', uid: b.uid ?? (id + '@pim'),
      title: b.title ?? '未命名', description: b.description ?? null, location: b.location ?? null,
      dtStart: b.dtStart, dtEnd: b.dtEnd, rrule: b.rrule ?? null, status: 'CONFIRMED',
      source: 'manual', originalEventId: null, isAllDay: b.isAllDay ?? false,
      timeZoneId: b.timeZoneId ?? null, sourceTimeZoneId: null, sourceUid: null, recurrenceId: null,
      descriptionFormat: b.descriptionFormat ?? 'text', showAs: b.showAs ?? null,
      importance: b.importance ?? null, sensitivity: b.sensitivity ?? null,
      categories: b.categories ?? null, isReminderOn: b.isReminderOn ?? null,
      reminderMinutesBeforeStart: b.reminderMinutesBeforeStart ?? null,
      organizer: b.organizer ?? null, attendees: b.attendees ?? null,
      isOnlineMeeting: b.isOnlineMeeting ?? null, onlineMeetingProvider: b.onlineMeetingProvider ?? null,
      onlineMeetingUrl: b.onlineMeetingUrl ?? null, externalLink: b.externalLink ?? null,
      isSeriesMaster: Boolean(b.rrule), isException: false, seriesMasterId: null, isCancelled: false,
      outlookCalendarBindingId: null, outlookEventId: null, outlookEtag: null, outlookEventType: null,
    }
    EVENTS.push(ev)
    ok(res, ev)
  })
  return
}
if (/\/calendar\/events\/[^/]+$/.test(path) && req.method === 'PUT') {
  readBody((b) => {
    const id = path.split('/')[5]
    const idx = EVENTS.findIndex((e) => e.id === id)
    if (idx < 0) { json(res, 404, { code: 404, message: '事件不存在', data: null }); return }
    EVENTS[idx] = { ...EVENTS[idx], ...b, id }
    ok(res, EVENTS[idx])
  })
  return
}
if (/\/calendar\/events\/[^/]+$/.test(path) && req.method === 'DELETE') {
  const id = path.split('/')[5]
  EVENTS = EVENTS.filter((e) => e.id !== id)
  ok(res, '已删除')
  return
}
if (path === '/api/v1/calendar/events/batch-delete') {
  readBody((b) => {
    const ids = b.ids ?? []
    const before = EVENTS.length
    EVENTS = EVENTS.filter((e) => !ids.includes(e.id))
    ok(res, { operation: 'calendar.events.batch_delete', operationId: 'op-bd', affectedCount: before - EVENTS.length, affectedIds: ids, samples: [], message: `已删除 ${before - EVENTS.length} 个日程` })
  })
  return
}
if (path === '/api/v1/calendar/tasks' && req.method === 'POST') {
  readBody((b) => {
    const id = 't' + (++seq)
    const task = {
      id, calendarId: b.calendarId ?? null, uid: id + '@pim',
      title: b.title ?? '未命名', description: b.description ?? null,
      priority: b.priority ?? 5, estimatedDuration: b.estimatedDuration ?? null,
      minimumSegment: b.minimumSegment ?? null, dtStart: b.dtStart ?? null, due: b.due ?? null,
      status: b.status ?? 'NEEDS-ACTION', isInbox: !b.calendarId && !b.dtStart, sortOrder: TASKS.length + 1,
      subTasks: [], plannedEnd: b.plannedEnd ?? null, taskBookId: b.taskBookId ?? null,
      percentComplete: b.percentComplete ?? 0,
    }
    TASKS.push(task)
    ok(res, task)
  })
  return
}
if (/\/calendar\/tasks\/[^/]+$/.test(path) && req.method === 'DELETE') {
  const id = path.split('/')[5]
  TASKS = TASKS.filter((t) => t.id !== id)
  ok(res, { operation: 'calendar.tasks.delete', operationId: 'op-td', affectedCount: 1, affectedIds: [id], samples: [], message: '已删除' })
  return
}
if (path === '/api/v1/calendar/tasks/batch-delete') {
  readBody((b) => {
    const ids = b.ids ?? []
    const before = TASKS.length
    TASKS = TASKS.filter((t) => !ids.includes(t.id))
    ok(res, { operation: 'calendar.tasks.batch_delete', operationId: 'op-tbd', affectedCount: before - TASKS.length, affectedIds: ids, samples: [], message: `已删除 ${before - TASKS.length} 个任务` })
  })
  return
}
if (path === '/api/v1/calendar/tasks/{id}/checklist'.replace('{id}', path.split('/')[5] ?? '') && req.method === 'POST') {
  readBody((b) => ok(res, { id: 'chk-' + (++seq), taskId: path.split('/')[5], title: b.title ?? '', isDone: false, sortOrder: b.sortOrder ?? 0 }))
  return
}
if (/\/calendar\/tasks\/[^/]+\/checklist\/[^/]+$/.test(path) && req.method === 'PUT') {
  readBody((b) => ok(res, { id: path.split('/')[7], taskId: path.split('/')[5], title: b.title ?? '', isDone: b.isDone ?? false, sortOrder: 0 }))
  return
}
if (/\/calendar\/tasks\/[^/]+\/checklist\/[^/]+$/.test(path) && req.method === 'DELETE') {
  ok(res, path.split('/')[5])
  return
}
if (/\/calendar\/tasks\/[^/]+\/segments$/.test(path) && req.method === 'POST') {
  readBody((b) => ok(res, { id: 'seg-' + (++seq), taskId: path.split('/')[5], taskTitle: '', startsAt: b.startsAt, endsAt: b.endsAt, status: b.status ?? 'planned', source: b.source ?? 'manual', planningReason: b.planningReason ?? null, confirmationId: null }))
  return
}
if (path === '/api/v1/calendar/calendars' && req.method === 'POST') {
  readBody((b) => {
    const book = { id: 'cal-' + (++seq), name: b.name ?? '新日历本', color: b.color ?? '#3B82F6', kind: b.kind ?? 'calendar', isDefault: false, eventCount: 0, source: 'manual', outlookCalendarBindingId: null, canEdit: true }
    CAL_BOOKS.push(book)
    ok(res, book)
  })
  return
}
if (/\/calendar\/calendars\/[^/]+$/.test(path) && req.method === 'PUT') {
  readBody((b) => {
    const id = path.split('/')[5]
    const idx = CAL_BOOKS.findIndex((c) => c.id === id)
    if (idx >= 0) CAL_BOOKS[idx] = { ...CAL_BOOKS[idx], ...(b.name ? { name: b.name } : {}), ...(b.color ? { color: b.color } : {}) }
    ok(res, CAL_BOOKS[idx] ?? null)
  })
  return
}
if (/\/calendar\/calendars\/[^/]+$/.test(path) && req.method === 'DELETE') {
  const id = path.split('/')[5]
  CAL_BOOKS = CAL_BOOKS.filter((c) => c.id !== id)
  EVENTS = EVENTS.filter((e) => e.calendarId !== id)
  TASKS = TASKS.filter((t) => t.calendarId !== id)
  ok(res, { operation: 'calendar.calendars.delete', operationId: 'op-cd', affectedCount: 1, affectedIds: [id], samples: [], message: '已删除' })
  return
}

/* MOCK-AUDIT-FIX */
if (/mobile\/analytics\/sessions\/[^/]+\/events$/.test(path)) {
  ok(res, [{ id: 'ev-1', sessionId: 's1', deviceId: 'pixel-7', packageName: 'com.tencent.mm', eventType: 'ACTIVITY_RESUMED', eventTimeUtc: iso(21), className: 'MainActivity', rawJson: '{}' }, { id: 'ev-2', sessionId: 's1', deviceId: 'pixel-7', packageName: 'com.tencent.mm', eventType: 'ACTIVITY_PAUSED', eventTimeUtc: iso(21, 40), className: 'MainActivity', rawJson: '{}' }])
  return
}
if (/mobile\/devices\/[^/]+\/liveness\/events$/.test(path)) {
  ok(res, { items: [{ id: 'le-1', eventType: 'heartbeat', eventTypeLabel: '心搏', occurredAtUtc: at(10), reason: null, reasonLabel: null, inference: null, importance: 0, pssKb: 12000, rssKb: 45000, description: '定时心跳', payloadJson: '{}' }, { id: 'le-2', eventType: 'process-exit', eventTypeLabel: '进程退出', occurredAtUtc: at(3), reason: 'reboot', reasonLabel: '设备重启', inference: '系统启动时间晚于退出时间', importance: 100, pssKb: null, rssKb: null, description: null, payloadJson: '{}' }], page: 1, pageSize: 50, totalCount: 2, totalPages: 1 })
  return
}
if (path === '/api/v1/pc/aggregation/focus-blocks') {
  ok(res, { items: [{ startUtc: iso(9), endUtc: iso(11), startLocal: '09:00', endLocal: '11:00', durationMinutes: 120, mainApp: 'VS Code', topApps: [{ name: 'VS Code', minutes: 96 }, { name: 'Chrome', minutes: 24 }] }, { startUtc: iso(14), endUtc: iso(16), startLocal: '14:00', endLocal: '16:00', durationMinutes: 120, mainApp: 'Obsidian', topApps: [{ name: 'Obsidian', minutes: 80 }] }] })
  return
}
if (path === '/api/v1/pc/aggregation/late-night') {
  ok(res, { items: Array.from({ length: 7 }, (_, i) => ({ date: daysAgo(6 - i).slice(0, 10), minutes: Math.round(Math.random() * 60), hadActivity: true })) })
  return
}
if (path === '/api/v1/calendar/schedule') {
  readBody((b) => {
    const ids = (b.taskIds ?? []).slice(0, 5)
    const mkSlots = (stepH) => ids.map((id, i) => ({ taskId: id, title: (TASKS.find((t) => t.id === id) ?? {}).title ?? ('任务' + i), start: at(9 + i * stepH), end: at(10 + i * stepH) }))
    ok(res, [
      { algorithmName: 'greedy', slots: mkSlots(2), metrics: { tasks_scheduled: ids.length, total_tasks: ids.length } },
      { algorithmName: 'csp', slots: mkSlots(3), metrics: { tasks_scheduled: ids.length } },
    ])
  })
  return
}
if (/\/calendar\/tasks\/[^/]+$/.test(path) && req.method === 'PUT') {
  readBody((b) => ok(res, { ...(TASKS.find((t) => t.id === path.split('/')[5]) ?? TASKS[0]), ...b }))
  return
}

  if (req.method === 'POST' || req.method === 'PUT' || req.method === 'DELETE') {
    // 通用写操作回显成功
    seq++
    if (req.method === 'POST' && path === '/api/v1/calendar/events') { readBody((b) => ok(res, { ...EVENTS[0], id: `e${seq}`, ...b })); return }
    if (req.method === 'POST' && path === '/api/v1/calendar/tasks') { readBody((b) => ok(res, { ...TASKS[0], id: `t${seq}`, ...b })); return }
    if (req.method === 'DELETE') { ok(res, '已删除'); return }
    readBody((b) => ok(res, b))
    return
  }


if (path === '/api/v1/mobile/devices') {
  ok(res, [
    { id: 'md-1', deviceId: 'pixel-7', androidIdHash: 'hash-a', displayName: 'Pixel 7', manufacturer: 'Google', brand: 'Google', model: 'Pixel 7', androidVersion: 'Android 15', sdkInt: 35, appVersion: '1.4.0', metadataJson: '{"deviceKind":"phone"}', firstSeenAt: daysAgo(30), lastSeenAt: new Date().toISOString(), lastHeartbeatAt: null, lastSyncAt: null, isActive: true, deviceHash: 'hash-a', osVersion: 'Android 15', apiLevel: 35 },
    { id: 'md-2', deviceId: 'mi-pad', androidIdHash: 'hash-b', displayName: '小米平板', manufacturer: 'Xiaomi', brand: 'Xiaomi', model: 'Pad 6', androidVersion: 'Android 14', sdkInt: 34, appVersion: '1.3.2', metadataJson: '{"deviceKind":"tablet","smallestScreenWidthDp":800}', firstSeenAt: daysAgo(60), lastSeenAt: daysAgo(0.2), lastHeartbeatAt: null, lastSyncAt: null, isActive: true, deviceHash: 'hash-b', osVersion: 'Android 14', apiLevel: 34 },
  ])
  return
}

  json(res, 404, { code: 404, message: `接口不存在: ${path}`, data: null, timestamp: new Date().toISOString() })
})

server.listen(MOCK_PORT, MOCK_HOST, () => {
  const addr = server.address()
  const port = addr && typeof addr === 'object' ? addr.port : MOCK_PORT
  console.log(`mock api on http://${MOCK_HOST}:${port}`)
})
