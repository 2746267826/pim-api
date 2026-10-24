import assert from 'node:assert/strict';
import { describe, it, before } from 'node:test';
import { createRequire } from 'node:module';
import type { EventFieldDiffEntry } from '../../src/client-web/src/utils/eventFieldDiff';

const requireFromWeb = createRequire(new URL('../../src/client-web/package.json', import.meta.url));
const { JSDOM } = requireFromWeb('jsdom') as typeof import('jsdom');

let diffEventFields: (
  before: Record<string, unknown>,
  after: Record<string, unknown>,
) => EventFieldDiffEntry[];
let EVENT_FIELD_LABELS: Record<string, string>;
let formatFieldValue: (value: unknown, key: string) => string;
let summarizeEventFields: (record: Record<string, unknown>) => EventFieldDiffEntry[];
let toDiffRecord: (record: Record<string, unknown>) => Record<string, unknown>;
let diffGenericFields: (
  before: Record<string, unknown>,
  after: Record<string, unknown>,
) => EventFieldDiffEntry[];
let canonicalizeLegacyRecord: (record: Record<string, unknown>) => Record<string, unknown>;
let safeChangedFields: (
  changedFields: string[] | null | undefined,
) => Array<{ key: string; label: string }>;
let safeExternalEffectText: (value: unknown) => string;

function withTimeZone<T>(tz: string, fn: () => T): T {
  const previous = process.env.TZ;
  process.env.TZ = tz;
  try {
    return fn();
  } finally {
    if (previous === undefined) delete process.env.TZ;
    else process.env.TZ = previous;
  }
}

before(async () => {
  const dom = new JSDOM('<!DOCTYPE html><html><body></body></html>');
  globalThis.window = dom.window as unknown as Window & typeof globalThis;
  globalThis.document = dom.window.document;
  globalThis.Node = dom.window.Node;
  globalThis.DocumentFragment = dom.window.DocumentFragment;
  globalThis.Element = dom.window.Element;
  globalThis.HTMLElement = dom.window.HTMLElement;
  globalThis.HTMLDocument = dom.window.HTMLDocument;

  const mod = await import('../../src/client-web/src/utils/eventFieldDiff');
  diffEventFields = mod.diffEventFields;
  EVENT_FIELD_LABELS = mod.EVENT_FIELD_LABELS;
  formatFieldValue = mod.formatFieldValue;
  summarizeEventFields = mod.summarizeEventFields;
  toDiffRecord = mod.toDiffRecord;
  diffGenericFields = mod.diffGenericFields;
  canonicalizeLegacyRecord = mod.canonicalizeLegacyRecord;
  safeChangedFields = mod.safeChangedFields;
  safeExternalEffectText = mod.safeExternalEffectText;
});

