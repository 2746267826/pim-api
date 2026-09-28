package com.pim.app.status

import com.pim.app.data.MobileDataDao
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.map

/**
 * 状态页「当前状态 / 上次切换」所需的一组值，**同一快照内自洽**（D-12）：
 * 时长由注入的时钟在本次映射时算一次，不在渲染期反复取 `now`。
 */
data class PolicyTransitionState(
    val latest: PolicyTransitionSnapshot? = null,
    /** 当前状态已持续时长 = 快照时刻 − 最新一条记录时间；无记录为 null（→ 显示「未知」）。 */
    val currentDurationMillis: Long? = null
)

/**
 * 策略切换历史的读取入口（REQ-4 / REQ-5）。
 *
 * 抽象成接口是为了让界面层（`SettingsViewModel`）能在单测里用最轻的替身驱动，
 * 而不必为了一个只读列表拖起一整套 Room（沿用仓库既有做法：`ConnectionProbeEvidenceStore`）。
 */
interface PolicyTransitionHistorySource {
    /** REQ-4：最新一条记录 + 已持续时长。 */
    fun observeCurrent(): Flow<PolicyTransitionState>

    /** REQ-5：30 天窗口内的记录，时间倒序。 */
    fun observeWindow(
        limit: Int = PolicyTransitionHistoryRepository.DEFAULT_WINDOW_LIMIT
    ): Flow<List<PolicyTransitionSnapshot>>

    /** REQ-5：30 天窗口内的条数（AC-5.4 的 N）。 */
    fun observeWindowCount(): Flow<Int>
}

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-4 / REQ-5：策略切换历史的读取入口。
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

    /** REQ-4：最新一条记录 + 已持续时长。 */
    override fun observeCurrent(): Flow<PolicyTransitionState> =
        dao.latestPolicyTransition().map { entity ->
            val snapshot = entity?.toPolicyTransitionSnapshot()
            val duration = snapshot?.let { (nowMillis() - it.occurredAtMillis).coerceAtLeast(0L) }
            PolicyTransitionState(latest = snapshot, currentDurationMillis = duration)
        }

    /** REQ-5：30 天窗口内的记录，时间倒序。 */
    override fun observeWindow(limit: Int): Flow<List<PolicyTransitionSnapshot>> =
        dao.policyTransitionsSince(sinceUtc(), limit)
            .map { rows -> rows.map { it.toPolicyTransitionSnapshot() } }

    /** REQ-5：30 天窗口内的条数（用于「仅显示最近 20 条 · 30 天内共 N 条」与 AC-5.4）。 */
    override fun observeWindowCount(): Flow<Int> = dao.policyTransitionCountSince(sinceUtc())

    private fun sinceUtc(): Long = nowMillis() - WINDOW_MILLIS

    companion object {
        /** 30 天（与 `ForensicRetention.WINDOW_DAYS` 同窗，P4：固定值、不联动日志保留天数）。 */
        const val WINDOW_DAYS = 30L
        const val WINDOW_MILLIS = WINDOW_DAYS * 24L * 60L * 60L * 1000L

        /** 默认窗口查询条数上限：板块默认 20 条 + 「展开全部」需要拿到全部。 */
        const val DEFAULT_WINDOW_LIMIT = Int.MAX_VALUE
    }
}
