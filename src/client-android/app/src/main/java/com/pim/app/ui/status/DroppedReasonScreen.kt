package com.pim.app.ui.status

import com.pim.app.location.passive.PassiveLocationContract
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Divider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import com.pim.app.forensics.DroppedReasonUiState
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter

/**
 * 「丢弃原因」页（REQ-9）：按原因统计 + 最近 20 条明细（时刻 / 原因 / 准确度 / provider / 策略档）。
 *
 * 与状态页同域：从状态页存活区块进入，返回即回到状态页。
 * 空态与超量截断提示都在这里给出（§6 页面清单要求覆盖的状态）。
 */
@Composable
internal fun DroppedReasonScreen(
    state: DroppedReasonUiState?,
    onBack: () -> Unit,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .fillMaxSize()
            .verticalScroll(rememberScrollState())
            .padding(16.dp)
            .testTag("dropped-reasons-screen"),
        verticalArrangement = Arrangement.spacedBy(8.dp)
    ) {
        TextButton(onClick = onBack, modifier = Modifier.testTag("dropped-reasons-back")) {
            Text("返回状态页")
        }

        Text(
            "丢弃原因",
            style = MaterialTheme.typography.titleLarge,
            fontWeight = FontWeight.Bold
        )

        if (state == null) {
            Text("正在读取本地丢弃诊断…", modifier = Modifier.testTag("dropped-reasons-loading"))
            return@Column
        }

        if (state.totalCount == 0) {
            Text(
                "暂无被丢弃的定位点。",
                modifier = Modifier.testTag("dropped-reasons-empty")
            )
            return@Column
        }

        Text(
            "累计被丢弃 ${state.totalCount} 条（本地保留 30 天）。",
            modifier = Modifier.testTag("dropped-reasons-total")
        )

        Divider()

        Text("按原因统计", fontWeight = FontWeight.SemiBold)
        Text(
            "原因说明：被丢弃说明这些定位点没有入库，明细随诊断导出包一并提供。",
            style = MaterialTheme.typography.bodySmall
        )
        state.byReason.forEach { item ->
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .testTag("dropped-reasons-count-${item.reason}"),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                Text(droppedReasonLabel(item.reason), fontWeight = FontWeight.Medium)
                Text("${item.count} 条")
            }
        }

        Divider()

        Text("最近 ${state.detailLimit} 条明细", fontWeight = FontWeight.SemiBold)
        state.recentDetails.forEach { detail ->
            Text(
                "${formatTime(detail.recordedAtUtc)} · ${droppedReasonLabel(detail.reason)}" +
                    " · 准确度 ${detail.accuracyMeters?.let { "$it 米" } ?: "不可用"}" +
                    " · ${detail.provider ?: "provider 不可用"}" +
                    " · 策略档 ${detail.policyMode}",
                style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.testTag("dropped-reasons-detail-${detail.recordedAtUtc}")
            )
        }

        if (state.totalCount > state.recentDetails.size) {
            Text(
                "仅显示最近 ${state.recentDetails.size} 条，完整明细见诊断导出包（不设条数上限）。",
                modifier = Modifier.testTag("dropped-reasons-truncated"),
                style = MaterialTheme.typography.bodySmall
            )
        }
    }
}

/** 丢弃原因的中文文案（AC-26.1）。未知原因原样显示标识，不猜成某个具体原因。 */
/**
 * 丢弃原因的中文标签。
 *
 * WO-ANDROID-GATE-20260926 REQ-14 规则 4：被动点的丢弃用**原因编码**（`passive-*`）留痕，
 * 这里必须补上映射，否则验收方在页面上只能看到原始编码 / 「其他原因」，无法区分来源。
 */
internal fun droppedReasonLabel(reason: String): String = when (reason) {
    "missing-horizontal-accuracy" -> "缺少水平准确度"
    "horizontal-accuracy-too-low" -> "水平准确度不达标"
    "altitude-missing-timeout" -> "等待高度超时"
    PassiveLocationContract.REASON_ACCURACY_TOO_LOW -> "被动定位精度不达标"
    PassiveLocationContract.REASON_MISSING_ACCURACY -> "被动定位缺少水平精度"
    PassiveLocationContract.REASON_DUPLICATE_FIX -> "被动定位与主动流重复"
    PassiveLocationContract.REASON_ENQUEUE_FAILED -> "被动定位入库失败"
    "sprint-enqueue-failed" -> "冲刺点入库失败"
    else -> reason
}

private val DETAIL_TIME_FORMATTER: DateTimeFormatter =
    DateTimeFormatter.ofPattern("MM-dd HH:mm:ss")

private fun formatTime(recordedAtUtc: Long): String =
    Instant.ofEpochMilli(recordedAtUtc).atZone(ZoneId.systemDefault()).format(DETAIL_TIME_FORMATTER)
