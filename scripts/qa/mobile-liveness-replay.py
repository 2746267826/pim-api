#!/usr/bin/env python3
"""阶段一取证回放脚本（WO-ANDROID-KEEPALIVE-20260923 REQ-5 ~ REQ-13）。

对着**隔离库**起一个 API 实例，用回放数据走一遍真实 HTTP 链路：
注册设备 → 上报心搏/退出/强停/丢弃原因 → 查询存活概览与摘要 → 体检数据项。

前置条件（缺一不可，脚本不做任何生产库操作）：
  1. 目标库是隔离库（例如 pim_test），绝不是 pim_prod；
  2. API 已用 `ASPNETCORE_URLS` 起在 <base>；连接串指向该隔离库；
  3. JWT 私钥与 DataProtection 目录已配置（见 PR「测试 / Tests」章节的命令）。

用法：
  python3 scripts/qa/mobile-liveness-replay.py [--base http://127.0.0.1:5999/api/v1] [--user-prefix ka_e2e]

退出码 0 表示全部断言通过；任一断言失败时报错退出。
"""

import argparse
import datetime
import json
import random
import sys
import urllib.error
import urllib.request

DEFAULT_BASE = "http://127.0.0.1:5999/api/v1"


def call(method, path, body=None, token=None, base=DEFAULT_BASE):
    url = base + path
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=120) as resp:
            raw = resp.read().decode()
            return resp.status, (json.loads(raw) if raw else None)
    except urllib.error.HTTPError as exc:
        raw = exc.read().decode()
        return exc.code, (json.loads(raw) if raw else None)


