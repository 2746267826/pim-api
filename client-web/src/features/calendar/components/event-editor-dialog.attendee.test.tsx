// @vitest-environment jsdom
/*
 * 日程编辑弹窗：参会人 email 门禁 + organizer 缺键。
 *
 * 契约依据（contract/openapi.json @ pim-api 940fe42f）：
 *   - CreateEventRequest / UpdateEventRequest 的 attendees[] 为 EventAttendeeDto，
 *     其 email 为 required 且非 nullable；name 可空。
 *   - 参会人只带姓名时，现实现 `email: a.email || undefined` 经 JSON.stringify 会丢键，
 *     发出缺必填字段的报文体（A-preflight §5.2 认定的形状不符）。
 *
 * 本文件已确认的产品行为要求（不是本测试自拟的假设）：
 *   活动参会人（姓名或邮箱任一非空）缺邮箱时，**阻止提交并提示补邮箱**；
 *   不得静默过滤该行，不得照旧发违规报文体；**任何一行不合法就整体不提交**，
 *   逐行检查（不能只看首行或末行），纠正后所有合法行都要原样发出。
 *
 * 全空占位行沿用既有忽略行为（submit 里 filter(a => a.name || a.email)），本次不改产品语义，
 * 只用正对照把它钉住。
 *
 * organizer：pim-api 侧 EventPersonDto? 可空，NormalizeOrganizer(null)→null，
 * System.Text.Json 把「缺键」与「显式 null」绑定为同一个 null，服务端可观察状态相同 →
 * 属工程等价（非产品决定）：未填组织者时**不发 organizer 键**。
 *
 * A2 测试源码约束：本文件不含 as any / as unknown as / @ts-ignore，不放宽 tsconfig。
 * 服务端线缆 null → 本地模型缺键的收窄集中在 serverAttendees() 一个显式边界里（见其注释）。
 *
 * 本文件只含测试，不含任何产品实现。
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { cleanup, fireEvent, render, screen } from '@testing-library/react'

import type { CalendarBook, EventPerson, EventResponse } from '../types'
import { EventEditorDialog } from './event-editor-dialog'

/** useSaveEvent 的入参形状，逐字段对齐 queries.ts 里的 useMutation input（受约束而非 any） */
type SaveEventInput = {
  id?: string
  body: Record<string, unknown>
  scope?: { scope?: 'this' | 'series'; recurrenceId?: string; originalEventId?: string }
}

const { saveMock, removeMock } = vi.hoisted(() => ({
  saveMock: vi.fn<(input: SaveEventInput) => Promise<void>>(),
  removeMock: vi.fn<(input: { id: string }) => Promise<void>>(),
}))

/* 只替换 mutation hook：本文件要断言的是「弹窗有没有发起保存」，不重复验证 hook 内部的
 * HTTP 拼装（calendar-api.test.ts 已覆盖 method/path/body 形状）。 */
vi.mock('../queries', () => ({
  useSaveEvent: () => ({ mutateAsync: saveMock, isPending: false }),
  useDeleteEvent: () => ({ mutateAsync: removeMock, isPending: false }),
}))

const CALENDARS: CalendarBook[] = [
  {
    id: 'cal-1',
    name: '默认日历',
    color: '#5b8def',
    kind: 'calendar',
    isDefault: true,
    eventCount: 0,
    source: 'local',
    outlookCalendarBindingId: null,
    canEdit: true,
    /* 工单 04 新增字段：日历本默认静音（本文件不验证静音行为，取默认值即可） */
    defaultMuted: false,
  },
]

/* 弹窗的 useEffect 依赖 initial/event/calendars 引用，必须稳定，否则 form.reset 会反复冲掉刚输入的内容 */
const NEW_EVENT_SLOT = { start: new Date('2026-10-12T02:00:00Z'), end: new Date('2026-10-12T03:00:00Z') }

