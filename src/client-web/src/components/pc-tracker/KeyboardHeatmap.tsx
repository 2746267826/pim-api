import type { KeystatsSummary } from '../../types';

const TEAL_STOPS = ['#f8fafc', '#ccfbf1', '#5eead4', '#14b8a6', '#0f766e'];
const MODIFIER_KEYS = new Set(['Ctrl', 'Shift', 'Win', 'Alt', 'Fn', 'Menu', 'Esc', 'Tab', 'Caps', 'CapsLock']);

interface SafeKeyCount {
  keyName: string;
  count: number;
}

type KeySpec = { code: string; label: string; units?: number };
type KeyRow = KeySpec[];
type KeyCluster = { name: string; rows: KeyRow[] };

function keyColor(count: number, max: number): string {
  if (count === 0 || max === 0) return TEAL_STOPS[0];
  const ratio = Math.min(count / max, 1);
  const idx = ratio * (TEAL_STOPS.length - 1);
  const low = Math.floor(idx);
  const high = Math.min(low + 1, TEAL_STOPS.length - 1);
  const t = idx - low;
  const l = parseInt(TEAL_STOPS[low].slice(1), 16);
  const h = parseInt(TEAL_STOPS[high].slice(1), 16);
  const r = Math.round(((l >> 16) & 0xff) + t * (((h >> 16) & 0xff) - ((l >> 16) & 0xff)));
  const g = Math.round(((l >> 8) & 0xff) + t * (((h >> 8) & 0xff) - ((l >> 8) & 0xff)));
  const b = Math.round((l & 0xff) + t * ((h & 0xff) - (l & 0xff)));
  return `rgb(${r},${g},${b})`;
}

function textColor(count: number, max: number): string {
  return count > max * 0.42 ? '#fff' : '#334155';
}

function normalizeKey(item: unknown): SafeKeyCount | null {
  if (!item || typeof item !== 'object') return null;
  const value = item as { keyName?: unknown; count?: unknown };
  if (typeof value.keyName !== 'string' || value.keyName.length === 0) return null;
  return {
    keyName: value.keyName,
    count: typeof value.count === 'number' && Number.isFinite(value.count) ? value.count : 0,
  };
}

function normalizeKeyCountEntries(value: unknown): SafeKeyCount[] {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return [];

  return Object.entries(value as Record<string, unknown>)
    .map(([keyName, count]) => ({
      keyName,
      count: typeof count === 'number' && Number.isFinite(count) ? count : 0,
    }))
    .filter(key => key.keyName.length > 0);
}

function key(code: string, label = code, units = 1): KeySpec {
  return { code, label, units };
}

const KEYBOARD_CLUSTERS: KeyCluster[] = [
  {
    name: 'Main',
    rows: [
      [key('Esc'), key('F1'), key('F2'), key('F3'), key('F4'), key('F5'), key('F6'), key('F7'), key('F8'), key('F9'), key('F10'), key('F11'), key('F12')],
      [key('`', '`'), key('1'), key('2'), key('3'), key('4'), key('5'), key('6'), key('7'), key('8'), key('9'), key('0'), key('-', '-'), key('=', '='), key('Backspace', 'Back', 2)],
      [key('Tab', 'Tab', 1.5), key('Q'), key('W'), key('E'), key('R'), key('T'), key('Y'), key('U'), key('I'), key('O'), key('P'), key('[', '['), key(']', ']'), key('\\', '\\', 1.5)],
      [key('CapsLock', 'Caps', 1.75), key('A'), key('S'), key('D'), key('F'), key('G'), key('H'), key('J'), key('K'), key('L'), key(';', ';'), key("'", "'"), key('Enter', 'Enter', 2.25)],
      [key('Shift', 'Shift', 2.25), key('Z'), key('X'), key('C'), key('V'), key('B'), key('N'), key('M'), key(',', ','), key('.', '.'), key('/', '/'), key('RShift', 'Shift', 2.75)],
      [key('Ctrl', 'Ctrl', 1.25), key('Win', 'Win', 1.25), key('Alt', 'Alt', 1.25), key('Space', 'Space', 6.25), key('RAlt', 'Alt', 1.25), key('RWin', 'Win', 1.25), key('Menu', 'Menu', 1.25), key('RCtrl', 'Ctrl', 1.25)],
    ],
  },
  {
    name: 'Navigation',
    rows: [
      [key('PrintScreen', 'Prt'), key('ScrollLock', 'Scr'), key('Pause', 'Pause')],
      [key('Insert', 'Ins'), key('Home'), key('PageUp', 'PgUp')],
      [key('Delete', 'Del'), key('End'), key('PageDown', 'PgDn')],
      [key('ArrowBlank1', '', 1), key('Up', '↑'), key('ArrowBlank2', '', 1)],
      [key('Left', '←'), key('Down', '↓'), key('Right', '→')],
    ],
  },
  {
    name: 'Numpad',
    rows: [
      [key('NumLock', 'Num'), key('NumpadDivide', '/'), key('NumpadMultiply', '*'), key('NumpadSubtract', '-')],
      [key('Numpad7', '7'), key('Numpad8', '8'), key('Numpad9', '9'), key('NumpadAdd', '+')],
      [key('Numpad4', '4'), key('Numpad5', '5'), key('Numpad6', '6'), key('NumpadAdd2', '+')],
      [key('Numpad1', '1'), key('Numpad2', '2'), key('Numpad3', '3'), key('NumpadEnter', 'Enter')],
      [key('Numpad0', '0', 2), key('NumpadDecimal', '.'), key('NumpadEnter2', 'Enter')],
    ],
  },
];

