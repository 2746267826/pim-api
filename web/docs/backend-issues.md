# PC Tracker 后端问题清单（Issues）

> **状态更新（2026-09-30）**：后端已在测试 API `v2026.09.752` 中**全部修复** PC-1 ~ PC-7 与 GAP-1，
> 并下发《PC记录接口变更与修复说明_20260930.md》。其中 **3 处为破坏性字段变更**，前端已同步适配：
>
> | 接口 | 变更 | 前端适配 |
> | --- | --- | --- |
> | `pc/activity-analysis` blocks[] | `intensityScore` → **`intensityLevel`/`intensityMax`**（0–5 档） | `types.ts`、`hourly-heat.ts` |
> | `pc/summary` heatmap[] | `intensityScore` → **`intensityLevel`/`intensityMax`**（活跃分钟分档） | `types.ts` |
> | `pc/heatmap/grid` cells | `intensityScore`（实为原始计数）→ **`keyPressCount`** + `intensityLevel`/`intensityMax` + 新增 `activeMinutes` | `types.ts`、`pc-tracker-page.tsx` |
>
> 行为变更要点：activity-analysis 覆盖全天且 `activeDurationSeconds ≤ 块时长`（可放心展示活跃分钟）；
> `summary.heatmap.activeMinutes` 口径变宽（含仅键盘输入分钟，9/27 由 569 → 722.8 分钟）；
> grid `dimension=hour` 跨日 → 400；分类建议返回全部待处理并逐条带 `generatedForDate`（`__IDLE__` 不再出现）；
> 新端点 `GET /pc/aggregation/keystats?start&end`（键鼠范围聚合，字段与单日 keystats 同构）。
> 两处推测被实测推翻：PC-1 真因是聚合分页截断（输入数据全天都在），PC-5 输入数据同样全天都在。
> 回归验证脚本：`client-web/scripts/verify-backend-20260930.mjs`（12 项全 PASS）。

> 依据：测试 API（地址见 `client-web/.env` 的 `PIM_API_TARGET`，版本 2026.09.740）实测数据，
> 复现命令均为 curl 可直接执行。业务日口径：Asia/Shanghai，04:00 起算。
> 整理日期：2026-09-29。前端已对 PC-1/PC-2/PC-3/PC-4 做了规避，但根因在后端。

---

## ✅ PC-1 activity-analysis 覆盖稀疏：仅少数小时有数据，远少于同期其他接口（已修复）

**现象**：`GET /api/v1/pc/activity-analysis?date=...&blockMinutes=60` 返回 24 个小时块，
但绝大多数块 `activeDurationSeconds = 0`、`categories = []`、`apps = []`。

| 业务日 | 非零块（换算为本地时刻） | 同期 summary.heatmap 非零小时 | summary.timeline 覆盖 |
| --- | --- | --- | --- |
| 2026-09-27 | 仅 11:00、12:00 两块 | 13 小时（11:00–23:00） | 501 条记录，11:03–23:05，合计 569 分钟 |
| 2026-09-26 | 仅 10:00、11:00、12:00 三块 | 13 小时 | — |

**复现**：

```bash
curl 'http://127.0.0.1:3000/api/v1/pc/activity-analysis?date=2026-09-27&blockMinutes=60'
# 本地 13:00–23:00 对应的块（UTC 05:00–15:00）全部为：
# { "activeDurationSeconds": 0, "intensityScore": 0, "categories": [], "apps": [] }
```

**对照**：同一次 `GET /api/v1/pc/summary?date=2026-09-27` 返回的 `heatmap` 字段
（逐小时桶）有完整的 13 小时数据，`activeMinutes` 合计 488 分钟，与 `timeline`
的 569 分钟量级吻合。两个接口理应同源，覆盖却差 11 小时。

**影响**：前端「时间块热力」卡大量空行（用户报告"数据少了很多"）。

**推测方向**：activity-analysis 的聚合数据源（疑似 input-active / input-minute 记录）
只落库了部分时段；或聚合查询按 UTC 日过滤导致跨日段被裁掉。
建议核对 `PcTrackerModule.cs:495-522` 的取数范围与 timeline/heatmap 是否同表。

---

