package com.pim.app.location.sprint

/**
 * WO-ANDROID-GATE-20260926 REQ-2 / REQ-3 / REQ-4 / REQ-5 / REQ-8：30 秒定位冲刺的领域契约。
 *
 * 本文件**不依赖任何 Android 类**，因此窗口判定、收点规则与开关语义可以脱离设备直接单测。
 * 这些数值全部是需求方确认过的参数（工单 §7），改动前请对照工单。
 */
object LocationSprintContract {
    /**
     * 冲刺窗口上限（REQ-2 / A2：最长 30 秒）。窗口**不早退**（REQ-3 / A6 / D6）：
     * 即使中途已拿到达标点，也继续取点直到本上限。
     */
    const val WINDOW_MILLIS = 30_000L

    /**
     * 冲刺取点目标间隔（D3：≈1 秒）：沿用基线手动采集的固有节奏
     * （`LocationUpdateSource` 的 `intervalMillis = 1000` / `minUpdateIntervalMillis = 800`）。
     */
    const val SAMPLE_INTERVAL_MILLIS = 1_000L

    /**
     * 冲刺独立注册流的最小更新间隔（D3 下界 800 毫秒来自基线；
     * AC-2.1 要求相邻取点间隔中位数 ∈ [800, 1500] 毫秒）。
     */
    const val MIN_SAMPLE_INTERVAL_MILLIS = 800L

    /** 高速档注册间隔（REQ-10：不改动采集频率档位）。 */
    const val HIGH_SPEED_INTERVAL_MILLIS = 2_500L
}

/**
 * 冲刺**未发起**的原因编码（AC-8.2：未冲刺时原因非空）。
 *
 * 关闭状态写的是**跳过**记录而不是「已冲刺」记录（AC-5.4），
 * 靠 [SprintOutcome] 把两者分开，台账与状态页只把 `executed` 计为「已冲刺」。
 */
object SprintSkipReasons {
    /** 冲刺开关关闭（REQ-5）：最常被验收方复查的一条。 */
    const val DISABLED = "sprint-disabled"

    /** 高速档（2.5 秒一拍）期间不发起冲刺（AC-2.4）。 */
    const val HIGH_SPEED = "high-speed"

    /** 非采集时段（策略档为 Off / 已暂停）。 */
    const val NOT_COLLECTING = "not-collecting"

    /** 缺少定位权限或系统定位服务未开启（REQ-8 要求的可见出口，不静默失败）。 */
    const val PREREQUISITE_BLOCKED = "prerequisite-blocked"

    /**
     * 本拍尚未到采集周期（REQ-2 / D2：每个采集周期一次）。
     *
     * 采集循环每 30 秒唤醒一次，而档位可能是 45/120/600 秒 —— 未到点的唤醒
     * 不发起冲刺，也**不写**「跳过」台账（否则台账会被每 30 秒一条的噪声灌满）。
     */
    const val NOT_THIS_PERIOD = "not-this-period"

    /**
     * 本周期已有窗口在跑（手动在自动窗口期间触发时使用）。
     *
     * 不用它去跳过**周期驱动**的请求（那会违反 AC-2.5 的「窗口与周期相接」）；
     * 只用于手动这一拍 —— 手动与自动是两个独立触发源，同一周期不得冲两次（D2）。
     */
    const val WINDOW_ALREADY_OPEN = "window-already-open"

    val ALL = setOf(
        DISABLED, HIGH_SPEED, NOT_COLLECTING, PREREQUISITE_BLOCKED, WINDOW_ALREADY_OPEN
    )

    fun label(reason: String): String = when (reason) {
        DISABLED -> "冲刺开关已关闭"
        HIGH_SPEED -> "高速轨迹档不叠加冲刺"
        NOT_COLLECTING -> "当前不在采集时段"
        PREREQUISITE_BLOCKED -> "定位权限或系统定位未就绪"
        NOT_THIS_PERIOD -> "尚未到本采集周期"
        WINDOW_ALREADY_OPEN -> "本采集周期已冲刺"
        else -> "未冲刺"
    }
}

/** 冲刺台账的结果（AC-8.1 / AC-8.2）。 */
object SprintOutcome {
    /** 本周期真的跑了一段冲刺窗口。 */
    const val EXECUTED = "executed"

    /** 本周期按规则跳过（原因见 [SprintSkipReasons]）。 */
    const val SKIPPED = "skipped"

    val ALL = setOf(EXECUTED, SKIPPED)

    fun label(outcome: String): String = when (outcome) {
        EXECUTED -> "已冲刺"
        SKIPPED -> "已跳过"
        else -> "未知结果"
    }
}

/**
 * 一次冲刺的执行结果（由 [LocationSprintRunner] 在窗口结束后产出）。
 *
 * 口径（工单 §6）：
 * - [sampleCount] = **回调条数（去重之前）**，即定位回调到达应用的次数；
 * - [acceptedCount] = **入库条数**（通过质量门并写入台账的点数）；
 * - [bestAccuracyMeters] = 窗口内最小水平精度（无点时为 null），且**必须达标**才算「最好的一条」。
 */
data class SprintWindowResult(
    val startedAtUtcMillis: Long,
    val endedAtUtcMillis: Long,
    val sampleCount: Int,
    val bestAccuracyMeters: Float?,
    val acceptedCount: Int
) {
    /** AC-2.2 / AC-3.1：窗口跨度（用于判定 ≤ 30 秒 + ε 与 ≥ 30 秒 − 1 个采样间隔）。 */
    val durationMillis: Long get() = endedAtUtcMillis - startedAtUtcMillis
}