function aliasesFor(code: string) {
  const map: Record<string, string[]> = {
    Ctrl: ['Ctrl', 'LCtrl', 'ControlLeft'],
    RCtrl: ['RCtrl', 'ControlRight'],
    Shift: ['Shift', 'LShift', 'ShiftLeft'],
    RShift: ['RShift', 'ShiftRight'],
    Alt: ['Alt', 'LAlt', 'AltLeft'],
    RAlt: ['RAlt', 'AltRight'],
    Win: ['Win', 'LWin', 'MetaLeft'],
    RWin: ['RWin', 'MetaRight'],
    Space: ['Space', 'Spacebar', ' '],
    Backspace: ['Backspace', 'Back'],
    CapsLock: ['CapsLock', 'Caps'],
    Up: ['Up', 'ArrowUp'],
    Down: ['Down', 'ArrowDown'],
    Left: ['Left', 'ArrowLeft'],
    Right: ['Right', 'ArrowRight'],
    PageUp: ['PageUp', 'PgUp'],
    PageDown: ['PageDown', 'PgDn'],
    PrintScreen: ['PrintScreen', 'PrtSc'],
    NumpadDivide: ['NumpadDivide', 'Num/', 'Num /'],
    NumpadMultiply: ['NumpadMultiply', 'Num*', 'Num *'],
    NumpadSubtract: ['NumpadSubtract', 'Num-', 'Num -'],
    NumpadAdd: ['NumpadAdd', 'Num+', 'Num +'],
    NumpadAdd2: ['NumpadAdd', 'Num+', 'Num +'],
    NumpadEnter: ['NumpadEnter', 'NumEnter', 'Num Enter'],
    NumpadEnter2: ['NumpadEnter', 'NumEnter', 'Num Enter'],
    NumpadDecimal: ['NumpadDecimal', 'Num.', 'Num .'],
  };

  if (code.startsWith('Numpad') && /^Numpad\d$/.test(code)) {
    return [code, `Num${code.slice(-1)}`, `Num ${code.slice(-1)}`];
  }

  if (code.startsWith('ArrowBlank')) return [];
  return map[code] ?? [code];
}

function countForKey(keyCounts: Map<string, number>, code: string) {
  return aliasesFor(code).reduce((sum, alias) => sum + (keyCounts.get(alias) ?? 0), 0);
}

function isModifier(code: string) {
  return MODIFIER_KEYS.has(code) || code.startsWith('R') && MODIFIER_KEYS.has(code.slice(1));
}

