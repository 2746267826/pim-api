import { useState, type KeyboardEvent } from 'react';
import { Plus, X } from 'lucide-react';
import { Field } from '../../dialogs/common';
import type { EventFormValue } from '../../utils/eventDraft';

interface EventAdvancedFieldsProps {
  form: EventFormValue;
  onChange: (patch: Partial<EventFormValue>) => void;
  disabled?: boolean;
}

const SHOW_AS_OPTIONS = [
  { value: 'free', label: '空闲' },
  { value: 'tentative', label: '暂定' },
  { value: 'busy', label: '忙碌' },
  { value: 'oof', label: '不在办公室' },
  { value: 'workingElsewhere', label: '在其他地点办公' },
] as const;

const IMPORTANCE_OPTIONS = [
  { value: 'low', label: '低' },
  { value: 'normal', label: '普通' },
  { value: 'high', label: '高' },
] as const;

const SENSITIVITY_OPTIONS = [
  { value: 'normal', label: '普通' },
  { value: 'personal', label: '个人' },
  { value: 'private', label: '私人' },
  { value: 'confidential', label: '机密' },
] as const;

const inputClass = 'w-full border rounded px-3 py-2 text-sm disabled:bg-slate-100 disabled:text-slate-500';

export default function EventAdvancedFields({ form, onChange, disabled = false }: EventAdvancedFieldsProps) {
  const [categoryInput, setCategoryInput] = useState('');

  function addCategory() {
    const value = categoryInput.trim();
    if (!value) return;
    onChange({ categories: [...(form.categories ?? []), value] });
    setCategoryInput('');
  }

  function removeCategory(index: number) {
    const next = [...(form.categories ?? [])];
    next.splice(index, 1);
    onChange({ categories: next });
  }

  function handleCategoryKeyDown(e: KeyboardEvent<HTMLInputElement>) {
    if (e.key !== 'Enter') return;
    e.preventDefault();
    addCategory();
  }

  return (
    <div className="space-y-3">
      <Field label="显示状态">
        <select
          value={form.showAs ?? ''}
          onChange={e => onChange({ showAs: e.target.value })}
          disabled={disabled}
          className={inputClass}
        >
          <option value="">未设置</option>
          {SHOW_AS_OPTIONS.map(option => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </select>
      </Field>

      <Field label="重要性">
        <select
          value={form.importance ?? ''}
          onChange={e => onChange({ importance: e.target.value })}
          disabled={disabled}
          className={inputClass}
        >
          <option value="">未设置</option>
          {IMPORTANCE_OPTIONS.map(option => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </select>
      </Field>

      <Field label="敏感度">
        <select
          value={form.sensitivity ?? ''}
          onChange={e => onChange({ sensitivity: e.target.value })}
          disabled={disabled}
          className={inputClass}
        >
          <option value="">未设置</option>
          {SENSITIVITY_OPTIONS.map(option => (
            <option key={option.value} value={option.value}>{option.label}</option>
          ))}
        </select>
      </Field>

      <div className="mb-3">
        <span className="text-sm font-medium text-gray-600 block mb-1">分类</span>
        <div className="flex flex-wrap gap-1.5">
          {(form.categories ?? []).map((category, index) => (
            <span key={`${category}-${index}`} className="event-category-chip">
              <span className="min-w-0 break-words">{category}</span>
              <button
                type="button"
                aria-label={`移除分类 ${category}`}
                title={`移除分类 ${category}`}
                onClick={() => removeCategory(index)}
                disabled={disabled}
                className="event-category-chip-remove"
              >
                <X size={12} />
              </button>
            </span>
          ))}
        </div>
        <div className="mt-2 flex flex-wrap items-center gap-2">
          <input
            type="text"
            value={categoryInput}
            onChange={e => setCategoryInput(e.target.value)}
            onKeyDown={handleCategoryKeyDown}
            placeholder="输入分类后回车添加"
            disabled={disabled}
            className={`${inputClass} min-w-0 flex-1`}
          />
          <button
            type="button"
            onClick={addCategory}
            disabled={disabled || categoryInput.trim() === ''}
            className="pim-button-secondary px-2.5 py-1.5 text-xs disabled:opacity-50"
          >
            <Plus size={14} />
            添加分类
          </button>
        </div>
      </div>

      <label className="flex items-center gap-2 rounded-lg border border-slate-200 bg-slate-50 px-3 py-2 text-sm text-slate-700">
        <input
          type="checkbox"
          checked={Boolean(form.isReminderOn)}
          onChange={e => onChange(e.target.checked
            ? { isReminderOn: true, reminderMinutesBeforeStart: form.reminderMinutesBeforeStart ?? 15 }
            : { isReminderOn: false, reminderMinutesBeforeStart: null })}
          disabled={disabled}
          className="h-4 w-4 rounded border-slate-300 text-blue-600 focus:ring-blue-200"
        />
        提醒
      </label>
      {form.isReminderOn && (
        <Field label="提前提醒（分钟）">
          <input
            type="number"
            min={0}
            value={form.reminderMinutesBeforeStart ?? ''}
            onChange={e => {
              const minutes = Number(e.target.value);
              onChange({
                reminderMinutesBeforeStart: e.target.value === '' || !Number.isFinite(minutes) || minutes < 0
                  ? null
                  : minutes,
              });
            }}
            disabled={disabled}
            className={inputClass}
          />
        </Field>
      )}
    </div>
  );
}
