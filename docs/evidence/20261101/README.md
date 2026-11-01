# issue #551 复现材料：缺口窗口永不收敛

配套 issue：`2746267826/pim-api#551`

| 文件 | 作用 |
|---|---|
| `gap-window-repro.py` | 复刻服务端 `MobileGapService.GetGapsAsync` 的缺口回报，并与「三源覆盖合并取补集」的真实空洞并排输出 |
| `coverage-windows.sql` | 导出覆盖窗口 CSV 的只读 SQL（事件源窗口 / 非 fallback 汇总 / `failed_count=0` 且已完成的批次） |
| `client-gap-retries.py` | 解析 Android 诊断包 `pim-diagnostics-*.zip`，统计窗口上传结果、每轮缺口窗口拆分、重试排行 |

三个脚本都**不写库、不联网**：`gap-window-repro.py` 只读本地 CSV，`client-gap-retries.py` 只读本地 zip。

## 跑法

```bash
# 1) 导出覆盖窗口（按仓库 AGENTS.md：用镜像库 pim_test 或只读副本，不要在生产库开写会话）
psql "$PIM_TEST_DSN" -f coverage-windows.sql > coverage-windows.csv

# 2) 对比两种口径（区间与 Android 客户端一致：now-14d .. now）
python3 gap-window-repro.py --csv coverage-windows.csv \
    --range-start 2026-10-18T10:02:13Z --range-end 2026-11-01T10:02:13Z

# 3) 客户端侧证据（Android 状态中心 → 导出诊断 得到的 zip）
python3 client-gap-retries.py pim-diagnostics-1793525302613.zip
```

## 判读要点

- **服务端口径**：每个「一天」窗口最多产出一个缺口，范围是 `[首个断点 → 窗口结束]`，断点之后即使覆盖恢复也不再续算（`MobileGapService.cs:97-119`）。
- **真实空洞**：三源合并后取补集，只保留 ≥5 分钟的洞。
- 两者的差距就是「重复上传量」的来源：客户端按服务端回报的窗口重采，采到的事件服务端早已存在 → `skipped`，覆盖不推进 → 下一轮继续回报同样的窗口。

## 注意

- 用这份 SQL 导出的 CSV **不要提交进仓库**（仓库规范禁止把生产数据导出物放进任何产物）；本目录只放脚本与 SQL。
- 数字会随时间漂移：客户端每轮重传都会给同一个历史窗口新增一条 `completed` 批次（`failed_count=0`），覆盖窗口随之增加，缺口起点随之移动，但**缺口数量不会收敛**。