function KeyCap({
  spec,
  count,
  maxKey,
}: {
  spec: KeySpec;
  count: number;
  maxKey: number;
}) {
  if (!spec.label) {
    return <div style={{ width: `${(spec.units ?? 1) * 2.15}rem` }} />;
  }

  const mod = isModifier(spec.code);
  const backgroundColor = mod ? '#e2e8f0' : keyColor(count, maxKey);
  const color = mod ? '#64748b' : textColor(count, maxKey);

  return (
    <div
      tabIndex={0}
      className="group relative flex h-9 items-center justify-center rounded-lg border border-white/80 font-mono text-[10px] font-semibold shadow-sm outline-none transition-transform hover:-translate-y-0.5 hover:ring-2 hover:ring-blue-300 focus:ring-2 focus:ring-blue-300"
      style={{
        width: `${(spec.units ?? 1) * 2.15}rem`,
        minWidth: `${(spec.units ?? 1) * 2.15}rem`,
        backgroundColor,
        color,
      }}
      title={`${spec.label}: ${count.toLocaleString('zh-CN')}`}
    >
      {spec.label}
      {count > 0 && (
        <span className="absolute bottom-0.5 right-1 text-[8px] leading-none" style={{ color }}>{count}</span>
      )}
      <div className="pointer-events-none absolute bottom-full left-1/2 z-50 mb-2 hidden -translate-x-1/2 whitespace-nowrap rounded-xl bg-slate-950 px-2 py-1 text-[10px] text-white shadow-2xl group-hover:block group-focus:block">
        {spec.label}: {count.toLocaleString('zh-CN')}
      </div>
    </div>
  );
}

function KeyboardCluster({ cluster, keyCounts, maxKey }: { cluster: KeyCluster; keyCounts: Map<string, number>; maxKey: number }) {
  return (
    <div className="flex flex-col gap-1">
      <div className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-400">{cluster.name}</div>
      {cluster.rows.map((row, rowIndex) => (
        <div key={rowIndex} className="flex gap-1">
          {row.map((spec, index) => (
            <KeyCap key={`${spec.code}-${index}`} spec={spec} count={countForKey(keyCounts, spec.code)} maxKey={maxKey} />
          ))}
        </div>
      ))}
    </div>
  );
}

interface MouseZoneProps {
  label: string;
  zone: string;
  count: number;
  maxKey: number;
  x: number;
  y: number;
  width: number;
  height: number;
  rx: number;
  /**
   * 标签与计数画在区块内还是区块下方。
   *
   * 滚轮槽只有 18px 宽（滚轮键 =「中键」），两个字 + 五位计数塞不进去，硬塞会压到
   * 左/右键区块上（见 WO-FRONTEND-PC-20261001 REQ-1 的界面实测）。该槽改为在下方
   * 居中标注，位置由 `x + width / 2` 与滚轮槽对齐。
   */
  labelPlacement?: 'inside' | 'below';
}

function MouseZone({
  label,
  zone,
  count,
  maxKey,
  x,
  y,
  width,
  height,
  rx,
  labelPlacement = 'inside',
}: MouseZoneProps) {
  const bg = keyColor(count, maxKey);
  const color = textColor(count, maxKey);
  const title = `${label}: ${count.toLocaleString('zh-CN')}`;
  const labelY = labelPlacement === 'below' ? y + height + 14 : y + height / 2 - 2;
  const countY = labelPlacement === 'below' ? y + height + 27 : y + height / 2 + 12;

  return (
    <g
      tabIndex={0}
      data-mouse-zone={zone}
      className="outline-none focus-visible:ring-2 focus-visible:ring-blue-300"
      role="img"
      aria-label={title}
    >
      <title>{title}</title>
      <rect
        x={x}
        y={y}
        width={width}
        height={height}
        rx={rx}
        fill={bg}
        stroke="rgba(255,255,255,0.9)"
        strokeWidth="2"
      />
      <text
        x={x + width / 2}
        y={labelY}
        textAnchor="middle"
        dominantBaseline="middle"
        fill={color}
        fontSize="11"
        fontWeight="700"
      >
        {label}
      </text>
      {count > 0 && (
        <text
          x={x + width / 2}
          y={countY}
          textAnchor="middle"
          dominantBaseline="middle"
          fill={color}
          fontSize="10"
          fontWeight="700"
        >
          {count.toLocaleString('zh-CN')}
        </text>
      )}
    </g>
  );
}

