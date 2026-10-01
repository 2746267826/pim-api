import { chartColors } from './chartColors';
import type { EChartsOption } from '../../lib/echarts';
import { PC_BUSINESS_DAY_START_HOUR, PC_BUSINESS_HOURS, pcHourLabel } from '../../utils/pcBusinessDay';
import type { HeatmapBucket, HeatmapGridResponse, PcActivityAnalysisBlock, TimelineItem } from '../../types';

/**
 * 电脑记录页热力/甘特图表 option 纯函数：输入数据、输出 EChartsOption，不依赖组件/页面。
 * 桶到单元格的映射集中在 mapActivityGrid（纯函数，可测），组件点击通过 data 项携带的
 * bucket/segment/blockIndex 反查原始数据。
 */

const MS_PER_DAY = 24 * 60 * 60 * 1000;
const MONDAY_EPOCH_MS = Date.UTC(1970, 0, 5);

/** 周一起算的周内序号（周一=0 … 周日=6） */
function mondayWeekday(date: Date): number {
  return (date.getUTCDay() + 6) % 7;
}

function localDateStr(ms: number): string {
  const d = new Date(ms);
  return `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}-${String(d.getUTCDate()).padStart(2, '0')}`;
}

/**
 * 桶 start → 业务日（yyyy-MM-dd）。
 *
 * `heatmap/grid` 的 day/month/year 桶起点是业务日窗口 `[前一日 20:00Z, 当日 20:00Z)`，
 * 直接取字面量前 10 位会比业务日早一天（20:00Z = 次日 04:00 +08:00）。这里统一换算到
 * 固定 +08:00 再取日期，与后端业务日口径一致（见 utils/pcBusinessDay.ts）。
 */
export function bucketDatePart(start: string): string {
  const ms = Date.parse(start);
  // 无时区的字面量按原样取前 10 位（保守兜底，不让提示整块消失）。
  if (Number.isNaN(ms)) return start.slice(0, 10);
  const shifted = new Date(ms + SHANGHAI_OFFSET_MS);
  return `${shifted.getUTCFullYear()}-${pad(shifted.getUTCMonth() + 1)}-${pad(shifted.getUTCDate())}`;
}

/** 活动热力单元格：x/y 网格坐标 + 原始桶 + 用于着色的强度值 */
export interface ActivityCell {
  x: number;
  y: number;
  bucket: HeatmapBucket;
  value: number;
}

export interface ActivityGridMap {
  cells: ActivityCell[];
  xLabels: string[];
  yLabels: string[];
}

/**
 * 桶 start → **业务日**坐标（该业务日的 UTC 午夜 Date）。
 *
 * `heatmap/grid` 的 day/month/year 桶起点是业务日窗口 `[前一日 20:00Z, 当日 20:00Z)`，
 * 它的 UTC 日历日恰好是业务日的**前一天**。直接 `new Date(start)` 再取 UTC 字段，
 * 会把格子画到前一天/上一周/上一个月（跨月时整格进错月）—— 这正是 REQ-3 要消除的
 * 「整体偏移一天」。换算到固定 +08:00 后再归一到 UTC 午夜，后续用 UTC getter
 * 读出的即业务日的星期、日、月、年。
 */
function parseBucketBusinessDate(start: string): Date | null {
  const ms = Date.parse(start);
  if (Number.isNaN(ms)) return null;
  const shifted = new Date(ms + SHANGHAI_OFFSET_MS);
  return new Date(Date.UTC(shifted.getUTCFullYear(), shifted.getUTCMonth(), shifted.getUTCDate()));
}

/**
 * 桶 → 单元格映射（纯函数，四维度）：
 * - hour：x = 业务时序号（04:00 起，(hour - 4 + 24) % 24），y = 0；
 * - day：x = 周一起算的周内序号，y = 周一锚定周的行序号（跨周升序）；
 * - month：x = 当月第几天 - 1（1-31），y = 月份行序号（升序）；
 * - year：x = 年内周序号（周一锚定，0..52，GitHub calendar 形态），y = 周内序号。
 *
 * day/month/year 的坐标一律按**业务日**（+08:00）计算，与工具提示的日期同源。
 */
