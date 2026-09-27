package com.pim.app.location.quality

import com.pim.app.settings.TrackingSettings

data class RawLocationFix(
    val latitude: Double,
    val longitude: Double,
    val horizontalAccuracyMeters: Float?,
    val altitudeMeters: Double?,
    val provider: String,
    val recordedAtMillis: Long,
    val policyMode: String,
    val scheduleLowFrequency: Boolean,
    val motionSignal: String,
    val speedMetersPerSecond: Float? = null,
    val bearingDegrees: Float? = null
)

data class QualityAcceptedLocation(
    val fix: RawLocationFix,
    val altitudeMeters: Double?,
    val acceptedAtMillis: Long,
    val qualityFlags: Set<String>
)

data class PendingAltitudeFix(
    val fix: RawLocationFix,
    val deadlineMillis: Long
)

sealed class QualityDecision {
    data class AcceptNow(val accepted: QualityAcceptedLocation) : QualityDecision()
    data class WaitForAltitude(val pending: PendingAltitudeFix) : QualityDecision()
    data class Drop(val fix: RawLocationFix, val reason: String) : QualityDecision()
}

class LocationQualityGate(
    private val maxAccuracyMetersExclusive: Float = MAX_ACCURACY_METERS_EXCLUSIVE,
    private val altitudeWaitTimeoutMillis: Long = 15_000L
) {
    fun evaluate(fix: RawLocationFix, nowMillis: Long = fix.recordedAtMillis): QualityDecision {
        val accuracy = fix.horizontalAccuracyMeters
            ?: return QualityDecision.Drop(fix, "missing-horizontal-accuracy")

        if (!accuracy.isFinite() || accuracy >= maxAccuracyMetersExclusive) {
            return QualityDecision.Drop(fix, "horizontal-accuracy-too-low")
        }

        val altitude = fix.altitudeMeters
        return if (altitude != null) {
            QualityDecision.AcceptNow(
                QualityAcceptedLocation(
                    fix = fix,
                    altitudeMeters = altitude,
                    acceptedAtMillis = nowMillis,
                    qualityFlags = emptySet()
                )
            )
        } else {
            QualityDecision.WaitForAltitude(
                PendingAltitudeFix(
                    fix = fix,
                    deadlineMillis = fix.recordedAtMillis + altitudeWaitTimeoutMillis
                )
            )
        }
    }

    fun timeoutDecision(pending: PendingAltitudeFix, nowMillis: Long): QualityDecision {
        if (nowMillis < pending.deadlineMillis) {
            return QualityDecision.WaitForAltitude(pending)
        }

        return QualityDecision.AcceptNow(
            QualityAcceptedLocation(
                fix = pending.fix,
                altitudeMeters = null,
                acceptedAtMillis = nowMillis,
                qualityFlags = setOf("altitude-missing-timeout")
            )
        )
    }

    companion object {
        /**
         * 精度门槛（米，**严格小于**才收）：WO-ANDROID-GATE-20260926 REQ-1 / A1 + A10（2026-09-26 复核确认 30 米）。
         *
         * **这是门槛的唯一来源**：用户可见文案（手动定位页「精度规则」、状态页丢弃原因说明）
         * 必须引用本常量派生，不得在第二处写死（AC-1.3 / AC-12.3）。
         */
        const val MAX_ACCURACY_METERS_EXCLUSIVE = 30f
        const val LOW_QUALITY_ACCURACY_FLAG = "low-quality-accuracy"

        fun fromTrackingSettings(settings: TrackingSettings): LocationQualityGate =
            LocationQualityGate(
                altitudeWaitTimeoutMillis = settings.altitudeWaitTimeoutMillis
            )

        /**
         * 门槛的用户可见数值文案（AC-1.3 / AC-12.3）：**唯一**的文案格式化点，
         * 保证所有页面显示的门槛与采集判定同源。整数值去掉小数尾巴（30.0 → "30"）。
         */
        fun displayThresholdMeters(): String =
            if (MAX_ACCURACY_METERS_EXCLUSIVE == MAX_ACCURACY_METERS_EXCLUSIVE.toInt().toFloat()) {
                MAX_ACCURACY_METERS_EXCLUSIVE.toInt().toString()
            } else {
                MAX_ACCURACY_METERS_EXCLUSIVE.toString()
            }
    }
}
