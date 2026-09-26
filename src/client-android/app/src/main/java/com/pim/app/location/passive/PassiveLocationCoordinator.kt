package com.pim.app.location.passive

import com.pim.app.location.LocationPointPayload
import com.pim.app.location.acquisition.LocationAcquisitionOperations
import com.pim.app.location.acquisition.TriggerType
import com.pim.app.location.quality.RawLocationFix
import com.pim.app.mobile.logs.StructuredLogRepository
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException

/**
 * 被动定位的运行时编排（WO-ANDROID-GATE-20260926 REQ-14）。
 *
 * 把「注册监听 → 判定 → 入库/留痕 → 计数落台账」收敛到一个类里，
 * 让 [com.pim.app.location.service.ForegroundLocationService] 只负责生命周期
 * （start/stop），保持既有的「服务不直接碰质量门与丢弃诊断」边界
 * （既有 `serviceDoesNotOwnFusedLocationProviderCallback` 契约测试守着这一点）。
 *
 * 三条硬约束（改动前请读工单）：
 * - AC-14.3：不设限流；
 * - AC-14.7：不触碰主动流的注册与锚点（本类只**读**主动流最近 fix 做重复判定）；
 * - AC-14.8：不产出/不修改冲刺台账口径。
 */
@Singleton
class PassiveLocationCoordinator internal constructor(
    private val source: PassiveLocationSource,
    private val operations: LocationAcquisitionOperations,
    private val ledger: PassiveLocationLedger,
    private val logs: StructuredLogRepository,
    private val nowUtcMillis: () -> Long
) {
    @Inject
    constructor(
        source: PassiveLocationSource,
        operations: LocationAcquisitionOperations,
        ledger: PassiveLocationLedger,
        logs: StructuredLogRepository
    ) : this(source, operations, ledger, logs, System::currentTimeMillis)

    private var processor: PassiveLocationProcessor? = null
    private var windowStartUtcMillis: Long = nowUtcMillis()

    /**
     * 窗口序号（台账幂等键的一部分）。
     *
     * **不能只用墙钟秒做窗口键**：同一秒内刷新两次会命中同一个键，
     * `insertIgnore` 返回 -1，第二个窗口的计数被**静默丢掉** ——
     * 分母缺失会让 AC-14.2 的三数对账失效。序号保证每个窗口一个键。
     */
    private var windowSequence: Long = 0L

    /** 最近一次注册结果（AC-14.1：注册成功日志 + 可核对状态）。 */
    val registration: PassiveRegistrationResult
        get() = source.registration

    fun isRegistered(): Boolean = source.isRegistered()

    /**
     * 启动被动监听（AC-14.1：随采集服务启动）。
     *
     * @param activeFixProvider 主动流最近 fix 的**只读快照**（AC-14.5 重复判定用）。
     */
    fun start(activeFixProvider: () -> List<RawLocationFix>): PassiveRegistrationResult {
        if (source.isRegistered()) return source.registration
        windowStartUtcMillis = nowUtcMillis()
        windowSequence += 1L
        val newProcessor = source.processorUsing(activeFixProvider)
        newProcessor.onAccepted = { result ->
            val raw = LocationPointPayload.encode(
                accepted = result.accepted,
                source = result.source,
                submittedAtMillis = nowUtcMillis()
            )
            // REQ-14 规则 6：入库点 source = passive、provider 保持原始提供方。
            operations.enqueueAccepted(result.accepted, raw, result.source)
            operations.scheduleSync()
        }
        newProcessor.onDrop = { fix, reason ->
            // REQ-14 规则 4：原因编码落既有丢弃诊断表（不改库结构）。
            operations.recordDropped(fix, reason)
        }
        processor = newProcessor
        val registered = source.register(newProcessor)
        // AC-14.1：注册成功即留台账痕迹（含 provider 与时刻），供 dumpsys/logcat 交叉核对。
        return registered
    }

    /**
     * 把当前窗口的三数计数落台账并开启新窗口（AC-14.2）。
     *
     * **必须周期性调用，不能只在停止时调用**：服务可能被系统杀掉或被
     * force-stop，`onDestroy`/`stopCollection` 都不保证执行。真机实测确认过这一点
     * （force-stop 后计数器一行都没有），那会让「回调总数」这个**分母**消失，
     * 验收方看到「入库 N 条」却无从判断是否丢点。
     *
     * @return 本次落库的计数（无回调时为 null）。
     */
    suspend fun flushWindow(): PassiveLocationCounters? {
        val active = processor ?: return null
        // **先取快照，不立即清零**：先清后写时，写入被取消（停止采集）或抛异常
        // 会让整个窗口的分母消失（独立 review round 2 指出）。先写后清则最坏情况
        // 是重复写同一窗口 —— 幂等键会拦下，不会重复计数。
        val counters = active.peekCounters()
        if (counters.callbackCount == 0) return null

        val flushedWindowStart = windowStartUtcMillis
        val flushedSequence = windowSequence
        val written = ledger.recordCounters(
            occurredAtUtcMillis = nowUtcMillis(),
            windowStartUtcMillis = flushedWindowStart,
            windowSequence = flushedSequence,
            callbackCount = counters.callbackCount,
            acceptedCount = counters.acceptedCount,
            droppedCount = counters.droppedCount,
            duplicateCount = counters.duplicateCount
        )
        if (!written) {
            // 写失败（含被取消/键冲突）→ **保留计数**，等下一次刷新重试；
            // 分母缺失是 AC-14.2 的致命问题，宁可重复写也不能丢。
            logs.error(
                "passive-location",
                "被动计数台账写入失败，保留计数待下次重试：" +
                    "windowStart=$flushedWindowStart seq=$flushedSequence"
            )
            return counters
        }
        // 写入成功后扣除**已落库的那一份**（不是清零）：写台账期间到达的回调
        // 不属于刚写的这一行，清零会把它们一起抹掉（M-1）。
        active.subtractCounters(counters)
        windowStartUtcMillis = nowUtcMillis()
        windowSequence += 1L
        return counters
    }

    /**
     * 停止被动监听（AC-14.1：服务停止后窗口内不再产生被动点）。
     *
     * 停止前把本窗口的三数计数落台账（AC-14.2）：否则「回调总数」这个分母
     * 会随着服务停止而消失，验收方无法对账。
     */
    suspend fun stop() {
        // 先落库再摘掉处理器：顺序反了会让 flushWindow 看不到窗口计数，
        // 分母就丢了（真机实测踩过）。
        flushWindow()
        processor = null
        source.unregister()
    }

    /** 测试接缝：当前活动的判定器（生产代码不得依赖）。 */
    internal fun activeProcessorForTest(): PassiveLocationProcessor? = processor

    /** 当前窗口的三数计数快照（诊断/状态页用）。 */
    fun counters(): PassiveLocationCounters? = processor?.countersSnapshot()
}
