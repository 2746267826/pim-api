import { readFileSync } from 'node:fs';

const SRC = '../../src/client-web/src';

const failures: string[] = [];

function readSource(relativePath: string): string {
  try {
    return readFileSync(new URL(`${SRC}/${relativePath}`, import.meta.url), 'utf8');
  } catch {
    failures.push(`Missing source: src/client-web/src/${relativePath}`);
    return '';
  }
}

const editorSource = readSource('dialogs/EventEditorDialog.tsx');
const packageJsonSource = readFileSync(new URL('../../src/client-web/package.json', import.meta.url), 'utf8');
const eventSectionSource = readSource('components/calendar/EventSection.tsx');
const richEditorSource = readSource('components/calendar/RichDescriptionEditor.tsx');
const advancedSource = readSource('components/calendar/EventAdvancedFields.tsx');
const collaborationSource = readSource('components/calendar/EventCollaborationFields.tsx');
const meetingSource = readSource('components/calendar/EventMeetingFields.tsx');
const attachmentSource = readSource('components/calendar/EventAttachmentFields.tsx');
const recurrenceSource = readSource('components/calendar/EventRecurrenceSummary.tsx');
const outlookInfoSource = readSource('components/calendar/OutlookAdditionalInfo.tsx');
const eventDraftSource = readSource('utils/eventDraft.ts');
const typesSource = readSource('types/index.ts');

// ── Editor imports every Task 7 section component ────────────────────────────
{
  const imports = [
    "from '../components/calendar/EventSection'",
    "from '../components/calendar/RichDescriptionEditor'",
    "from '../components/calendar/EventAdvancedFields'",
    "from '../components/calendar/EventCollaborationFields'",
    "from '../components/calendar/EventMeetingFields'",
    "from '../components/calendar/EventAttachmentFields'",
    "from '../components/calendar/EventRecurrenceSummary'",
    "from '../components/calendar/OutlookAdditionalInfo'",
  ];
  for (const imp of imports) {
    if (!editorSource.includes(imp)) failures.push(`EventEditorDialog must import ${imp}`);
  }
}

// ── One drawer editor with all visible section labels ────────────────────────
{
  const sections = [
    '<EventSection title="基本信息" defaultOpen>',
    '<EventSection title="高级">',
    '<EventSection title="协作">',
    '<EventSection title="会议">',
    '<EventSection title="附件">',
    '<EventSection title="重复">',
  ];
  for (const section of sections) {
    if (!editorSource.includes(section)) failures.push(`Editor must render ${section}`);
  }
  if (!editorSource.includes('(isOutlook || event?.source === \'outlook-ics\') && <OutlookAdditionalInfo')) {
    failures.push('Outlook 附加信息 must render for Outlook events and outlook-ics imports');
  }
  if (!editorSource.includes('<EventAdvancedFields')) failures.push('Editor must render EventAdvancedFields');
  if (!editorSource.includes('<EventCollaborationFields')) failures.push('Editor must render EventCollaborationFields');
  if (!editorSource.includes('<EventMeetingFields')) failures.push('Editor must render EventMeetingFields');
  if (!editorSource.includes('<EventAttachmentFields')) failures.push('Editor must render EventAttachmentFields');
  if (!editorSource.includes('<EventRecurrenceSummary')) failures.push('Editor must render EventRecurrenceSummary');
  if (!editorSource.includes('<RichDescriptionEditor')) failures.push('Editor must render RichDescriptionEditor');
}

// ── Basic section keeps the existing common fields and controls ──────────────
{
  const basics = [
    '日历本',
    '标题',
    '全天事件',
    '开始时间',
    '结束时间',
    '地点',
    '描述',
  ];
  for (const label of basics) {
    if (!editorSource.includes(label)) failures.push(`Basic section must contain ${label}`);
  }
  const datetimeCount = editorSource.match(/type="datetime-local"/g)?.length ?? 0;
  if (datetimeCount < 2) failures.push('Start/end inputs must be datetime-local');
  if (!editorSource.includes('timeZoneId')) failures.push('Editor must preserve event timezone input behavior (timeZoneId)');
}

