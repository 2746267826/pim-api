package com.pim.app.location.sprint

import com.pim.app.location.LocationSnapshot
import com.pim.app.location.acquisition.LocationUpdateEvent
import com.pim.app.location.acquisition.LocationUpdateRequest
import com.pim.app.location.acquisition.LocationUpdateSource
import javax.inject.Inject
import javax.inject.Singleton

/**
 * 冲刺更新源的实现：复用同一个底层 provider（GMS fused），
 * 但每次冲刺都发起**独立的** `updates()` 注册（AC-5.6）。
 *
 * 与 [com.pim.app.location.acquisition.LocationAcquisitionEngine] 的区别：
 * 引擎持有的是主流那条**常驻**注册；这里每次冲刺各自注册、各自取消，
 * 因此取消冲刺不会碰到主流的 registration（反之亦然）。
 */
@Singleton
class FusedSprintUpdateSource @Inject constructor(
    private val source: LocationUpdateSource
) : SprintUpdateSource {

    override suspend fun streamSprintWindow(
        request: LocationUpdateRequest,
        onSnapshot: suspend (LocationSnapshot) -> Unit
    ) {
        source.updates(request).collect { event ->
            when (event) {
                is LocationUpdateEvent.Candidate -> onSnapshot(event.location)
                // 冲刺忽略可用性事件：它是有界窗口，可用性变化由主流负责上报。
                is LocationUpdateEvent.Availability -> Unit
            }
        }
    }
}
