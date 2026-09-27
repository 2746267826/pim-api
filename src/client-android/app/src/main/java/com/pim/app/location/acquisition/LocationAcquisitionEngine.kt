package com.pim.app.location.acquisition

import com.pim.app.location.LocationSnapshot
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.flow.collect
import kotlinx.coroutines.withTimeoutOrNull
import javax.inject.Inject
import javax.inject.Singleton

sealed interface LocationEngineCompletion {
    data object TimedOut : LocationEngineCompletion
    data class Failed(val reason: String) : LocationEngineCompletion
}

data class LocationEngineRequest(
    val sessionId: String,
    val priority: Int,
    val timeoutMillis: Long,
    val startedAtWallClockMillis: Long
)

data class LocationEngineResult(
    val sessionId: String,
    val bestLocation: LocationSnapshot?,
    val completion: LocationEngineCompletion
)

interface LocationAcquisitionRunner {
    suspend fun acquire(
        request: LocationEngineRequest,
        onCandidate: suspend (LocationSnapshot) -> Unit,
        onAvailabilityChanged: suspend (Boolean) -> Unit = {}
    ): LocationEngineResult

    /**
     * 常驻流：按 [LocationUpdateRequest] 的 interval 持续回调候选 fix，
     * 直到协程被取消（durationMillis <= 0 时 LocationRequest 无时限）。
     */
    suspend fun stream(
        request: LocationUpdateRequest,
        onCandidate: suspend (LocationSnapshot) -> Unit
    )
}

@Singleton
class LocationAcquisitionEngine @Inject constructor(
    private val source: LocationUpdateSource
) : LocationAcquisitionRunner {

    override suspend fun stream(
        request: LocationUpdateRequest,
        onCandidate: suspend (LocationSnapshot) -> Unit
    ) {
        source.updates(request).collect { event ->
            when (event) {
                is LocationUpdateEvent.Candidate -> onCandidate(event.location)
                is LocationUpdateEvent.Availability -> {
                    // 常驻流忽略可用性事件
                }
            }
        }
    }

    override suspend fun acquire(
        request: LocationEngineRequest,
        onCandidate: suspend (LocationSnapshot) -> Unit,
        onAvailabilityChanged: suspend (Boolean) -> Unit
    ): LocationEngineResult {
        var bestLocation: LocationSnapshot? = null

        return try {
            withTimeoutOrNull(request.timeoutMillis) {
                source.updates(
                    LocationUpdateRequest(
                        priority = request.priority,
                        durationMillis = request.timeoutMillis
                    )
                ).collect { event ->
                    when (event) {
                        is LocationUpdateEvent.Candidate -> {
                            val snapshot = event.location
                            if (snapshot.timeMillis < request.startedAtWallClockMillis) return@collect

                            // WO-ANDROID-GATE-20260926 REQ-15 / AC-4.1 / AC-4.2 / AC-15.3：
                            // **每一条候选都必须交付**，这里不再是「只有更好才交付」。
                            // 基线只在精度改善时调用 onCandidate，等价于一条静默的点级取舍：
                            // 同一个 30 秒窗口里 12 / 18 / 25 米三条达标点会被吞掉两条，
                            // 与「期间达标的都留」（A5）和「窗口内不得有任何客户端裁剪」直接冲突。
                            // 客户端现在只做精度过滤，取舍交给服务端（REQ-15）。
                            //
                            // bestLocation 仍只用于「本轮最好一条」的回报（预热与 low-quality
                            // 兜底依赖它），与「是否交付」解耦。
                            if (isBetterThan(snapshot, bestLocation)) {
                                bestLocation = snapshot
                            }
                            onCandidate(snapshot)
                        }
                        is LocationUpdateEvent.Availability -> {
                            onAvailabilityChanged(event.available)
                        }
                    }
                }
            }

            LocationEngineResult(
                sessionId = request.sessionId,
                bestLocation = bestLocation,
                completion = LocationEngineCompletion.TimedOut
            )
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            LocationEngineResult(
                sessionId = request.sessionId,
                bestLocation = bestLocation,
                completion = LocationEngineCompletion.Failed(
                    reason = e.message ?: e.javaClass.simpleName
                )
            )
        }
    }

    private fun isBetterThan(candidate: LocationSnapshot, current: LocationSnapshot?): Boolean {
        val candidateAccuracy = candidate.horizontalAccuracyMeters
        val currentAccuracy = current?.horizontalAccuracyMeters

        val candidateValid = candidateAccuracy != null && candidateAccuracy.isFinite()
        val currentValid = currentAccuracy != null && currentAccuracy.isFinite()

        return when {
            current == null -> true
            candidateValid && !currentValid -> true
            !candidateValid && currentValid -> false
            !candidateValid && !currentValid -> {
                candidate.timeMillis > current.timeMillis
            }
            else -> {
                val cmp = candidateAccuracy!!.compareTo(currentAccuracy!!)
                when {
                    cmp < 0 -> true
                    cmp > 0 -> false
                    else -> candidate.timeMillis > current.timeMillis
                }
            }
        }
    }
}