## ✅ PC-2 activity-analysis 块内时长合计超过块本身时长（已修复：块内重叠消解，activeDurationSeconds 恒 ≤ 块时长）

**现象**：`blockMinutes=60` 的块，块内 `apps`/`categories` 的 `durationSeconds`
合计明显超过 3600 秒；`activeDurationSeconds` 本身也大于 3600。

**证据（业务日 2026-09-27，UTC 03:00 块 = 本地 11:00–12:00）**：

```json
"apps": [
  { "appName": "unknown",  "durationSeconds": 3240 },
  { "appName": "zcode",    "durationSeconds": 1239.996 },
  { "appName": "explorer", "durationSeconds": 839.998 },
  { "appName": "msedge",   "durationSeconds": 289.996 },
  { "appName": "github.com","durationSeconds": 139.996 }
]
// 合计 ≈ 5748.99s ≈ 95.8 分钟 > 60 分钟
"activeDurationSeconds": 5959.98   // ≈ 99.3 分钟 > 60 分钟
```

2026-09-26 同样：UTC 02:00 块 activeSec=3860（64 分钟）、UTC 03:00 块 6740
（112 分钟）、UTC 04:00 块 5460（91 分钟）。

**可能原因**：键盘/鼠标等多路输入流被分别累加（重叠计时）；或 `durationSeconds`
的聚合粒度是原始事件时长而非去重后的墙钟时长。

**影响**：前端无法把 `activeDurationSeconds` 换算成「该小时活跃 X 分钟」展示
（会显示"活跃 99 分钟"，明显不合常理）；也无法作为占比条形。

---

## ✅ PC-3 intensityScore 语义不一致：同名字段两种量纲（已修复：统一改名 intensityLevel 0–5 档 + keyPressCount，破坏性变更）

| 接口 | 量纲 | 实测值 |
| --- | --- | --- |
| `activity-analysis.blocks[].intensityScore` | 0–5 档位 | 4 |
| `summary.heatmap[].intensityScore` | 0–5 档位 | 1–5 |
| `pc/heatmap/grid` 的 `intensityScore` | 原始计数 | 3495 – 32886 |

同一个字段名在 activity-analysis / summary 中是 0–5 档位，在 heatmap/grid 中是
原始计数（keyPresses 量级）。前端最初把 0–5 当百分比渲染，导致有数据的块
只显示 4% 宽的小点（用户视角即"没有数据"）。

**建议**：统一命名为 `intensityLevel`（0–5 档）与 `intensityScore`（原始计数），
或在两个接口都附 `intensityMax` 说明量纲。

---

## ✅ PC-4 heatmap/grid 按 UTC 日切桶，而非业务日（已修复：day 桶改为业务日窗口 本地 04:00 ~ 次日 04:00）

**现象**：`GET /api/v1/pc/heatmap/grid?...&dimension=day` 的桶起点是 UTC 零点
（如 `2026-09-21T00:00:00Z` = 本地 08:00），而非业务日 04:00。

**对照**：`summary.heatmap` 的桶起点是本地 04:00
（如 `2026-09-26T20:00:00Z` = 本地 2026-09-27 04:00），按业务日切分。

**影响**：对 +08:00 用户，一个 UTC 日桶横跨本地 08:00 → 次日 08:00，
活跃会被摊到错误的本地日；跨日视图（贡献图）的日期归属偏移最多一天。
前端目前只能按桶起点标注日期，属近似。

**建议**：grid 桶改为按业务日（或接受 `timezone` 参数；聚合组接口已支持
`timezone`，grid 组未支持）。

---

## ✅ PC-5 采集数据可靠性不足，且输入类数据只覆盖部分时段（已修复：quality 区分采集停机 vs 数据滞后并给缺数时段；输入数据实测全天都在）

**现象**：`GET /api/v1/pc/quality` 报 `overallStatus=3`（故障）
"所选范围内的 PC 事实数据可靠性不足"；`tracker-events` 组件事件数偏低
（实测 106 条）。

结合 PC-1：input 类数据在 9.26 仅覆盖 10:00–13:00、9.27 仅覆盖 11:00–13:00，
而应用切换事件（timeline 来源）覆盖全天。疑似采集端 input 记录上报中断
（tracker 心跳仍在：`/pc/tracker/health/latest` status=running）。

