package com.pim.app.location.acquisition

import android.os.SystemClock
import com.google.android.gms.location.Priority
import com.pim.app.location.LocationPointPayload
import com.pim.app.location.LocationSnapshot
import com.pim.app.location.quality.AltitudeWaitCoordinator
import com.pim.app.location.quality.LocationQualityGate
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.QualityDecision
import com.pim.app.location.quality.RawLocationFix
import com.pim.app.settings.TrackingSettings
import com.pim.app.settings.TrackingSettingsStore
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonPrimitive
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicReference
import javax.inject.Inject
import javax.inject.Singleton

interface LocationAcquisitionOperations {
    suspend fun enqueueAccepted(accepted: QualityAcceptedLocation, rawJson: String, source: String)
    suspend fun recordDropped(fix: RawLocationFix, reason: String)
    fun scheduleSync()
}

/**
 * 统一采集引擎（设计文档 §3.1/§3.3）：
 * - 手动触发 = 立即执行一次同一引擎（一次性采集，30s 截止，达标入库；超时用
 *   最好 fix 并标记 low-quality，绝不静默）。
 * - 自动采集 = 同一引擎的常驻流：注册时先预热等 GPS 收敛（冷启动），随后系统按
 *   interval 回调 fix，逐点过 30m 质量门入库；≥30m 的 fix 走 drop 诊断，不回退。
 * - priority 恒为 HIGH_ACCURACY（§3.2）；省电只靠采样间隔。
 * 手动会话状态走 [state]，自动流状态走 [streamState]，两者互不干扰。
 */
