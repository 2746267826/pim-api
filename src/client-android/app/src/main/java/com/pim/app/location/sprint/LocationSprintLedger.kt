package com.pim.app.location.sprint

import com.pim.app.data.ForensicEventDao
import com.pim.app.data.ForensicEventEntity
import com.pim.app.forensics.ForensicEventTypes
import com.pim.app.mobile.logs.StructuredLogRepository
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException
import org.json.JSONObject

/**
 * 冲刺台账的设备端写入口（WO-ANDROID-GATE-20260926 REQ-8 / AC-8.1 ~ AC-8.4）。
 *
 * 复用既有取证事件通道 `mobile_forensic_events`（先例 `KeepAliveLedger`）：
 * 上传、幂等、30 天清理、诊断导出这些既有能力直接生效，不必重做一套。
 *
 * **口径纪律（AC-5.4 / AC-14.8）**：
 * - 只有 [SprintOutcome.EXECUTED] 的记录才算「已冲刺」；
 * - 关闭状态下写的是 [SprintOutcome.SKIPPED] 记录，原因见 [SprintSkipReasons]
 *   —— 既满足 AC-8.2（未冲刺原因非空），又不违反 AC-5.4（不得伪造已执行记录）；
 * - 被动点**不**计入本类统计（由 [PassiveLocationLedger] 单独记账）。
 *
 * 负载**只含结构化诊断字段**，不含经纬度（AC-8.3）。
 */
