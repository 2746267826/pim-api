package com.pim.app.location.passive

import com.pim.app.data.ForensicEventDao
import com.pim.app.data.ForensicEventEntity
import com.pim.app.location.sprint.PassiveLocationEventTypes
import com.pim.app.mobile.logs.StructuredLogRepository
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException
import org.json.JSONObject

/**
 * 被动定位的计数台账（WO-ANDROID-GATE-20260926 REQ-14 / AC-14.2 / AC-14.3）。
 *
 * AC-14.2 / AC-14.3 要求对账三个数：**被动回调总数（去重前）/ 入库数 / 丢弃记录数**，
 * 且**缺口 = 0**。入库数与丢弃数分别落在点表 `source = passive` 与丢弃诊断表
 * （原因编码 `passive-*`），回调总数没有落点 —— 因此按周期写一条摘要事件，
 * 让验收方能在台账里直接读出分母。
 */
@Singleton
open class PassiveLocationLedger @Inject constructor(
    private val dao: ForensicEventDao,
    private val logs: StructuredLogRepository
) {

    /** 写入一条被动回调计数摘要。 */
    open suspend fun recordCounters(
        occurredAtUtcMillis: Long,
        windowStartUtcMillis: Long,
        windowSequence: Long,
        callbackCount: Int,
        acceptedCount: Int,
        droppedCount: Int,
        duplicateCount: Int
    ): Boolean = try {
        val payload = JSONObject()
            .put("windowStartUtcMillis", windowStartUtcMillis)
            .put("windowSequence", windowSequence)
            .put("passiveCallbackCount", callbackCount)
            .put("passiveAcceptedCount", acceptedCount)
            .put("passiveDroppedCount", droppedCount)
            .put("passiveDuplicateCount", duplicateCount)
            // 三数对账缺口（AC-14.2）：回调 = 入库 + 丢弃 + 重复 时必须为 0。
            .put(
                "passiveUnaccountedCount",
                callbackCount - acceptedCount - droppedCount - duplicateCount
            )
        dao.insertIgnore(
            ForensicEventEntity(
                eventType = PassiveLocationEventTypes.PASSIVE_COUNTER,
                occurredAtUtc = occurredAtUtcMillis,
                // 幂等键含**窗口序号**：同一秒内刷新两次不会撞键，
                // 否则第二个窗口的计数会被 insertIgnore 静默丢掉（分母缺失）。
                clientItemKey = "passive-counter-$windowSequence-${windowStartUtcMillis / 1_000L}",
                payloadJson = payload.toString()
            )
        ) != -1L
    } catch (ex: CancellationException) {
        throw ex
    } catch (ex: Exception) {
        logs.error(
            "passive-location",
            "写入被动定位计数失败：${ex.message ?: ex::class.java.simpleName}",
            ex
        )
        false
    }
}
