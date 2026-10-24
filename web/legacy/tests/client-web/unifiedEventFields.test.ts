import assert from 'node:assert/strict';
import { describe, it } from 'node:test';
import {
  buildUnifiedEventDraft,
  type EventFormValue,
} from '../../src/client-web/src/utils/eventDraft';
import type {
  EventResponse,
  OutlookEventDraft,
  OutlookWriteResult,
  UnifiedEventDraft,
} from '../../src/client-web/src/types';

const form: EventFormValue = {
  calendarId: 'cal-1',
  title: ' 同步会议 ',
  description: ' 会议说明 ',
  descriptionFormat: 'html',
  location: ' 会议室 A ',
  dtStart: '2026-07-13T09:00',
  dtEnd: '2026-07-13T10:00',
  isAllDay: false,
  timeZoneId: 'Asia/Shanghai',
  showAs: 'busy',
  importance: 'normal',
  sensitivity: 'normal',
  categories: [' 工作 ', '工作', '私事'],
  isReminderOn: true,
  reminderMinutesBeforeStart: 15,
  organizer: { name: '张三', email: 'zhangsan@example.com' },
  attendees: [
    { name: ' 李四 ', email: 'lisi@example.com', type: 'required' },
    { name: '  ', email: '', type: 'optional' },
    { name: '王五', email: ' wang@example.com ', type: ' ' },
  ],
  isOnlineMeeting: true,
  onlineMeetingProvider: 'teams',
  onlineMeetingUrl: 'https://teams.example.com/m/1',
  externalLink: ' https://example.com/note ',
  attachmentReferences: [{ kind: 'file', id: 'f1', name: '议程.pdf' }],
};

describe('buildUnifiedEventDraft', () => {
  it('normalizes strings, categories, organizer and attendees, and converts datetime-local values', () => {
    const draft = buildUnifiedEventDraft(form);
    assert.equal(draft.title, '同步会议');
    assert.equal(draft.description, '会议说明');
    assert.equal(draft.location, '会议室 A');
    assert.equal(draft.descriptionFormat, 'html');
    assert.equal(draft.showAs, 'busy');
    assert.equal(draft.importance, 'normal');
    assert.equal(draft.sensitivity, 'normal');
    assert.equal(draft.dtStart, '2026-07-13T01:00:00.000Z');
    assert.equal(draft.dtEnd, '2026-07-13T02:00:00.000Z');
    assert.equal(draft.timeZoneId, 'Asia/Shanghai');
    assert.deepEqual(draft.categories, ['工作', '私事']);
    assert.equal(draft.reminderMinutesBeforeStart, 15);
    assert.deepEqual(draft.organizer, { name: '张三', email: 'zhangsan@example.com' });
    assert.deepEqual(draft.attendees, [
      { name: '李四', email: 'lisi@example.com', type: 'required' },
      { name: '王五', email: 'wang@example.com', type: 'required' },
    ]);
    assert.equal(draft.isOnlineMeeting, true);
    assert.equal(draft.onlineMeetingProvider, 'teams');
    assert.equal(draft.onlineMeetingUrl, 'https://teams.example.com/m/1');
    assert.equal(draft.externalLink, 'https://example.com/note');
    assert.deepEqual(draft.attachmentReferences, [{ kind: 'file', id: 'f1', name: '议程.pdf' }]);
  });

  it('never emits uid or rRule', () => {
    const draft = buildUnifiedEventDraft(form);
    assert.equal('uid' in draft, false);
    assert.equal('rRule' in draft, false);
    // @ts-expect-error rRule must not be part of the unified draft type
    draft.rRule;
    // @ts-expect-error uid must not be part of the unified draft type
    draft.uid;
  });

  it('returns empty arrays and null reminders when fields are absent or disabled', () => {
    const empty: EventFormValue = {
      calendarId: 'cal-2',
      title: '空表单',
      dtStart: '2026-07-13T09:00',
      dtEnd: '2026-07-13T10:00',
      categories: [],
      isReminderOn: false,
      reminderMinutesBeforeStart: 15,
      organizer: { name: '  ', email: '' },
      attendees: [{ name: ' ', email: '  ' }],
      attachmentReferences: undefined,
    };
    const draft = buildUnifiedEventDraft(empty);
    assert.deepEqual(draft.categories, []);
    assert.equal(draft.reminderMinutesBeforeStart, null);
    assert.equal(draft.organizer, null);
    assert.deepEqual(draft.attendees, []);
    assert.deepEqual(draft.attachmentReferences, []);
    assert.equal(draft.description, null);
    assert.equal(draft.location, null);
  });

  it('produces identical common drafts for the manual API request and the Outlook command', () => {
    const common = buildUnifiedEventDraft(form);
    const manual: Partial<UnifiedEventDraft> = { ...common };
    const outlook: OutlookEventDraft = { ...common };
    assert.deepEqual(manual, outlook);
    assert.deepEqual(common, buildUnifiedEventDraft(form));
  });
});