function baseServerEvent(overrides: Partial<EventResponse> = {}): EventResponse {
  return {
    id: 'evt-1',
    calendarId: 'cal-1',
    uid: 'uid-1',
    title: '周会',
    description: null,
    location: null,
    dtStart: '2026-10-12T02:00:00Z',
    dtEnd: '2026-10-12T03:00:00Z',
    rRule: null,
    status: 'confirmed',
    source: 'local',
    originalEventId: null,
    isAllDay: false,
    timeZoneId: null,
    sourceTimeZoneId: null,
    sourceUid: null,
    recurrenceId: null,
    descriptionFormat: null,
    showAs: null,
    importance: null,
    sensitivity: null,
    categories: null,
    isReminderOn: null,
    reminderMinutesBeforeStart: null,
    organizer: null,
    attendees: null,
    isOnlineMeeting: null,
    onlineMeetingProvider: null,
    onlineMeetingUrl: null,
    externalLink: null,
    isSeriesMaster: false,
    isException: false,
    seriesMasterId: null,
    isCancelled: false,
    outlookCalendarBindingId: null,
    outlookEventId: null,
    outlookEtag: null,
    outlookEventType: null,
    /* 工单 04 新增字段：本文件不验证静音行为，取「未指定」即可 */
    isMuted: null,
    ...overrides,
  }
}

/**
 * 服务端线缆形状：pim-api 的 EventFieldValidator 只校验 attendees[].type、不校验 email，
 * 历史数据里确实会回吐 email:null；而本地手写模型 EventPerson 声明的是 email?:string。
 *
 * 这里用一个**受约束的显式边界**代替类型断言（不使用 as any / as unknown as / @ts-ignore，
 * 也不放宽 tsconfig）：夹具以线缆形状声明（satisfies 静态约束，email 的字面量 null 被保留），
 * 再由下面的适配器归一成本地模型形状。适配器是纯函数、无断言，只有「null → 缺键」一处映射。
 *
 * 为什么这一步不弱化被测行为：弹窗读参会人一律是 `a.name ?? ''` / `a.email ?? ''`
 * （event-editor-dialog.tsx:157），null 与缺键在该读取处完全不可区分；本文件对
 * 「email 为 null 的历史行」的全部断言都建立在「邮箱输入框读到空串」这一可观察事实上，
 * 而适配器恰好把线缆 null 归到同一个空串入口。归一方向与产品语义一致（补邮箱后放行）。
 */
type ServerEventPerson = { name: string | null; email: string | null; type: string | null }

function serverAttendees(wire: readonly ServerEventPerson[]): EventPerson[] {
  return wire.map((person) => ({
    ...(person.name == null ? {} : { name: person.name }),
    ...(person.email == null ? {} : { email: person.email }),
    ...(person.type == null ? {} : { type: person.type }),
  }))
}

/** 唯一的线缆行夹具：email 的 null 是字面量，满足上面的受约束形状 */
const WIRE_NO_EMAIL = { name: '李四', email: null, type: 'required' } satisfies ServerEventPerson

let fetchMock: ReturnType<typeof vi.fn>
let openChangeMock: ReturnType<typeof vi.fn<(open: boolean) => void>>

beforeEach(() => {
  saveMock.mockReset()
  removeMock.mockReset()
  openChangeMock = vi.fn<(open: boolean) => void>()
  fetchMock = vi.fn(
    async () => new Response(JSON.stringify({ code: 0, message: 'ok', data: null }), { status: 200 }),
  )
  vi.stubGlobal('fetch', fetchMock)
})

afterEach(() => {
  cleanup()
  vi.unstubAllGlobals()
})

/**
 * 排空 submit 的异步链（zodResolver 校验是 Promise）后再断言。
 * 不用 waitFor：它会在第一次轮询就 resolve——此时提交链还没跑完，负向断言会出现假绿。
 * settle() 的充分性由同文件正对照（合法邮箱必须已被观察到调用）证明。
 */
