#!/usr/bin/env bash
# 阶段一取证的真机/模拟器取证脚本（WO-ANDROID-KEEPALIVE-20260923 REQ-1 / REQ-2）。
#
# 这是**真实设备行为**的证据采集脚本（不是单元测试）：在已启动的模拟器或已连接的
# 真机上安装 debug 包，依次做 am kill / am force-stop / 重新打开，然后从应用私有目录
# 拉出取证台账，打印进程退出与强停判定结果。
#
# 用法（先 `adb devices` 确认目标设备）：
#   bash scripts/qa/android-forensics-emulator.sh
#
# 需要的环境变量：ANDROID_HOME（默认 /home/coder/Android/sdk）
#
# 说明：
# - 只读取应用自己的私有目录（debug 包可用 run-as），不抓 logcat、不要求 root。
# - 拉到本地的数据库副本**可能包含既有的定位点（经纬度）**，因此脚本结束时一律删除；
#   需要保留现场时显式设置 KEEP_LEDGER_COPY=1（并自行保证不把它提交/上传）。
# - 打印出来的输出只含取证台账字段，不含坐标与凭据。
set -euo pipefail

ANDROID_HOME="${ANDROID_HOME:-/home/coder/Android/sdk}"
KEEP_LEDGER_COPY="${KEEP_LEDGER_COPY:-0}"
ADB="$ANDROID_HOME/platform-tools/adb"
PKG="com.pim.app"
APK="${APK_PATH:-app/build/outputs/apk/debug/app-debug.apk}"
WORKDIR="$(mktemp -d)"

echo "== 设备 =="
"$ADB" devices
"$ADB" wait-for-device

if [ ! -f "$APK" ]; then
  echo "找不到 $APK，请先执行：cd src/client-android && ./gradlew :app:assembleDebug" >&2
  exit 1
fi

echo "== 安装 $APK =="
"$ADB" install -r -t "$APK" >/dev/null

dump_ledger() {
  local label="$1"
  # Room 默认用 WAL：只拉主库文件会看不到尚未 checkpoint 的新表与新行，必须连 WAL 一起拉。
  for suffix in "" "-wal" "-shm"; do
    "$ADB" shell run-as "$PKG" cat "databases/pim.db$suffix" > "$WORKDIR/pim.db$suffix" 2>/dev/null || rm -f "$WORKDIR/pim.db$suffix"
  done
  if [ ! -s "$WORKDIR/pim.db" ]; then
    echo "  （无法读取应用数据库：设备可能不是 debug 包）"
    return
  fi
  python3 - "$WORKDIR/pim.db" "$label" <<'PY'
import sqlite3, sys, json
db, label = sys.argv[1], sys.argv[2]
conn = sqlite3.connect(db)
try:
    rows = conn.execute(
        "SELECT event_type, occurred_at_utc, client_item_key, payload_json FROM mobile_forensic_events ORDER BY occurred_at_utc"
    ).fetchall()
except sqlite3.OperationalError as exc:
    print(f"  [{label}] 读取失败：{exc}")
    sys.exit(0)
print(f"  [{label}] 取证台账共 {len(rows)} 条")
for event_type, occurred, key, payload in rows[-8:]:
    try:
        data = json.loads(payload)
    except Exception:
        data = {"raw": payload}
    keep = {k: data[k] for k in ("reason", "kind", "evidence", "importance", "pssKb", "rssKb", "inference", "bootElapsedMs", "standbyBucket", "screenOn", "batteryPercent") if k in data}
    print(f"    {event_type:14s} {occurred} {key:32s} {json.dumps(keep, ensure_ascii=False)}")
conn.close()
PY
}

echo "== 场景 1：冷启动建立基线（写心跳 + 登记哨兵） =="
"$ADB" shell am start -n "$PKG/.MainActivity" >/dev/null
sleep 12
dump_ledger "cold-start"

echo "== 场景 2：am kill（普通进程回收，应留下进程退出记录） =="
"$ADB" shell am kill "$PKG" || true
sleep 5
"$ADB" shell am start -n "$PKG/.MainActivity" >/dev/null
sleep 12
dump_ledger "after-am-kill"

echo "== 场景 3：am force-stop（用户强停，哨兵应被清空） =="
"$ADB" shell am force-stop "$PKG"
sleep 5
"$ADB" shell am start -n "$PKG/.MainActivity" >/dev/null
sleep 15
dump_ledger "after-force-stop"

echo "== 场景 4：再次冷启动（验证重复读取不产生重复台账） =="
"$ADB" shell am force-stop "$PKG"
sleep 3
"$ADB" shell am start -n "$PKG/.MainActivity" >/dev/null
sleep 12
dump_ledger "second-force-stop"

echo
echo "== 状态页存活区块的实际文案（uiautomator dump） =="
"$ADB" shell uiautomator dump /sdcard/ka_window.xml >/dev/null 2>&1 || true
"$ADB" shell cat /sdcard/ka_window.xml 2>/dev/null | tr '>' '\n' | grep -o 'text="[^"]*"' | grep -E '设备存活|存活|覆盖率|最近心搏|最近死因|无数据|丢弃原因' | head -30 || true

echo
if [ "$KEEP_LEDGER_COPY" = "1" ]; then
  echo "保留现场：$WORKDIR/pim.db（注意：该副本可能含既有定位点，切勿提交或上传）"
else
  # 默认删除本地副本：它可能含经纬度，不属于取证输出的一部分。
  rm -rf "$WORKDIR"
  echo "本地数据库副本已删除（需要保留现场请设置 KEEP_LEDGER_COPY=1）"
fi