// ── EventSection: semantic, unmounted-when-closed disclosure ─────────────────
{
  if (!eventSectionSource.includes('<section')) failures.push('EventSection must use a semantic <section>');
  if (!eventSectionSource.includes('<h3')) failures.push('EventSection must use a semantic heading <h3>');
  if (!eventSectionSource.includes('aria-expanded')) failures.push('EventSection disclosure must expose aria-expanded');
  if (!eventSectionSource.includes('type="button"')) failures.push('EventSection toggle must be a button');
  if (!eventSectionSource.includes('{open &&')) failures.push('EventSection must unmount closed content for drawer focus trapping');
  if (!eventSectionSource.includes('defaultOpen = false')) failures.push('EventSection must default to collapsed');
  if (!eventSectionSource.includes('useState(defaultOpen)')) failures.push('EventSection must use local disclosure state');
  if (!eventSectionSource.includes('useId')) failures.push('EventSection must use useId for the content region id');
  if (!eventSectionSource.includes('aria-controls=')) failures.push('EventSection toggle must reference content via aria-controls');
  if (!eventSectionSource.includes('id={contentId}')) failures.push('EventSection content region must carry the generated id');
}

// ── RichDescriptionEditor: Tiptap v3, headings 2/3, sanitizer, Chinese icons ─
{
  if (!richEditorSource.includes("from '@tiptap/react'")) failures.push('Rich editor must use @tiptap/react');
  if (!richEditorSource.includes("from '@tiptap/starter-kit'")) failures.push('Rich editor must use StarterKit');
  if (!richEditorSource.includes("from '@tiptap/extension-link'")) failures.push('Rich editor must use Link extension');
  if (!richEditorSource.includes("from '@tiptap/extension-underline'")) failures.push('Rich editor must use Underline extension');
  if (!richEditorSource.includes('levels: [2, 3]')) failures.push('StarterKit headings must be limited to levels [2, 3]');
  if (!richEditorSource.includes('link: false') || !richEditorSource.includes('underline: false')) {
    failures.push('StarterKit must explicitly disable its built-in link and underline so the explicit Link/Underline extensions are the only instances');
  }
  if (!richEditorSource.includes('immediatelyRender: false') && !richEditorSource.includes('immediatelyRender={false}')) {
    failures.push('Rich editor must set immediatelyRender: false');
  }
  if (!richEditorSource.includes('sanitizeDescriptionHtml(ed.getHTML())')) {
    failures.push('Rich editor must sanitize editor.getHTML() before onChange');
  }
  if (!richEditorSource.includes('data-description-html-preview')) {
    failures.push('Rich editor surface must keep data-description-html-preview');
  }
  if (!richEditorSource.includes('setEditable(!disabled)')) failures.push('Rich editor must support disabled read-only rendering');
  if (!richEditorSource.includes("'aria-label': '描述'")) {
    failures.push('Rich editor content area must expose an accessible name 描述');
  }
  if (!richEditorSource.includes('aria-pressed=')) failures.push('Toolbar format buttons must expose aria-pressed state');
  const iconLabels = ['加粗', '斜体', '下划线', '无序列表', '有序列表', '引用', '代码块', '链接', '撤销', '重做'];
  for (const label of iconLabels) {
    if (!richEditorSource.includes(`aria-label="${label}"`)) failures.push(`Toolbar icon must have Chinese aria-label ${label}`);
    if (!richEditorSource.includes(`title="${label}"`)) failures.push(`Toolbar icon must have Chinese title ${label}`);
  }
  for (const icon of ['Bold', 'Italic', 'Underline', 'List', 'ListOrdered', 'Quote', 'Code', 'Undo2', 'Redo2']) {
    if (!richEditorSource.includes(icon)) failures.push(`Toolbar must use Lucide ${icon} icon`);
  }
  if (richEditorSource.includes('@mdxeditor')) failures.push('Rich editor must not use MDXEditor');
  if (richEditorSource.includes('dangerouslySetInnerHTML')) failures.push('Rich editor must not use dangerouslySetInnerHTML');
}