async function settle(): Promise<void> {
  await new Promise((r) => setTimeout(r, 0))
  await new Promise((r) => setTimeout(r, 0))
}

/**
 * 查找「提示补邮箱」的可见告警文案。
 *
 * 弹窗既有约定：提交期错误走 InlineAlert（tone=crit→role="alert"，
 * tone=warn/info→role="status"），见 event-editor-dialog.tsx 的 setError/conflict 分支。
 * 这里接受这两种角色，以免把提示的措辞与色阶写死；「更多设置」里的静态 Label
 * （如「组织者邮箱」）不是 alert/status，不会被误当成提示。
 *
 * @returns 命中的提示文案；没有任何指向邮箱的告警时返回 null
 */
function attendeeEmailPrompt(): string | null {
  const dialog = screen.getByRole('dialog')
  const nodes = dialog.querySelectorAll('[role="alert"], [role="status"]')
  for (const node of nodes) {
    const text = node.textContent ?? ''
    if (text.includes('邮箱')) return text
  }
  return null
}

/** DOM 查询全部走 @testing-library 的泛型参数收窄，不用 `as HTMLInputElement` 断言 */
function titleInput(): HTMLInputElement {
  return screen.getByLabelText<HTMLInputElement>(/^标题/)
}

function attendeeNameInput(): HTMLInputElement {
  return screen.getByPlaceholderText<HTMLInputElement>('姓名')
}

function attendeeEmailInput(): HTMLInputElement {
  return screen.getByPlaceholderText<HTMLInputElement>('邮箱')
}

/** 多行场景用：按行序取全部参会人输入框（同名 placeholder 用 getAllBy*） */
function attendeeNameInputs(): HTMLInputElement[] {
  return screen.getAllByPlaceholderText<HTMLInputElement>('姓名')
}

function attendeeEmailInputs(): HTMLInputElement[] {
  return screen.getAllByPlaceholderText<HTMLInputElement>('邮箱')
}

/** 弹窗读参会人用的是 `a.name ?? ''`（event-editor-dialog.tsx:157），故输入框值即归一后结果 */
function attendeeNameValues(): string[] {
  return attendeeNameInputs().map((input) => input.value)
}

function attendeeEmailValues(): string[] {
  return attendeeEmailInputs().map((input) => input.value)
}

/** 按 id 取输入框：用 instanceof 运行时收窄，不用 `as HTMLInputElement` 断言 */
function inputById(id: string): HTMLInputElement {
  const element = document.getElementById(id)
  if (!(element instanceof HTMLInputElement)) throw new Error(`未找到输入框 #${id}`)
  return element
}

function openMoreSettings(): void {
  fireEvent.click(screen.getByRole('button', { name: /提醒、闲忙、会议、参会人/ }))
}

function addAttendeeRow(): void {
  fireEvent.click(screen.getByRole('button', { name: '添加' }))
}

function clickSave(): void {
  fireEvent.click(screen.getByRole('button', { name: '保存' }))
}

function renderNewDialog() {
  return render(
    <EventEditorDialog open onOpenChange={openChangeMock} calendars={CALENDARS} initial={NEW_EVENT_SLOT} event={null} />,
  )
}

function renderEditDialog(event: EventResponse) {
  return render(<EventEditorDialog open onOpenChange={openChangeMock} calendars={CALENDARS} event={event} />)
}

