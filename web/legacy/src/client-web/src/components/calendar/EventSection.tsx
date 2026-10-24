import { useId, useState, type ReactNode } from 'react';
import { ChevronDown } from 'lucide-react';

interface EventSectionProps {
  title: string;
  summary?: string;
  defaultOpen?: boolean;
  children: ReactNode;
}

export default function EventSection({ title, summary, defaultOpen = false, children }: EventSectionProps) {
  const [open, setOpen] = useState(defaultOpen);
  const contentId = useId();

  return (
    <section className="event-editor-section" data-event-editor-section>
      <h3 className="event-editor-section-heading">
        <button
          type="button"
          aria-expanded={open}
          aria-controls={contentId}
          onClick={() => setOpen(current => !current)}
          className="event-editor-section-toggle"
        >
          <span className="event-editor-section-title">{title}</span>
          {summary && <span className="event-editor-section-summary">{summary}</span>}
          <ChevronDown
            aria-hidden="true"
            size={16}
            className={`event-editor-section-caret${open ? ' event-editor-section-caret-open' : ''}`}
          />
        </button>
      </h3>
      {open && (
        <div id={contentId} className="event-editor-section-body">{children}</div>
      )}
    </section>
  );
}
