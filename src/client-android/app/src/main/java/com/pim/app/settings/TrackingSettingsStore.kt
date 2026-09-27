package com.pim.app.settings

import android.content.SharedPreferences
import com.pim.app.location.policy.TrackingPolicy

data class TrackingSettings(
    val profile: String,
    val continuousCollectionEnabled: Boolean,
    val normalIntervalMillis: Long,
    val scheduleLowFrequencyIntervalMillis: Long,
    val movementIntervalMillis: Long,
    val scheduleRecoveryThresholdMeters: Double,
    val altitudeWaitTimeoutMillis: Long,
    val syncOnUnmeteredOnly: Boolean,
    val logRetentionDays: Int = 7,
    val verboseLoggingUntilUtcMillis: Long? = null,
    /**
     * 高频冲刺开关（WO-ANDROID-GATE-20260926 REQ-5）。
     *
     * 默认**开**（D1：需求方要先体验效果）。关闭后必须**真的不再发起任何冲刺**，
     * 且切换后**无需重启**即自下一个周期起生效（AC-5.3 / AC-5.5）——
     * 采集循环每个周期都重新读本字段，不做一次性快照。
     *
     * 范围外提醒：本开关只控制冲刺窗口，**不**控制采集频率档位（REQ-10）。
     */
    val sprintEnabled: Boolean = true
) {
    companion object {
        fun defaults(): TrackingSettings = TrackingSettings(
            profile = "power-saving",
            continuousCollectionEnabled = false,
            normalIntervalMillis = 3 * 60 * 1000L,
            scheduleLowFrequencyIntervalMillis = 15 * 60 * 1000L,
            movementIntervalMillis = 60 * 1000L,
            scheduleRecoveryThresholdMeters = 100.0,
            altitudeWaitTimeoutMillis = 15 * 1000L,
            syncOnUnmeteredOnly = false,
            sprintEnabled = true
        )
    }
}