export function mapActivityGrid(data: HeatmapGridResponse | undefined): ActivityGridMap | null {
  if (!data) return null;
  const buckets = (data.grid ?? [])
    .flatMap(row => Array.isArray(row) ? row : [])
    .filter((c): c is HeatmapBucket => !!c && typeof c.start === 'string')
    .sort((a, b) => a.start.localeCompare(b.start));

  if (buckets.length === 0) return null;

  const dimension = data.dimension || 'day';
  const cells: ActivityCell[] = [];

  if (dimension === 'hour') {
    for (const bucket of buckets) {
      const x = ((bucket.hour - PC_BUSINESS_DAY_START_HOUR) % 24 + 24) % 24;
      cells.push({ x, y: 0, bucket, value: bucket.intensityLevel ?? 0 });
    }
    return { cells, xLabels: PC_BUSINESS_HOURS.map(pcHourLabel), yLabels: ['强度'] };
  }

  if (dimension === 'day') {
    const weekKeyOf = (ms: number) => Math.floor((ms - MONDAY_EPOCH_MS) / (7 * MS_PER_DAY));
    const entries = buckets.map(bucket => {
      const d = parseBucketBusinessDate(bucket.start);
      if (!d) return null;
      const ms = d.getTime();
      return { bucket, ms, weekday: mondayWeekday(d), weekKey: weekKeyOf(ms - mondayWeekday(d) * MS_PER_DAY) };
    }).filter((e): e is NonNullable<typeof e> => e !== null);
    const minWeek = Math.min(...entries.map(e => e.weekKey));
    for (const e of entries) {
      cells.push({ x: e.weekday, y: e.weekKey - minWeek, bucket: e.bucket, value: e.bucket.keyPressCount ?? 0 });
    }
    const maxWeek = Math.max(...entries.map(e => e.weekKey));
    const yLabels: string[] = [];
    for (let w = minWeek; w <= maxWeek; w++) yLabels.push(localDateStr(MONDAY_EPOCH_MS + w * 7 * MS_PER_DAY));
    return { cells, xLabels: ['周一', '周二', '周三', '周四', '周五', '周六', '周日'], yLabels };
  }

  if (dimension === 'month') {
    const entries = buckets.map(bucket => {
      const d = parseBucketBusinessDate(bucket.start);
      if (!d) return null;
      const key = `${d.getUTCFullYear()}-${String(d.getUTCMonth() + 1).padStart(2, '0')}`;
      return { bucket, key, dayIndex: d.getUTCDate() - 1 };
    }).filter((e): e is NonNullable<typeof e> => e !== null);
    const monthKeys = [...new Set(entries.map(e => e.key))].sort();
    for (const e of entries) {
      cells.push({ x: e.dayIndex, y: monthKeys.indexOf(e.key), bucket: e.bucket, value: e.bucket.keyPressCount ?? 0 });
    }
    return { cells, xLabels: Array.from({ length: 31 }, (_, i) => String(i + 1)), yLabels: monthKeys };
  }

  // year：53 周列 × 7 行
  for (const bucket of buckets) {
    const d = parseBucketBusinessDate(bucket.start);
    if (!d) continue;
    const yearStart = Date.UTC(d.getUTCFullYear(), 0, 1);
    const dayOfYear = Math.floor((d.getTime() - yearStart) / MS_PER_DAY);
    const offset = mondayWeekday(new Date(yearStart));
    const weekOfYear = Math.floor((dayOfYear + offset) / 7);
    cells.push({ x: Math.min(weekOfYear, 52), y: mondayWeekday(d), bucket, value: bucket.keyPressCount ?? 0 });
  }
  return {
    cells,
    xLabels: Array.from({ length: 53 }, (_, i) => String(i + 1)),
    yLabels: ['周一', '周二', '周三', '周四', '周五', '周六', '周日'],
  };
}