/**
 * 滚轮量（`scrollDistance`，单位 px）的界面取值。
 *
 * 与点击次数（次数）量级差 3 个数量级，所以不参与鼠标区的色阶归一化，单独成列展示
 * （WO-FRONTEND-PC-20261001 D-1 / P 表默认方案：卡片内单列数字、原始量级）。
 * 保留两位小数，与接口原值（实测 474069.94）对得上，不做四舍五入到整数。
 */
function formatScrollDistance(value: number): string {
  const safe = Number.isFinite(value) ? value : 0;
  return `${safe.toLocaleString('zh-CN', { minimumFractionDigits: 0, maximumFractionDigits: 2 })} px`;
}

function MouseHeatmap({ keystats, maxKey }: { keystats: KeystatsView; maxKey: number }) {
  // 色阶只按点击次数归一化：`scrollDistance` 是滚动距离（px），混进来会把所有按键/点击
  // 都压成接近 0 的浅色。
  const maxMouse = Math.max(
    maxKey,
    keystats.leftClicks,
    keystats.rightClicks,
    keystats.middleClicks,
    keystats.sideBackClicks,
    keystats.sideForwardClicks,
    1,
  );

  return (
    <div className="min-w-[260px] rounded-2xl border border-slate-200 bg-white/85 p-4">
      <div className="mb-3 text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-400">鼠标</div>
      <svg
        viewBox="0 0 240 300"
        className="mx-auto h-72 w-full max-w-[240px]"
        role="img"
        aria-label="鼠标热力图"
      >
        <defs>
          <filter id="mouse-shadow" x="-20%" y="-10%" width="140%" height="130%">
            <feDropShadow dx="0" dy="8" stdDeviation="8" floodColor="#0f172a" floodOpacity="0.12" />
          </filter>
        </defs>
        <rect x="62" y="12" width="130" height="246" rx="58" fill="#f1f5f9" stroke="#dbe3ee" strokeWidth="2" filter="url(#mouse-shadow)" />
        <path d="M127 18v104" stroke="#dbe3ee" strokeWidth="2" strokeLinecap="round" />
        <MouseZone label="左键" zone="left" count={keystats.leftClicks} maxKey={maxMouse} x={68} y={24} width={56} height={92} rx={26} />
        <MouseZone label="右键" zone="right" count={keystats.rightClicks} maxKey={maxMouse} x={130} y={24} width={56} height={92} rx={26} />
        {/* 滚轮键（中键）：取值是 `middleClicks`（被按下的次数），不再是「滚轮」量。 */}
        <MouseZone label="中键" zone="middle" count={keystats.middleClicks} maxKey={maxMouse} x={112} y={50} width={18} height={64} rx={9} labelPlacement="below" />
        <MouseZone label="侧后" zone="side-back" count={keystats.sideBackClicks} maxKey={maxMouse} x={28} y={118} width={38} height={48} rx={16} />
        <MouseZone label="侧前" zone="side-forward" count={keystats.sideForwardClicks} maxKey={maxMouse} x={28} y={176} width={38} height={48} rx={16} />
        <circle cx="127" cy="242" r="11" fill="#e2e8f0" stroke="rgba(255,255,255,0.9)" strokeWidth="2" />
        <text x="127" y="270" textAnchor="middle" fill="#475569" fontSize="13" fontWeight="700">
          {keystats.totalClicks.toLocaleString('zh-CN')}
        </text>
        <text x="127" y="287" textAnchor="middle" fill="#94a3b8" fontSize="10">
          总点击
        </text>
      </svg>

      {/* 滚轮的真实物理量：滚动距离（与点击次数不是同一个量纲，单列）。 */}
      <div
        data-mouse-metric="scrollDistance"
        className="mt-3 flex items-center justify-between gap-2 rounded-xl border border-slate-200 bg-slate-50 px-3 py-2"
      >
        <span className="text-[10px] font-semibold uppercase tracking-[0.16em] text-slate-400">滚轮量</span>
        <span className="text-sm font-bold text-slate-700">{formatScrollDistance(keystats.scrollDistance)}</span>
      </div>
    </div>
  );
}

interface Props {
  /**
   * 单日视图传 `summary.keystats`（带 `date`），范围视图传 `/pc/aggregation/keystats`
   * 的结果（无 `date`、多 `totalKeyPresses`）—— 同一组件复用（REQ-6 / AC-6.2）。
   */
  keystats: KeystatsView | null;
  /** 请求失败（如范围端点 400 起止颠倒）。显示可读提示，不静默空白（REQ-6.3）。 */
  error?: unknown;
}

