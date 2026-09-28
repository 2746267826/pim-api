package com.pim.app.ui.settings

import java.io.File
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-5 / **AC-5.6** 的源码谓词守卫。
 *
 * 设备侧（androidTest）读不到仓库源码，所以「板块标题逐字」「位置在诊断之后」
 * 「清除诊断数据说明文字」这三条放在单测里断言（沿用 `SprintServiceWiringTest` 的既有范式）。
 */
class PolicyTransitionHistorySectionWiringTest {

    private fun repoSource(relativePath: String): String {
        var current: File? = File("").canonicalFile
        while (current != null) {
            val candidate = current.resolve(relativePath)
            if (candidate.isFile) return candidate.readText(Charsets.UTF_8)
            current = current.parentFile
        }
        error("$relativePath not found")
    }

    private fun settingsSource(): String =
        repoSource("src/main/java/com/pim/app/ui/settings/SettingsScreen.kt")

    @Test
    fun `板块标题与说明文案逐字`() {
        val source = settingsSource()
        assertTrue(
            "板块标题必须逐字为「策略切换历史」",
            source.contains("PimSection(\"策略切换历史\"")
        )
        assertTrue(
            "空态文案必须逐字（D-8）",
            source.contains("暂无记录（修复后的新版本开始记录）")
        )
        assertTrue(
            "说明文字必须逐字为「本地保留 30 天」",
            source.contains("\"本地保留 30 天\"")
        )
        assertTrue(
            "超 20 条时的提示行必须逐字",
            source.contains("仅显示最近 \$POLICY_HISTORY_PAGE_SIZE 条 · ") &&
                source.contains("30 天内共 \${state.policyHistoryTotalInWindow} 条")
        )
    }

    @Test
    fun `板块位置在诊断板块之后`() {
        val source = settingsSource()
        val diagnostics = source.indexOf("PimSection(\"诊断\")")
        val history = source.indexOf("PimSection(\"策略切换历史\"")
        assertTrue("两个板块都必须能找到", diagnostics > 0 && history > 0)
        assertTrue("策略切换历史必须排在诊断之后（AC-5.6）", diagnostics < history)
    }

    @Test
    fun `清除诊断数据的说明文字注明会一并清除历史`() {
        val source = settingsSource()
        assertTrue(
            "按钮说明必须包含「将一并清除策略切换历史」",
            source.contains("将一并清除策略切换历史")
        )
    }

    @Test
    fun `板块由设置页装配且展开走板块内回调`() {
        val source = settingsSource()
        assertTrue(
            "设置页必须装配历史板块",
            source.contains("PolicyTransitionHistorySection(")
        )
        assertTrue(
            "「展开全部」必须走板块内回调（D-7：不新建二级页面）",
            source.contains("onExpandPolicyHistory = viewModel::expandPolicyTransitionHistory") &&
                source.contains("onClick = onExpandPolicyHistory")
        )
    }
}