@Singleton
class LocationSprintLedger @Inject constructor(
    private val dao: ForensicEventDao,
    private val logs: StructuredLogRepository
) : SprintLedgerPort {

    /** 写入一条**已执行**的冲刺记录（AC-8.1：字段齐备）。 */
    override suspend fun recordExecuted(result: SprintWindowResult): Boolean = record(
        eventType = LocationSprintEventTypes.SPRINT,
        occurredAtUtcMillis = result.startedAtUtcMillis,
        clientItemKey = sprintKey(result.startedAtUtcMillis, SprintOutcome.EXECUTED, null),
        payload = JSONObject()
            .put("outcome", SprintOutcome.EXECUTED)
            .put("outcomeLabel", SprintOutcome.label(SprintOutcome.EXECUTED))
            .put("sprintStartedAtUtcMillis", result.startedAtUtcMillis)
            .put("sprintEndedAtUtcMillis", result.endedAtUtcMillis)
            .put("sprintDurationMillis", result.durationMillis)
            .put("sprintSampleCount", result.sampleCount)
            .put(
                "sprintBestAccuracyMeters",
                result.bestAccuracyMeters?.toDouble() ?: JSONObject.NULL
            )
            .put("sprintAcceptedCount", result.acceptedCount)
            .put("sprintSkipReason", JSONObject.NULL)
    )

    /**
     * 写入一条**未冲刺**记录（AC-8.2：原因非空）。
     *
     * 注意 AC-5.4：这里写入的是「跳过」而不是「已冲刺」，
     * 台账消费方必须按 `outcome` 区分，不得把跳过计成已冲刺。
     */
    override suspend fun recordSkipped(
        occurredAtUtcMillis: Long,
        reason: String
    ): Boolean = record(
        eventType = LocationSprintEventTypes.SPRINT,
        occurredAtUtcMillis = occurredAtUtcMillis,
        clientItemKey = sprintKey(occurredAtUtcMillis, SprintOutcome.SKIPPED, reason),
        payload = JSONObject()
            .put("outcome", SprintOutcome.SKIPPED)
            .put("outcomeLabel", SprintOutcome.label(SprintOutcome.SKIPPED))
            .put("sprintStartedAtUtcMillis", JSONObject.NULL)
            .put("sprintEndedAtUtcMillis", JSONObject.NULL)
            .put("sprintDurationMillis", JSONObject.NULL)
            .put("sprintSampleCount", JSONObject.NULL)
            .put("sprintBestAccuracyMeters", JSONObject.NULL)
            .put("sprintAcceptedCount", JSONObject.NULL)
            .put("sprintSkipReason", reason)
            .put("sprintSkipReasonLabel", SprintSkipReasons.label(reason))
    )

    /**
     * 最近 24 小时内的**已执行**冲刺次数（REQ-9 / D7）。
     *
     * 只数 `outcome = executed` 的记录：AC-5.3.1 的验收口径是「台账不再新增**已冲刺**记录」，
     * 关闭期间产生的跳过记录不得被算进去。
     */
    suspend fun executedCountSince(fromUtcMillis: Long): Int {
        return try {
            // 走 SQL（事件类型 + 时间窗 + outcome 载荷），不是「读最近 N 条再过滤」：
            // 高速档下跳过记录可达约 34560 条/天，固定条数上限会把窗口内的已执行记录
            // 挤出读取范围，把「其实冲了很多次」显示成 0 次。
            dao.countByTypeInWindowWithPayloadLike(
                eventType = SPRINT_EVENT_TYPE,
                fromUtc = fromUtcMillis,
                payloadLike = EXECUTED_PAYLOAD_LIKE
            )
        } catch (ex: CancellationException) {
            throw ex
        } catch (ex: Exception) {
            // 读失败必须**显式上抛**，不能悄悄变成 0 次：那会把「读不到」伪装成
            // 「一次都没冲」，正好掩盖 AC-5.3 要复查的「关了还在跑」。
            logs.error("location-sprint", "读取冲刺台账失败：${ex.message ?: ex::class.java.simpleName}", ex)
            throw ex
        }
    }

    /**
     * 最近一次**已执行**窗口的跨度（毫秒）；没有已执行记录时为 null。
     *
     * 供状态页现场核对「约 30 秒、不早退」（AC-2.2 / AC-3.1），
     * 避免验收方必须导出诊断包才能看到窗口跨度。
     */
    suspend fun lastExecutedWindowDurationMillis(): Long? = try {
        // 走 SQL（与 24 小时计数同一口径）：高速档的跳过记录可达数万条/天，
        // 「读最近 N 条再过滤」会让这一行随跳过记录的增长而消失（I-3）。
        dao.latestPayloadByTypeLike(SPRINT_EVENT_TYPE, EXECUTED_PAYLOAD_LIKE)?.let { payload ->
            runCatching {
                val root = org.json.JSONObject(payload)
                if (root.isNull("sprintDurationMillis")) null
                else root.getLong("sprintDurationMillis")
            }.getOrNull()
        }
    } catch (ex: CancellationException) {
        throw ex
    } catch (_: Exception) {
        null
    }

    /** 时间窗内是否存在**任何**冲刺台账记录（含跳过记录）。 */
    suspend fun hasAnySprintLedgerSince(fromUtcMillis: Long): Boolean =
        dao.countByTypeInWindow(SPRINT_EVENT_TYPE, fromUtcMillis) > 0

    /** 时间窗内是否有心跳（AC-9.2 的空态判定三条件之一：无定位点、无心跳、无台账）。 */
    suspend fun hasAnyHeartbeatSince(fromUtcMillis: Long): Boolean =
        dao.countByTypeInWindow(ForensicEventTypes.HEARTBEAT, fromUtcMillis) > 0

    private suspend fun record(
        eventType: String,
        occurredAtUtcMillis: Long,
        clientItemKey: String,
        payload: JSONObject
    ): Boolean = try {
        dao.insertIgnore(
            ForensicEventEntity(
                eventType = eventType,
                occurredAtUtc = occurredAtUtcMillis,
                clientItemKey = clientItemKey,
                payloadJson = payload.toString()
            )
        ) != -1L
    } catch (ex: CancellationException) {
        throw ex
    } catch (ex: Exception) {
        // 写台账失败不得中断采集（沿用阶段一 AC-3.3 的约定），但必须留可见出口。
        logs.error("location-sprint", "写入冲刺台账失败：${ex.message ?: ex::class.java.simpleName}", ex)
        false
    }

    /**
     * 冲刺记录的幂等键。
     *
     * 已执行记录按「发起时刻」去重（同一窗口只可能有一条）；
     * 跳过记录额外带上原因，避免同一时刻的不同原因互相覆盖。
     */
    fun sprintKey(occurredAtUtcMillis: Long, outcome: String, skipReason: String?): String =
        "sprint-${occurredAtUtcMillis / 1_000L}-$outcome${skipReason?.let { "-$it" } ?: ""}"

    companion object {
        /**
         * 「已执行」在台账载荷里的匹配串。
         *
         * 断言用 `payload_json LIKE` 而不是精确 JSON 匹配：载荷由设备端 `JSONObject`
         * 序列化，字段顺序与转义不保证稳定，精确匹配随时会因格式变化而失效。
         */
        const val EXECUTED_PAYLOAD_LIKE = "%\"outcome\":\"executed\"%"

        /** 冲刺台账的事件类型（供谓词与测试引用，避免字符串散落）。 */
        val SPRINT_EVENT_TYPE = LocationSprintEventTypes.SPRINT

        /** 从台账负载里读出 outcome；无法解析时按「非已执行」处理（保守，不虚报）。 */
        fun payloadOutcome(payloadJson: String): String? = runCatching {
            val root = JSONObject(payloadJson)
            if (root.isNull("outcome")) null else root.optString("outcome").takeIf { it.isNotBlank() }
        }.getOrNull()
    }
}
