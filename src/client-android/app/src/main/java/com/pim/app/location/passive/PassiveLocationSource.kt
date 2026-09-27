package com.pim.app.location.passive

import android.annotation.SuppressLint
import android.content.Context
import android.location.Location
import android.location.LocationListener
import android.location.LocationManager
import android.os.Bundle
import android.os.Looper
import com.pim.app.location.quality.LocationQualityGate
import com.pim.app.mobile.logs.StructuredLogRepository
import com.pim.app.location.quality.RawLocationFix
import com.pim.app.settings.TrackingSettingsStore
import dagger.hilt.android.qualifiers.ApplicationContext
import javax.inject.Inject
import javax.inject.Singleton
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/** 被动监听的注册/注销结果（AC-14.1 要求留下注册成功日志）。 */
sealed interface PassiveRegistrationResult {
    data class Registered(val provider: String, val registeredAtUtcMillis: Long) : PassiveRegistrationResult
    data class Failed(val reason: String) : PassiveRegistrationResult
    data object NotRegistered : PassiveRegistrationResult
}

/**
 * 被动定位源（WO-ANDROID-GATE-20260926 REQ-14 / A9）。
 *
 * 用平台 `LocationManager.PASSIVE_PROVIDER` 注册**被动监听**：自己不发起定位，
 * 只接收系统中其他应用/服务已经触发的定位结果。
 *
 * **与现架构的分工**：现有主动流走 Google `FusedLocationProviderClient`（高层封装，
 * 无 passive 等价物），因此被动必须走平台 `LocationManager`，且**不能复用现有
 * `updates()` 单例绑定**（Hilt 单实例绑定 + `LocationUpdateRequest` 语义错配）。
 *
 * **三条硬约束**（改动前请先读工单）：
 * - AC-14.1：随采集服务生命周期启停，注册成功必须留日志（含 provider 名与时刻）；
 * - AC-14.3：**不设限流** —— 本类的注册请求里没有任何最小间隔/最小距离节流语义；
 * - AC-14.7：**不得扰动主流** —— 全程不触碰主动流的注册间隔与周期锚点。
 */