function pad(n: number) {
  return String(n).padStart(2, '0');
}

/**
 * PC 模块的墙钟时区固定为 Asia/Shanghai（UTC+8，无夏令时），与后端业务日口径一致
 * （见 `utils/pcBusinessDay.ts`：前端业务日计算不得使用浏览器时区，否则异地客户端
 * 会在 04:00 边界错一天）。接口返回的时间戳本身带 +08:00，因此这里按固定偏移换算，
 * 而不是用 `getHours()` / `new Date(y,m,d,h)` 这类依赖浏览器时区的取值方式。
 */
const SHANGHAI_OFFSET_MS = 8 * 60 * 60 * 1000;

/** 墙钟毫秒 → 上海时区的「时:分」（PC 模块统一口径，勿用浏览器本地 getter）。 */
export function formatClock(ms: number): string {
  const shifted = new Date(ms + SHANGHAI_OFFSET_MS);
  return `${pad(shifted.getUTCHours())}:${pad(shifted.getUTCMinutes())}`;
}

/** 墙钟毫秒 → 上海时区的小时（0–23）。 */
function shanghaiHour(ms: number): number {
  return new Date(ms + SHANGHAI_OFFSET_MS).getUTCHours();
}

/** 墙钟毫秒所在上海整点的毫秒时刻。 */
function shanghaiHourStartMs(ms: number): number {
  return Math.floor((ms + SHANGHAI_OFFSET_MS) / (60 * 60 * 1000)) * 60 * 60 * 1000 - SHANGHAI_OFFSET_MS;
}

interface CategoryGanttChunk {
  hour: number;
  minuteStart: number;
  minuteEnd: number;
  segment: TimelineItem;
}

/**
 * 把时间段拆成每小时内的分钟区间（上海墙钟小时）。
 *
 * 小时归属与 0–60 分钟坐标都按固定 +08:00 计算。接口返回的是 UTC 形式的时间戳
 * （`...Z` / `+00:00`，如 `2026-09-13T02:39:24Z` = 北京 10:39），而 PC 业务日口径固定
 * Asia/Shanghai。换算到固定 +08:00 后，展示的才是用户预期的北京时间；若改用浏览器本地
 * 时区，非 UTC+8 的用户会看到整体平移的小时行（例如 UTC 下 13:36 落到 5 点行），
 * 在有夏令时的时区还会出现重复或跳过的整点行。
 */
export function splitCategoryTimelineIntoChunks(timeline: TimelineItem[]): CategoryGanttChunk[] {
  const chunks: CategoryGanttChunk[] = [];
  for (const segment of timeline) {
    if (!segment.start || !segment.end) continue;
    const startMs = new Date(segment.start).getTime();
    const endMs = new Date(segment.end).getTime();
    if (Number.isNaN(startMs) || Number.isNaN(endMs) || endMs <= startMs) continue;

    // 每次迭代覆盖一个上海墙钟整点，含首尾不完整的部分。
    let hourStartMs = shanghaiHourStartMs(startMs);
    while (hourStartMs < endMs) {
      const hourEndMs = hourStartMs + 60 * 60 * 1000;
      const from = Math.max(startMs, hourStartMs);
      const to = Math.min(endMs, hourEndMs);
      if (to > from) {
        chunks.push({
          hour: shanghaiHour(hourStartMs),
          minuteStart: (from - hourStartMs) / 60000,
          minuteEnd: (to - hourStartMs) / 60000,
          segment,
        });
      }
      hourStartMs = hourEndMs;
    }
  }
  return chunks;
}