describe('diffEventFields', () => {
  it('reports a modified title with its Chinese label', () => {
    const entries = diffEventFields({ title: '旧' }, { title: '新' });
    assert.equal(entries.length, 1);
    assert.deepEqual(entries[0], { key: 'title', label: '标题', kind: 'modified', before: '旧', after: '新' });
  });

  it('marks fields present only after as added', () => {
    const entries = diffEventFields({}, { title: '新' });
    assert.equal(entries.length, 1);
    assert.equal(entries[0].kind, 'added');
    assert.equal(entries[0].before, undefined);
    assert.equal(entries[0].after, '新');
  });

  it('marks fields present only before as removed', () => {
    const entries = diffEventFields({ title: '旧' }, {});
    assert.equal(entries.length, 1);
    assert.equal(entries[0].kind, 'removed');
    assert.equal(entries[0].before, '旧');
    assert.equal(entries[0].after, undefined);
  });

  it('omits unchanged fields', () => {
    assert.deepEqual(diffEventFields({ title: '同' }, { title: '同' }), []);
    assert.deepEqual(diffEventFields({ title: '同', location: 'A' }, { title: '同', location: 'A' }), []);
  });

  it('treats null, undefined and empty strings equivalently', () => {
    assert.deepEqual(diffEventFields({ description: null }, { description: undefined }), []);
    assert.deepEqual(diffEventFields({ description: '' }, { description: null }), []);
    assert.deepEqual(diffEventFields({ location: '' }, { location: '' }), []);
    const entries = diffEventFields({ location: '' }, { location: '某地' });
    assert.equal(entries.length, 1);
    assert.equal(entries[0].kind, 'added');
  });

  it('diffs arrays with element ordering', () => {
    const entries = diffEventFields({ categories: ['a', 'b'] }, { categories: ['a', 'c'] });
    assert.equal(entries.length, 1);
    assert.deepEqual(entries[0], {
      key: 'categories',
      label: '分类',
      kind: 'modified',
      before: ['a', 'b'],
      after: ['a', 'c'],
    });
    assert.deepEqual(diffEventFields({ categories: ['a', 'b'] }, { categories: ['a', 'b'] }), []);
    assert.deepEqual(diffEventFields({ categories: ['a', 'b'] }, { categories: [] })[0].kind, 'removed');
    assert.deepEqual(diffEventFields({ categories: [] }, { categories: ['a'] })[0].kind, 'added');
  });

  it('diffs nested organizer objects', () => {
    const entries = diffEventFields(
      { organizer: { name: 'A', email: 'a@example.com' } },
      { organizer: { name: 'B', email: 'a@example.com' } },
    );
    assert.equal(entries.length, 1);
    assert.equal(entries[0].key, 'organizer');
    assert.equal(entries[0].label, '组织者');
    assert.deepEqual(entries[0].before, { name: 'A', email: 'a@example.com' });
    assert.deepEqual(entries[0].after, { name: 'B', email: 'a@example.com' });
    assert.deepEqual(
      diffEventFields({ organizer: null }, { organizer: { name: 'A', email: 'a@example.com' } })[0].kind,
      'added',
    );
  });

  it('diffs nested attendee arrays', () => {
    const before = { attendees: [{ name: 'A', email: 'a@example.com', type: 'required' }] };
    const after = {
      attendees: [
        { name: 'A', email: 'a@example.com', type: 'required' },
        { name: 'B', email: 'b@example.com', type: 'optional' },
      ],
    };
    const entries = diffEventFields(before, after);
    assert.equal(entries.length, 1);
    assert.equal(entries[0].key, 'attendees');
    assert.deepEqual(entries[0].before, before.attendees);
    assert.deepEqual(entries[0].after, after.attendees);
  });

  it('turns HTML descriptions into text-only safe summaries', () => {
    const entries = diffEventFields(
      { description: '<p>旧<strong>内容</strong></p>' },
      { description: '<p>新内容</p>' },
    );
    assert.equal(entries.length, 1);
    assert.equal(entries[0].key, 'description');
    assert.equal(entries[0].before, '旧内容');
    assert.equal(entries[0].after, '新内容');
    assert.deepEqual(diffEventFields({ description: '<p>一样</p>' }, { description: '一样' }), []);
  });

  it('ignores HTML descriptions that normalize to empty', () => {
    assert.deepEqual(diffEventFields({ description: '<p> </p>' }, { description: undefined }), []);
    assert.deepEqual(diffEventFields({ description: '<p> </p>' }, { description: '' }), []);
    assert.deepEqual(diffEventFields({ description: '<p></p>' }, { description: '<p> </p>' }), []);
  });

  it('ignores whitespace-entity HTML descriptions and normalizes their text', () => {
    assert.deepEqual(diffEventFields({ description: '<p>&nbsp;</p>' }, { description: undefined }), []);
    assert.deepEqual(diffEventFields({ description: '<p>&nbsp;</p>' }, { description: '' }), []);
    assert.deepEqual(diffEventFields({ description: '<p>A&nbsp;B</p>' }, { description: 'A B' }), []);
  });

  it('ignores unwrapped whitespace-entity descriptions when the format is explicitly html', () => {
    assert.deepEqual(
      diffEventFields(
        { descriptionFormat: 'html', description: '&nbsp;' },
        { descriptionFormat: 'html', description: '' },
      ),
      [],
    );
    assert.deepEqual(
      diffEventFields(
        { descriptionFormat: 'html', description: '&Tab;' },
        { descriptionFormat: 'html', description: '' },
      ),
      [],
    );
    assert.deepEqual(
      diffEventFields(
        { descriptionFormat: 'html', description: '&#9;' },
        { descriptionFormat: 'html', description: '' },
      ),
      [],
    );
  });

  it('treats whitespace-only plain descriptions as absent', () => {
    assert.deepEqual(diffEventFields({ description: '   ' }, { description: undefined }), []);
    assert.deepEqual(diffEventFields({ description: '\t\n  ' }, { description: null }), []);
    assert.deepEqual(diffEventFields({}, { description: ' ' }), []);
    assert.deepEqual(diffEventFields({ description: ' ' }, { description: '' }), []);
  });

  it('still reports meaningful plain-text description changes', () => {
    const entries = diffEventFields({ description: '旧内容' }, { description: '新内容' });
    assert.equal(entries.length, 1);
    assert.equal(entries[0].key, 'description');
    assert.equal(entries[0].kind, 'modified');
    assert.deepEqual(diffEventFields({ description: ' ' }, { description: '新内容' })[0].kind, 'added');
  });

  it('treats explicit plain descriptions as literal text even when they look like HTML', () => {
    const entries = diffEventFields(
      { descriptionFormat: 'plain', description: 'Use <b> for bold' },
      { descriptionFormat: 'plain', description: 'Use for bold' },
    );
    assert.equal(entries.length, 1);
    assert.deepEqual(entries[0], {
      key: 'description',
      label: '描述',
      kind: 'modified',
      before: 'Use <b> for bold',
      after: 'Use for bold',
    });
    assert.deepEqual(
      diffEventFields(
        { descriptionFormat: 'plain', description: 'Use <b> for bold' },
        { descriptionFormat: 'plain', description: 'Use <b> for bold' },
      ),
      [],
    );
  });

  it('exposes stable Simplified Chinese labels for every shared field', () => {
    const before: Record<string, unknown> = {};
    const after: Record<string, unknown> = {};
    for (const key of Object.keys(EVENT_FIELD_LABELS)) {
      before[key] = '旧值';
      after[key] = '新值';
    }
    const entries = diffEventFields(before, after);
    assert.equal(entries.length, Object.keys(EVENT_FIELD_LABELS).length);
    for (const entry of entries) {
      assert.equal(entry.label, EVENT_FIELD_LABELS[entry.key]);
      assert.equal(entry.kind, 'modified');
    }
    assert.deepEqual(EVENT_FIELD_LABELS, {
      title: '标题',
      description: '描述',
      descriptionFormat: '描述格式',
      location: '地点',
      dtStart: '开始时间',
      dtEnd: '结束时间',
      isAllDay: '全天事件',
      timeZoneId: '时区',
      showAs: '显示状态',
      importance: '重要性',
      sensitivity: '敏感度',
      categories: '分类',
      isReminderOn: '提醒',
      reminderMinutesBeforeStart: '提醒提前量',
      organizer: '组织者',
      attendees: '参会者',
      isOnlineMeeting: '在线会议',
      onlineMeetingProvider: '会议提供方',
      onlineMeetingUrl: '会议链接',
      externalLink: '外部链接',
      attachmentReferences: '附件',
    });
  });

  it('never inspects or emits raw metadata, Outlook identifiers or unknown keys', () => {
    const before: Record<string, unknown> = {
      externalMetadataJson: '{"raw":1}',
      outlookEventId: 'graph-1',
      outlookEtag: 'etag-a',
      outlookCalendarBindingId: 'binding-1',
      source: 'outlook',
      uid: 'uid-1',
      rRule: 'FREQ=DAILY',
      mysteryKey: 1,
      title: '旧',
    };
    const after: Record<string, unknown> = {
      externalMetadataJson: '{"raw":2}',
      outlookEventId: 'graph-2',
      outlookEtag: 'etag-b',
      outlookCalendarBindingId: 'binding-2',
      source: 'ics',
      uid: 'uid-2',
      rRule: 'FREQ=WEEKLY',
      mysteryKey: 2,
      title: '新',
    };
    const entries = diffEventFields(before, after);
    assert.equal(entries.length, 1);
    assert.deepEqual(entries[0], { key: 'title', label: '标题', kind: 'modified', before: '旧', after: '新' });
  });

  it('matches naive values to explicit offsets only through the record timeZoneId', () => {
    for (const tz of ['UTC', 'Asia/Shanghai']) {
      withTimeZone(tz, () => {
        assert.deepEqual(
          diffEventFields(
            { dtStart: '2026-07-14T09:00:00', timeZoneId: 'Asia/Shanghai' },
            { dtStart: '2026-07-14T01:00:00.000Z', timeZoneId: 'Asia/Shanghai' },
          ),
          [],
          `naive Asia/Shanghai must match Z when both records carry that zone (host TZ=${tz})`,
        );
      });
    }
  });

  it('compares explicit offsets as instants on any host zone', () => {
    for (const tz of ['UTC', 'Asia/Shanghai']) {
      withTimeZone(tz, () => {
        assert.deepEqual(
          diffEventFields(
            { dtStart: '2026-07-14T09:00:00+08:00', dtEnd: '2026-07-14T10:00:00+08:00' },
            { dtStart: '2026-07-14T01:00:00.000Z', dtEnd: '2026-07-14T02:00:00.000Z' },
          ),
          [],
          `explicit offsets must compare as instants (host TZ=${tz})`,
        );
      });
    }
  });

  it('keeps naive values without a valid zone modified against explicit offsets on any host zone', () => {
    for (const tz of ['UTC', 'Asia/Shanghai']) {
      withTimeZone(tz, () => {
        const entries = diffEventFields(
          { dtStart: '2026-07-14T09:00:00' },
          { dtStart: '2026-07-14T01:00:00.000Z' },
        );
        assert.equal(entries.length, 1, `naive without zone must stay modified (host TZ=${tz})`);
        assert.equal(entries[0].key, 'dtStart');
        assert.equal(entries[0].kind, 'modified');
      });
    }
  });

  it('compares naive values deterministically without host-zone dependence', () => {
    for (const tz of ['UTC', 'Asia/Shanghai']) {
      withTimeZone(tz, () => {
        assert.deepEqual(
          diffEventFields(
            { dtStart: '2026-07-14T09:00:00' },
            { dtStart: '2026-07-14T09:00:00' },
          ),
          [],
          `same naive literal without a zone must not diff (host TZ=${tz})`,
        );
        assert.equal(
          diffEventFields(
            { dtStart: '2026-07-14T09:00:00' },
            { dtStart: '2026-07-14T10:00:00' },
          ).length,
          1,
          `different naive literals without a zone must diff (host TZ=${tz})`,
        );
        const zonedEntries = diffEventFields(
          { dtStart: '2026-07-14T09:00:00', timeZoneId: 'Asia/Shanghai' },
          { dtStart: '2026-07-14T01:00:00', timeZoneId: 'UTC' },
        );
        assert.ok(
          !zonedEntries.some(entry => entry.key === 'dtStart'),
          `naive values in their own record zones must match instants (host TZ=${tz})`,
        );
      });
    }
  });

  it('treats false toggles as equivalent to unset', () => {
    assert.deepEqual(diffEventFields({ isReminderOn: undefined }, { isReminderOn: false }), []);
    assert.deepEqual(diffEventFields({ isOnlineMeeting: null }, { isOnlineMeeting: false }), []);
    assert.deepEqual(diffEventFields({ isAllDay: false }, { isAllDay: false }), []);
    const entries = diffEventFields({ isReminderOn: undefined }, { isReminderOn: true });
    assert.equal(entries.length, 1);
    assert.equal(entries[0].kind, 'added');
    const removed = diffEventFields({ isReminderOn: true }, { isReminderOn: false });
    assert.equal(removed.length, 1);
    assert.equal(removed[0].kind, 'modified');
  });

  it('does not mutate its inputs', () => {
    const before: Record<string, unknown> = { description: '<p>旧内容</p>' };
    const after: Record<string, unknown> = { description: '<p>新内容</p>' };
    diffEventFields(before, after);
    assert.equal(before.description, '<p>旧内容</p>');
    assert.equal(after.description, '<p>新内容</p>');
  });
});

