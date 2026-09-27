package com.pim.app.location.sprint

import com.pim.app.location.LocationSnapshot
import com.pim.app.location.acquisition.LocationUpdateRequest

/**
 * 冲刺专用的定位更新源（WO-ANDROID-GATE-20260926 AC-5.6 / AC-14.7）。
 *
 * **为什么必须与主流分开**：AC-5.6 明令冲刺不得改变主流的注册间隔与周期锚点，
 * 且禁止「临时改注册间隔再改回」的实现。若冲刺复用主流那个
 * `LocationAcquisitionRunner` 单例，两者的注册状态会经同一个对象耦合
 * （取消冲刺流可能影响主流、测试与真机都难以证明互不干扰）。
 *
 * 因此冲刺走**自己的**绑定：同一个底层 provider（GMS / 平台），
 * 但注册与取消完全独立 —— 「独立的高频窗口」在类型上就成立，而不是靠约定。
 */
interface SprintUpdateSource {
    /**
     * 注册一段有界的高频取点流，按 [onSnapshot] 逐条回调，直到协程被取消。
     *
     * 返回的流**不得**被主流的注册状态影响，也不得反过来影响主流。
     */
    suspend fun streamSprintWindow(
        request: LocationUpdateRequest,
        onSnapshot: suspend (LocationSnapshot) -> Unit
    )
}
