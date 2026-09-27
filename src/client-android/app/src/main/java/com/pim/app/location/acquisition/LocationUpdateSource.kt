package com.pim.app.location.acquisition

import android.annotation.SuppressLint
import android.content.Context
import android.location.Location
import android.os.Looper
import com.google.android.gms.location.FusedLocationProviderClient
import com.google.android.gms.location.LocationAvailability
import com.google.android.gms.location.LocationCallback
import com.google.android.gms.location.LocationRequest
import com.google.android.gms.location.LocationResult
import com.google.android.gms.location.LocationServices
import com.pim.app.location.LocationSnapshot
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.channels.awaitClose
import kotlinx.coroutines.flow.Flow
import kotlinx.coroutines.flow.callbackFlow
import javax.inject.Inject
import javax.inject.Singleton

data class LocationUpdateRequest(
    val priority: Int,
    val durationMillis: Long,
    val intervalMillis: Long = 1_000L,
    val minUpdateIntervalMillis: Long = 800L
)

sealed interface LocationUpdateEvent {
    data class Candidate(val location: LocationSnapshot) : LocationUpdateEvent
    data class Availability(val available: Boolean) : LocationUpdateEvent
}

interface LocationUpdateSource {
    fun updates(request: LocationUpdateRequest): Flow<LocationUpdateEvent>
}

@Singleton
class FusedLocationUpdateSource @Inject constructor(
    @ApplicationContext context: Context
) : LocationUpdateSource {

    private val fusedLocationClient: FusedLocationProviderClient =
        LocationServices.getFusedLocationProviderClient(context)

    @SuppressLint("MissingPermission")
    override fun updates(request: LocationUpdateRequest): Flow<LocationUpdateEvent> = callbackFlow {
        val builder = LocationRequest.Builder(request.priority, request.intervalMillis)
            .setMinUpdateIntervalMillis(request.minUpdateIntervalMillis)
        if (request.durationMillis > 0L) {
            builder.setDurationMillis(request.durationMillis)
        }
        // AC-5.6 提醒：冲刺走的是**独立**注册（LocationSprintController），
        // 它有自己的 interval/duration，绝不通过改写主流注册来实现 —— 那会把下一拍
        // 从「改回时刻」重新计时，把周期锚点拖长。
        val locationRequest = builder.build()

        val callback = object : LocationCallback() {
            override fun onLocationResult(result: LocationResult) {
                for (location in result.locations) {
                    trySend(LocationUpdateEvent.Candidate(location.toSnapshot()))
                }
            }

            override fun onLocationAvailability(availability: LocationAvailability) {
                trySend(LocationUpdateEvent.Availability(availability.isLocationAvailable))
            }
        }

        val task = fusedLocationClient.requestLocationUpdates(
            locationRequest,
            callback,
            Looper.getMainLooper()
        )

        task.addOnFailureListener { exception ->
            close(exception)
        }

        awaitClose {
            fusedLocationClient.removeLocationUpdates(callback)
        }
    }

    private companion object {
        private fun Location.toSnapshot(): LocationSnapshot = LocationSnapshot(
            latitude = latitude,
            longitude = longitude,
            horizontalAccuracyMeters = if (hasAccuracy()) accuracy else null,
            provider = provider ?: "unknown",
            source = "realtime",
            altitudeMeters = if (hasAltitude()) altitude else null,
            speedMetersPerSecond = if (hasSpeed()) speed else null,
            bearingDegrees = if (hasBearing()) bearing else null,
            timeMillis = time
        )
    }
}