describe('日程参会人：缺邮箱必须阻止提交并提示补邮箱', () => {
  it('手填只有姓名没有邮箱：不发保存请求、不关弹窗，并提示补邮箱', async () => {
    renderNewDialog()
    fireEvent.change(titleInput(), { target: { value: '周会' } })
    openMoreSettings()
    addAttendeeRow()
    fireEvent.change(attendeeNameInput(), { target: { value: '张三' } })

    clickSave()
    await settle()

    // 先断「没有发保存请求」：这是本工单的核心缺陷事实
    expect(saveMock).not.toHaveBeenCalled()
    expect(fetchMock).not.toHaveBeenCalled()
    // 保存被拦下，弹窗不得关闭
    expect(openChangeMock).not.toHaveBeenCalledWith(false)
    // 提示必须指向「邮箱」
    expect(attendeeEmailPrompt()).not.toBeNull()
    // 不得静默丢行：提示后该行仍在、姓名不丢
    expect(attendeeNameInput().value).toBe('张三')
  })

  it('载入服务端 email 为 null 的参会人后保存：不发保存请求、不关弹窗，并提示补邮箱', async () => {
    renderEditDialog(baseServerEvent({ attendees: serverAttendees([WIRE_NO_EMAIL]) }))
    // 编辑态带参会人时「更多设置」自动展开；线缆 null 在弹窗里读到的是空邮箱框
    expect(attendeeNameInput().value).toBe('李四')
    expect(attendeeEmailInput().value).toBe('')

    clickSave()
    await settle()

    expect(saveMock).not.toHaveBeenCalled()
    expect(fetchMock).not.toHaveBeenCalled()
    expect(openChangeMock).not.toHaveBeenCalledWith(false)
    expect(attendeeEmailPrompt()).not.toBeNull()
    expect(attendeeNameInput().value).toBe('李四')
  })
})

describe('逐行门禁：合法行与缺邮箱行混合时，不得只检查首行/末行，也不得静默过滤', () => {
  it('新建：合法行 + 缺邮箱行 + 空占位行 → 阻止提交并提示；补齐邮箱后两条合法行都发出', async () => {
    renderNewDialog()
    fireEvent.change(titleInput(), { target: { value: '周会' } })
    openMoreSettings()
    addAttendeeRow()
    addAttendeeRow()
    addAttendeeRow()

    // 缺邮箱行放在中间：只查首行的实现会漏放行；空占位行沿用既有忽略语义
    fireEvent.change(attendeeNameInputs()[0], { target: { value: '张三' } })
    fireEvent.change(attendeeEmailInputs()[0], { target: { value: 'zhangsan@example.com' } })
    fireEvent.change(attendeeNameInputs()[1], { target: { value: '李四' } })

    clickSave()
    await settle()

    // 任一行不合法就整体不提交：既不发保存，也不关弹窗
    expect(saveMock).not.toHaveBeenCalled()
    expect(fetchMock).not.toHaveBeenCalled()
    expect(openChangeMock).not.toHaveBeenCalledWith(false)
    expect(attendeeEmailPrompt()).not.toBeNull()
    // 三行都还在，姓名/邮箱原值都不丢（不得静默过滤合法行或缺邮箱行）
    expect(attendeeNameValues()).toEqual(['张三', '李四', ''])
    expect(attendeeEmailValues()).toEqual(['zhangsan@example.com', '', ''])

    // 补齐中间行的邮箱后才放行，且两条合法行都发出（不是只发第一条）
    fireEvent.change(attendeeEmailInputs()[1], { target: { value: 'lisi@example.com' } })
    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(body.attendees).toEqual([
      { name: '张三', email: 'zhangsan@example.com', type: 'required' },
      { name: '李四', email: 'lisi@example.com', type: 'required' },
    ])
    expect(openChangeMock).toHaveBeenCalledWith(false)
    expect(attendeeEmailPrompt()).toBeNull()
  })

  it('历史回写：email 为 null 的行 + 合法行 → 阻止提交并提示；补齐后两行都按原顺序原类型发出', async () => {
    renderEditDialog(
      baseServerEvent({
        attendees: serverAttendees([
          WIRE_NO_EMAIL,
          { name: '张三', email: 'zhangsan@example.com', type: 'optional' },
        ]),
      }),
    )

    // 缺邮箱行放在首位：只查末行的实现会漏放行
    expect(attendeeNameValues()).toEqual(['李四', '张三'])
    expect(attendeeEmailValues()).toEqual(['', 'zhangsan@example.com'])

    clickSave()
    await settle()

    expect(saveMock).not.toHaveBeenCalled()
    expect(fetchMock).not.toHaveBeenCalled()
    expect(openChangeMock).not.toHaveBeenCalledWith(false)
    expect(attendeeEmailPrompt()).not.toBeNull()
    expect(attendeeNameValues()).toEqual(['李四', '张三'])
    expect(attendeeEmailValues()).toEqual(['', 'zhangsan@example.com'])

    fireEvent.change(attendeeEmailInputs()[0], { target: { value: 'lisi@example.com' } })
    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    // 顺序与 type 都必须照旧：门禁只拦提交，不得改写行内容
    expect(body.attendees).toEqual([
      { name: '李四', email: 'lisi@example.com', type: 'required' },
      { name: '张三', email: 'zhangsan@example.com', type: 'optional' },
    ])
    expect(openChangeMock).toHaveBeenCalledWith(false)
    expect(attendeeEmailPrompt()).toBeNull()
  })
})

