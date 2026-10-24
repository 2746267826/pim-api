import { useState } from 'react'
import { Archive, Pencil, Plus, Repeat, Trash2 } from 'lucide-react'
import { useArchiveHabit, useCreateHabit, useDeleteHabit, useHabits, useUpdateHabit } from '../queries'
import type { HabitRoutine } from '../types'
import { notifySuccess } from '@/lib/notify'
import { CADENCE_LABEL } from '@/lib/enums'
import { Stagger, StaggerItem } from '@/components/motion/primitives'
import { Button, Card, CardTitle, Dialog, DialogBody, DialogFooter, DialogHeader, DialogContent, EmptyState, Input, Label, PageHeader, Segmented, Select, StatusBadge, TwoStepButton } from '@/components/ui'

type Tab = 'active' | 'planned' | 'archived'

const CADENCE_OPTIONS = [
  { value: 'Daily', label: CADENCE_LABEL.Daily },
  { value: 'Weekly', label: CADENCE_LABEL.Weekly },
  { value: 'Monthly', label: CADENCE_LABEL.Monthly },
]

/** 习惯页（02 §habits：执行中/规划/归档三标签 + 规则创建 + 只读规则卡） */
export function HabitsPage() {
  const [tab, setTab] = useState<Tab>('active')
  const { data: habits = [] } = useHabits()
  const create = useCreateHabit()
  const update = useUpdateHabit()
  const archive = useArchiveHabit()
  const remove = useDeleteHabit()
  const [title, setTitle] = useState('')
  const [cadence, setCadence] = useState('Daily')
  /* 编辑弹窗状态（null=关闭） */
  const [editing, setEditing] = useState<HabitRoutine | null>(null)
  const [editTitle, setEditTitle] = useState('')
  const [editDesc, setEditDesc] = useState('')
  const [editCadence, setEditCadence] = useState('Daily')

  function openEdit(h: HabitRoutine) {
    setEditing(h)
    setEditTitle(h.title)
    setEditDesc(h.description ?? '')
    setEditCadence(h.cadence)
  }

  function saveEdit() {
    if (!editing) return
    const t = editTitle.trim()
    if (!t) return
    update.mutate(
      {
        id: editing.id,
        // 部分更新：三项都发；description 空串=显式清空（契约 §2.3）
        body: { title: t, description: editDesc.trim(), cadence: editCadence },
      },
      {
        onSuccess: () => {
          notifySuccess('习惯已更新')
          setEditing(null)
        },
      },
    )
  }

  const filtered = habits.filter((h) =>
    tab === 'archived' ? h.status === 'Archived' : tab === 'active' ? h.status === 'Active' : h.source !== 'manual',
  )

  return (
    <div>
      <PageHeader
        title="习惯"
        subtitle="习惯规则中心（完成历史与日历投射为占位，由后端规则引擎生成）"
        actions={<Segmented value={tab} onValueChange={setTab} options={[
          { value: 'active', label: '执行中' },
          { value: 'planned', label: '规划' },
          { value: 'archived', label: '归档' },
        ]} />}
      />

      <div className="grid grid-cols-1 gap-4 xl:grid-cols-[380px_minmax(0,1fr)]">
        {/* 创建表单 */}
        <Card className="h-fit p-4">
          <CardTitle>创建习惯规则</CardTitle>
          <p className="mt-1 text-xs text-text-3">时间段规则将生成日历图层占位（由后端规则引擎投射）。</p>
          <form
            className="mt-3 space-y-3"
            onSubmit={async (e) => {
              e.preventDefault()
              const t = title.trim()
              if (!t) return
              await create.mutateAsync({ title: t, cadence })
              notifySuccess('习惯已创建')
              setTitle('')
            }}
          >
            <div>
              <Label htmlFor="habit-title">标题</Label>
              <Input id="habit-title" placeholder="如：每天阅读 30 分钟" value={title} onChange={(e) => setTitle(e.target.value)} />
            </div>
            <div>
              <Label htmlFor="habit-cadence">频率</Label>
              <Select id="habit-cadence" value={cadence} onValueChange={setCadence} options={CADENCE_OPTIONS} />
            </div>
            <Button type="submit" variant="primary" className="w-full" loading={create.isPending}>
              <Plus className="size-4" aria-hidden /> 创建习惯
            </Button>
          </form>
        </Card>

        {/* 规则卡列表 */}
        <div className="space-y-3">
          {filtered.length === 0 ? (
            <Card>
              <EmptyState icon={Repeat} title="暂无习惯规则" description="左侧创建第一条习惯。" />
            </Card>
          ) : (
            <Stagger className="space-y-3">
              {filtered.map((h, i) => {
                const archived = h.status.toLowerCase() === 'archived'
                return (
                  <StaggerItem key={h.id} index={i}>
                    <Card className="flex items-start gap-3 p-4">
                      <span className="grid size-9 shrink-0 place-items-center rounded-ctl bg-primary-soft text-primary">
                        <Repeat className="size-4" aria-hidden />
                      </span>
                      <div className="min-w-0 flex-1">
                        <div className="truncate text-sm font-medium text-text-1">{h.title}</div>
                        {h.description && <div className="mt-0.5 line-clamp-2 text-xs text-text-3">{h.description}</div>}
                        <div className="mt-0.5 text-xs text-text-4">
                          {CADENCE_LABEL[h.cadence] ?? h.cadence} · 来源 {h.source}
                        </div>
                      </div>
                      <div className="flex shrink-0 flex-col items-end gap-1.5">
                        <StatusBadge tone={archived ? 'neutral' : 'ok'} dot={false}>
                          {archived ? '已归档' : '执行中'}
                        </StatusBadge>
                        <div className="flex items-center gap-1">
                          <Button variant="ghost" size="sm" onClick={() => openEdit(h)} title="编辑">
                            <Pencil className="size-3.5" aria-hidden /> 编辑
                          </Button>
                          {archived ? (
                            /* 无独立恢复端点：PUT 部分更新把 status 设回 Active */
                            <Button
                              variant="secondary"
                              size="sm"
                              loading={update.isPending}
                              onClick={() =>
                                update.mutate(
                                  { id: h.id, body: { status: 'Active' } },
                                  { onSuccess: () => notifySuccess('已恢复执行') },
                                )
                              }
                            >
                              恢复执行
                            </Button>
                          ) : (
                            <Button
                              variant="ghost"
                              size="sm"
                              loading={archive.isPending}
                              onClick={() =>
                                archive.mutate(h.id, {
                                  onSuccess: () => notifySuccess('已归档（历史投射保留，日历图层移除）'),
                                })
                              }
                              title="归档：历史 occurrence 保留，日历图层移除"
                            >
                              <Archive className="size-3.5" aria-hidden /> 归档
                            </Button>
                          )}
                          {/* 软删除会连历史 occurrence 一并移除且不可逆——两步武装确认 */}
                          <TwoStepButton
                            armLabel="确认删除？"
                            className="px-2 text-text-4 hover:text-crit"
                            onConfirm={() =>
                              remove.mutate(h.id, {
                                onSuccess: () => notifySuccess('已删除（含历史投射）'),
                              })
                            }
                          >
                            <Trash2 className="size-3.5" aria-hidden />
                          </TwoStepButton>
                        </div>
                      </div>
                    </Card>
                  </StaggerItem>
                )
              })}
            </Stagger>
          )}
          <Card className="p-4">
            <CardTitle className="text-[13px]">完成历史</CardTitle>
            <p className="mt-1 text-xs text-text-4">占位面板：完成历史将由习惯发生记录聚合（后端规划中）。</p>
          </Card>
        </div>
      </div>

      {/* 编辑习惯（PUT 部分更新：title/description/cadence 三项全发；描述空串=清空） */}
      <Dialog open={editing != null} onOpenChange={(o) => !o && setEditing(null)}>
        <DialogContent className="max-w-[420px]">
          <DialogHeader title="编辑习惯" description="部分更新：只提交此处修改的字段" />
          <DialogBody className="space-y-3">
            <div>
              <Label htmlFor="habit-edit-title">标题</Label>
              <Input
                id="habit-edit-title"
                value={editTitle}
                maxLength={255}
                onChange={(e) => setEditTitle(e.target.value)}
              />
            </div>
            <div>
              <Label htmlFor="habit-edit-desc">描述</Label>
              <Input
                id="habit-edit-desc"
                placeholder="可选；清空文本框并保存即清除描述"
                value={editDesc}
                onChange={(e) => setEditDesc(e.target.value)}
              />
            </div>
            <div>
              <Label htmlFor="habit-edit-cadence">频率</Label>
              <Select
                id="habit-edit-cadence"
                value={editCadence}
                onValueChange={setEditCadence}
                options={CADENCE_OPTIONS}
              />
            </div>
          </DialogBody>
          <DialogFooter>
            <Button variant="secondary" onClick={() => setEditing(null)}>取消</Button>
            <Button variant="primary" loading={update.isPending} disabled={!editTitle.trim()} onClick={saveEdit}>
              保存
            </Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>
    </div>
  )
}
