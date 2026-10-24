import { useRef } from 'react';
import type { KeyboardEvent } from 'react';

interface SegmentedOption<T extends string> {
  value: T;
  label: string;
}

interface SegmentedControlProps<T extends string> {
  value: T;
  options: SegmentedOption<T>[];
  onChange: (value: T) => void;
  ariaLabel: string;
}

export default function SegmentedControl<T extends string>({
  value,
  options,
  onChange,
  ariaLabel,
}: SegmentedControlProps<T>) {
  const buttonRefs = useRef<Array<HTMLButtonElement | null>>([]);
  const selectedIndex = options.findIndex(option => option.value === value);
  const tabbableIndex = selectedIndex >= 0 ? selectedIndex : 0;

  function moveToOption(nextIndex: number) {
    const nextOption = options[nextIndex];
    if (!nextOption) return;
    onChange(nextOption.value);
    requestAnimationFrame(() => buttonRefs.current[nextIndex]?.focus());
  }

  function handleKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    if (options.length === 0) return;

    switch (event.key) {
      case 'ArrowRight':
      case 'ArrowDown':
        event.preventDefault();
        moveToOption((index + 1) % options.length);
        break;
      case 'ArrowLeft':
      case 'ArrowUp':
        event.preventDefault();
        moveToOption((index - 1 + options.length) % options.length);
        break;
      case 'Home':
        event.preventDefault();
        moveToOption(0);
        break;
      case 'End':
        event.preventDefault();
        moveToOption(options.length - 1);
        break;
    }
  }

  return (
    <div className="inline-flex rounded-xl border border-slate-200 bg-slate-100 p-1" role="radiogroup" aria-label={ariaLabel}>
      {options.map((option, index) => (
        <button
          key={option.value}
          ref={element => {
            buttonRefs.current[index] = element;
          }}
          type="button"
          role="radio"
          aria-checked={value === option.value}
          tabIndex={index === tabbableIndex ? 0 : -1}
          onClick={() => onChange(option.value)}
          onKeyDown={event => handleKeyDown(event, index)}
          className={`px-3 py-1.5 text-sm rounded-lg transition-colors ${
            value === option.value
              ? 'bg-blue-600 text-white shadow-sm'
              : 'text-slate-600 hover:bg-white'
          }`}
        >
          {option.label}
        </button>
      ))}
    </div>
  );
}