describe('formatFieldValue', () => {
  it('renders booleans as 是/否', () => {
    assert.equal(formatFieldValue(true, 'isAllDay'), '是');
    assert.equal(formatFieldValue(false, 'isAllDay'), '否');
  });

  it('joins string arrays with 、', () => {
    assert.equal(formatFieldValue(['a', 'b'], 'categories'), 'a、b');
  });

  it('renders attendee objects as name/email text with Chinese type labels', () => {
    const attendees = [
      { name: '张三', email: 'zhangsan@example.com', type: 'required' },
      { name: '李四', email: 'lisi@example.com', type: 'optional' },
    ];
    assert.equal(
      formatFieldValue(attendees, 'attendees'),
      '张三 zhangsan@example.com（必须）、李四 lisi@example.com（可选）',
    );
  });

  it('renders organizer as name/email text', () => {
    assert.equal(
      formatFieldValue({ name: '组织者', email: 'owner@example.com' }, 'organizer'),
      '组织者 owner@example.com',
    );
  });

  it('renders attachment references by name only', () => {
    const refs = [
      { kind: 'outlook', id: 'id-1', name: '会议纪要.docx', canDownload: true },
      { kind: 'pimFile', id: 'id-2', name: '清单.pdf', canDownload: true },
    ];
    assert.equal(formatFieldValue(refs, 'attachmentReferences'), '会议纪要.docx、清单.pdf');
  });

  it('renders numbers and strings literally and truncates long strings', () => {
    assert.equal(formatFieldValue(15, 'reminderMinutesBeforeStart'), '15');
    const long = 'x'.repeat(500);
    const rendered = formatFieldValue(long, 'location');
    assert.ok(rendered.length < long.length, 'long values must be truncated');
    assert.ok(rendered.endsWith('…'));
  });

  it('renders empty values as —', () => {
    assert.equal(formatFieldValue(undefined, 'title'), '—');
    assert.equal(formatFieldValue(null, 'title'), '—');
    assert.equal(formatFieldValue('', 'title'), '—');
  });

  it('renders unknown nested objects as a safe placeholder, never raw JSON', () => {
    const value = { subject: '内部标题', attendees: [{ name: 'x' }] };
    const rendered = formatFieldValue(value, 'unknownKey');
    assert.equal(rendered, '（结构化数据已隐藏）');
    assert.ok(!rendered.includes('内部标题'), 'placeholder must not expose nested values');
    assert.ok(!rendered.includes('{'), 'placeholder must not expose JSON structure');
    assert.ok(!rendered.includes('"'), 'placeholder must not expose serialized structure');
    const arrayRendered = formatFieldValue([{ raw: 1 }], 'unknownKey');
    assert.equal(arrayRendered, '（结构化数据已隐藏）');
  });

  it('never renders Graph ids, ETags, URLs or provider GUIDs as diff values', () => {
    assert.equal(formatFieldValue('graph-evt-001', 'onlineMeetingUrl'), '（外部标识已隐藏）');
    assert.equal(formatFieldValue('https://teams.microsoft.com/l/meetup-join/abc', 'onlineMeetingUrl'), '（外部链接已隐藏）');
    assert.equal(formatFieldValue('a1b2c3d4-e5f6-7890-abcd-ef1234567890', 'externalLink'), '（外部标识已隐藏）');
    assert.equal(formatFieldValue('etag-new-001', 'location'), '（外部标识已隐藏）');
    assert.equal(formatFieldValue('普通标题', 'title'), '普通标题');
  });
});

