package com.pim.app.location.sprint

/**
 * 冲刺台账的写入端口（REQ-8）。
 *
 * 抽成接口是为了让窗口判定与开关语义能在 JVM 单测里用可注入的假件复现，
 * 真机路径由 [LocationSprintLedger] 落到既有取证事件通道。
 */
interface SprintLedgerPort {
    suspend fun recordExecuted(result: SprintWindowResult): Boolean
    suspend fun recordSkipped(occurredAtUtcMillis: Long, reason: String): Boolean
}
