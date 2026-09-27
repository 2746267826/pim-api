package com.pim.app.location.quality

import com.pim.app.settings.TrackingSettings
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class LocationQualityGateTest {
    private val gate = LocationQualityGate(
        altitudeWaitTimeoutMillis = 15_000L
    )

    @Test
    fun missingHorizontalAccuracyIsDropped() {
        val result = gate.evaluate(fix(horizontalAccuracyMeters = null), nowMillis = 2_000L)

        assertTrue(result is QualityDecision.Drop)
        val dropped = result as QualityDecision.Drop
        assertEquals("missing-horizontal-accuracy", dropped.reason)
        assertEquals("PowerSavingNormal", dropped.fix.policyMode)
        assertEquals("Unknown", dropped.fix.motionSignal)
    }

    @Test
    fun defaultGateThresholdIsThirtyMetersExclusive() {
        val defaultGate = LocationQualityGate()
        val accepted = defaultGate.evaluate(
            fix(horizontalAccuracyMeters = 29.9f, altitudeMeters = 12.0),
            nowMillis = 2_000L
        )
        assertTrue(accepted is QualityDecision.AcceptNow)
        val dropped = defaultGate.evaluate(
            fix(horizontalAccuracyMeters = 30f, altitudeMeters = 12.0),
            nowMillis = 2_000L
        )
        assertEquals("horizontal-accuracy-too-low", (dropped as QualityDecision.Drop).reason)
    }

    @Test
    fun accuracyBelowThirtyMetersIsAccepted() {
        val result = gate.evaluate(
            fix(horizontalAccuracyMeters = 29.9f, altitudeMeters = 12.0),
            nowMillis = 2_000L
        )

        assertTrue(result is QualityDecision.AcceptNow)
        val accepted = (result as QualityDecision.AcceptNow).accepted
        assertEquals(12.0, accepted.altitudeMeters!!, 0.001)
        assertEquals(2_000L, accepted.acceptedAtMillis)
        assertFalse(accepted.qualityFlags.contains("altitude-missing-timeout"))
        assertEquals("PowerSavingNormal", accepted.fix.policyMode)
    }

    @Test
    fun accuracyAtThirtyMetersIsDropped() {
        val result = gate.evaluate(fix(horizontalAccuracyMeters = 30.0f), nowMillis = 2_000L)

        assertTrue(result is QualityDecision.Drop)
        val dropped = result as QualityDecision.Drop
        assertEquals("horizontal-accuracy-too-low", dropped.reason)
        assertEquals(30.0f, dropped.fix.horizontalAccuracyMeters)
    }

    @Test
    fun accuracyAboveThirtyMetersIsDropped() {
        val result = gate.evaluate(fix(horizontalAccuracyMeters = 80.0f), nowMillis = 2_000L)

        assertTrue(result is QualityDecision.Drop)
        assertEquals("horizontal-accuracy-too-low", (result as QualityDecision.Drop).reason)
    }

    @Test
    fun nonFiniteAccuracyIsDropped() {
        val result = gate.evaluate(fix(horizontalAccuracyMeters = Float.NaN), nowMillis = 2_000L)

        assertTrue(result is QualityDecision.Drop)
        assertEquals("horizontal-accuracy-too-low", (result as QualityDecision.Drop).reason)
    }

    @Test
    fun missingAltitudeWaitsUntilDeadline() {
        val result = gate.evaluate(
            fix(horizontalAccuracyMeters = 18f, altitudeMeters = null, recordedAtMillis = 1_000L),
            nowMillis = 1_000L
        )

        assertTrue(result is QualityDecision.WaitForAltitude)
        val pending = (result as QualityDecision.WaitForAltitude).pending
        assertEquals(16_000L, pending.deadlineMillis)

        val stillWaiting = gate.timeoutDecision(pending, nowMillis = 15_999L)
        assertTrue(stillWaiting is QualityDecision.WaitForAltitude)
    }

    @Test
    fun missingAltitudeTimeoutAcceptsNullAltitudeWithQualityFlag() {
        val waiting = gate.evaluate(
            fix(horizontalAccuracyMeters = 18f, altitudeMeters = null, recordedAtMillis = 1_000L),
            nowMillis = 1_000L
        ) as QualityDecision.WaitForAltitude

        val result = gate.timeoutDecision(waiting.pending, nowMillis = 16_000L)

        assertTrue(result is QualityDecision.AcceptNow)
        val accepted = (result as QualityDecision.AcceptNow).accepted
        assertNull(accepted.altitudeMeters)
        assertEquals(16_000L, accepted.acceptedAtMillis)
        assertTrue(accepted.qualityFlags.contains("altitude-missing-timeout"))
        assertEquals("PowerSavingNormal", accepted.fix.policyMode)
        assertEquals("Unknown", accepted.fix.motionSignal)
    }

    @Test
    fun fromTrackingSettingsAppliesFixedThirtyMeterThresholdAndSettingsAltitudeWait() {
        val settings = TrackingSettings.defaults().copy(
            altitudeWaitTimeoutMillis = 20_000L
        )
        val gate = LocationQualityGate.fromTrackingSettings(settings)

        val dropped = gate.evaluate(
            fix(horizontalAccuracyMeters = 35f, altitudeMeters = null, recordedAtMillis = 1_000L),
            nowMillis = 1_000L
        )
        assertTrue(dropped is QualityDecision.Drop)
        assertEquals("horizontal-accuracy-too-low", (dropped as QualityDecision.Drop).reason)

        val waiting = gate.evaluate(
            fix(horizontalAccuracyMeters = 15f, altitudeMeters = null, recordedAtMillis = 1_000L),
            nowMillis = 1_000L
        )
        assertTrue(waiting is QualityDecision.WaitForAltitude)
        assertEquals(21_000L, (waiting as QualityDecision.WaitForAltitude).pending.deadlineMillis)
    }

    private fun fix(
        horizontalAccuracyMeters: Float?,
        altitudeMeters: Double? = null,
        recordedAtMillis: Long = 1_000L
    ) = RawLocationFix(
        latitude = 31.230416,
        longitude = 121.473701,
        horizontalAccuracyMeters = horizontalAccuracyMeters,
        altitudeMeters = altitudeMeters,
        provider = "gps",
        recordedAtMillis = recordedAtMillis,
        policyMode = "PowerSavingNormal",
        scheduleLowFrequency = false,
        motionSignal = "Unknown"
    )
}
