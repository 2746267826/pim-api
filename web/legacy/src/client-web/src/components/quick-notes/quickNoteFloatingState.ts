export const QUICK_NOTE_DRAFT_KEY = 'pim.quickNotes.floatingDraft';
export const QUICK_NOTE_PANEL_POSITION_KEY = 'pim.quickNotes.panelPosition';

const PANEL_MARGIN = 12;

export interface PanelPoint {
  x: number;
  y: number;
}

export interface PanelSize {
  width: number;
  height: number;
}

export function clampPanelPosition(
  point: PanelPoint,
  viewport: PanelSize,
  panel: PanelSize,
): PanelPoint {
  const maxX = Math.max(PANEL_MARGIN, viewport.width - panel.width - PANEL_MARGIN);
  const maxY = Math.max(PANEL_MARGIN, viewport.height - panel.height - PANEL_MARGIN);

  return {
    x: Math.min(Math.max(point.x, PANEL_MARGIN), maxX),
    y: Math.min(Math.max(point.y, PANEL_MARGIN), maxY),
  };
}

export function loadPanelPosition(viewport: PanelSize, panel: PanelSize): PanelPoint {
  const fallback = clampPanelPosition(
    {
      x: viewport.width - panel.width - PANEL_MARGIN,
      y: viewport.height - panel.height - PANEL_MARGIN,
    },
    viewport,
    panel,
  );

  if (typeof localStorage === 'undefined') {
    return fallback;
  }

  try {
    const stored = localStorage.getItem(QUICK_NOTE_PANEL_POSITION_KEY);
    if (!stored) {
      return fallback;
    }

    const parsed = JSON.parse(stored) as Partial<PanelPoint>;
    if (typeof parsed.x !== 'number' || typeof parsed.y !== 'number') {
      return fallback;
    }

    return clampPanelPosition(parsed as PanelPoint, viewport, panel);
  } catch {
    return fallback;
  }
}

export function savePanelPosition(point: PanelPoint) {
  if (typeof localStorage === 'undefined') {
    return;
  }

  try {
    localStorage.setItem(QUICK_NOTE_PANEL_POSITION_KEY, JSON.stringify(point));
  } catch {
    // Panel position persistence is best-effort.
  }
}

/**
 * 读取上次未保存的快速记录草稿（#300）。
 * 旧 QuickNoteGlobalPanel 有该能力，入口统一到 QuickNoteDialog 后必须保留，
 * 否则关闭 / 刷新会丢失未保存内容。
 */
export function loadQuickNoteDraft(): string {
  if (typeof localStorage === 'undefined') {
    return '';
  }

  try {
    return localStorage.getItem(QUICK_NOTE_DRAFT_KEY) ?? '';
  } catch {
    return '';
  }
}

/** 保存草稿；内容为空时清除键，避免留下空草稿把编辑框占成空串。 */
export function saveQuickNoteDraft(markdown: string) {
  if (typeof localStorage === 'undefined') {
    return;
  }

  try {
    if (markdown) {
      localStorage.setItem(QUICK_NOTE_DRAFT_KEY, markdown);
    } else {
      localStorage.removeItem(QUICK_NOTE_DRAFT_KEY);
    }
  } catch {
    // Draft persistence is best-effort.
  }
}

/** 保存成功后清除草稿，避免下次打开又恢复已提交的内容。 */
export function clearQuickNoteDraft() {
  if (typeof localStorage === 'undefined') {
    return;
  }

  try {
    localStorage.removeItem(QUICK_NOTE_DRAFT_KEY);
  } catch {
    // Clearing the draft is best-effort.
  }
}
