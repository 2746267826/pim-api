#!/usr/bin/env python3
"""复现 MobileGapService 的缺口回报 vs 真实未覆盖区间（只读，无副作用）。

背景：issue #551 —— 服务端每天回报的「缺口窗口」远大于真实空洞。
本脚本把两种算法并排跑一遍，用同一份覆盖窗口数据：

  1) gap_service_windows()  —— 复刻 `MobileGapService.GetGapsAsync`（pim-api master 88c11c6）
  2) true_gaps()            —— 把三源覆盖区间合并后取补集（真实未覆盖区间）

输入：coverage-windows.csv（列：source,start_utc,end_utc，时间一律 UTC ISO-8601）
      取数 SQL 见同目录 coverage-windows.sql，在**镜像库 pim_test** 或只读副本上执行，
      不要对着生产库开写会话。

用法：
    python3 gap-window-repro.py --csv coverage-windows.csv
    python3 gap-window-repro.py --csv coverage-windows.csv \
        --range-start 2026-10-18T10:02:13Z --range-end 2026-11-01T10:02:13Z

    # 固定时间窗可让输出与 issue #551 里的数字逐位对齐
"""
from __future__ import annotations

import argparse
import csv
import sys
from datetime import datetime, timedelta, timezone

MAX_BACKFILL_AGE = timedelta(days=14)   # MobileGapService.cs:11
MIN_GAP_THRESHOLD = timedelta(minutes=5)  # MobileGapService.cs:12


def parse_utc(value: str) -> datetime:
    value = value.strip()
    if value.endswith("Z"):
        value = value[:-1] + "+00:00"
    dt = datetime.fromisoformat(value)
    if dt.tzinfo is None:
        dt = dt.replace(tzinfo=timezone.utc)
    return dt.astimezone(timezone.utc)


def load_coverage(path: str) -> list[tuple[datetime, datetime]]:
    """读覆盖窗口；source 仅用于展示，判定口径与 GetGapsAsync 一致（三源并列）。"""
    windows: list[tuple[datetime, datetime]] = []
    with open(path, newline="", encoding="utf-8") as fh:
        reader = csv.DictReader(fh)
        missing = {"start_utc", "end_utc"} - set(reader.fieldnames or [])
        if missing:
            sys.exit(f"CSV 缺少列：{sorted(missing)}")
        for row in reader:
            start, end = parse_utc(row["start_utc"]), parse_utc(row["end_utc"])
            if end > start:
                windows.append((start, end))
    return windows


def clip(window: tuple[datetime, datetime], start: datetime, end: datetime):
    s = max(window[0], start)
    e = min(window[1], end)
    return (s, e) if e > s else None


def continuous_coverage_end(overlapping, cursor: datetime, window_end: datetime) -> datetime:
    """MobileGapService.ContinuousCoverageEnd：从 cursor 起连续覆盖到的位置。"""
    end = cursor
    for s, e in overlapping:
        if e <= cursor:
            continue
        if s > end:
            break
        if e > end:
            end = e
    return min(end, window_end)


def gap_service_windows(cov, start: datetime, end: datetime):
    """复刻 GetGapsAsync：按 request.RangeStartUtc 对齐的「一天」为窗口，
    每个窗口最多产出一个 [首个断点 → 窗口结束]，断点之后即使恢复覆盖也不再续算。"""
    gaps = []
    cursor = start
    while cursor < end:
        window_end = min(cursor + timedelta(days=1), end)
        overlapping = sorted(
            w for w in (clip(c, cursor, window_end) for c in cov if c[1] > cursor and c[0] < window_end) if w
        )
        covered_until = continuous_coverage_end(overlapping, cursor, window_end)
        if covered_until >= window_end:
            cursor = window_end
            continue
        gap_start = covered_until if covered_until > cursor else cursor
        if window_end - gap_start >= MIN_GAP_THRESHOLD:
            gaps.append((gap_start, window_end))
        cursor = window_end
    return gaps


def true_gaps(cov, start: datetime, end: datetime):
    """三源合并后取补集：把覆盖区间合并成不相交段，再求 [start,end] 内的空洞。"""
    merged: list[list[datetime]] = []
    for s, e in sorted(w for w in (clip(c, start, end) for c in cov) if w):
        if merged and s <= merged[-1][1]:
            merged[-1][1] = max(merged[-1][1], e)
        else:
            merged.append([s, e])

    gaps = []
    cursor = start
    for s, e in merged:
        if s > cursor:
            gaps.append((cursor, s))
        cursor = max(cursor, e)
    if cursor < end:
        gaps.append((cursor, end))
    return [(s, e) for s, e in gaps if e - s >= MIN_GAP_THRESHOLD]


def fmt(dt: datetime) -> str:
    return dt.astimezone(timezone(timedelta(hours=8))).strftime("%Y-%m-%d %H:%M")


def hours(windows) -> float:
    return sum((e - s).total_seconds() for s, e in windows) / 3600.0


def dump(title: str, windows) -> None:
    print(f"\n{title}：{len(windows)} 个窗口，合计 {hours(windows):.1f} 小时")
    for s, e in windows:
        print(f"  {fmt(s)} → {fmt(e)}   {(e - s).total_seconds() / 3600:5.1f}h")


def main() -> int:
    ap = argparse.ArgumentParser(description="对比 GapService 回报窗口与真实空洞")
    ap.add_argument("--csv", required=True, help="覆盖窗口 CSV（source,start_utc,end_utc）")
    ap.add_argument("--range-start", help="客户端请求区间起点（默认 now-14d，与 Android 端一致）")
    ap.add_argument("--range-end", help="客户端请求区间终点（默认 now）")
    ap.add_argument("--device", default="(all)", help="仅用于打印，脚本不按设备过滤")
    args = ap.parse_args()

    cov = load_coverage(args.csv)
    if not cov:
        sys.exit("CSV 里没有有效覆盖窗口")

    end = parse_utc(args.range_end) if args.range_end else datetime.now(timezone.utc).replace(microsecond=0)
    start = parse_utc(args.range_start) if args.range_start else end - MAX_BACKFILL_AGE
    if end <= start:
        sys.exit("range-end 必须晚于 range-start")

    print(f"设备：{args.device}")
    print(f"覆盖窗口：{len(cov)} 条；请求区间：{fmt(start)} → {fmt(end)}（北京时间）")

    service = gap_service_windows(cov, start, end)
    real = true_gaps(cov, start, end)

    dump("服务端口径（复刻 GetGapsAsync）", service)
    dump("真实未覆盖（三源合并取补集）", real)

    print("\n对比：")
    print(f"  服务端回报 {len(service)} 个 / {hours(service):.1f} 小时")
    print(f"  真实空洞 {len(real)} 个 / {hours(real):.1f} 小时")
    if hours(real) > 0:
        print(f"  放大倍数：{hours(service) / hours(real):.1f}x")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
