import { Field } from '../../dialogs/common';
import type { EventFormValue } from '../../utils/eventDraft';

interface EventMeetingFieldsProps {
  form: EventFormValue;
  onChange: (patch: Partial<EventFormValue>) => void;
  disabled?: boolean;
  providerReadOnly?: boolean;
}

const PROVIDER_OPTIONS = [
  { value: 'teams', label: '微软 Teams' },
  { value: 'zoom', label: 'Zoom' },
  { value: 'meet', label: 'Google Meet' },
  { value: 'other', label: '其他' },
] as const;

const inputClass = 'w-full border rounded px-3 py-2 text-sm disabled:bg-slate-100 disabled:text-slate-500';

export default function EventMeetingFields({
  form,
  onChange,
  disabled = false,
  providerReadOnly = false,
}: EventMeetingFieldsProps) {
  return (
    <div className="space-y-3">
      <label className="flex items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700">
        <input
          type="checkbox"
          checked={Boolean(form.isOnlineMeeting)}
          onChange={e => onChange({ isOnlineMeeting: e.target.checked })}
          disabled={disabled}
          className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-200"
        />
        在线会议
      </label>

      <Field label="会议提供方">
        <select
          value={form.onlineMeetingProvider ?? ''}
          onChange={e => onChange({ onlineMeetingProvider: e.target.value })}
          disabled={disabled || !form.isOnlineMeeting}
          className={inputClass}
        >
          <option value="">未设置</option>
          {PROVIDER_OPTIONS.map(option => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </select>
      </Field>

      <Field label="会议链接">
        <input
          type="url"
          value={form.onlineMeetingUrl ?? ''}
          onChange={e => onChange({ onlineMeetingUrl: e.target.value })}
          disabled={disabled || providerReadOnly}
          placeholder="https://..."
          className={inputClass}
        />
      </Field>

      <Field label="外部链接">
        <input
          type="url"
          value={form.externalLink ?? ''}
          onChange={e => onChange({ externalLink: e.target.value })}
          disabled={disabled || providerReadOnly}
          placeholder="https://..."
          className={inputClass}
        />
      </Field>
    </div>
  );
}
