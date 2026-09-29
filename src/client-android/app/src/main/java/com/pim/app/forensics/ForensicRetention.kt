package com.pim.app.forensics

import com.pim.app.data.ForensicEventDao
import com.pim.app.data.MobileDataDao
import com.pim.app.mobile.logs.StructuredLogRepository
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException

/**
 * 取证数据的保留策略（REQ-6 / REQ-9）。
 *
 * 两条通道都**不设条数上限**（R4-P3），统一按 30 天时间清理兜底：
 * - [ForensicEventDao]：进程退出原因台账与存活心跳（REQ-1 ~ REQ-4，AC-6.2）；
 * - [MobileDataDao]：定位丢弃原因明细（REQ-9，AC-9.3）。
 * - [MobileDataDao]：策略切换历史（WO-ANDROID-POLICY-TRANSITION-20260928 REQ-6 / AC-6.2）。
 *
 * 清理只按时间，不看上传状态：30 天内未上传的条目无论如何都不会被删，
 * 因此"清理后设备端条数 == 待上传计数"仍然成立（AC-6.2）。
 */
@Singleton
class ForensicRetention @Inject constructor(
    private val forensicDao: ForensicEventDao,
    private val mobileDataDao: MobileDataDao,
    private val logs: StructuredLogRepository
) {
    /** 清理窗口：30 天。 */
    val windowMillis: Long = WINDOW_DAYS * 24L * 60L * 60L * 1000L

    /** 执行一次时间清理，返回被移除的条目总数。任何失败都只记日志，不阻断采集与同步（REQ-28）。 */
    suspend fun purgeExpired(nowUtcMillis: Long): Int {
        val cutoff = nowUtcMillis - windowMillis
        var removed = 0

        removed += runCatchingStep("取证台账") { forensicDao.deleteOlderThan(cutoff) }
        removed += runCatchingStep("丢弃原因明细") { mobileDataDao.deleteDroppedDiagnosticsOlderThan(cutoff) }
        // WO-ANDROID-POLICY-TRANSITION-20260928 REQ-6：策略切换历史走**同一条**启动清理时间线，
        // 只按时间清、边界严格小于（恰好 30 天前的记录保留，D-11）。
        removed += runCatchingStep("策略切换历史") { mobileDataDao.deletePolicyTransitionsOlderThan(cutoff) }

        if (removed > 0) {
            logs.info("forensics", "按 30 天窗口清理本地取证数据，共移除 $removed 条。")
        }
        return removed
    }

    private suspend fun runCatchingStep(label: String, block: suspend () -> Int): Int = try {
        block()
    } catch (ex: CancellationException) {
        throw ex
    } catch (ex: Exception) {
        logs.error("forensics", "$label 时间清理失败：${ex.message ?: ex::class.java.simpleName}", ex)
        0
    }

    companion object {
        /** 与工单确认的时间清理窗口一致（AC-6.2 / AC-9.3）。 */
        const val WINDOW_DAYS = 30L
    }
}