// ── Advanced: selects, chips, reminder checkbox + number ─────────────────────
{
  const selectCount = advancedSource.match(/<select/g)?.length ?? 0;
  if (selectCount < 3) failures.push('showAs/importance/sensitivity must be selects (found 3+ selects)');
  for (const value of ['free', 'tentative', 'busy', 'oof', 'workingElsewhere', 'low', 'high', 'personal', 'private', 'confidential']) {
    if (!advancedSource.includes(`value: '${value}'`) && !advancedSource.includes(`value="${value}"`)) {
      failures.push(`Advanced select missing option ${value}`);
    }
  }
  for (const label of ['显示状态', '重要性', '敏感度', '分类', '提醒', '提前提醒（分钟）']) {
    if (!advancedSource.includes(label)) failures.push(`Advanced section must show ${label}`);
  }
  if (!advancedSource.includes('添加分类')) failures.push('Categories must have an add-chip control');
  if (!advancedSource.includes('移除分类')) failures.push('Categories must have remove-chip controls');
  if (advancedSource.includes(".split(',')")) failures.push('Categories must be chips, not comma-only string storage');
  if (!advancedSource.includes('type="checkbox"')) failures.push('Reminder must be a checkbox');
  if (!advancedSource.includes('type="number"')) failures.push('Reminder minutes must be a number input');
  if (!advancedSource.includes('min={0}')) failures.push('Reminder minutes must be min=0');
  if (!advancedSource.includes('form.isReminderOn &&')) failures.push('Reminder minutes must render only when reminder is enabled');
  if (!advancedSource.includes('reminderMinutesBeforeStart: form.reminderMinutesBeforeStart ?? 15')) {
    failures.push('Enabling the reminder must write an explicit default of 15 minutes');
  }
  if (advancedSource.includes('value={form.reminderMinutesBeforeStart ?? 15}')) {
    failures.push('Reminder minutes input must show the real empty value, not mask null as 15');
  }
}

// ── Collaboration: organizer + attendee rows ─────────────────────────────────
{
  if (!collaborationSource.includes('组织者')) failures.push('Collaboration section must label 组织者');
  if (!collaborationSource.includes('参会者')) failures.push('Collaboration section must label 参会者');
  for (const value of ['required', 'optional', 'resource']) {
    if (!collaborationSource.includes(`value: '${value}'`) && !collaborationSource.includes(`value="${value}"`)) {
      failures.push(`Attendee type select missing ${value}`);
    }
  }
  if (!collaborationSource.includes('添加参会者')) failures.push('Attendees must have an add-row control');
  if (!collaborationSource.includes('移除参会者')) failures.push('Attendee rows must have a remove control');
  if (!collaborationSource.includes('disabled={disabled || providerReadOnly}')) {
    failures.push('Organizer must be provider-read-only for Outlook');
  }
  if (!collaborationSource.includes('aria-label="组织者姓名"')) failures.push('Organizer name input must have an explicit aria-label');
  if (!collaborationSource.includes('aria-label="组织者邮箱"')) failures.push('Organizer email input must have an explicit aria-label');
  if (!collaborationSource.includes('aria-label={`参会者 ${index + 1} 姓名`}')) {
    failures.push('Attendee name inputs must carry row-numbered aria-labels');
  }
  if (!collaborationSource.includes('aria-label={`参会者 ${index + 1} 邮箱`}')) {
    failures.push('Attendee email inputs must carry row-numbered aria-labels');
  }
}

// ── Meeting: checkbox, provider select, provider-read-only URLs ──────────────
{
  if (!meetingSource.includes('在线会议')) failures.push('Meeting section must label 在线会议');
  for (const value of ['teams', 'zoom', 'meet', 'other']) {
    if (!meetingSource.includes(`value: '${value}'`) && !meetingSource.includes(`value="${value}"`)) {
      failures.push(`Meeting provider select missing ${value}`);
    }
  }
  if (!meetingSource.includes('会议链接')) failures.push('Meeting section must label 会议链接');
  if (!meetingSource.includes('外部链接')) failures.push('Meeting section must label 外部链接');
  if (!meetingSource.includes('disabled={disabled || providerReadOnly}')) {
    failures.push('Meeting URLs must be provider-read-only for Outlook');
  }
}

