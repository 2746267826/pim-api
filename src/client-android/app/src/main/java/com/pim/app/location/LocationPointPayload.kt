package com.pim.app.location

import com.pim.app.location.quality.QualityAcceptedLocation
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonNull
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.buildJsonArray
import kotlinx.serialization.json.buildJsonObject

/**
 * 定位点上传负载的**唯一构造点**（主动流、冲刺窗口、被动源共用）。
 *
 * WO-ANDROID-GATE-20260926 新增两个采集来源（冲刺沿用主动流、被动源独立），
 * 若各自拼一份 JSON，`source` / `policyMode` / `qualityFlags` 等字段会很快分叉，
 * 而服务端的 `IsAutoSubmitted` 正是由 `source` 推导（`Source == "auto"`）——
 * 分叉会直接改变服务端语义。因此收敛到一处。
 */
object LocationPointPayload {

    private val json = Json

    /**
     * @param source 入库点表 `source` 列的值：`auto` / `manual` / `passive`
     *   （REQ-14 规则 6：被动点新增 `passive`）。
     */
    fun encode(
        accepted: QualityAcceptedLocation,
        source: String,
        submittedAtMillis: Long
    ): String {
        val fix = accepted.fix
        val payload = buildJsonObject {
            put("latitude", JsonPrimitive(fix.latitude))
            put("longitude", JsonPrimitive(fix.longitude))
            put(
                "horizontalAccuracyMeters",
                finiteJsonNumberOrNull(fix.horizontalAccuracyMeters?.toDouble())
            )
            put("provider", JsonPrimitive(fix.provider))
            put("source", JsonPrimitive(source))
            put("altitudeMeters", finiteJsonNumberOrNull(accepted.altitudeMeters))
            put(
                "speedMetersPerSecond",
                finiteJsonNumberOrNull(fix.speedMetersPerSecond?.toDouble())
            )
            put(
                "bearingDegrees",
                finiteJsonNumberOrNull(fix.bearingDegrees?.toDouble())
            )
            put("recordedAtUnixMs", JsonPrimitive(fix.recordedAtMillis))
            put("submittedAtUnixMs", JsonPrimitive(submittedAtMillis))
            put("policyMode", JsonPrimitive(fix.policyMode))
            put("scheduleLowFrequency", JsonPrimitive(fix.scheduleLowFrequency))
            put("motionSignal", JsonPrimitive(fix.motionSignal))
            put(
                "qualityFlags",
                buildJsonArray {
                    accepted.qualityFlags.sorted().forEach { add(JsonPrimitive(it)) }
                }
            )
        }
        return json.encodeToString(JsonElement.serializer(), payload)
    }

    private fun finiteJsonNumberOrNull(value: Double?): JsonElement =
        if (value == null || !value.isFinite()) JsonNull else JsonPrimitive(value)
}
