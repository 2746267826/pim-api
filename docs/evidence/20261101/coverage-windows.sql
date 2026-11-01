-- issue #551 复现材料：导出「覆盖窗口」，喂给 gap-window-repro.py
--
-- 口径与 MobileGapService.GetGapsAsync 一致（master 88c11c6，:47-73）：
--   1) 使用记录事件的源窗口      mobile_usage_events.source_window_start_utc / source_window_end_utc
--   2) 非 fallback 的使用汇总     mobile_usage_summaries.window_start_utc / window_end_utc
--                                （source_kind 以 usage-stats-fallback 开头的**不算**覆盖）
--   3) failed_count = 0 且状态为 completed / completed-with-errors 的批次
--      mobile_sync_batches.window_start_utc / window_end_utc
--
-- 执行环境：按仓库 AGENTS.md，用镜像库 pim_test 或只读副本，不要在生产库开写会话。
-- 输出：CSV（source,start_utc,end_utc），时间统一 UTC。

COPY (
    SELECT 'events' AS source,
           source_window_start_utc AS start_utc,
           source_window_end_utc   AS end_utc
      FROM mobile_usage_events
     WHERE source_window_start_utc > now() - interval '20 days'
     GROUP BY 2, 3

    UNION

    SELECT 'summaries',
           window_start_utc,
           window_end_utc
      FROM mobile_usage_summaries
     WHERE window_start_utc > now() - interval '20 days'
       AND source_kind NOT LIKE '%fallback%'
     GROUP BY 2, 3

    UNION

    SELECT 'batches',
           window_start_utc,
           window_end_utc
      FROM mobile_sync_batches
     WHERE window_start_utc > now() - interval '20 days'
       AND failed_count = 0
       AND status IN ('completed', 'completed-with-errors')
)
TO STDOUT WITH (FORMAT csv, HEADER true);

-- 跑法示例（只读）：
--   psql "$PIM_TEST_DSN" -f coverage-windows.sql > coverage-windows.csv
--   python3 gap-window-repro.py --csv coverage-windows.csv \
--       --range-start 2026-10-18T10:02:13Z --range-end 2026-11-01T10:02:13Z