describe('summarizeEventFields', () => {
  it('returns all non-empty known fields with Chinese labels', () => {
    const entries = summarizeEventFields({ title: '会议', location: 'A', isAllDay: false });
    assert.equal(entries.length, 3);
    assert.deepEqual(entries.map(e => e.key), ['title', 'location', 'isAllDay']);
    assert.equal(entries[0].label, '标题');
    assert.equal(entries[0].kind, 'modified');
    assert.equal(entries[0].after, '会议');
  });

  it('omits empty and unknown fields', () => {
    const entries = summarizeEventFields({
      title: '',
      location: null,
      externalMetadataJson: '{"raw":1}',
      outlookEtag: 'etag',
      mystery: 1,
    });
    assert.deepEqual(entries, []);
  });
});

describe('toDiffRecord', () => {
  it('copies only known writable fields', () => {
    const record = toDiffRecord({
      title: '新',
      location: 'A',
      externalMetadataJson: '{"raw":1}',
      outlookEventId: 'graph-1',
      rRule: 'FREQ=DAILY',
      mystery: 1,
      attachmentReferences: [{ kind: 'outlook', id: 'x', name: '附件' }],
    });
    assert.deepEqual(record, {
      title: '新',
      location: 'A',
      attachmentReferences: [{ kind: 'outlook', id: 'x', name: '附件' }],
    });
  });

  it('omits fields absent from the source', () => {
    assert.deepEqual(toDiffRecord({}), {});
  });
});

