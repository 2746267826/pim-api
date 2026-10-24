import { describe, it, expect } from 'vitest';
import fs from 'node:fs';
import path from 'node:path';

/**
 * WO-FRONTEND-PC-20261001 · REQ-4 / AC-4.2
 *
 * AW（ActivityWatch）2026-09-01 退役后，`/api/v1/pc/aw/*` 这类遗留端点要么退役、要么已经
 * 与 `summary` 不同源（#379：`pc/aw/heatmap` 的 `activeMinutes` 全 0）。前端曾经留着两个
 * 没有任何调用方的取数函数指向它们（`getPcTimeline` → `/pc/aw/timeline`、
 * `getPcHeatmap` → `/pc/aw/heatmap`，即工单里的 `getPcAwHeatmap`）。
 *
 * 这组用例把「前端不再依赖退役端点」钉成可执行断言：以后谁再加回一条 `/pc/aw/` 取数都会红。
 */

const SRC = path.resolve(__dirname, '../..');

function sourceFiles(dir: string, out: string[] = []): string[] {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) {
      if (entry.name === '__tests__' || entry.name === 'node_modules') continue;
      sourceFiles(full, out);
    } else if (/\.(ts|tsx)$/.test(entry.name)) {
      out.push(full);
    }
  }
  return out;
}

function filesMentioning(needle: string): string[] {
  return sourceFiles(SRC)
    .filter(file => fs.readFileSync(file, 'utf8').includes(needle))
    .map(file => path.relative(SRC, file))
    .sort();
}

describe('WO-FRONTEND-PC-20261001 AC-4.1 / AC-4.2 · 前端不得再依赖退役的 AW 遗留端点', () => {
  it('src 下没有任何模块请求 /pc/aw/ 系列端点', () => {
    expect(
      filesMentioning('/pc/aw/'),
      '仍有模块指向退役的 /pc/aw/* 端点（AW 已退役，#379）',
    ).toEqual([]);
  });

  it('pcTracker 不再导出无调用方的 AW 取数函数', async () => {
    const api = await import('../pcTracker');
    expect(Object.keys(api)).not.toContain('getPcHeatmap');
    expect(Object.keys(api)).not.toContain('getPcTimeline');
    expect(Object.keys(api)).not.toContain('getPcAwHeatmap');
  });

  it('现役取数函数仍在（清理不得误删）', async () => {
    const api = await import('../pcTracker');
    for (const name of ['getPcSummary', 'getPcHeatmapGrid', 'getPcQuality', 'getPcKeystatsRange']) {
      expect(Object.keys(api), `${name} 必须保留`).toContain(name);
    }
  });
});
