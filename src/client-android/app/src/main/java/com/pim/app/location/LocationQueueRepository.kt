package com.pim.app.location

import com.pim.app.data.MobileDataDao
import com.pim.app.data.MobileLocationDroppedDiagnosticEntity
import com.pim.app.data.MobileLocationPointEntity
import com.pim.app.data.MobileLocationPolicyTransitionEntity
import com.pim.app.location.policy.LocationPolicyMode
import com.pim.app.location.policy.PolicyDecision
import com.pim.app.location.quality.QualityAcceptedLocation
import com.pim.app.location.quality.RawLocationFix
import javax.inject.Inject

/**
 * 定位点入库队列。
 *
 * **WO-ANDROID-GATE-20260926 REQ-15 / AC-15.1 / AC-15.2 / AC-15.3**：
 * 客户端**只做精度过滤** —— 一旦某个 fix 通过了质量门，就必须**逐条入库**。
 * 此处已**取消**两条点级裁剪：
 *
 * 1. 静止聚类丢弃（原 `TrajectoryCompressor.shouldClusterDrop`，5 米 / 30 秒）——
 *    基线实现命中即静默 `return -1L`，既不入库也不留记录，属 AC-15.3 明令禁止的
 *    「命中即 return 且无记录」；
 * 2. 上传前抽稀（见 [com.pim.app.mobile.sync.LocationUploadCoordinator]）。
 *
 * 滤波与舍弃交给服务端（A9 / A11）。`sprintSampleCount` 与入库条数的对账（AC-4.5）
 * 依赖这里「不吞点」这一性质，改动前请先读 REQ-15。
 */
class LocationQueueRepository @Inject constructor(
    private val dao: MobileDataDao
) {
    /**
     * 入库一条已通过质量门的定位点。
     *
     * @return 新插入行的 id；失败时为 -1（由 Room 决定，**不是**客户端裁剪的结果）。
     */
    suspend fun enqueueAccepted(
        accepted: QualityAcceptedLocation,
        rawJson: String,
        source: String = "auto"
    ): Long {
        return dao.insertLocationPoint(
            MobileLocationPointEntity.fromAccepted(accepted, rawJson, source)
        )
    }

    suspend fun recordDropped(
        fix: RawLocationFix,
        reason: String,
        createdAtUtc: Long = System.currentTimeMillis()
    ): Long {
        return dao.insertDroppedLocationDiagnostic(
            MobileLocationDroppedDiagnosticEntity.fromDropped(fix, reason, createdAtUtc)
        )
    }

    suspend fun recordPolicyTransition(
        fromMode: LocationPolicyMode?,
        decision: PolicyDecision,
        occurredAtUtc: Long = System.currentTimeMillis()
    ): Long {
        return dao.insertPolicyTransition(
            MobileLocationPolicyTransitionEntity.fromDecision(fromMode, decision, occurredAtUtc)
        )
    }
}