// ── Attachments: PIM files via getFileItems, Outlook refs read-only ──────────
{
  if (!attachmentSource.includes("import { getFileItems } from '../../api/files'")) {
    failures.push('Native attachments must use the existing getFileItems API');
  }
  if (!attachmentSource.includes('添加附件')) failures.push('Native attachments must have an add control');
  if (!attachmentSource.includes('移除附件')) failures.push('pimFile references must have a remove control');
  if (!attachmentSource.includes("kind === 'pimFile'")) failures.push('Remove control must be scoped to pimFile references');
  if (!attachmentSource.includes("kind === 'outlook'")) failures.push('Outlook attachments must be handled separately');
  if (!attachmentSource.includes('Outlook 附件（只读）')) failures.push('Outlook attachment refs must be visible but read-only');
  if (!attachmentSource.includes('aria-label="选择附件文件"')) failures.push('Attachment file select must have an explicit aria-label');
  if (attachmentSource.includes('type="file"')) failures.push('Attachments must not add upload/binary behavior');
  if (attachmentSource.includes('uploadFile')) failures.push('Attachments must not add upload behavior');
  if (attachmentSource.includes('estimatedDuration')) failures.push('Attachments must not use a duration dropdown');
}

// ── Recurrence: human-readable summary only, no raw editor ───────────────────
{
  for (const summary of ['每天', '每周', '每月', '每年']) {
    if (!recurrenceSource.includes(summary)) failures.push(`Recurrence summary must cover ${summary}`);
  }
  if (!recurrenceSource.includes('不重复')) failures.push('Recurrence summary must state 不重复 when no rule');
  if (recurrenceSource.includes('<input')) failures.push('Recurrence must not contain an input (no raw RRULE editor)');
  if (recurrenceSource.includes('JSON.stringify')) failures.push('Recurrence must not emit raw recurrence JSON');
  if (recurrenceSource.includes('recurrenceMetadataJson')) failures.push('Recurrence must not use raw recurrence metadata');
  if (recurrenceSource.includes('exDatesJson')) failures.push('Recurrence must not use raw exDates JSON');
}

// ── Outlook additional info: allowlisted summary only ────────────────────────
{
  if (!outlookInfoSource.includes('Outlook 附加信息')) failures.push('Outlook info must use the 附加信息 section label');
  if (!outlookInfoSource.includes('<EventSection title="Outlook 附加信息"')) {
    failures.push('Outlook info must be a default-collapsed EventSection');
  }
  if (!outlookInfoSource.includes('summary=')) failures.push('Outlook info must surface the hidden-field count in the collapsed header');
  if (!outlookInfoSource.includes('info.groups')) failures.push('Outlook info must use only event.outlookAdditionalInfo.groups');
  if (!outlookInfoSource.includes('hiddenFieldCount')) failures.push('Outlook info must show hiddenFieldCount');
  if (!outlookInfoSource.includes('hiddenFieldCount <= 0')) {
    failures.push('Outlook info must still render the hidden-field count when groups are empty');
  }
  if (!outlookInfoSource.includes('已隐藏')) failures.push('Outlook info must describe hidden fields without values');
  if (!outlookInfoSource.includes('break-words') || !outlookInfoSource.includes('overflow-hidden')) {
    failures.push('Outlook info must protect against text overflow');
  }
  if (outlookInfoSource.includes('externalMetadataJson')) failures.push('Outlook info must not render externalMetadataJson');
  if (outlookInfoSource.includes('sourceSnapshot')) failures.push('Outlook info must not render raw Graph source');
  if (outlookInfoSource.includes('JSON.stringify')) failures.push('Outlook info must not render raw metadata JSON');
  if (outlookInfoSource.includes('dangerouslySetInnerHTML')) failures.push('Outlook info must not use dangerouslySetInnerHTML');
}

