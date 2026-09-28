package com.pim.app.status

import com.pim.app.data.MobileDataDao
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/** REQ-5：30 天窗口内的一次快照——**行与分母来自同一个窗口起点**，不会自相矛盾（AC-5.4）。 */
data class PolicyTransitionWindow(
    val rows: List<PolicyTransitionSnapshot> = emptyList(),
    /** 窗口内全部条数（底部「30 天内共 N 条」逐字取它）。 */
    val totalInWindow: Int = 0
)

/**
 * 状态页「当前状态 / 上次切换」（REQ-4）与设置页「策略切换历史」（REQ-5）的读取入口。
 *
 * 抽象成接口是为了让界面层能在单测里用最轻的替身驱动，而不必为了两块 UI 拖起一整套 Room
 * （沿用仓库既有做法：`ConnectionProbeEvidenceStore`）。
 *
 * 注意：「已持续」时长**不在**这里的流里算（那会只在 Room 发射时才更新），
 * 而是由 `StatusCenterRepository` 在每次重建状态快照时用注入时钟重算（D-12）。
 */
interface PolicyTransitionHistorySource {
    /** REQ-4：最新一条切换记录；无记录为 null（界面显示「暂无记录」/「未知」）。 */
    fun observeLatest(): Flow<PolicyTransitionSnapshot?>

    /** REQ-5：30 天窗口内的记录（时间倒序）与窗口内条数。 */
    fun observeWindow(): Flow<PolicyTransitionWindow>
}

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-4 / REQ-5：策略切换历史的读取实现。
 *
 * - REQ-4：状态页只取**最新 1 条**（查询由 `limit = 5` 收敛）；
 * - REQ-5：设置页「策略切换历史」取 **30 天窗口**内全部记录（时间倒序）与窗口内条数。
 *
 * 时间窗口固定 30 天、**不联动**设置页「日志保留天数」（P4；该设置默认 7 天、可选 1/7/14/30）。
 * 清理走启动清理链（见 `ForensicRetention`），这里只负责读。
 */
@Singleton
class PolicyTransitionHistoryRepository internal constructor(
    private val dao: MobileDataDao,
    private val nowMillis: () -> Long
) : PolicyTransitionHistorySource {
    @Inject
    constructor(dao: MobileDataDao) : this(dao, System::currentTimeMillis)

    /** REQ-4：最新一条记录。 */
    override fun observeLatest(): Flow<PolicyTransitionSnapshot?> =
        dao.latestPolicyTransition().map { it?.toPolicyTransitionSnapshot() }

    /**
     * REQ-5：30 天窗口内的记录与条数。
     *
     * **一次查询同时给出行与分母**：板块默认只画 20 条是在界面层 `take(20)`，
     * 因此「30 天内共 N 条」永远等于窗口内实际行数（AC-5.4），不会出现两条查询错帧。
     */
    override fun observeWindow(): Flow<PolicyTransitionWindow> =
        dao.policyTransitionsSince(sinceUtc(), limit = Int.MAX_VALUE).map { rows ->
            val snapshots = rows.map { it.toPolicyTransitionSnapshot() }
            PolicyTransitionWindow(rows = snapshots, totalInWindow = snapshots.size)
        }

    private fun sinceUtc(): Long = nowMillis() - WINDOW_MILLIS

    companion object {
        /** 30 天（与 `ForensicRetention.WINDOW_DAYS` 同窗，P4：固定值、不联动日志保留天数）。 */
        const val WINDOW_DAYS = 30L
        const val WINDOW_MILLIS = WINDOW_DAYS * 24L * 60L * 60L * 1000L
    }
}

/**
 * REQ-4（D-2 / P5）：「当前状态」的已持续时长 = 快照时刻 − 库中最新一条切换记录时间。
 *
 * 无记录时返回 null → 界面显示「未知」；设备时钟回拨（记录时间在未来）时收敛到 0
 * （界面显示「不足 1 分钟」），不显示负数时长。纯函数，便于逐档验收（AC-4.2）。
 */
internal fun currentPolicyDurationMillis(
    latest: PolicyTransitionSnapshot?,
    nowMillis: Long
): Long? = latest?.let { (nowMillis - it.occurredAtMillis).coerceAtLeast(0L) }