describe('正对照：合法与既有路径不受影响（防止把门禁写成一刀切）', () => {
  it('合法邮箱的参会人保存成功并保留姓名', async () => {
    renderNewDialog()
    fireEvent.change(titleInput(), { target: { value: '周会' } })
    openMoreSettings()
    addAttendeeRow()
    fireEvent.change(attendeeNameInput(), { target: { value: '张三' } })
    fireEvent.change(attendeeEmailInput(), { target: { value: 'zhangsan@example.com' } })

    clickSave()
    await settle()

    // 本用例同时证明 settle() 的观察窗口足够长（否则这里会失败）
    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(body.attendees).toEqual([{ name: '张三', email: 'zhangsan@example.com', type: 'required' }])
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('全空占位行沿用既有忽略行为：不提示、attendees 上行为空数组', async () => {
    renderNewDialog()
    fireEvent.change(titleInput(), { target: { value: '周会' } })
    openMoreSettings()
    addAttendeeRow()
    addAttendeeRow()

    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(body.attendees).toEqual([])
    expect(screen.queryByRole('alert')).toBeNull()
  })

  it('服务端 email 为 null 的参会人补上邮箱后可保存成功（恢复路径）', async () => {
    renderEditDialog(
      baseServerEvent({ attendees: serverAttendees([WIRE_NO_EMAIL]) }),
    )
    fireEvent.change(attendeeEmailInput(), { target: { value: 'lisi@example.com' } })

    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(body.attendees).toEqual([{ name: '李四', email: 'lisi@example.com', type: 'required' }])
    expect(screen.queryByRole('alert')).toBeNull()
  })
})

describe('组织者：未填时保存请求不含 organizer 键（工程等价，非产品决定）', () => {
  it('未填组织者：报文体不含 organizer 键', async () => {
    renderNewDialog()
    fireEvent.change(titleInput(), { target: { value: '周会' } })

    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(Object.keys(body)).not.toContain('organizer')
    // 非空断言：避免「什么都没发」也算通过
    expect(body.title).toBe('周会')
  })

  it('填了组织者姓名与邮箱：仍照旧发送 organizer 对象', async () => {
    renderNewDialog()
    fireEvent.change(titleInput(), { target: { value: '周会' } })
    openMoreSettings()
    fireEvent.change(inputById('event-organizer-name'), { target: { value: '王五' } })
    fireEvent.change(inputById('event-organizer-email'), { target: { value: 'wangwu@example.com' } })

    clickSave()
    await settle()

    expect(saveMock).toHaveBeenCalledTimes(1)
    const body = saveMock.mock.calls[0][0].body
    expect(body.organizer).toEqual({ name: '王五', email: 'wangwu@example.com' })
  })
})