class TrackingSettingsStore(
    private val preferences: SharedPreferences
) {
    fun read(): TrackingSettings {
        val defaults = TrackingSettings.defaults()
        return defaults.copy(
            profile = preferences.getString(KEY_PROFILE, defaults.profile) ?: defaults.profile,
            continuousCollectionEnabled = preferences.getBoolean(
                KEY_CONTINUOUS_COLLECTION,
                defaults.continuousCollectionEnabled
            ),
            normalIntervalMillis = preferences.getLong(KEY_NORMAL_INTERVAL, defaults.normalIntervalMillis),
            scheduleLowFrequencyIntervalMillis = preferences.getLong(
                KEY_SCHEDULE_LOW_FREQUENCY_INTERVAL,
                defaults.scheduleLowFrequencyIntervalMillis
            ),
            movementIntervalMillis = preferences.getLong(KEY_MOVEMENT_INTERVAL, defaults.movementIntervalMillis),
            scheduleRecoveryThresholdMeters = preferences.getFloat(
                KEY_SCHEDULE_RECOVERY_THRESHOLD,
                defaults.scheduleRecoveryThresholdMeters.toFloat()
            ).toDouble(),
            altitudeWaitTimeoutMillis = preferences.getLong(
                KEY_ALTITUDE_WAIT_TIMEOUT,
                defaults.altitudeWaitTimeoutMillis
            ),
            syncOnUnmeteredOnly = preferences.getBoolean(
                KEY_SYNC_ON_UNMETERED_ONLY,
                defaults.syncOnUnmeteredOnly
            ),
            logRetentionDays = preferences.getInt(
                KEY_LOG_RETENTION_DAYS,
                defaults.logRetentionDays
            ),
            verboseLoggingUntilUtcMillis = preferences.getString(
                KEY_VERBOSE_LOGGING_UNTIL, null
            )?.toLongOrNull() ?: defaults.verboseLoggingUntilUtcMillis,
            sprintEnabled = preferences.getBoolean(KEY_SPRINT_ENABLED, defaults.sprintEnabled)
        )
    }

    fun write(settings: TrackingSettings): TrackingSettings {
        TrackingSettingsValidator.validateOrThrow(settings)
        preferences.edit()
            .putString(KEY_PROFILE, settings.profile)
            .putBoolean(KEY_CONTINUOUS_COLLECTION, settings.continuousCollectionEnabled)
            .putLong(KEY_NORMAL_INTERVAL, settings.normalIntervalMillis)
            .putLong(KEY_SCHEDULE_LOW_FREQUENCY_INTERVAL, settings.scheduleLowFrequencyIntervalMillis)
            .putLong(KEY_MOVEMENT_INTERVAL, settings.movementIntervalMillis)
            .putFloat(KEY_SCHEDULE_RECOVERY_THRESHOLD, settings.scheduleRecoveryThresholdMeters.toFloat())
            .putLong(KEY_ALTITUDE_WAIT_TIMEOUT, settings.altitudeWaitTimeoutMillis)
            .putBoolean(KEY_SYNC_ON_UNMETERED_ONLY, settings.syncOnUnmeteredOnly)
            .putInt(KEY_LOG_RETENTION_DAYS, settings.logRetentionDays)
            .putString(KEY_VERBOSE_LOGGING_UNTIL, settings.verboseLoggingUntilUtcMillis?.toString())
            .putBoolean(KEY_SPRINT_ENABLED, settings.sprintEnabled)
            .apply()
        return read()
    }

    fun setContinuousCollectionEnabled(enabled: Boolean): TrackingSettings {
        return write(read().copy(continuousCollectionEnabled = enabled))
    }

    /**
     * 切换高频冲刺开关（REQ-5 / AC-5.5）。
     *
     * 只写这一个字段并**立即落盘**：采集循环与手动会话每拍重新读，
     * 因此无需重启应用/采集服务即自下一个周期起生效。
     */
    fun setSprintEnabled(enabled: Boolean): TrackingSettings {
        return write(read().copy(sprintEnabled = enabled))
    }

    fun applyPreset(profileId: String): TrackingSettings {
        val preset = TrackingPresetCatalog.get(profileId)
            ?: throw IllegalArgumentException("Unknown preset: $profileId")
        val current = read()
        val updated = preset.applyTo(current)
        return write(updated)
    }

    fun setVerboseLoggingEnabled(enabled: Boolean, nowUtcMillis: Long): TrackingSettings {
        val current = read()
        val updated = current.copy(
            verboseLoggingUntilUtcMillis = if (enabled) nowUtcMillis + 24 * 60 * 60 * 1000L else null
        )
        return write(updated)
    }

    fun isVerboseLoggingEnabled(nowUtcMillis: Long): Boolean {
        val settings = read()
        val deadline = settings.verboseLoggingUntilUtcMillis ?: return false
        if (nowUtcMillis >= deadline) {
            write(settings.copy(verboseLoggingUntilUtcMillis = null))
            return false
        }
        return true
    }

    fun resetOperationalDefaults(): TrackingSettings {
        return write(TrackingSettings.defaults())
    }

    private companion object {
        const val KEY_PROFILE = "tracking.profile"
        const val KEY_CONTINUOUS_COLLECTION = "tracking.continuous_collection_enabled"
        const val KEY_NORMAL_INTERVAL = "tracking.normal_interval_millis"
        const val KEY_SCHEDULE_LOW_FREQUENCY_INTERVAL = "tracking.schedule_low_frequency_interval_millis"
        const val KEY_MOVEMENT_INTERVAL = "tracking.movement_interval_millis"
        const val KEY_SCHEDULE_RECOVERY_THRESHOLD = "tracking.schedule_recovery_threshold_meters"
        const val KEY_ALTITUDE_WAIT_TIMEOUT = "tracking.altitude_wait_timeout_millis"
        const val KEY_SYNC_ON_UNMETERED_ONLY = "tracking.sync_on_unmetered_only"
        const val KEY_LOG_RETENTION_DAYS = "tracking.log_retention_days"
        const val KEY_VERBOSE_LOGGING_UNTIL = "tracking.verbose_logging_until_utc_millis"
        const val KEY_SPRINT_ENABLED = "tracking.sprint_enabled"
    }
}

fun TrackingSettings.toTrackingPolicy(): TrackingPolicy = TrackingPolicy(
    normalIntervalMillis = normalIntervalMillis,
    scheduleLowFrequencyIntervalMillis = scheduleLowFrequencyIntervalMillis,
    movementIntervalMillis = movementIntervalMillis,
    scheduleRecoveryThresholdMeters = scheduleRecoveryThresholdMeters,
    altitudeWaitTimeoutMillis = altitudeWaitTimeoutMillis
)
