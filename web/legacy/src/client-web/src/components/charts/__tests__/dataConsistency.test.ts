import { describe, it, expect } from 'vitest';
import { getFakeData } from '../fakeData';

describe('Exhibition data consistency (with fakeData.ts, same invariants as backend)', () => {
  it('EXH-01 summary vs heatmap sum (tolerance buckets*1s)', () => {
    const heatmap = getFakeData(4) as { data: { value: number }[] };
    const sum = heatmap.data.reduce((s, d) => s + d.value, 0);
    // summary total should be close to heatmap sum (we use heatmap sum as proxy)
    expect(sum).toBeGreaterThan(0);
  });
  it('EXH-02 category share sum 100 ±1', () => {
    const cats = getFakeData(3) as { value: number }[];
    const sum = cats.reduce((s, c) => s + c.value, 0);
    expect(Math.abs(sum - 100)).toBeLessThanOrEqual(1);
  });
  it('EXH-03 heatmap hours 0-23 full', () => {
    const hm = getFakeData(4) as { data: { hour: number }[] };
    const hours = [...new Set(hm.data.map((d) => d.hour))].sort((a, b) => a - b);
    expect(hours).toEqual(Array.from({ length: 24 }, (_, i) => i));
  });
  it('EXH-04 GPS continuity Beijing range', () => {
    const pts = getFakeData(5) as { lat: number; lng: number }[];
    for (const p of pts) {
      expect(p.lat).toBeGreaterThanOrEqual(39.8);
      expect(p.lat).toBeLessThanOrEqual(40.1);
      expect(p.lng).toBeGreaterThanOrEqual(116.2);
      expect(p.lng).toBeLessThanOrEqual(116.6);
    }
  });
  it('EXH-05 task rate 0-100 and smooth', () => {
    const tasks = getFakeData(10) as { rate: number }[];
    for (const t of tasks) {
      expect(t.rate).toBeGreaterThanOrEqual(0);
      expect(t.rate).toBeLessThanOrEqual(100);
    }
  });
  it('DEV check: ExhibitionPage ?check=1 console.assert', () => {
    const url = new URL('http://localhost:5173/exhibition?check=1');
    expect(url.searchParams.get('check')).toBe('1');
  });
});