describe('unified event response contract', () => {
  it('mirrors the backend EventResponse fields and keeps raw metadata private', () => {
    const response: EventResponse = {
      id: 'event-1',
      calendarId: 'cal-1',
      uid: 'uid-1',
      title: '同步会议',
      description: '<p>说明</p>',
      descriptionFormat: 'html',
      location: '会议室 A',
      dtStart: '2026-07-13T01:00:00.000Z',
      dtEnd: '2026-07-13T02:00:00.000Z',
      status: 'confirmed',
      source: 'outlook',
      showAs: 'busy',
      importance: 'normal',
      sensitivity: 'normal',
      categories: ['工作'],
      isReminderOn: true,
      reminderMinutesBeforeStart: 15,
      organizer: { name: '张三', email: 'zhangsan@example.com' },
      attendees: [{ name: '李四', email: 'lisi@example.com', type: 'required' }],
      isOnlineMeeting: true,
      onlineMeetingProvider: 'teams',
      onlineMeetingUrl: 'https://teams.example.com/m/1',
      externalLink: 'https://example.com/note',
      attachmentReferences: [{ kind: 'file', id: 'f1', name: '议程.pdf', canDownload: true }],
      outlookAdditionalInfo: {
        groups: [
          {
            key: 'meeting',
            label: '会议',
            items: [
              { key: 'joinUrl', label: '加入链接', value: 'https://teams.example.com/m/1' },
            ],
          },
        ],
        hiddenFieldCount: 2,
      },
    };
    assert.ok(response.outlookAdditionalInfo);
    assert.equal(response.outlookAdditionalInfo?.groups.length, 1);
    assert.equal(response.outlookAdditionalInfo?.hiddenFieldCount, 2);
    assert.equal(response.descriptionFormat, 'html');
    assert.deepEqual(response.categories, ['工作']);
    assert.equal(response.organizer?.email, 'zhangsan@example.com');
    assert.equal('externalMetadataJson' in response, false);
    // @ts-expect-error externalMetadataJson must not exist on EventResponse
    response.externalMetadataJson;
  });

  it('OutlookWriteResult exposes typed latestEvent and keeps latestOutlookJson deprecated', () => {
    const event: EventResponse = {
      id: 'event-1',
      calendarId: 'cal-1',
      uid: 'uid-1',
      title: '同步会议',
      dtStart: '2026-07-13T01:00:00.000Z',
      dtEnd: '2026-07-13T02:00:00.000Z',
      status: 'confirmed',
      source: 'outlook',
    };
    const result: OutlookWriteResult = {
      status: 'conflict',
      latestEvent: event,
      latestOutlookJson: '{"legacy":true}',
      latestEtag: 'etag-1',
    };
    assert.equal(result.latestEvent?.title, '同步会议');
    assert.equal(result.latestEvent?.id, 'event-1');
    assert.equal(result.latestOutlookJson, '{"legacy":true}');
  });

  it('keeps OutlookEventDraft compatible with old consumers that set uid and rRule', () => {
    const legacyDraft: OutlookEventDraft = {
      calendarId: 'cal-1',
      title: '旧客户',
      dtStart: '2026-07-13T01:00:00.000Z',
      dtEnd: '2026-07-13T02:00:00.000Z',
      rRule: 'FREQ=DAILY',
      uid: 'uid-legacy',
    };
    assert.equal(legacyDraft.uid, 'uid-legacy');
    assert.equal(legacyDraft.rRule, 'FREQ=DAILY');
    assert.equal(legacyDraft.title, '旧客户');
  });
});
