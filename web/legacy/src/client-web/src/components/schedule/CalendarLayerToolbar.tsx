import type { CalendarLayerId } from '../../types';

export interface CalendarLayerToolbarOption {
  value: CalendarLayerId;
  label: string;
}

interface CalendarLayerToolbarProps {
  options: CalendarLayerToolbarOption[];
  activeLayerIds: CalendarLayerId[];
  outlookOnly: boolean;
  onToggleLayer: (layerId: CalendarLayerId) => void;
  onToggleOutlookOnly: (enabled: boolean) => void;
}

export default function CalendarLayerToolbar({
  options,
  activeLayerIds,
  outlookOnly,
  onToggleLayer,
  onToggleOutlookOnly,
}: CalendarLayerToolbarProps) {
  const activeLayers = new Set(activeLayerIds);

  return (
    <section className="pim-panel flex flex-wrap items-center gap-2 p-3" aria-label="日历图层工具栏">
      <span className="mr-1 text-xs font-semibold text-slate-500">图层</span>
      {options.map(layer => {
        const active = activeLayers.has(layer.value);

        return (
          <button
            key={layer.value}
            type="button"
            onClick={() => onToggleLayer(layer.value)}
            aria-pressed={active}
            className={`rounded-lg border px-3 py-1.5 text-xs font-semibold transition-colors ${
              active
                ? 'border-blue-200 bg-blue-50 text-blue-700'
                : 'border-slate-200 bg-white text-slate-500 hover:bg-slate-50'
            }`}
          >
            {layer.label}
          </button>
        );
      })}
      <label className="ml-auto inline-flex items-center gap-2 rounded-lg border border-slate-200 bg-white px-3 py-1.5 text-xs font-semibold text-slate-600">
        <input
          type="checkbox"
          checked={outlookOnly}
          onChange={event => onToggleOutlookOnly(event.target.checked)}
          className="h-3.5 w-3.5 rounded border-slate-300 text-blue-600 focus:ring-blue-200"
        />
        仅看微软同步
      </label>
    </section>
  );
}