/** 单日 summary.keystats 与范围聚合结果共有的字段视图（范围结果没有 `date`）。 */
export type KeystatsView = Omit<KeystatsSummary, 'date'> & { date?: string };

function readableError(error: unknown): string {
  if (error instanceof Error && error.message) return error.message;
  if (typeof error === 'string' && error) return error;
  return '键鼠统计加载失败，请稍后重试。';
}

export default function KeyboardHeatmap({ keystats, error }: Props) {
  const safeKeystats: KeystatsView = keystats ?? {
    keyPresses: 0,
    totalClicks: 0,
    leftClicks: 0,
    rightClicks: 0,
    middleClicks: 0,
    sideBackClicks: 0,
    sideForwardClicks: 0,
    mouseDistance: 0,
    scrollDistance: 0,
    peakKps: 0,
    peakCps: 0,
    keyPressCounts: {},
    topKeys: [],
  };

  const completeKeyCounts = normalizeKeyCountEntries(safeKeystats.keyPressCounts);
  const topKeys = Array.isArray(safeKeystats.topKeys)
    ? safeKeystats.topKeys.map(normalizeKey).filter(key => key !== null)
    : [];
  const heatmapKeys = completeKeyCounts.length > 0 ? completeKeyCounts : topKeys;
  const keyCounts = new Map(heatmapKeys.map(key => [key.keyName, key.count]));
  const allCounts = heatmapKeys.map(key => key.count);
  const maxKey = Math.max(...allCounts, 1);

  const shortcuts = heatmapKeys
    .filter(keyItem => keyItem.keyName.includes('+'))
    .sort((a, b) => b.count - a.count);

  // 请求失败且**没有任何数据**时，不要画一张「全是 0 次」的矩阵 —— 那会被读成真实数据（REQ-6.3）。
  // 但 React Query 在刷新失败时会同时给出 error 与上一份成功的 data；这种情形要保留矩阵，
  // 只在下方叠加错误条，否则用户会因为一次刷新失败丢掉已经拿到的真实数据。
  if (error && !keystats) {
    return (
      <div role="alert" className="rounded-2xl border border-amber-200 bg-amber-50 px-3 py-3 text-xs text-amber-800">
        <span className="font-medium">键鼠统计请求未成功：</span>
        <span className="break-words">{readableError(error)}</span>
      </div>
    );
  }

  return (
    <div className="space-y-4">
      <div className="overflow-x-auto rounded-2xl border border-slate-200 bg-slate-50 p-4">
        <div className="flex min-w-[1180px] gap-4">
          <div className="flex flex-1 gap-4">
            {KEYBOARD_CLUSTERS.map(cluster => (
              <KeyboardCluster key={cluster.name} cluster={cluster} keyCounts={keyCounts} maxKey={maxKey} />
            ))}
          </div>
          <MouseHeatmap keystats={safeKeystats} maxKey={maxKey} />
        </div>
      </div>

      {error && keystats ? (
        <div role="alert" className="rounded-2xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-800">
          <span className="font-medium">上次刷新失败，以下为最近一次成功的数据：</span>
          <span className="break-words">{readableError(error)}</span>
        </div>
      ) : null}

      {!keystats ? (
        <div className="rounded-2xl border border-amber-200 bg-amber-50 px-3 py-2 text-xs text-amber-700">
          当前日期暂无键鼠数据，已展示完整键鼠布局骨架。
        </div>
      ) : null}

      {shortcuts.length > 0 && (
        <div className="rounded-2xl border border-slate-200 bg-slate-50 p-3">
          <div className="mb-2 text-xs font-semibold text-slate-700">快捷键统计</div>
          <div className="flex flex-wrap gap-2">
            {shortcuts.map(shortcut => (
              <span key={shortcut.keyName} className="rounded-lg border border-blue-200 bg-blue-50 px-2 py-1 text-xs text-blue-700">
                {shortcut.keyName} <span className="text-blue-500">{shortcut.count.toLocaleString('zh-CN')}</span>
              </span>
            ))}
          </div>
        </div>
      )}
    </div>
  );
}
