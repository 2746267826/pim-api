package com.pim.app.location.sprint

/**
 * 冲刺台账的事件类型（WO-ANDROID-GATE-20260926 REQ-8 / AC-8.4）。
 *
 * 复刻阶段二 `KeepAliveEventTypes` 的先例：**新建独立对象**而不是并入阶段一的
 * `ForensicEventTypes.ALL`，以免改动那次已验证的写入口。
 *
 * **三处必须一致**，漏一处就会出现「设备上记了、服务端永远收不到」：
 * 1. 本对象（设备端事件类型登记处）；
 * 2. `Pim.Core.Liveness.ForensicEventTypes`（服务端契约）；
 * 3. `MobileForensicIngestService.KnownEventTypes`（**最关键**：未登记会被显式拒绝）。
 */
object LocationSprintEventTypes {
    /** 冲刺台账（发起/结束时刻、取点条数、最好精度、入库条数、未冲刺原因）。 */
    const val SPRINT = "location-sprint"

    val ALL = setOf(SPRINT)

    fun label(eventType: String): String = when (eventType) {
        SPRINT -> "定位冲刺"
        else -> "未知事件"
    }
}

/**
 * 被动定位的计数器事件类型（REQ-14 / AC-14.2 / AC-14.3）。
 *
 * AC-14.2/AC-14.3 要求对账「被动回调总数（去重前）/ 入库数 / 丢弃记录数」且缺口 = 0。
 * 丢弃记录本身写在既有的 `mobile_location_dropped_diagnostics`（原因编码 `passive-*`，
 * **不改库结构**），入库点在点表 `source = passive`；回调总数没有落点，
 * 因此用一个**周期性摘要**事件把它留在台账里，供验收方按三数对账。
 */
object PassiveLocationEventTypes {
    /** 被动回调计数摘要（按采集会话/周期落一条）。 */
    const val PASSIVE_COUNTER = "passive-location-counter"

    val ALL = setOf(PASSIVE_COUNTER)

    fun label(eventType: String): String = when (eventType) {
        PASSIVE_COUNTER -> "被动定位计数"
        else -> "未知事件"
    }
}