@Singleton
class LocationAcquisitionCoordinator @Inject constructor(
    private val runner: LocationAcquisitionRunner,
    private val prerequisiteChecker: LocationPrerequisiteChecker,
    private val operations: LocationAcquisitionOperations,
    private val json: Json,
    private val trackingSettingsStore: TrackingSettingsStore
) {
    internal var testScope: CoroutineScope? = null
    internal var uuidGenerator: () -> String = { UUID.randomUUID().toString() }
    internal var wallClockMillis: () -> Long = { System.currentTimeMillis() }
    internal var elapsedRealtimeMillis: () -> Long = { SystemClock.elapsedRealtime() }
    // Test seam: invoked in cancelCurrentSession after a cancellable state snapshot
    // is read but before the CAS to Cancelled, so a stale-read interleaving with an
    // in-flight acceptance can be reproduced deterministically.
    internal var beforeCancellingSessionJob: (() -> Unit)? = null
    // Test seam: invoked in cancelCurrentSession immediately after the Cancelled
    // state claim wins and before the session job is cancelled/cleared.
    internal var afterSessionCancelledClaim: (() -> Unit)? = null
    // Invoked after every recorded point (manual or stream), so the service can
    // update the policy anchor, notification texts and the next-fix countdown.
    internal var onRecorded: (suspend (LocationSnapshot) -> Unit)? = null

    private val internalScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val scope: CoroutineScope get() = testScope ?: internalScope

    private val _state = MutableStateFlow(LocationAcquisitionState())
    val state: StateFlow<LocationAcquisitionState> = _state.asStateFlow()

    private val _streamState = MutableStateFlow(AutomaticStreamState())
    val streamState: StateFlow<AutomaticStreamState> = _streamState.asStateFlow()

    private var sessionJob: Job? = null
    private var streamJob: Job? = null
    // ─── 手动一次性采集 ─────────────────────────────────────────

    fun startManualSession(): SessionStartResult {
        // Check prerequisites before replacing: a blocked restart must not
        // destroy the in-flight manual one-shot.
        when (val precheck = prerequisiteChecker.check(TriggerType.MANUAL)) {
            is LocationPrerequisiteResult.Blocked -> {
                if (!_state.value.isBusy) {
                    _state.value = LocationAcquisitionState(
                        phase = AcquisitionPhase.Idle,
                        errorReason = precheck.reason
                    )
                }
                return SessionStartResult.Rejected(precheck.reason)
            }
            is LocationPrerequisiteResult.Ready -> {}
        }

        // Restart semantics: an in-flight manual one-shot is replaced by the new one.
        cancelCurrentSession(_state.value.sessionId)

        val sessionId = uuidGenerator()
        startSession(sessionId, TriggerType.MANUAL, null)
        return SessionStartResult.Started(sessionId)
    }

    fun cancelCurrentSession(expectedSessionId: String? = null): Boolean {
        val cancellablePhases = setOf(
            AcquisitionPhase.Preparing,
            AcquisitionPhase.Acquiring,
            AcquisitionPhase.Evaluating
        )
        while (true) {
            val current = _state.value
            if (expectedSessionId != null && current.sessionId != expectedSessionId) return false
            if (current.phase !in cancellablePhases) return false

            val ownerJob = sessionJob
            beforeCancellingSessionJob?.invoke()

            if (_state.compareAndSet(
                    current,
                    current.copy(phase = AcquisitionPhase.Cancelled, errorReason = null)
                )
            ) {
                afterSessionCancelledClaim?.invoke()
                ownerJob?.cancel()
                clearSessionJobIfOwned(ownerJob)
                return true
            }
        }
    }

    // ─── 自动常驻流 ─────────────────────────────────────────────

    fun startAutomaticStream(context: AcquisitionContext): Boolean {
        if (_streamState.value.active) return updateAutomaticStream(context)
        startStreamJob(context, warmUp = true)
        return true
    }

    fun updateAutomaticStream(context: AcquisitionContext): Boolean {
        if (!_streamState.value.active) {
            startStreamJob(context, warmUp = true)
            return true
        }
        val current = _streamState.value
        // 间隔或上下文标注（policyMode/scheduleLowFrequency/motionSignal）任一
        // 变化都重注册，避免流内 fix 携带过期元数据。
        // 注意：重注册不做预热（warmUp=false）。这是对设计文档 S3-2"重新
        // startAutomaticStream（预热 + 新 interval 常驻）"的刻意偏差——流已热
        // （GPS 保持连接），且运动状态频繁变化时反复预热会中断采集节奏；
        // 冷启动预热只发生在 startAutomaticStream（active=false → warmUp=true）。
        if (current.requestIntervalMillis != context.requestIntervalMillis ||
            current.policyMode != context.policyMode ||
            current.scheduleLowFrequency != context.scheduleLowFrequency ||
            current.motionSignal != context.motionSignal
        ) {
            startStreamJob(context, warmUp = false)
        }
        return true
    }

    fun stopAutomaticStream() {
        streamJob?.cancel()
        streamJob = null
        _streamState.value = AutomaticStreamState()
    }

    fun isAutomaticStreamActive(): Boolean = _streamState.value.active

    private fun startStreamJob(context: AcquisitionContext, warmUp: Boolean) {
        streamJob?.cancel()
        lateinit var job: Job
        val newJob = scope.launch(start = CoroutineStart.LAZY) {
            _streamState.update {
                it.copy(
                    active = true,
                    requestIntervalMillis = context.requestIntervalMillis,
                    policyMode = context.policyMode,
                    scheduleLowFrequency = context.scheduleLowFrequency,
                    motionSignal = context.motionSignal,
                    lastError = null
                )
            }
            try {
                if (warmUp) {
                    warmUpOnce(context)
                }
                val request = LocationUpdateRequest(
                    priority = Priority.PRIORITY_HIGH_ACCURACY,
                    intervalMillis = context.requestIntervalMillis,
                    durationMillis = 0L
                )
                runner.stream(request) { snapshot ->
                    handleStreamFix(snapshot, context)
                }
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                _streamState.update { it.copy(lastError = e.message) }
            } finally {
                // 流 job 结束（异常/被替换外的正常终结）必须复位 active，
                // 否则 service 下轮看到 isAutomaticStreamActive()==true 永不
                // 重注册，自动采集静默死亡。被替换的旧 job 不得覆盖新 job。
                if (streamJob === job) {
                    _streamState.update { it.copy(active = false) }
                }
            }
        }
        job = newJob
        streamJob = newJob
        newJob.start()
    }

    /** 冷启动预热：等 GPS 收敛（≤30s），接受首个 <30m fix 并入库；不回退。 */
    private suspend fun warmUpOnce(context: AcquisitionContext) {
        val settings = trackingSettingsStore.read()
        val sessionId = "warmup-${uuidGenerator()}"
        when (val outcome = acquireOneShot(
            sessionId = sessionId,
            triggerType = TriggerType.AUTOMATIC,
            context = context,
            ownerJob = null,
            settings = settings,
            sink = StateSink.STREAM,
            allowLowQualityFallback = false,
            collectAll = false
        )) {
            is OneShotOutcome.Accepted -> {
                // 流预热固定 collectAll = false，因此这里只会有一条（保持基线语义）。
                // 入库已在采集过程中逐条完成（见 enqueueAcceptedNow），此处只更新状态。
                val warmed = outcome.accepted.single()
                try {
                    val snapshot = warmed.fix.toSnapshot()
                    _streamState.update {
                        it.copy(
                            latestFix = snapshot,
                            latestQualityFlags = warmed.qualityFlags,
                            lastError = null
                        )
                    }
                    onRecorded?.invoke(snapshot)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: Exception) {
                    _streamState.update { it.copy(lastError = e.message) }
                }
            }
            is OneShotOutcome.Failed -> {
                _streamState.update { it.copy(lastError = outcome.reason) }
            }
            is OneShotOutcome.NoFix -> {
                // 预热未收敛不阻塞常驻流注册
            }
        }
    }

    private suspend fun handleStreamFix(snapshot: LocationSnapshot, context: AcquisitionContext) {
        // WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 / AC-15.3：
        // 常规流 2 秒去重已**整体取消**。基线在这里对「与上一条入库 fix 相差 <2s」的
        // 回调直接 `return` —— 既不入库也不留诊断，正是 AC-15.3 禁止的静默丢弃路径。
        // 客户端现在只做精度过滤，达标点逐条入库、逐条进入上传队列；
        // 滤波与舍弃交给服务端（A9 / A11）。
        val fix = snapshot.toRawFix(TriggerType.AUTOMATIC, context)
        val gate = LocationQualityGate.fromTrackingSettings(trackingSettingsStore.read())
        when (val decision = gate.evaluate(fix, wallClockMillis())) {
            is QualityDecision.AcceptNow -> {
                enqueueStreamFix(decision.accepted, snapshot, decision.accepted.qualityFlags)
            }
            is QualityDecision.WaitForAltitude -> {
                // 流模式不做 15s 等待：缺海拔直接带标记接受（GPS 热态下海拔基本都有）
                enqueueStreamFix(
                    QualityAcceptedLocation(
                        fix = fix,
                        altitudeMeters = null,
                        acceptedAtMillis = wallClockMillis(),
                        qualityFlags = setOf(STREAM_ALTITUDE_MISSING_FLAG)
                    ),
                    snapshot,
                    setOf(STREAM_ALTITUDE_MISSING_FLAG)
                )
            }
            is QualityDecision.Drop -> {
                recordDrop(decision.fix, decision.reason)
            }
        }
    }

    private suspend fun enqueueStreamFix(
        accepted: QualityAcceptedLocation,
        snapshot: LocationSnapshot,
        flags: Set<String>
    ) {
        try {
            val raw = rawJson(accepted, TriggerType.AUTOMATIC.storageSource)
            operations.enqueueAccepted(accepted, raw, TriggerType.AUTOMATIC.storageSource)
            operations.scheduleSync()
            _streamState.update {
                it.copy(latestFix = snapshot, latestQualityFlags = flags, lastError = null)
            }
            onRecorded?.invoke(snapshot)
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            _streamState.update { it.copy(lastError = e.message) }
        }
    }

    // ─── 会话内部（一次性采集，手动/自动预热共用） ───────────────

    private enum class StateSink { SESSION, STREAM }

    private sealed interface OneShotOutcome {
        /**
         * 收下的点。
         *
         * WO-ANDROID-GATE-20260926 D6 / REQ-4：手动会话跑满 30 秒并**收集期间全部达标点**
         * （不再「首个达标点即结束」），因此这里是一组而不是单条。
         * 流预热仍只取最好一条（`collectAll = false`），保持既有行为不被改动。
         */
        data class Accepted(val accepted: List<QualityAcceptedLocation>) : OneShotOutcome
        data object NoFix : OneShotOutcome
        data class Failed(val reason: String) : OneShotOutcome
    }

    private fun startSession(
        sessionId: String,
        triggerType: TriggerType,
        context: AcquisitionContext?
    ) {
        val nowElapsed = elapsedRealtimeMillis()
        // Snapshot tracking settings once per session; later changes only affect
        // the next session, never the active one.
        val settings = trackingSettingsStore.read()
        _state.value = LocationAcquisitionState(
            sessionId = sessionId,
            triggerType = triggerType,
            phase = AcquisitionPhase.Preparing,
            startedAtElapsedRealtimeMs = nowElapsed,
            deadlineAtElapsedRealtimeMs = nowElapsed + 30_000L
        )
        lateinit var job: Job
        val newJob = scope.launch(start = CoroutineStart.LAZY) {
            runSession(sessionId, triggerType, context, job, settings)
        }
        job = newJob
        sessionJob = newJob
        newJob.start()
    }

    private suspend fun runSession(
        sessionId: String,
        triggerType: TriggerType,
        context: AcquisitionContext?,
        ownerJob: Job,
        settings: TrackingSettings
    ) = coroutineScope {
        val tickerJob = launch {
            while (isActive) {
                delay(1_000L)
                updateStateIfCurrent(sessionId, ownerJob) { state ->
                    val start = state.startedAtElapsedRealtimeMs
                    if (start != null) {
                        state.copy(elapsedMs = elapsedRealtimeMillis() - start)
                    } else {
                        state
                    }
                }
            }
        }
        try {
            when (val outcome = acquireOneShot(
                sessionId = sessionId,
                triggerType = triggerType,
                context = context,
                ownerJob = ownerJob,
                settings = settings,
                sink = StateSink.SESSION,
                allowLowQualityFallback = true,
                collectAll = true
            )) {
                is OneShotOutcome.Accepted ->
                    handleAccepted(outcome.accepted, sessionId, ownerJob)
                is OneShotOutcome.NoFix ->
                    updateStateIfCurrent(sessionId, ownerJob) {
                        it.copy(
                            phase = AcquisitionPhase.TimedOut,
                            errorReason = "获取位置超时，未获得任何定位结果"
                        )
                    }
                is OneShotOutcome.Failed ->
                    updateStateIfCurrent(sessionId, ownerJob) {
                        it.copy(phase = AcquisitionPhase.Failed, errorReason = outcome.reason)
                    }
            }
        } catch (e: CancellationException) {
            updateStateIfCurrent(sessionId, ownerJob) {
                it.copy(phase = AcquisitionPhase.Cancelled)
            }
            throw e
        } catch (e: Exception) {
            updateStateIfCurrent(sessionId, ownerJob) {
                it.copy(phase = AcquisitionPhase.Failed, errorReason = e.message)
            }
        } finally {
            tickerJob.cancel()
            clearSessionJobIfOwned(ownerJob)
        }
    }

    private suspend fun acquireOneShot(
        sessionId: String,
        triggerType: TriggerType,
        context: AcquisitionContext?,
        ownerJob: Job?,
        settings: TrackingSettings,
        sink: StateSink,
        allowLowQualityFallback: Boolean,
        /**
         * true = 收下本次会话期间**全部**达标点（手动会话，D6 / REQ-4）；
         * false = 只保留最好一条（流预热，保持基线语义不被改动）。
         */
        collectAll: Boolean
    ): OneShotOutcome = coroutineScope {
        updateSinkPhase(sink, sessionId, ownerJob, AcquisitionPhase.Acquiring)

        val sessionStartedWallClockMillis = wallClockMillis()
        val sessionStartedElapsedRealtimeMillis = elapsedRealtimeMillis()
        val deadlineCapMillis = sessionStartedWallClockMillis + 30_000L
        val deadlineCapElapsedRealtimeMillis = sessionStartedElapsedRealtimeMillis + 30_000L
        // 每个 fix 一个独立的等待器：海拔等待的**判定条件**（同一道门、同一超时、
        // 同一个 quality flag）完全不变，只是不再共享「单个 pending」状态 ——
        // 共享状态下后一个缺海拔的点会顶掉前一个，让前一个静默消失（AC-15.3 禁止）。
        fun newAltitudeWaiter() = AltitudeWaitCoordinator(
            gate = LocationQualityGate.fromTrackingSettings(settings),
            nowMillis = wallClockMillis,
            nowElapsedRealtimeMillis = elapsedRealtimeMillis,
            delayMillis = { delay(it) }
        )

        val request = LocationEngineRequest(
            sessionId = sessionId,
            priority = Priority.PRIORITY_HIGH_ACCURACY,
            timeoutMillis = 30_000L,
            startedAtWallClockMillis = sessionStartedWallClockMillis
        )

        // REQ-4「最好一条」：按**精度**取最优（不是最后一条）。
        // 存 RawLocationFix（low-quality 兜底与 rawJson 需要它）与它来源的
        // snapshot（会话展示 `bestLocation` 要保留引擎交付的那个对象）。
        val bestFix = AtomicReference<RawLocationFix?>(null)
        // 「最好一条」对应的引擎快照（会话展示 `bestLocation` 保留该对象身份）。
        val bestFixSource = AtomicReference<LocationSnapshot?>(null)
        val acceptedLocations = java.util.Collections.synchronizedList(
            mutableListOf<QualityAcceptedLocation>()
        )
        val engineResult = AtomicReference<LocationEngineResult?>(null)
        val qualityJobs = mutableListOf<Job>()
        val engineJobRef = AtomicReference<Job?>(null)
        // 会话是否已经采集结束（引擎已返回）。REQ-3 取消了「达标即取消引擎」，
        // 因此迟到的回调必须由本标志拦住，不能让它们在终态之后继续改写状态
        // （既有那条守卫走的正是「已达标即 return」，随着早退一起消失了）。
        val engineFinished = AtomicBoolean(false)
        // 入库失败（DB 异常等）必须让会话显式失败，不得静默丢弃（AC-15.3）。
        val enqueueError = AtomicReference<Exception?>(null)
        // 预热是否已经收下首个达标点（范围外行为，保持基线；手动会话不使用本标志）。
        val warmUpClaimed = AtomicBoolean(false)
        // 手动会话的中止判定：停止采集/取消后，迟到的达标点不得再入库。
        fun sessionStillActive(): Boolean = when (sink) {
            StateSink.SESSION -> isCurrentSession(sessionId) &&
                _state.value.phase != AcquisitionPhase.Cancelled
            StateSink.STREAM -> true
        }

        // REQ-15 / AC-15.1 / AC-15.3：达标点**逐条入库**（不是窗口结束才批量落库），
        // 这样进程中途被杀也不会丢已达标点，且不存在「命中即 return 且无记录」的路径。
        suspend fun enqueueAcceptedNow(accepted: QualityAcceptedLocation) {
            if (!sessionStillActive()) return
            try {
                val json = rawJson(accepted, triggerType.storageSource)
                operations.enqueueAccepted(accepted, json, triggerType.storageSource)
                operations.scheduleSync()
            } catch (e: CancellationException) {
                throw e
            } catch (e: Exception) {
                enqueueError.compareAndSet(null, e)
            }
        }

        /**
         * 只在**更好**时更新「本轮最好一条」。
         *
         * 精度更小者优先；精度不可用时按时间新者优先（与
         * [LocationAcquisitionEngine] 的 `isBetterThan` 同口径，
         * 保证会话展示与 low-quality 兜底都取真正最优的那条，而不是最后一条）。
         */
        /** @return true 表示这条确实更优（调用方据此更新会话展示的「最佳位置」）。 */
        fun updateBestIfBetter(fix: RawLocationFix, snapshot: LocationSnapshot): Boolean {
            while (true) {
                val current = bestFix.get()
                if (!isBetterFix(fix, current)) return false
                if (bestFix.compareAndSet(current, fix)) {
                    bestFixSource.set(snapshot)
                    return true
                }
            }
        }

        /**
         * 收下一个达标点。
         *
         * @return true 表示本次确实收下了（预热只认首条，其余返回 false）。
         */
        fun onQualityAccepted(
            accepted: QualityAcceptedLocation,
            sourceSnapshot: LocationSnapshot
        ): Boolean {
            // D6 / AC-6.4：**手动会话不早退** —— 达标点照收，会话继续跑满 30 秒
            // （基线在这里无条件 `cancel()` 引擎，正是本工单取消的「首个达标点即结束」）。
            //
            // 但**流预热**（collectAll = false）保持基线语义：拿到首个达标点即结束，
            // 只入库一条。REQ-10 要求「范围外一律不改」，预热属于范围外，
            // 因此不能顺手一起改掉（独立 review 指出过这一点）。
            if (!collectAll && !warmUpClaimed.compareAndSet(false, true)) return false
            acceptedLocations += accepted
            // REQ-4 的「最好一条」判定：按精度取最优（与引擎 isBetterThan 同口径），
            // 不能取「最后一条」—— 基线 bestSnapshot 每来一条就覆盖，取消早退后
            // 那条赋值会把最好一条冲成最后一条（独立 review round 2 指出）。
            updateBestIfBetter(accepted.fix, sourceSnapshot)
            if (!collectAll) {
                engineJobRef.get()?.cancel()
            }
            return true
        }

        val engineJob = launch {
            try {
                engineResult.set(
                    runner.acquire(
                        request = request,
                        onCandidate = { snapshot ->
                            if (engineFinished.get()) return@acquire
                            // 记录最新取点用于实时进度（AC-6.2）。
                            updateSinkLatest(sink, sessionId, ownerJob, snapshot, AcquisitionPhase.Evaluating)
                            // REQ-4「最好一条」：**每条候选**都要参与比较（不只是达标的那些），
                            // 否则「无达标点时的手动 low-quality 兜底」（AC-4.4 显式例外）
                            // 会因为没有候选入选而拿不到 best —— 兜底会退化成「不入库」。
                            if (updateBestIfBetter(snapshot.toRawFix(triggerType, context), snapshot)) {
                                updateSinkBest(sink, sessionId, ownerJob, snapshot)
                            }

                            val fix = snapshot.toRawFix(triggerType, context)
                            val qualityJob = launch {
                                newAltitudeWaiter().handleFix(
                                    fix = fix,
                                    deadlineCapMillis = deadlineCapMillis,
                                    deadlineCapElapsedRealtimeMillis = deadlineCapElapsedRealtimeMillis,
                                    onAccepted = { accepted ->
                                        // onQualityAccepted 对预热只认首条（warmUpClaimed），
                                        // 因此用它的返回值判断「本次是否真的收下」，
                                        // 不能用 acceptedLocations.size（它恒为 1，会重复入库）。
                                        val acceptedNow = onQualityAccepted(accepted, snapshot)
                                        if (acceptedNow) enqueueAcceptedNow(accepted)
                                    },
                                    onDropped = { droppedFix, reason -> recordDrop(droppedFix, reason) }
                                )
                            }
                            qualityJobs += qualityJob
                        }
                    )
                )
            } catch (_: CancellationException) {
                // external cancel（早退已按 D6 取消，这里只剩外部取消）
            } finally {
                engineFinished.set(true)
            }
        }
        engineJobRef.set(engineJob)

        engineJob.join()
        qualityJobs.toList().forEach { job ->
            if (job.isActive) {
                job.join()
            }
        }

        if (sink == StateSink.SESSION) {
            if (!isCurrentSession(sessionId)) return@coroutineScope OneShotOutcome.NoFix
            // A cancelled terminal state retains its sessionId; leftover session work
            // must not turn it into TimedOut/Failed or start an enqueue.
            if (_state.value.phase == AcquisitionPhase.Cancelled) return@coroutineScope OneShotOutcome.NoFix
        }

        enqueueError.get()?.let { failure ->
            return@coroutineScope OneShotOutcome.Failed(
                failure.message ?: failure.javaClass.simpleName
            )
        }

        val accepted = acceptedLocations.toList()
        if (accepted.isNotEmpty()) {
            // 入库已在 onAccepted 时逐条完成（见 enqueueAcceptedNow），这里只回报结果。
            // 流预热保持基线语义：只取最好一条（bestSnapshot 已是本轮最优）。
            return@coroutineScope OneShotOutcome.Accepted(
                if (collectAll) accepted else listOf(accepted.first())
            )
        }

        val best = bestFix.get()
        if (best != null && allowLowQualityFallback) {
            // AC-4.4 的显式例外：手动无达标点时保持既有 low-quality 兜底入库 1 条。
            // 用的是**精度最优**的那条（不是最后一条）。
            val fallback = QualityAcceptedLocation(
                fix = best,
                altitudeMeters = best.altitudeMeters,
                acceptedAtMillis = wallClockMillis(),
                qualityFlags = setOf(LocationQualityGate.LOW_QUALITY_ACCURACY_FLAG)
            )
            enqueueAcceptedNow(fallback)
            enqueueError.get()?.let { failure ->
                return@coroutineScope OneShotOutcome.Failed(
                    failure.message ?: failure.javaClass.simpleName
                )
            }
            return@coroutineScope OneShotOutcome.Accepted(listOf(fallback))
        }

        val completion = engineResult.get()?.completion
        return@coroutineScope if (completion is LocationEngineCompletion.Failed) {
            OneShotOutcome.Failed(completion.reason)
        } else {
            OneShotOutcome.NoFix
        }
    }

    private fun updateSinkPhase(
        sink: StateSink,
        sessionId: String?,
        ownerJob: Job?,
        phase: AcquisitionPhase
    ) {
        when (sink) {
            StateSink.SESSION -> updateStateIfCurrent(sessionId, ownerJob) { it.copy(phase = phase) }
            StateSink.STREAM -> {} // 流预热保持 StreamState.active，无需 phase
        }
    }

    /**
     * 展示层：记录**最新**取点与会话阶段。
     *
     * 与 `bestLocation`（按精度取最优）分开：实时进度要的是「刚取到哪个点」，
     * 而「最佳位置」要的是「本轮最好的一条」—— 两者混用会让 low-quality
     * 兜底与界面都退化成「最后一条」（独立 review round 2 指出）。
     */
    private fun updateSinkLatest(
        sink: StateSink,
        sessionId: String?,
        ownerJob: Job?,
        snapshot: LocationSnapshot,
        phase: AcquisitionPhase
    ) {
        when (sink) {
            StateSink.SESSION -> updateStateIfCurrent(sessionId, ownerJob) {
                it.copy(phase = phase)
            }
            StateSink.STREAM -> _streamState.update { it.copy(latestFix = snapshot) }
        }
    }

    /**
     * 把「本轮最好一条」同步到会话展示（`bestLocation`）与常驻流的最新点。
     *
     * 只在**确实更优**时调用（由 [updateBestIfBetter] 的返回值决定），
     * 因此 `bestLocation` 始终是精度最优的一条而不是最后一条。
     */
    private fun updateSinkBest(
        sink: StateSink,
        sessionId: String?,
        ownerJob: Job?,
        sourceSnapshot: LocationSnapshot
    ) {
        // 保留引擎交付的 snapshot 身份：`bestLocation` 的语义是「本轮最好的一条」，
        // 不是「最后一条」，也不能被重建成另一个对象（会话展示与既有用例都按它比对）。
        when (sink) {
            StateSink.SESSION -> updateStateIfCurrent(sessionId, ownerJob) {
                it.copy(bestLocation = sourceSnapshot)
            }
            StateSink.STREAM -> _streamState.update { it.copy(latestFix = sourceSnapshot) }
        }
    }

    /**
     * 会话正常收尾：入库已在采集过程中逐条完成（[enqueueAcceptedNow]），
     * 这里只落终态并把最好的一条回报给通知/状态页。
     */
    private suspend fun handleAccepted(
        accepted: List<QualityAcceptedLocation>,
        sessionId: String,
        ownerJob: Job
    ) {
        if (!isCurrentSession(sessionId)) return
        updateStateIfCurrent(sessionId, ownerJob) {
            it.copy(
                phase = AcquisitionPhase.Completed,
                lastQualityFlags = accepted.lastOrNull()?.qualityFlags ?: emptySet(),
                errorReason = null
            )
        }
        accepted.lastOrNull()?.let { location -> onRecorded?.invoke(location.fix.toSnapshot()) }
    }

    private suspend fun recordDrop(fix: RawLocationFix, reason: String) {
        try {
            operations.recordDropped(fix, reason)
        } catch (e: CancellationException) {
            throw e
        } catch (_: Exception) {
        }
    }

    private fun clearSessionJobIfOwned(ownerJob: Job?) {
        if (ownerJob != null && sessionJob === ownerJob) {
            sessionJob = null
        }
    }

    private fun updateStateIfCurrent(
        sessionId: String?,
        ownerJob: Job? = null,
        transform: (LocationAcquisitionState) -> LocationAcquisitionState
    ) {
        if (sessionId == null) return
        if (ownerJob != null && sessionJob != null && sessionJob !== ownerJob) return
        if (_state.value.sessionId != sessionId) return
        _state.update { current ->
            if (current.sessionId != sessionId) return@update current
            val next = transform(current)
            // The Cancelled terminal state is final for this session: leftover
            // work must not resurrect or relabel it.
            if (current.phase == AcquisitionPhase.Cancelled &&
                next.phase != AcquisitionPhase.Cancelled
            ) {
                current
            } else {
                next
            }
        }
    }

    private fun isCurrentSession(sessionId: String?): Boolean =
        sessionId != null && _state.value.sessionId == sessionId

    /**
     * 「更好的一条」判定（与 [LocationAcquisitionEngine.isBetterThan] 同口径）。
     *
     * 精度有限且更小者更优；精度不可用时按时间新者优先。
     */
    private fun isBetterFix(candidate: RawLocationFix, current: RawLocationFix?): Boolean {
        if (current == null) return true
        val candidateAccuracy = candidate.horizontalAccuracyMeters
        val currentAccuracy = current.horizontalAccuracyMeters
        val candidateValid = candidateAccuracy != null && candidateAccuracy.isFinite()
        val currentValid = currentAccuracy != null && currentAccuracy.isFinite()
        return when {
            candidateValid && !currentValid -> true
            !candidateValid && currentValid -> false
            !candidateValid && !currentValid -> candidate.recordedAtMillis > current.recordedAtMillis
            else -> {
                val cmp = candidateAccuracy!!.compareTo(currentAccuracy!!)
                cmp < 0 || (cmp == 0 && candidate.recordedAtMillis > current.recordedAtMillis)
            }
        }
    }

    /** 负载构造收敛到 [LocationPointPayload]（主动流/冲刺/被动三源共用一份）。 */
    private fun rawJson(accepted: QualityAcceptedLocation, source: String): String =
        LocationPointPayload.encode(
            accepted = accepted,
            source = source,
            submittedAtMillis = wallClockMillis()
        )

    private fun LocationSnapshot.toRawFix(
        triggerType: TriggerType,
        context: AcquisitionContext?
    ): RawLocationFix = RawLocationFix(
        latitude = latitude,
        longitude = longitude,
        horizontalAccuracyMeters = horizontalAccuracyMeters,
        altitudeMeters = altitudeMeters,
        provider = provider,
        recordedAtMillis = timeMillis,
        policyMode = context?.policyMode ?: "PowerSavingNormal",
        scheduleLowFrequency = context?.scheduleLowFrequency ?: false,
        motionSignal = context?.motionSignal ?: "Unknown",
        speedMetersPerSecond = speedMetersPerSecond,
        bearingDegrees = bearingDegrees
    )

    private fun RawLocationFix.toSnapshot(): LocationSnapshot = LocationSnapshot(
        latitude = latitude,
        longitude = longitude,
        horizontalAccuracyMeters = horizontalAccuracyMeters,
        provider = provider,
        source = "acquisition",
        altitudeMeters = altitudeMeters,
        speedMetersPerSecond = speedMetersPerSecond,
        bearingDegrees = bearingDegrees,
        timeMillis = recordedAtMillis
    )

    private companion object {
        const val STREAM_ALTITUDE_MISSING_FLAG = "altitude-missing"
    }
}