**建议**：
1. 核查 tracker 端 input/keyboard/mouse 采样与上报的重连与补传逻辑；
2. 在 quality 组件里区分「事件数少」「输入数据缺失」两类降级原因，
   便于定位是采集中断还是聚合缺陷。

---

## ✅ PC-6 分类建议包含「空闲时段」簇，且样本日期与所选业务日无关（已修复：__IDLE__ 不再出现，逐条下发 generatedForDate，date 语义已明确为「只决定扫描哪天」）

**现象**：`GET /api/v1/pc/classification/suggestions?date=2026-09-27` 返回 2 条：

1. `clusterKey = "app:__idle__"`（空闲时段），样本 2 条；
2. `clusterKey = "app:java"`，样本 1 条。

两点的 `sampleRecordsJson` 中样本时间分别为 **2026-09-19** 与 **2026-09-04**，
与所选业务日 2026-09-27 无关。

**问题**：
1. `__idle__`（空闲）是哨兵记录，不是真实应用，对其生成"归类建议"没有意义
   （采纳后会把空闲时段归入某分类）；
2. `date` 参数疑似只影响待办生成范围，样本可能横跨任意历史日期——
   前端展示「影响面为该业务日」会与实际不符（预览 2026-09-27 影响记录数为 0）。

**建议**：聚合时排除 `__IDLE__` 记录；或在前端语义上单列"空闲时段"处理；
`date` 参数的过滤语义建议在文档中明确（是"建议生成日"还是"样本所属日"）。

---

## ✅ PC-7（低优先级）heatmap/grid 的 hour 维度只返回起始日 24 桶（已修复：跨日 + hour 返回 400 + 明确文案）

`dimension=hour` 时返回 `start` 当日的 24 个小时桶单行（规格已有备注），
跨日范围下其余日期被忽略。前端曾因该行为把整图打坏（现已移除该用法）。
建议：要么在跨日 + hour 时报 400，要么返回所有日期 × 24 小时的矩阵，
避免"参数合法但结果与范围预期不符"。

---

## 功能缺口（非缺陷，供排期参考）

### ✅ GAP-1 键鼠逐键分布没有范围版聚合（已实现：GET /pc/aggregation/keystats?start&end，字段与单日 keystats 同构）

`summary.keystats`（keyPressCounts / 左右键 / 滚轮）只支持单日 `date`；
聚合组（`/pc/aggregation/*`，共 4 个：focus-blocks / app-usage / late-night /
category-distribution）没有键盘/鼠标维度的接口。
导致范围模式（近 30/90 天）下「键盘热力图」「鼠标热力图」无数据可渲染，
前端只能隐藏该卡片。

**替代方案不可行（已实测排除）**：`GET /pc/detail` 支持 `dateFrom/dateTo`
且 `input-minute` 记录带 `keyCounts`，理论上前端可自行聚合；但记录量实测为
**9.27 单日 658 条、近 30 天 15,246 条**（pageSize 上限 200 → 30 天需 77 次请求），
90 天约 4.5 万条，前端逐页拉取聚合不可行。

**建议**：聚合组增加键鼠维度，例如
`GET /api/v1/pc/aggregation/keyboard?start&end`，
返回 `{ keyCounts, leftClicks, middleClicks, rightClicks, scrollDistance, peakKps, peakCps }`。
前端数据字段与单日版完全同构，后端就绪后把卡片恢复到范围模式只需去掉一层条件。

---

## 已在前端规避的事项（供对照，不需后端改动）

| 问题 | 前端规避方式 |
| --- | --- |
| PC-1 覆盖稀疏 | 「时间块热力」条形改用 summary.heatmap（覆盖完整），activity-analysis 仅取待分类计数与块内应用 |
| PC-2 时长超限 | 不再把 activeDurationSeconds 换算成分钟展示 |
| PC-3 量纲不一致 | 条宽用 activeMinutes/60，颜色用 0–5 档位色阶 |
| PC-4 UTC 切桶 | 贡献图按桶起点标注日期，并在卡片说明中注明该局限 |
| PC-6 空闲时段显示为"未知应用" | clusterKey 解析回退：`app:__idle__` → 空闲时段，`app:xxx` → xxx |