// ── Shared builder: manual and Outlook paths use buildUnifiedEventDraft ──────
{
  if (!editorSource.includes("import { buildUnifiedEventDraft, type EventFormValue } from '../utils/eventDraft'")) {
    failures.push('Editor must import the shared buildUnifiedEventDraft and EventFormValue');
  }
  const builderCalls = editorSource.match(/buildUnifiedEventDraft\(/g)?.length ?? 0;
  if (builderCalls < 2) failures.push('Manual save and Outlook draft must both call buildUnifiedEventDraft');
  if (!editorSource.includes('...buildUnifiedEventDraft({')) failures.push('Outlook buildDraft must spread the shared builder result');
  if (!editorSource.includes('uid: event?.uid || undefined')) failures.push('Outlook buildDraft must append only legacy uid');
  if (editorSource.includes('rRule:')) failures.push('Outlook buildDraft must not reattach rRule (server rejects non-empty rRule)');
  if (!editorSource.includes('useState<EventFormValue>')) failures.push('Editor must replace scalar state with one EventFormValue object');
  for (const scalar of ['const [title, setTitle]', 'const [description, setDescription]', 'const [location, setLocation]',
    'const [calendarId, setCalendarId]', 'const [isAllDay, setIsAllDay]', 'const [dtStart, setDtStart]', 'const [dtEnd, setDtEnd]']) {
    if (editorSource.includes(scalar)) failures.push(`Editor must not keep scalar state: ${scalar}`);
  }
  if (!eventDraftSource.includes('export function buildUnifiedEventDraft')) {
    failures.push('Shared builder must exist in eventDraft.ts');
  }
}

// ── Preserved behavior: validation, invalidation, writeback state machine ────
{
  if (!editorSource.includes('if (isProcessing) return;')) {
    failures.push('Native create/update must guard against double submit with isProcessing');
  }
  for (const preserved of [
    '请输入标题',
    '请选择开始和结束时间',
    '结束时间必须晚于开始时间',
    '缺少版本标识',
    'crypto.randomUUID',
    'expectedEtag',
    'latestEvent',
    '基于 Outlook 最新版本重新比较',
    'formKey',
  ]) {
    if (!editorSource.includes(preserved)) failures.push(`Editor must preserve ${preserved}`);
  }
  if (editorSource.includes('latestOutlookJson')) {
    failures.push('Editor must not consume the deprecated latestOutlookJson');
  }
  if (!editorSource.includes("queryKey: ['events']")) failures.push('Editor must preserve event query invalidation');
  if (!editorSource.includes("queryKey: ['calendar-layers']")) failures.push('Editor must preserve calendar-layer invalidation');
}

// ── Raw metadata must never be rendered anywhere ─────────────────────────────
{
  const allSources = [editorSource, eventSectionSource, richEditorSource, advancedSource,
    collaborationSource, meetingSource, attachmentSource, recurrenceSource, outlookInfoSource];
  for (const [name, source] of [['EventEditorDialog', editorSource], ['EventSection', eventSectionSource],
    ['RichDescriptionEditor', richEditorSource], ['EventAdvancedFields', advancedSource],
    ['EventCollaborationFields', collaborationSource], ['EventMeetingFields', meetingSource],
    ['EventAttachmentFields', attachmentSource], ['EventRecurrenceSummary', recurrenceSource],
    ['OutlookAdditionalInfo', outlookInfoSource]] as const) {
    if (source.includes('externalMetadataJson')) failures.push(`${name} must not render externalMetadataJson`);
    if (source.includes('sourceSnapshot')) failures.push(`${name} must not render raw Graph source`);
    if (source.includes('dangerouslySetInnerHTML')) failures.push(`${name} must not use dangerouslySetInnerHTML`);
  }
  if (typesSource.includes('externalMetadataJson')) failures.push('EventResponse must not expose externalMetadataJson');
  if (allSources.some(s => s.includes('secret'))) failures.push('Editor sources must not render secret values');
}

// ── Native editability vs Outlook provider read-only: event source flags ─────
{
  if (!editorSource.includes('isOutlookExisting')) failures.push('Editor must keep Outlook existing-event detection');
  if (!editorSource.includes('isNewOutlook')) failures.push('Editor must keep new-Outlook detection');
  if (!editorSource.includes('providerReadOnly={isOutlook}')) failures.push('Sections must receive providerReadOnly from the Outlook source flag');
}

// ── CI entry point: contract test must run in the task script ───────────────
{
  if (!packageJsonSource.includes('unifiedEventEditorUi.test.tsx')) {
    failures.push('test:schedule-workbench-complete must run unifiedEventEditorUi.test.tsx');
  }
}

main().catch((error: unknown) => {
  console.error(error);
  process.exitCode = 1;
});

function main(): Promise<void> {
  if (failures.length > 0) {
    throw new AggregateError(failures, 'unifiedEventEditorUi contract tests failed');
  }
  return Promise.resolve();
}