/** 分类时间线甘特：xAxis 为每小时 0–60 分钟，yAxis 为有数据的本地小时行。 */
export function buildCategoryGanttOption(timeline: TimelineItem[], showAllHours = false): EChartsOption {
  const chunks = splitCategoryTimelineIntoChunks(timeline);
  const touchedHours = [...new Set(chunks.map(chunk => chunk.hour))].sort((a, b) => a - b);
  const rowHours = showAllHours ? Array.from({ length: 24 }, (_, hour) => hour) : touchedHours;
  const rows = rowHours.map(hour => `${pad(hour)}:00`);
  const rowIndex = new Map(rowHours.map((hour, index) => [hour, index]));
  const data = chunks.map(chunk => ({
    value: [chunk.minuteStart, chunk.minuteEnd, rowIndex.get(chunk.hour) ?? 0] as [number, number, number],
    itemStyle: { color: chunk.segment.categoryColor || '#94a3b8' },
    segment: chunk.segment,
  }));

  const option: EChartsOption = {
    tooltip: {
      trigger: 'item',
      formatter: (params: unknown) => {
        const p = (Array.isArray(params) ? params[0] : params) as { data?: { segment?: TimelineItem } } | undefined;
        const seg = p?.data?.segment;
        if (!seg) return '';
        const lines = [`${seg.categoryName || '其他'} · ${seg.appName || '未知应用'}`];
        if (seg.windowTitle) lines.push(seg.windowTitle);
        const start = new Date(seg.start);
        const end = new Date(seg.end);
        lines.push(`${formatClock(start.getTime())} - ${formatClock(end.getTime())} · ${Math.round(seg.durationMinutes)} 分钟`);
        return lines.join('\n');
      },
      backgroundColor: 'rgba(15, 23, 42, 0.92)',
      textStyle: { color: '#fff', fontSize: 11 },
    },
    grid: { left: 40, right: 12, top: 8, bottom: 22 },
    xAxis: [
      {
        type: 'value',
        min: 0,
        max: 60,
        interval: 10,
        axisLabel: { fontSize: 10, color: chartColors.textMuted, formatter: '{value}′' },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
        splitLine: { lineStyle: { color: chartColors.borderSoft } },
      },
    ],
    yAxis: [
      {
        type: 'category',
        data: rows,
        axisLabel: { fontSize: 10, color: chartColors.textMuted },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
      },
    ],
    series: [
      {
        type: 'custom',
        // value = [startMs, endMs, yIdx]：x 取维度 0-1 的时间区间，y 取维度 2 的行索引，
        // 让 time 轴 min/max 与刻度按真实区间计算
        encode: { x: [0, 1], y: 2 },
        // params 在真实渲染下不含取值所需字段，故本 renderItem 只使用 api（见下方注释）。
        renderItem: (_params: unknown, api: unknown) => {
          // ECharts 真实调用 renderItem 时 params **不含** value / data（只有
          // dataIndex/dataIndexInside/encode/itemPayload 等），因此必须用 api 取值取色：
          //   - 取值：api.value(0..2) —— 即 data 里 value 的三个分量；
          //   - 取色：api.visual('color') —— 即该数据项 itemStyle.color。
          // 曾因读 params.value / params.data 而走兜底 [0,0,0] / 灰色：坐标落到 1970 年
          // （x ≈ -1.8e8 px，画布之外），甘特图整块空白（#153 / #186 / #282）。
          const a = api as {
            value?: (dim: number) => number;
            visual?: (key: string) => unknown;
            coord?: (v: number[]) => number[];
            size?: (v: number[]) => number[];
          };
          const startMs = Number(a.value?.(0));
          const endMs = Number(a.value?.(1));
          const yIdx = Number(a.value?.(2)) || 0;
          let x = 0;
          let y = 0;
          let width = 4;
          let rowH = 44;
          try {
            const start = a.coord ? a.coord([startMs, yIdx]) : [0, 0];
            const end = a.coord ? a.coord([endMs, yIdx]) : [0, 0];
            const size = a.size ? a.size([0, 1]) : [1, 44];
            rowH = Number(size[1]) || 44;
            x = start[0];
            width = Math.max(end[0] - start[0], 2);
            y = start[1];
          } catch {
            // 纯函数环境下 api 不可用时返回占位 rect，不影响 option 结构断言
          }
          const visual = a.visual?.('color');
          const fill = typeof visual === 'string' && visual ? visual : '#94a3b8';
          return {
            type: 'rect',
            shape: { x, y: y - rowH * 0.25, width, height: rowH * 0.5, r: 4 },
            style: { fill, stroke: '#ffffff', lineWidth: 1 },
          };
        },
        data,
        emphasis: { focus: 'series' },
      },
    ],
  };
  return option as EChartsOption;
}

