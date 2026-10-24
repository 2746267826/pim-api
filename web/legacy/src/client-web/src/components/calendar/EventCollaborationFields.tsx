import { Plus, X } from 'lucide-react';
import type { EventAttendee } from '../../types';
import type { EventFormValue } from '../../utils/eventDraft';

interface EventCollaborationFieldsProps {
  form: EventFormValue;
  onChange: (patch: Partial<EventFormValue>) => void;
  disabled?: boolean;
  providerReadOnly?: boolean;
}

const ATTENDEE_TYPE_OPTIONS = [
  { value: 'required', label: '必选' },
  { value: 'optional', label: '可选' },
  { value: 'resource', label: '资源' },
] as const;

const inputClass = 'w-full border rounded px-3 py-2 text-sm disabled:bg-slate-100 disabled:text-slate-500';

export default function EventCollaborationFields({
  form,
  onChange,
  disabled = false,
  providerReadOnly = false,
}: EventCollaborationFieldsProps) {
  function updateOrganizer(patch: Partial<EventAttendee>) {
    onChange({ organizer: { name: form.organizer?.name ?? '', email: form.organizer?.email ?? '', ...patch } });
  }

  function updateAttendee(index: number, patch: Partial<EventAttendee>) {
    const attendees = [...(form.attendees ?? [])];
    attendees[index] = { ...attendees[index], ...patch };
    onChange({ attendees });
  }

  function addAttendee() {
    onChange({ attendees: [...(form.attendees ?? []), { name: '', email: '', type: 'required' }] });
  }

  function removeAttendee(index: number) {
    const attendees = [...(form.attendees ?? [])];
    attendees.splice(index, 1);
    onChange({ attendees });
  }

  return (
    <div className="space-y-3">
      <div>
        <p className="mb-1 text-sm font-medium text-slate-600">组织者</p>
        <div className="flex flex-col gap-2 sm:flex-row">
          <input
            type="text"
            aria-label="组织者姓名"
            placeholder="姓名"
            value={form.organizer?.name ?? ''}
            onChange={e => updateOrganizer({ name: e.target.value })}
            disabled={disabled || providerReadOnly}
            className={inputClass}
          />
          <input
            type="text"
            aria-label="组织者邮箱"
            placeholder="邮箱"
            value={form.organizer?.email ?? ''}
            onChange={e => updateOrganizer({ email: e.target.value })}
            disabled={disabled || providerReadOnly}
            className={inputClass}
          />
        </div>
        {providerReadOnly && !disabled && (
          <p className="mt-1 text-xs text-slate-400">Outlook 组织者由 Microsoft 提供，仅可查看。</p>
        )}
      </div>

      <div>
        <p className="mb-1 text-sm font-medium text-slate-600">参会者</p>
        {(form.attendees ?? []).length === 0 && !disabled && (
          <p className="text-xs text-slate-400">暂无参会者，点击下方按钮添加。</p>
        )}
        <div className="space-y-2">
          {(form.attendees ?? []).map((attendee, index) => (
            <div key={index} className="event-attendee-row" data-attendee-row>
              <input
                type="text"
                aria-label={`参会者 ${index + 1} 姓名`}
                placeholder="姓名"
                value={attendee.name ?? ''}
                onChange={e => updateAttendee(index, { name: e.target.value })}
                disabled={disabled}
                className={inputClass}
              />
              <input
                type="text"
                aria-label={`参会者 ${index + 1} 邮箱`}
                placeholder="邮箱"
                value={attendee.email}
                onChange={e => updateAttendee(index, { email: e.target.value })}
                disabled={disabled}
                className={inputClass}
              />
              <select
                aria-label={`参会者 ${index + 1} 类型`}
                value={attendee.type ?? 'required'}
                onChange={e => updateAttendee(index, { type: e.target.value })}
                disabled={disabled}
                className={`${inputClass} w-auto`}
              >
                {ATTENDEE_TYPE_OPTIONS.map(option => (
                  <option key={option.value} value={option.value}>{option.label}</option>
                ))}
              </select>
              <button
                type="button"
                aria-label={`移除参会者 ${index + 1}`}
                title="移除参会者"
                onClick={() => removeAttendee(index)}
                disabled={disabled}
                className="event-attendee-remove"
              >
                <X size={14} />
              </button>
            </div>
          ))}
        </div>
        <button
          type="button"
          onClick={addAttendee}
          disabled={disabled}
          className="pim-button-secondary mt-2 px-2.5 py-1.5 text-xs disabled:opacity-50"
        >
          <Plus size={14} />
          添加参会者
        </button>
      </div>
    </div>
  );
}
