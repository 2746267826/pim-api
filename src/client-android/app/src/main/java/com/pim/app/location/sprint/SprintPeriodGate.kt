package com.pim.app.location.sprint

/**
 * 冲刺的**周期门控**（WO-ANDROID-GATE-20260926 REQ-2 / D2 / AC-2.5）。
 *
 * 为什么需要它：采集循环的唤醒周期恒为 **30 秒**（`withTimeoutOrNull(30_000L)`，
 * 另加运动信号与 fix 驱动的即时唤醒），而策略档的注册间隔是 30 / 45 / 120 / 600 秒。
 * 「每轮唤醒都冲」会让 45 秒档退化成 30 秒一拍，违反 D2「每个采集周期执行一次冲刺」，
 * 也与工单 §1 的占空比推算（步行 45 秒档 ≈67%）不符。
 *
 * 本类把「循环唤醒」收敛成「采集周期」：
 * 只有距上一个**周期锚点**已过该档的注册间隔时才放行。
 *
 * **运动/车载档（30 秒硬下限）**：间隔与唤醒周期相同，因此每一轮都放行，
 * 窗口与周期相接、接近连续采样 —— 这正是需求方选定的方案 B（A8 / AC-2.5），
 * 不是「节拍过密」。这里**没有**任何「过密即跳过」的规则。
 *
 * 纯计算，不依赖 Android，可脱离设备单测。
 */
class SprintPeriodGate {
    /** 上一个周期锚点（放行时刻）；null = 尚未冲过。 */
    private var anchorUtcMillis: Long? = null

    /** 上一次决策所依据的档位间隔（间隔改变时立即按新值重算）。 */
    private var lastIntervalMillis: Long = 0L

    /**
     * 判断此刻是否到了「本采集周期该冲」的时刻。
     *
     * @param nowUtcMillis 当前墙钟（与策略决策同一时间源）。
     * @param requestIntervalMillis 当前策略档的注册间隔。
     */
    fun shouldStart(nowUtcMillis: Long, requestIntervalMillis: Long): Boolean {
        val anchor = anchorUtcMillis
        // 首次、或档位间隔被改动（含用户手动改档、运动信号切换档位）→ 立即放行，
        // 否则用户会觉得「把间隔改快了但冲刺还在等旧周期」。
        if (anchor == null || requestIntervalMillis != lastIntervalMillis) {
            lastIntervalMillis = requestIntervalMillis
            return true
        }
        return nowUtcMillis - anchor >= requestIntervalMillis
    }

    /**
     * 记录本次周期锚点（发起冲刺时调用）。
     *
     * 锚点用**放行时刻**而不是窗口结束时刻：AC-5.6 要求周期锚点由周期驱动，
     * 不能被冲刺窗口的长度拖后（否则 45 秒档会变成 30+45=75 秒一拍）。
     */
    fun onWindowStarted(startedAtUtcMillis: Long) {
        anchorUtcMillis = startedAtUtcMillis
    }

    /**
     * 距下一个周期边界还有多少毫秒（供采集循环把等待收敛到边界）。
     *
     * 为什么需要：采集循环的兜底唤醒是 30 秒，而档位可能是 45 / 120 / 600 秒。
     * 边界与唤醒点**不对齐**时（例如 45 秒档在 30 秒网格上），本拍会被判
     * 「未到点」，下一拍最早也要等 30 秒 —— 于是 45 秒档实际变成 **60 秒**才冲一次
     * （独立 review round 3 的 I-2：占空比从 67% 掉到约 50%，且是**结构性**的，
     * 不是偶发抖动）。
     *
     * 让循环最多等到边界即可对齐；**不放宽**放行规则，也不加长 30 秒的兜底上限
     * （返回值不超过调用方给的 `capMillis`）。
     *
     * @return 建议等待的毫秒数（未冲过 / 档位变化时为 0 = 立即放行，无需等待）。
     */
    fun suggestedWaitMillis(
        nowUtcMillis: Long,
        requestIntervalMillis: Long,
        capMillis: Long
    ): Long {
        val anchor = anchorUtcMillis ?: return 0L
        if (requestIntervalMillis != lastIntervalMillis) return 0L
        if (requestIntervalMillis <= capMillis) {
            // 档位间隔本身不超过兜底唤醒粒度（如运动/车载 30 秒）：按上限即可，
            // 无需额外对齐（AC-2.5 的「窗口与周期相接」保持原样）。
            return 0L
        }
        val untilBoundary = (anchor + requestIntervalMillis - nowUtcMillis).coerceAtLeast(0L)
        // 只**缩短**等待（对齐到更早的边界），绝不超过兜底上限。
        return untilBoundary.coerceAtMost(capMillis)
    }

    /** 采集停止/重启：清除锚点，下次立即恢复冲刺。 */
    fun reset() {
        anchorUtcMillis = null
        lastIntervalMillis = 0L
    }

    /** 测试辅助：当前锚点。 */
    internal fun lastDecisionAtUtcMillisForTest(): Long = anchorUtcMillis ?: 0L
}