describe('diffGenericFields', () => {
  it('diffs arbitrary keys with the key as label', () => {
    const entries = diffGenericFields(
      { 标题: '旧', 不变: 1 },
      { 标题: '新', 不变: 1, 新增: 'x' },
    );
    assert.equal(entries.length, 2);
    assert.deepEqual(entries[0], { key: '标题', label: '标题', kind: 'modified', before: '旧', after: '新' });
    assert.equal(entries[1].key, '新增');
    assert.equal(entries[1].kind, 'added');
  });

  it('treats empty values as absent and omits unchanged keys', () => {
    assert.deepEqual(
      diffGenericFields({ a: null, b: '' }, { a: undefined, b: 'x' }),
      [{ key: 'b', label: 'b', kind: 'added', before: undefined, after: 'x' }],
    );
  });

  it('renders removed keys', () => {
    const entries = diffGenericFields({ a: 1, b: 2 }, { a: 1 });
    assert.equal(entries.length, 1);
    assert.equal(entries[0].key, 'b');
    assert.equal(entries[0].kind, 'removed');
  });
});

describe('canonicalizeLegacyRecord', () => {
  it('maps PascalCase and provider casing variants to canonical business keys', () => {
    assert.deepEqual(
      canonicalizeLegacyRecord({
        Subject: '标题',
        Start: '2026-07-14T09:00:00',
        IsAllDay: true,
        TimeZoneId: 'Asia/Shanghai',
        ReminderMinutesBeforeStart: 15,
        OnlineMeetingUrl: 'https://x',
        AttachmentReferences: [{ name: 'a' }],
      }),
      {
        title: '标题',
        dtStart: '2026-07-14T09:00:00',
        isAllDay: true,
        timeZoneId: 'Asia/Shanghai',
        reminderMinutesBeforeStart: 15,
        onlineMeetingUrl: 'https://x',
        attachmentReferences: [{ name: 'a' }],
      },
    );
  });

  it('drops internal Outlook metadata in every casing before normalization', () => {
    assert.deepEqual(
      canonicalizeLegacyRecord({
        ChangeKey: 'ck-1',
        change_key: 'ck-2',
        ChangeKeyValue: 'ckv',
        OutlookEtag: 'etag-a',
        outlookEventId: 'graph-1',
        externalMetadataJson: '{"raw":1}',
        Title: 't',
      }),
      { title: 't' },
    );
  });

  it('filters iCalUId, recurrenceId and sourceIcsComponent in every casing while keeping ordinary event keys', () => {
    assert.deepEqual(
      canonicalizeLegacyRecord({
        iCalUId: '040000008200E00074C5B7101A82E008',
        icaluid: '0400-2',
        ICALUID: '0400-3',
        iCal_UID: '0400-4',
        recurrenceId: '2026-07-14T09:00:00',
        recurrence_id: '2026-07-14T10:00:00',
        RecurrenceID: '2026-07-14T11:00:00',
        sourceIcsComponent: 'BEGIN:VEVENT',
        source_ics_component: 'BEGIN:VTODO',
        SourceICSComponent: 'BEGIN:VEVENT',
        Title: '可见标题',
        location: '会议室 A',
        DtStart: '2026-07-14T09:00:00',
      }),
      {
        title: '可见标题',
        location: '会议室 A',
        dtStart: '2026-07-14T09:00:00',
      },
    );
  });
});

