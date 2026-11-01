#!/usr/bin/env python3
"""从 PIM Android 诊断包统计「缺口窗口重采重传」的证据（只读，本地文件）。

输入：pim-diagnostics-*.zip（App 26.11.x 的状态中心 → 导出诊断）
输出：
  1) 每天窗口上传结果（accepted / skipped / failed）
  2) 服务端每轮回报的缺口窗口数 → 客户端拆成多少个上传窗口
  3) 被反复重试且零接收的窗口排行
  4) 同步触发次数与间隔中位数
  5) HTTP 状态码分布

用法：
    python3 client-gap-retries.py pim-diagnostics-1793525302613.zip
    python3 client-gap-retries.py pim-diagnostics-*.zip --top 10
"""
from __future__ import annotations

import argparse
import collections
import json
import re
import statistics
import sys
import zipfile
from datetime import datetime, timedelta, timezone

CST = timezone(timedelta(hours=8))


def to_cst(ms: int, fmt: str = "%m-%d %H:%M") -> str:
    return datetime.fromtimestamp(ms / 1000, CST).strftime(fmt)


def read_json(zf: zipfile.ZipFile, name: str):
    try:
        return json.loads(zf.read(name).decode("utf-8"))
    except KeyError:
        return None


def iter_log_lines(zf: zipfile.ZipFile):
    for name in sorted(n for n in zf.namelist() if n.startswith("logs/") and n.endswith(".jsonl")):
        with zf.open(name) as fh:
            for raw in fh:
                raw = raw.strip()
                if not raw:
                    continue
                try:
                    yield name, json.loads(raw)
                except json.JSONDecodeError:
                    continue


def main() -> int:
    ap = argparse.ArgumentParser(description="统计诊断包里的缺口重采重传证据")
    ap.add_argument("zip_path")
    ap.add_argument("--top", type=int, default=10, help="排行榜条数")
    args = ap.parse_args()

    try:
        zf = zipfile.ZipFile(args.zip_path)
    except (OSError, zipfile.BadZipFile) as exc:
        sys.exit(f"打不开诊断包：{exc}")

    manifest = read_json(zf, "manifest.json") or {}
    status = read_json(zf, "status.json") or {}
    counts = read_json(zf, "database-counts.json") or {}

    print(f"包：{args.zip_path}")
    print(f"App：{manifest.get('appVersionName')}（code {manifest.get('appVersionCode')}）"
          f"，导出时间 {to_cst(manifest.get('createdAtUtc') or 0, '%Y-%m-%d %H:%M:%S')} 北京时间")
    print(f"status：pending usage={status.get('usageEventsPendingCount')}"
          f"，aggregateRejected={status.get('aggregateRejectedCount')}"
          f"，summaries 行数={counts.get('mobileUsageSummariesRowCount')}")

    per_day = collections.defaultdict(collections.Counter)
    windows: dict[tuple[str, str], dict[str, int]] = {}
    day_windows = collections.defaultdict(set)
    gap_msgs = collections.defaultdict(list)
    sync_times: list[int] = []
    http_codes = collections.Counter()
    last_by_day: dict[str, str] = {}

    for _, row in iter_log_lines(zf):
        ts = row.get("occurredAtUtc")
        if not ts:
            continue
        day = to_cst(ts, "%m-%d")
        msg = row.get("message") or ""
        details = row.get("details") or {}
        last_by_day[day] = max(last_by_day.get(day, ""), to_cst(ts, "%H:%M"))

        if "windowStartUtc" in details and "acceptedCount" in details:
            key = (details["windowStartUtc"], details["windowEndUtc"])
            agg = windows.setdefault(key, {"attempts": 0, "accepted": 0, "skipped": 0, "failed": 0})
            agg["attempts"] += 1
            agg["accepted"] += details.get("acceptedCount") or 0
            agg["skipped"] += details.get("skippedCount") or 0
            agg["failed"] += details.get("failedCount") or 0
            day_windows[day].add(key)
            d = per_day[day]
            d["uploads"] += 1
            d["accepted"] += details.get("acceptedCount") or 0
            d["skipped"] += details.get("skippedCount") or 0
            d["rejected"] += details.get("rejectedCount") or 0
            d["failed"] += details.get("failedCount") or 0
            if (details.get("acceptedCount") or 0) == 0:
                d["zero_accepted"] += 1

        m = re.match(r"服务器返回 (\d+) 个缺口窗口，已拆为 (\d+) 个上传窗口", msg)
        if m:
            gap_msgs[day].append((int(m.group(1)), int(m.group(2))))
        if msg.startswith("正在采集服务器缺口窗口的使用记录"):
            per_day[day]["gap_collect"] += 1
        if msg.startswith("开始执行打开 App 后的手机同步"):
            sync_times.append(ts)
        if "HttpException: HTTP" in msg:
            http_codes[msg.split("HTTP")[-1].strip()] += 1

    print("\n== 每天窗口上传结果 ==")
    print(f"{'日期':6} {'上传次数':>8} {'accepted':>9} {'skipped':>9} {'failed':>10} {'零接收':>7} {'去重窗口':>7}")
    for day in sorted(per_day):
        d = per_day[day]
        print(f"{day:6} {d['uploads']:>8} {d['accepted']:>9} {d['skipped']:>9} "
              f"{d['failed']:>10} {d['zero_accepted']:>7} {len(day_windows[day]):>7}")

    print("\n== 服务端每轮回报的缺口窗口 → 客户端拆分 ==")
    for day in sorted(gap_msgs):
        vals = gap_msgs[day]
        splits = [b for _, b in vals]
        print(f"{day}: {len(vals)} 轮，服务端窗口数 {sorted({a for a, _ in vals})}，"
              f"拆分窗口 {min(splits)}~{max(splits)} 个，采集调用 {per_day[day]['gap_collect']} 次")

    print(f"\n== 反复重试的窗口（top {args.top}）==")
    ranked = sorted(windows.items(), key=lambda kv: (-kv[1]["attempts"], kv[1]["accepted"]))
    print(f"{'次数':>6} {'accepted':>9} {'skipped':>9} {'窗口（北京时间）':<37}")
    for (ws, we), agg in ranked[: args.top]:
        s = to_cst(int(datetime.fromisoformat(ws.replace("Z", "+00:00")).timestamp() * 1000))
        e = to_cst(int(datetime.fromisoformat(we.replace("Z", "+00:00")).timestamp() * 1000))
        print(f"{agg['attempts']:>6} {agg['accepted']:>9} {agg['skipped']:>9} {s} → {e}")

    total_attempts = sum(w["attempts"] for w in windows.values())
    zero_attempts = sum(w["attempts"] for w in windows.values() if w["accepted"] == 0)
    print(f"\n窗口上传合计 {total_attempts} 次 / 去重窗口 {len(windows)} 个；"
          f"零接收尝试 {zero_attempts} 次（{zero_attempts / max(total_attempts, 1) * 100:.1f}%）")

    if sync_times:
        gaps = [(b - a) / 1000 for a, b in zip(sorted(sync_times), sorted(sync_times)[1:])]
        print(f"同步触发 {len(sync_times)} 次；间隔中位数 {statistics.median(gaps):.1f}s，"
              f"最小 {min(gaps):.1f}s，最大 {max(gaps) / 60:.1f}min")

    if http_codes:
        print("HTTP 状态码：" + "，".join(f"{k}×{v}" for k, v in sorted(http_codes.items(), key=lambda kv: -kv[1])))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
