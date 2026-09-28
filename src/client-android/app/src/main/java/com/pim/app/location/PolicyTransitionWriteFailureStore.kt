package com.pim.app.location

import android.content.SharedPreferences
import com.pim.app.di.PolicyTransitionPreferences
import com.pim.app.mobile.logs.StructuredLogRepository
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-3（D-4）：策略切换写入失败的可见状态。
 *
 * @property consecutiveFailures 连续写入失败次数（成功写入一次即归零，不是累计值）。
 * @property lastFailureAtUtcMillis 最近一次失败的时间；成功写入后与计数**同时**清空。
 */
data class PolicyTransitionWriteFailure(
    val consecutiveFailures: Int = 0,
    val lastFailureAtUtcMillis: Long? = null
) {
    val hasFailure: Boolean get() = consecutiveFailures > 0

    companion object {
        val None = PolicyTransitionWriteFailure()
    }
}

/**
 * 写入失败状态的**只读**视图（界面层依赖它，单测可用最轻的替身）。
 */
interface PolicyTransitionWriteFailureSource {
    val state: StateFlow<PolicyTransitionWriteFailure>
}

/**
 * 持久化「连续写入失败次数 + 最近一次失败时间」（REQ-3 / AC-3.3）。
 *
 * - 用 `SharedPreferences.commit()` 同步落盘：进程重启后仍在（AC-3.3）；
 * - 成功写入一次**同时**归零两项（AC-3.2 / AC-3.4）；
 * - 失败时写一条 error 级结构化日志，**含异常类名与摘要**，且不受「详细日志」开关限制
 *   （`StructuredLogRepository` 只对 `debug` 级做门控，`error` 始终落盘，AC-3.5）。
 *
 * 计数逻辑只做 `synchronized` 内的轻量读写，且 `SharedPreferences.commit()` 这种磁盘写
 * 一律切到 [ioDispatcher]，**不占用主线程做重活**（REQ-3 反面行为）。
 */
@Singleton
class PolicyTransitionWriteFailureStore internal constructor(
    private val preferences: SharedPreferences,
    private val logs: StructuredLogRepository,
    private val nowMillis: () -> Long,
    private val ioDispatcher: CoroutineDispatcher = Dispatchers.IO
) : PolicyTransitionWriteFailureSource {
    @Inject
    constructor(
        @PolicyTransitionPreferences preferences: SharedPreferences,
        logs: StructuredLogRepository
    ) : this(preferences, logs, System::currentTimeMillis)

    private val lock = Any()
    private val mutableState = MutableStateFlow(load())

    override val state: StateFlow<PolicyTransitionWriteFailure> = mutableState.asStateFlow()

    /** 记录一次失败：计数严格 +1、最近失败时间刷新，并落一条 error 日志。 */
    suspend fun recordFailure(throwable: Throwable) {
        val now = nowMillis()
        val updated = withContext(ioDispatcher) {
            synchronized(lock) {
                val current = mutableState.value
                val next = PolicyTransitionWriteFailure(
                    consecutiveFailures = current.consecutiveFailures + 1,
                    lastFailureAtUtcMillis = now
                )
                persist(next)
                next
            }
        }
        logs.error(
            "location",
            "策略切换记录写入失败：${throwable.javaClass.simpleName} ${throwable.message ?: ""}".trim(),
            throwable,
            mapOf(
                "consecutiveFailures" to updated.consecutiveFailures,
                "lastFailureAtUtc" to updated.lastFailureAtUtcMillis
            )
        )
    }

    /** 成功写入一次：连续失败计数与最近失败时间**同时**归零（AC-3.2 / AC-3.4）。 */
    suspend fun recordSuccess() {
        withContext(ioDispatcher) {
            synchronized(lock) {
                if (mutableState.value == PolicyTransitionWriteFailure.None) return@synchronized
                persist(PolicyTransitionWriteFailure.None)
            }
        }
    }

    private fun persist(value: PolicyTransitionWriteFailure) {
        preferences.edit()
            .putInt(KEY_CONSECUTIVE_FAILURES, value.consecutiveFailures)
            .apply {
                val at = value.lastFailureAtUtcMillis
                if (at == null) remove(KEY_LAST_FAILURE_AT_UTC) else putLong(KEY_LAST_FAILURE_AT_UTC, at)
            }
            .commit()
        mutableState.value = value
    }

    private fun load(): PolicyTransitionWriteFailure {
        val count = preferences.getInt(KEY_CONSECUTIVE_FAILURES, 0).coerceAtLeast(0)
        if (count == 0) return PolicyTransitionWriteFailure.None
        val at = preferences.getLong(KEY_LAST_FAILURE_AT_UTC, 0L).takeIf { it > 0L }
        return PolicyTransitionWriteFailure(count, at)
    }

    companion object {
        const val KEY_CONSECUTIVE_FAILURES = "policy_transition_write_consecutive_failures"
        const val KEY_LAST_FAILURE_AT_UTC = "policy_transition_write_last_failure_at_utc"
    }
}