describe('diffGenericFields legacy snapshot compatibility', () => {
  it('never emits changeKey or case variants of internal Outlook metadata as diff rows', () => {
    const entries = diffGenericFields(
      {
        Title: '旧',
        changeKey: 'ck-1',
        ChangeKey: 'ck-2',
        ChangeKeyValue: 'ckv-1',
        outlookChangeKey: 'ock-1',
        CHANGEKEY: 'CK-1',
        OutlookEtag: 'etag-old',
      },
      {
        Title: '新',
        changeKey: 'ck-1b',
        ChangeKey: 'ck-2b',
        ChangeKeyValue: 'ckv-2',
        outlookChangeKey: 'ock-2',
        CHANGEKEY: 'CK-2',
        OutlookEtag: 'etag-new',
      },
    );
    assert.equal(entries.length, 1);
    assert.deepEqual(entries[0], {
      key: 'title',
      label: '标题',
      kind: 'modified',
      before: '旧',
      after: '新',
    });
  });

  it('merges PascalCase before and camelCase after snapshots into one modified row', () => {
    const entries = diffGenericFields(
      { Subject: '旧主题', Location: '会议室 A' },
      { subject: '新主题', location: '会议室 A' },
    );
    assert.equal(entries.length, 1);
    assert.deepEqual(entries[0], {
      key: 'title',
      label: '标题',
      kind: 'modified',
      before: '旧主题',
      after: '新主题',
    });
  });

  it('never emits iCalUId, recurrenceId or sourceIcsComponent provider metadata as diff rows', () => {
    const entries = diffGenericFields(
      {
        Title: '旧',
        iCalUId: '0400-old',
        recurrenceId: '2026-07-14T09:00:00',
        sourceIcsComponent: 'BEGIN:VEVENT',
      },
      {
        Title: '新',
        iCalUId: '0400-new',
        recurrence_id: '2026-07-14T10:00:00',
        SourceICSComponent: 'BEGIN:VTODO',
      },
    );
    assert.equal(entries.length, 1);
    assert.deepEqual(entries[0], {
      key: 'title',
      label: '标题',
      kind: 'modified',
      before: '旧',
      after: '新',
    });
  });

  it('keeps supported common event fields visible when provider metadata surrounds them', () => {
    const entries = diffGenericFields(
      {
        Title: '旧标题',
        iCalUId: '0400-old',
        Location: '会议室 A',
        recurrenceId: '2026-07-14T09:00:00',
        DtStart: '2026-07-14T09:00:00',
        sourceIcsComponent: 'BEGIN:VEVENT',
      },
      {
        title: '新标题',
        icaluid: '0400-new',
        location: '会议室 A',
        recurrence_id: '2026-07-14T10:00:00',
        dtStart: '2026-07-14T09:30:00',
        source_ics_component: 'BEGIN:VTODO',
      },
    );
    assert.deepEqual(entries.map(e => e.key), ['title', 'dtStart']);
    assert.deepEqual(entries[0], {
      key: 'title',
      label: '标题',
      kind: 'modified',
      before: '旧标题',
      after: '新标题',
    });
    assert.deepEqual(entries[1], {
      key: 'dtStart',
      label: '开始时间',
      kind: 'modified',
      before: '2026-07-14T09:00:00',
      after: '2026-07-14T09:30:00',
    });
  });
});