@Singleton
open class PassiveLocationSource @Inject constructor(
    @ApplicationContext private val context: Context,
    private val trackingSettingsStore: TrackingSettingsStore,
    private val logs: StructuredLogRepository
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private val locationManager: LocationManager?
        get() = context.getSystemService(Context.LOCATION_SERVICE) as? LocationManager

    private var listener: LocationListener? = null

    /** 注册状态（AC-14.1：状态页/诊断可读）。 */
    @Volatile
    var registration: PassiveRegistrationResult = PassiveRegistrationResult.NotRegistered
        private set

    /**
     * 注册被动监听（随采集服务启动）。
     *
     * 平台语义：`PASSIVE_PROVIDER` 的 `minTime`/`minDistance` 只是**系统侧**的建议值，
     * 平台会转投所有已发生的定位结果；这里刻意都用 0 表示「不做任何本地节流」，
     * 以贴合 REQ-14 规则 2「不设限流」。
     */
    @SuppressLint("MissingPermission")
    open fun register(processor: PassiveLocationProcessor): PassiveRegistrationResult {
        if (listener != null) return registration
        val manager = locationManager
            ?: return PassiveRegistrationResult.Failed("系统定位服务不可用").also {
                registration = it
            }

        val newListener = object : LocationListener {
            override fun onLocationChanged(location: Location) {
                val fix = location.toPassiveFix()
                // 回调线程上只做入队，判定与落库在协程里做（不阻塞系统回调）。
                scope.launch {
                    try {
                        processor.handle(fix)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        // 被动通道异常不得影响采集主流（AC-14.7），但要留痕（不静默）。
                        logWarn("被动定位处理失败：${e.message ?: e.javaClass.simpleName}")
                    }
                }
            }

            @Deprecated("平台 API 34 起废弃，保留以兼容旧系统回调")
            override fun onStatusChanged(provider: String?, status: Int, extras: Bundle?) = Unit

            override fun onProviderEnabled(provider: String) = Unit

            override fun onProviderDisabled(provider: String) = Unit
        }

        return try {
            manager.requestLocationUpdates(
                LocationManager.PASSIVE_PROVIDER,
                // REQ-14 规则 2：不设限流 —— 0 表示不施加任何本地最小间隔/距离。
                0L,
                0f,
                newListener,
                Looper.getMainLooper()
            )
            listener = newListener
            val registeredAtUtcMillis = System.currentTimeMillis()
            // AC-14.1：注册成功必须留下含 provider 名与时刻的日志（证据物之一），
            // 可配合 `dumpsys location` 取证。失败路径在下面同样留痕。
            //
            // 注意用 `StructuredLogRepository`（会落进诊断包导出的 mobile-*.jsonl），
            // **不是 Timber**：本应用从未 `Timber.plant(...)`，Timber 输出在真机上
            // 根本不会出现 —— 真机实测确认过这一点。若用 Timber，验收方在设备上
            // 找不到任何注册痕迹，「注册成功」就成了无法核对的口头声明。
            logRegistration(registeredAtUtcMillis)
            PassiveRegistrationResult
                .Registered(
                    provider = LocationManager.PASSIVE_PROVIDER,
                    registeredAtUtcMillis = registeredAtUtcMillis
                )
                .also { registration = it }
        } catch (e: Exception) {
            // AC-14.1：注册失败同样不得静默（否则「没收到被动点」无从解释）。
            scope.launch {
                runCatching {
                    logs.warn(
                        operation = "passive-location",
                        message = "被动定位监听注册失败：${e.message ?: e.javaClass.simpleName}"
                    )
                }
            }
            PassiveRegistrationResult
                .Failed(e.message ?: e.javaClass.simpleName)
                .also { registration = it }
        }
    }

    /** 注销被动监听（随采集服务停止）。AC-14.1：停止后窗口内不再产生被动点。 */
    fun unregister() {
        val current = listener ?: return
        runCatching { locationManager?.removeUpdates(current) }
            .onFailure { failure -> logWarn("注销被动定位监听失败：${failure.message ?: ""}") }
        listener = null
        registration = PassiveRegistrationResult.NotRegistered
    }

    /** 是否处于注册状态（AC-14.1）。 */
    fun isRegistered(): Boolean = listener != null

    /** 采集侧的处理器提供者（由 service 注入，保证与冲刺/主流共用同一道门）。 */
    internal fun processorUsing(
        activeFixProvider: () -> List<RawLocationFix>
    ): PassiveLocationProcessor = PassiveLocationProcessor(
        qualityGate = LocationQualityGate.fromTrackingSettings(trackingSettingsStore.read()),
        activeFixProvider = activeFixProvider
    )

    private fun logWarn(message: String) {
        scope.launch { runCatching { logs.warn(operation = "passive-location", message = message) } }
    }

    /**
     * 把「被动监听已注册」写进可导出的结构化日志（AC-14.1 的证据物）。
     *
     * 用 fire-and-forget 协程：注册发生在服务启动路径上，不能因为写日志而阻塞；
     * 写失败也不影响监听本身（沿用既有「写日志失败不影响采集」的约定）。
     */
    private fun logRegistration(registeredAtUtcMillis: Long) {
        scope.launch {
            runCatching {
                logs.info(
                    operation = "passive-location",
                    message = "被动定位监听已注册：provider=${LocationManager.PASSIVE_PROVIDER}，" +
                        "registeredAtUtcMillis=$registeredAtUtcMillis"
                )
            }
        }
    }

    private fun Location.toPassiveFix() = PassiveFix(
        latitude = latitude,
        longitude = longitude,
        horizontalAccuracyMeters = if (hasAccuracy()) accuracy else null,
        altitudeMeters = if (hasAltitude()) altitude else null,
        // REQ-14 规则 6：provider 保持**原始提供方**（gps/network/fused），
        // 不得改写为 passive —— 来源标记走点表 source 列与丢弃原因编码。
        provider = provider ?: PASSIVE_UNKNOWN_PROVIDER,
        recordedAtMillis = time
    )

    private companion object {
        const val PASSIVE_UNKNOWN_PROVIDER = "unknown"
    }
}