def iso(value):
    return value.strftime("%Y-%m-%dT%H:%M:%S.%f")[:-3] + "Z"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--base", default=DEFAULT_BASE)
    parser.add_argument("--user-prefix", default="ka_e2e")
    args = parser.parse_args()
    base = args.base

    username = f"{args.user_prefix}_{random.randint(100000, 999999)}"
    status, response = call(
        "POST",
        "/auth/register",
        {
            "username": username,
            "email": f"{username}@example.com",
            "password": "KaTest!2026",
        },
        base=base,
    )
    assert status in (200, 201), (status, response)
    token = response["data"]["accessToken"]
    print(f"registered {username}")

    now = datetime.datetime.now(datetime.timezone.utc)
    top = now - datetime.timedelta(hours=7)

    phone, tablet, legacy = "android-ka-phone", "android-ka-tablet", "android-ka-legacy"
    for device_id, name, kind, model in [
        (phone, "OPPO PLG110", "phone", "PLG110"),
        (tablet, "OPPO OPD2409", "tablet", "OPD2409"),
        # 旧客户端不带 deviceKind：靠 smallestScreenWidthDp 回退分块（不得混进手机块）。
        (legacy, "旧客户端平板", None, "LEGACY"),
    ]:
        metadata = {"deviceKind": kind} if kind else {"smallestScreenWidthDp": 720}
        status, response = call(
            "POST",
            "/mobile/devices/register",
            {
                "deviceId": device_id,
                "androidIdHash": None,
                "displayName": name,
                "manufacturer": "OPPO",
                "brand": "OPPO",
                "model": model,
                "androidVersion": "17",
                "sdkInt": 37,
                "appVersion": "2026.09.661",
                "metadataJson": json.dumps(metadata),
            },
            token,
            base=base,
        )
        assert status == 200, (status, response)

    events = []

    def heartbeat(at, bucket, screen_on, battery):
        events.append(
            {
                "clientItemKey": f"hb-{int(at.timestamp())}",
                "eventType": "heartbeat",
                "occurredAtUtc": iso(at),
                "payloadJson": json.dumps(
                    {
                        "bootElapsedMs": 1000,
                        "standbyBucket": bucket,
                        "screenOn": screen_on,
                        "batteryPercent": battery,
                    }
                ),
            }
        )

    # 前 3 小时每 15 分钟一跳，随后 2 小时静默，再恢复 2 小时。
    at = top
    while at < top + datetime.timedelta(hours=3):
        heartbeat(at, 30, True, 80)
        at += datetime.timedelta(minutes=15)
    at = top + datetime.timedelta(hours=5)
    while at < top + datetime.timedelta(hours=7):
        heartbeat(at, 40, False, 60)
        at += datetime.timedelta(minutes=15)

    events.append(
        {
            "clientItemKey": "exit-1",
            "eventType": "process-exit",
            "occurredAtUtc": iso(top + datetime.timedelta(hours=4)),
            "payloadJson": json.dumps(
                {
                    "reason": "REASON_LOW_MEMORY",
                    "importance": 100,
                    "pssKb": 12345,
                    "rssKb": 23456,
                    "screenOn": False,
                }
            ),
        }
    )
    events.append(
        {
            "clientItemKey": "fs-1",
            "eventType": "force-stop",
            "occurredAtUtc": iso(top + datetime.timedelta(hours=6)),
            "payloadJson": json.dumps(
                {"kind": "force-stop", "evidence": "sentinel-missing"}
            ),
        }
    )
    events.append(
        {
            "clientItemKey": "exit-unknown",
            "eventType": "process-exit",
            "occurredAtUtc": iso(top + datetime.timedelta(hours=6, minutes=30)),
            "payloadJson": json.dumps({"reason": "REASON_FROM_THE_FUTURE"}),
        }
    )

    payload = {
        "deviceId": phone,
        "batchId": "ka-batch-1",
        "events": events,
        "droppedReasonSummaries": [
            {
                "localDate": now.strftime("%Y-%m-%d"),
                "reason": "horizontal-accuracy-too-low",
                "count": 12,
            },
            {
                "localDate": now.strftime("%Y-%m-%d"),
                "reason": "missing-horizontal-accuracy",
                "count": 3,
            },
        ],
    }
    status, response = call("POST", "/mobile/forensics/events", payload, token, base=base)
    assert status == 200, (status, response)
    assert response["data"]["acceptedCount"] == len(events), response
    print(f"ingest#1 accepted={response['data']['acceptedCount']}")

    # AC-5.2：同一批数据重复提交，服务端事件条数不变（幂等）。
    status, response = call("POST", "/mobile/forensics/events", payload, token, base=base)
    assert status == 200, (status, response)
    assert response["data"]["acceptedCount"] == 0, response
    assert response["data"]["skippedCount"] == len(events), response
    print(f"ingest#2 (idempotent) skipped={response['data']['skippedCount']}")

    window = f"rangeStartUtc={iso(top)}&rangeEndUtc={iso(now)}"
    status, response = call("GET", f"/mobile/liveness/overview?{window}", None, token, base=base)
    assert status == 200, (status, response)
    data = response["data"]
    assert len(data["phones"]) == 1 and len(data["tablets"]) == 2, data
    assert data["unclassified"] == [], data
    print(
        "overview: phones=%d tablets=%d unclassified=%d"
        % (len(data["phones"]), len(data["tablets"]), len(data["unclassified"]))
    )

    block = data["phones"][0]
    assert block["hasData"] is True
    assert block["hasSilenceOverOneHour"] is True
    assert block["longestSilenceSeverity"] == "critical"
    assert any(cause["cause"] == "low-memory" for cause in block["causes"])
    unknown = next(cause for cause in block["causes"] if cause["cause"] == "unknown")
    assert unknown["inference"], "未知死因必须带推断依据（AC-7.1）"
    print("phone block:", json.dumps(block, ensure_ascii=False)[:400])

    # AC-7.5：从未上报的设备显示"无数据/未上报"，不得显示为 100% 存活。
    for tablet_block in data["tablets"]:
        assert tablet_block["hasData"] is False, tablet_block
        assert tablet_block["coverageByHour"] is None, tablet_block
        assert "无数据" in tablet_block["conclusion"], tablet_block
    print("tablet blocks show 无数据/未上报: OK")

    # AC-11.2：同一区间 REST 单设备摘要与概览块一致。
    status, single = call("GET", f"/mobile/devices/{phone}/liveness?{window}", None, token, base=base)
    assert status == 200, (status, single)
    for field in (
        "conclusion",
        "coverageByHour",
        "coverageByExpectedHeartbeat",
        "longestSilenceMinutes",
        "hasSilenceOverOneHour",
    ):
        assert single["data"][field] == block[field], (field, single["data"][field], block[field])
    print("device summary matches overview block: OK")

    # AC-7.2：逐条事件含类型/原因/上下文与原始 JSON。
    status, page = call(
        "GET",
        f"/mobile/devices/{phone}/liveness/events?{window}&page=1&pageSize=5",
        None,
        token,
        base=base,
    )
    assert status == 200 and page["data"]["totalCount"] == len(events), page
    assert page["data"]["items"][0]["payloadJson"]
    print("events page total=%d: OK" % page["data"]["totalCount"])

    # AC-9.2：服务端可见按天按原因的丢弃统计，数值与设备端一致。
    status, dropped = call(
        "GET", f"/mobile/devices/{phone}/dropped-reasons?{window}", None, token, base=base
    )
    assert status == 200, (status, dropped)
    counts = {row["reason"]: row["count"] for row in dropped["data"]["items"]}
    assert counts["horizontal-accuracy-too-low"] == 12, dropped
    assert counts["missing-horizontal-accuracy"] == 3, dropped
    print("dropped reasons:", json.dumps(dropped["data"]["items"], ensure_ascii=False))

    # AC-11.4：设备存在但无数据 → 200 + 空态；设备不存在 → 404。两者必须可分。
    status, _ = call(
        "GET", f"/mobile/devices/{tablet}/liveness?{window}", None, token, base=base
    )
    assert status == 200, status
    status, _ = call(
        "GET", "/mobile/devices/android-does-not-exist/liveness", None, token, base=base
    )
    assert status == 404, status
    print("no-data device 200 / unknown device 404: OK")

    # REQ-10：体检含「设备存活」数据项，且该项内部没有红/黄/绿档位字段。
    status, report = call(
        "POST", "/data-reliability/inspection/refresh", None, token, base=base
    )
    assert status == 200, (status, report)
    items = report["data"].get("deviceLiveness") or []
    assert items, report["data"]
    for item in items:
        for forbidden in ("status", "grade", "level", "isHealthy"):
            assert forbidden not in item["summary"], item
    print(
        "inspection status=%s red=%d yellow=%d green=%d unknown=%d deviceLiveness=%d"
        % (
            report["data"]["status"],
            report["data"]["redCount"],
            report["data"]["yellowCount"],
            report["data"]["greenCount"],
            report["data"]["unknownCount"],
            len(items),
        )
    )
    print("E2E OK")


if __name__ == "__main__":
    try:
        main()
    except AssertionError as exc:
        print("ASSERTION FAILED:", exc, file=sys.stderr)
        sys.exit(1)