describe('safeChangedFields', () => {
  it('keeps title and timeZoneId with Chinese labels, dropping unknown keys', () => {
    assert.deepEqual(
      safeChangedFields(['title', 'timeZoneId', 'mysteryKey', 'rRule', 'source']),
      [
        { key: 'title', label: '标题' },
        { key: 'timeZoneId', label: '时区' },
      ],
    );
  });

  it('strips sensitive provider keys in every casing and never renders raw names', () => {
    const result = safeChangedFields([
      'Title',
      'ChangeKey',
      'change_key',
      'OutlookEtag',
      'graphEventId',
      'outlookCalendarBindingId',
      'externalMetadataJson',
      'Title',
      'subject',
    ]);
    assert.deepEqual(result, [{ key: 'title', label: '标题' }]);
  });

  it('canonicalizes exact known casing variants and de-duplicates preserving input order', () => {
    const result = safeChangedFields(['Subject', 'subject', 'DtStart', 'dtStart', 'StartsAt', 'timeZoneId', 'timeZoneId']);
    assert.deepEqual(result, [
      { key: 'title', label: '标题' },
      { key: 'dtStart', label: '开始时间' },
      { key: 'timeZoneId', label: '时区' },
    ]);
  });

  it('treats null, undefined and empty arrays as no fields', () => {
    assert.deepEqual(safeChangedFields(null), []);
    assert.deepEqual(safeChangedFields(undefined), []);
    assert.deepEqual(safeChangedFields([]), []);
  });
});