/** 活动热力图：四维度统一入口，色阶 chartColors.githubGreen，着色值 = keyPressCount，上界 = maxKeyCount。 */
export function buildActivityHeatmapOption(data: HeatmapGridResponse | undefined): EChartsOption {
  if (!data) {
    return { series: [{ type: 'heatmap', data: [] }] } as EChartsOption;
  }
  const map = mapActivityGrid(data);
  if (!map) {
    return { series: [{ type: 'heatmap', data: [] }] } as EChartsOption;
  }
  const dimension = data.dimension || 'day';
  const grid =
    dimension === 'hour' ? { left: 34, right: 8, top: 8, bottom: 24 } :
    dimension === 'day' ? { left: 66, right: 8, top: 8, bottom: 16 } :
    dimension === 'month' ? { left: 58, right: 8, top: 8, bottom: 20 } :
    { left: 34, right: 8, top: 8, bottom: 10 };
  const axisLabelInterval = dimension === 'hour' ? 2 : dimension === 'month' ? 4 : undefined;

  const option: EChartsOption = {
    tooltip: {
      trigger: 'item',
      formatter: (params: unknown) => {
        const p = (Array.isArray(params) ? params[0] : params) as { data?: { bucket?: HeatmapBucket } } | undefined;
        const bucket = p?.data?.bucket;
        if (!bucket) return '';
        // 键数取原始计数 keyPressCount（上界 maxKeyCount）；activeMinutes 为桶内真实活跃分钟
        // （day/month/year 维度后端已由恒 0 改为真实值，REQ-9）。
        return `${bucketDatePart(bucket.start)} · ${bucket.keyPressCount ?? 0} 次输入 · 活跃 ${bucket.activeMinutes ?? 0} 分钟`;
      },
      backgroundColor: 'rgba(15, 23, 42, 0.92)',
      textStyle: { color: '#fff', fontSize: 11 },
    },
    grid,
    xAxis: [
      {
        type: 'category',
        data: map.xLabels,
        show: dimension !== 'year',
        axisLabel: { fontSize: 10, color: chartColors.textMuted, interval: axisLabelInterval },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
      },
    ],
    yAxis: [
      {
        type: 'category',
        data: map.yLabels,
        axisLabel: { fontSize: 10, color: chartColors.textMuted },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
      },
    ],
    visualMap: {
      min: 0,
      max: Math.max(data.maxKeyCount || 1, 1),
      calculable: false,
      show: false,
      inRange: { color: chartColors.githubGreen },
    },
    series: [
      {
        type: 'heatmap',
        data: map.cells.map(cell => ({ value: [cell.x, cell.y, cell.value], bucket: cell.bucket })),
        itemStyle: { borderColor: '#ffffff', borderWidth: 0.5 },
        emphasis: { itemStyle: { shadowBlur: 4, shadowColor: 'rgba(0, 0, 0, 0.3)' } },
        label: { show: false },
      },
    ],
  };
  return option as EChartsOption;
}

