package com.pim.app.location

import com.pim.app.location.policy.LocationPolicyMode
import com.pim.app.location.policy.PolicyDecision

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-1：策略切换记录的生产写入依赖。
 *
 * **为什么要有这个接口**
 *
 * 缺陷根因（见工单 §二）：`ForegroundLocationService.applyDecision()` 把写入委托给一个
 * **可空字段** `policyTransitionWriter`，而生产装配从未给它赋值——字段为空时静默跳过，
 * 写入异常又被空 `catch` 吞掉，于是 CI 全绿而功能静默失效一个半月（2026-07-26 起零新增）。
 *
 * 修法：写入依赖改为**非空**的生产依赖，由 [com.pim.app.di.PolicyTransitionModule] 绑定到
 * 真实仓库 [LocationQueueRepository]；没有绑定就是 Hilt 的编译期装配错误，
 * 运行期访问未初始化的字段会抛「装配错误」而不是静默跳过（AC-1.5）。
 */
fun interface PolicyTransitionRecorder {
    /**
     * 落库一条策略切换记录。
     *
     * @param fromMode 上一档位；进程 / 服务重启后的首条决策为 `null`（记「暂无 → 新模式」）。
     * @param decision 本次生效的决策（模式 / 间隔 / 原因以它为准）。
     * @return 新插入行的 id。
     */
    suspend fun record(fromMode: LocationPolicyMode?, decision: PolicyDecision): Long
}