describe('safeExternalEffectText', () => {
  it('hides Graph event ids, change keys, ETags and provider ids behind static Chinese summaries', () => {
    assert.equal(safeExternalEffectText('graph-evt-001'), '（外部标识已隐藏）');
    assert.equal(safeExternalEffectText('ck-abc123'), '（外部标识已隐藏）');
    assert.equal(safeExternalEffectText('etag-new-001'), '（外部标识已隐藏）');
    assert.equal(safeExternalEffectText('W/"etag-xyz"'), '（外部标识已隐藏）');
    assert.equal(
      safeExternalEffectText('a1b2c3d4-e5f6-7890-abcd-ef1234567890'),
      '（外部标识已隐藏）',
    );
  });

  it('hides http(s) URLs behind a static Chinese summary', () => {
    assert.equal(safeExternalEffectText('https://teams.microsoft.com/l/meetup-join/abc'), '（外部链接已隐藏）');
    assert.equal(safeExternalEffectText('http://example.com/calendar?x=1'), '（外部链接已隐藏）');
  });

  it('hides raw Graph event ids that use the AAMk provider prefix', () => {
    assert.equal(
      safeExternalEffectText('AAMkADe1f2g3h4i5j6k7l8m9n0p1q2r3s4t5'),
      '（外部标识已隐藏）',
    );
    assert.equal(
      safeExternalEffectText('GraphEventId=AAMkADe1f2g3h4i5j6k7l8m9n0'),
      '（外部标识已隐藏）',
    );
  });

  it('keeps ordinary text readable and length-bounded', () => {
    assert.equal(safeExternalEffectText('普通标题'), '普通标题');
    assert.equal(safeExternalEffectText('会议室 A'), '会议室 A');
    const long = '普通内容'.repeat(100);
    const rendered = safeExternalEffectText(long);
    assert.ok(rendered.length < long.length, 'long safe text must be truncated');
    assert.ok(rendered.endsWith('…'));
  });

  it('never exposes a broad id matcher: plain words containing id stay readable', () => {
    assert.equal(safeExternalEffectText('identified via uid'), 'identified via uid');
    assert.equal(safeExternalEffectText('grid layout'), 'grid layout');
  });

  it('formats empty values as empty and numbers as ordinary text', () => {
    assert.equal(safeExternalEffectText(''), '');
    assert.equal(safeExternalEffectText(15), '15');
    assert.equal(safeExternalEffectText(undefined), '');
  });
});