/**
 * 块内活跃分钟（分钟，四舍五入）。
 *
 * 后端修复后 `activeDurationSeconds` 恒 ≤ 块时长；这里仍夹紧一次，避免异常数据出现
 * 「活跃 99 分钟」这类不可能值。图表工具提示与详情面板共用本函数，两处数字必然一致。
 *
 * 夹紧的前置条件是「块的起止时间可解析」：判断不了块长度时（`start`/`end` 缺失或非法）
 * 原样返回，不做无依据的夹紧。起止颠倒时按区间长度（绝对值）夹紧。
 */
export function blockActiveMinutes(block: PcActivityAnalysisBlock): number {
  const raw = Math.max(Math.round((block.activeDurationSeconds || 0) / 60), 0);
  const startMs = new Date(block.start).getTime();
  const endMs = new Date(block.end).getTime();
  const blockMinutes = Math.abs(endMs - startMs) / 60000;
  if (!Number.isFinite(blockMinutes)) return raw;
  return Math.min(raw, Math.max(Math.round(blockMinutes), 0));
}

/**
 * 时间块热力：x = 块序号 1..n，y 单行，value = intensityLevel（0–5 档），
 * 色阶上界取块自带的 intensityMax（后端固定 5，见 references/A §A-1）。
 */
export function buildAnalysisBlocksOption(
  blocks: PcActivityAnalysisBlock[],
  selectedStart?: string | null,
): EChartsOption {
  // 后端同一响应内 intensityMax 一致；容错取各块最大值，缺省回退 5。
  const intensityMax = blocks.reduce((acc, b) => Math.max(acc, b.intensityMax ?? 5), 0) || 5;
  const option: EChartsOption = {
    tooltip: {
      trigger: 'item',
      formatter: (params: unknown) => {
        const p = (Array.isArray(params) ? params[0] : params) as { data?: { blockIndex?: number } } | undefined;
        const idx = p?.data?.blockIndex;
        const block = idx !== undefined ? blocks[idx] : undefined;
        if (!block) return '';
        const start = new Date(block.start);
        const end = new Date(block.end);
        // 后端修复后 activeDurationSeconds 恒 ≤ 块时长；仍夹紧一次，避免异常数据出现「活跃 99 分钟」。
        const activeMinutes = blockActiveMinutes(block);
        return `${formatClock(start.getTime())} - ${formatClock(end.getTime())} · 活跃 ${activeMinutes} 分钟\n${block.pendingClassificationCount} 条待分类 · ${block.contextSwitchCount} 次上下文切换`;
      },
      backgroundColor: 'rgba(15, 23, 42, 0.92)',
      textStyle: { color: '#fff', fontSize: 11 },
    },
    grid: { left: 24, right: 8, top: 8, bottom: 18 },
    xAxis: [
      {
        type: 'category',
        data: blocks.map((_, i) => i + 1),
        axisLabel: { fontSize: 10, color: chartColors.textMuted },
        axisLine: { lineStyle: { color: chartColors.borderSoft } },
        axisTick: { show: false },
      },
    ],
    yAxis: [
      {
        type: 'category',
        data: ['强度'],
        show: false,
      },
    ],
    visualMap: {
      min: 0,
      max: intensityMax,
      calculable: false,
      show: false,
      inRange: { color: ['#f8fafc', '#d9f2ec', '#9fdacf', '#43afa3', '#0f8f88', '#0b6b66'] },
    },
    series: [
      {
        type: 'heatmap',
        data: blocks.map((block, i) => ({
          value: [i, 0, block.intensityLevel ?? 0],
          blockIndex: i,
          itemStyle:
            selectedStart && block.start === selectedStart
              ? { borderColor: chartColors.primary, borderWidth: 2 }
              : block.pendingClassificationCount > 0
                ? { borderColor: chartColors.warning, borderWidth: 1 }
                : { borderColor: '#ffffff', borderWidth: 0.5 },
        })),
        itemStyle: { borderColor: '#ffffff', borderWidth: 0.5 },
        label: { show: false },
      },
    ],
  };
  return option as EChartsOption;
}
