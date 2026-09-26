package com.pim.app.location.sprint

import com.pim.app.data.MobileDataDao
import com.pim.app.settings.TrackingSettingsStore
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 状态页「最近 24 小时冲刺次数」的展示口径（REQ-9 / AC-9.2）。
 *
 * 三态必须分开（AC-9.2 明令分两态 + §5 的错误态）：
 * - [Empty]：最近 24 小时**无任何采集数据**（无定位点、无心跳、无台账记录）→ 「暂无」；
 * - [Value]：**有采集数据** → 如实显示次数（含 0 次）；
 * - [Failed]：读取失败 → 「读取失败」，**保留上次值**。
 *
 * 之所以不用「次数 == 0 就是暂无」，是因为那会让「冲刺关了、采集还在跑」与
 * 「压根没采集」在页面上长得一样 —— 验收方复验 AC-5.3 时正是靠这个区分。
 */
sealed interface SprintCountDisplay {
    /** 尚未读取完成：显示「—」（§5 加载中占位）。 */
    data object Loading : SprintCountDisplay

    /** 无采集数据：显示「暂无（最近 24 小时无采集数据）」。 */
    data object Empty : SprintCountDisplay

    /** 有采集数据：显示「N 次」（N 可以为 0）。 */
    data class Value(val count: Int) : SprintCountDisplay

    /** 读取失败：显示「读取失败」（§5 错误态），上层保留上次值。 */
    data object Failed : SprintCountDisplay
}

/** 状态页冲刺区块的展示数据（AC-9.1 / AC-9.2 / §5）。 */
data class SprintSummary(
    /** 冲刺开关当前状态（AC-9.2：空态与错误态也必须展示）。 */
    val enabled: Boolean?,
    /** 最近 24 小时已执行冲刺次数；无采集数据或读取失败时为 null（与「0 次」区分）。 */
    val count: Int?,
    val countDisplay: SprintCountDisplay,
    /**
     * 最近一次冲刺窗口的跨度（毫秒）；从未冲刺或读取失败时为 null。
     *
     * 让验收方**在应用内**就能核对「约 30 秒、不早退」（AC-2.2 / AC-3.1），
     * 不必先导出诊断包再翻台账（设备操作卡 §3 曾因此无法现场核对）。
     */
    val lastWindowDurationMillis: Long? = null
)

/**
 * 冲刺概况读取器（REQ-9 / D7：统计窗口 = 最近 24 小时）。
 *
 * 口径 = **设备端本地台账**，不新增接口（REQ-9 明文）。
 */
@Singleton
class LocationSprintSummaryRepository internal constructor(
    private val ledger: LocationSprintLedger,
    private val dao: MobileDataDao,
    private val trackingSettingsStore: TrackingSettingsStore,
    private val nowUtcMillis: () -> Long
) {
    /** 生产构造（注入时钟用系统时钟；测试用 `internal` 主构造注入固定时钟）。 */
    @Inject
    constructor(
        ledger: LocationSprintLedger,
        dao: MobileDataDao,
        trackingSettingsStore: TrackingSettingsStore
    ) : this(ledger, dao, trackingSettingsStore, System::currentTimeMillis)

    /**
     * 读取概况。
     *
     * 读取顺序刻意是「先判空态、再取次数」：空态只需要一次存在性查询，
     * 不会因为计数查询失败就把「有数据但 0 次」误显示成「暂无」。
     *
     * @param previous 上次成功读取到的次数（错误态时保留，见 §5「保留上次值」）。
     */
    suspend fun read(previous: Int? = null): SprintSummary {
        val now = nowUtcMillis()
        val windowStart = now - WINDOW_MILLIS

        val enabled = runCatching { trackingSettingsStore.read().sprintEnabled }.getOrNull()
        val lastWindowDuration = runCatching { ledger.lastExecutedWindowDurationMillis() }.getOrNull()

        val hasCollectionData = try {
            // AC-9.2 的空态定义是「无定位点、无心跳、无台账记录」三条都成立，
            // 因此定位点也算采集数据（只看心跳/台账会漏掉「只采了点」的情形）。
            dao.countLocationPointsSince(windowStart) > 0 ||
                ledger.hasAnySprintLedgerSince(windowStart) ||
                ledger.hasAnyHeartbeatSince(windowStart)
        } catch (ex: kotlinx.coroutines.CancellationException) {
            throw ex
        } catch (_: Exception) {
            return SprintSummary(
                enabled = enabled,
                count = previous,
                countDisplay = SprintCountDisplay.Failed
            )
        }

        if (!hasCollectionData) {
            // AC-9.2 态一：无采集数据 → 「暂无」。
            return SprintSummary(
                enabled = enabled,
                count = null,
                countDisplay = SprintCountDisplay.Empty,
                lastWindowDurationMillis = lastWindowDuration
            )
        }

        val count = try {
            ledger.executedCountSince(windowStart)
        } catch (ex: kotlinx.coroutines.CancellationException) {
            throw ex
        } catch (_: Exception) {
            // §5 错误态：读取失败并保留上次值，不伪装成 0 次。
            return SprintSummary(
                enabled = enabled,
                count = previous,
                countDisplay = SprintCountDisplay.Failed
            )
        }

        return SprintSummary(
            enabled = enabled,
            count = count,
            countDisplay = SprintCountDisplay.Value(count),
            lastWindowDurationMillis = lastWindowDuration
        )
    }

    companion object {
        /** 统计窗口：最近 24 小时（D7）。 */
        const val WINDOW_MILLIS = 24L * 60L * 60L * 1_000L
    }
}
