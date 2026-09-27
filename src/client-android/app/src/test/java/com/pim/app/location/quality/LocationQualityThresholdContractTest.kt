package com.pim.app.location.quality

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * WO-ANDROID-GATE-20260926 REQ-1 / REQ-7 / REQ-12（AC-1.1 / AC-1.2 / AC-1.3 / AC-7.1 / AC-7.2 / AC-12.3）。
 *
 * 门槛值必须**只有一个来源**（`LocationQualityGate.MAX_ACCURACY_METERS_EXCLUSIVE`），
 * 用户可见文案（手动定位页「精度规则」、状态页丢弃原因说明）必须由它推导，
 * 不得在第二处写死 —— 否则会出现「按 30 米收点、页面还写 < 20m」。
 *
 * 这里对**源码文本**断言是刻意的：本条的验收对象就是「有没有第二处写死」，
 * 纯行为测试（[LocationQualityGateTest]）无法覆盖这一点。
 */
class LocationQualityThresholdContractTest {

    private fun source(relativePath: String): String =
        appSourceFile(
            "src", "main", "java", "com", "pim", "app"
        ).resolve(relativePath).readText(Charsets.UTF_8)

    private fun mainSourceFiles(): List<File> =
        appSourceFile("src", "main", "java", "com", "pim", "app").walkTopDown()
            .filter { it.isFile && it.extension == "kt" }
            .toList()

    /** 从测试工作目录向上找模块根（Gradle 单测 cwd 会随配置变化，不能写死相对层级）。 */
    private fun appSourceFile(vararg parts: String): File {
        var current: File? = File("").canonicalFile
        while (current != null) {
            val candidate = parts.fold(current) { dir, part -> dir.resolve(part) }
            if (candidate.isDirectory) return candidate
            current = current.parentFile
        }
        error("Could not locate Android main sources: ${parts.joinToString(File.separator)}")
    }

    /** AC-1.3：门槛数值在代码中只有一处定义。 */
    @Test
    fun `门槛数值全仓只有一处字面量定义`() {
        val definitions = mainSourceFiles().filter { file ->
            THRESHOLD_DEFINITION.containsMatchIn(file.readText(Charsets.UTF_8))
        }

        assertEquals(
            "AC-1.3：门槛数值只允许在 LocationQualityGate 中定义一次",
            listOf("LocationQualityGate.kt"),
            definitions.map { it.name }.sorted()
        )
    }

    /** AC-1.1 / AC-1.2：门槛为 30 米，且严格小于才收。 */
    @Test
    fun `门槛常量为 30 米且严格小于才收`() {
        val gate = LocationQualityGate()

        assertTrue(
            "AC-1.1：精度 29.9 米的点必须被收下",
            gate.evaluate(fix(29.9f)) is QualityDecision.AcceptNow
        )
        assertEquals(
            "AC-1.2：精度 30.0 米的点必须被丢弃，原因不变",
            "horizontal-accuracy-too-low",
            (gate.evaluate(fix(30.0f)) as QualityDecision.Drop).reason
        )
        assertEquals(30f, LocationQualityGate.MAX_ACCURACY_METERS_EXCLUSIVE)
    }

    /** AC-12.3：手动定位页的门槛文案必须由门槛常量推导，不得写死。 */
    @Test
    fun `手动定位页门槛文案不再写死 20m`() {
        val scaffold = source("ui/PimAppScaffold.kt")

        assertFalse(
            "AC-12.3：手动定位页不得再出现写死的「精度门槛 < 20m」",
            scaffold.contains("精度门槛 < 20m")
        )
        assertFalse(
            "AC-12.3：手动定位页不得把门槛数值内联进文案（命中：${HARDCODED_SCAFFOLD_THRESHOLD.pattern}）",
            HARDCODED_SCAFFOLD_THRESHOLD.containsMatchIn(scaffold)
        )
        assertTrue(
            "AC-12.3：手动定位页门槛文案必须由 LocationQualityGate 派生",
            scaffold.contains("LocationQualityGate.displayThresholdMeters()")
        )
    }

    /** AC-12.3：状态页丢弃原因说明必须与采集门槛同一口径，不得留着旧的 50m 说法。 */
    @Test
    fun `状态页丢弃原因说明与采集门槛同口径`() {
        val issues = source("status/StatusIssue.kt")

        assertFalse(
            "AC-12.3：状态页不得再说「大于等于 50m 被丢弃」（50m 是查询侧口径，与采集门槛无关）",
            issues.contains("大于等于 50m 被丢弃")
        )
        assertTrue(
            "AC-12.3：状态页丢弃原因说明必须由 LocationQualityGate 派生",
            issues.contains("LocationQualityGate.displayThresholdMeters()")
        )
    }

    /** AC-7.1 / AC-7.2：不得新增用户可见的门槛可调项。 */
    @Test
    fun `不存在用户可见的门槛设置项`() {
        val settingsScreen = source("ui/settings/SettingsScreen.kt")
        val settingsViewModel = source("ui/settings/SettingsViewModel.kt")
        val settingsStore = source("settings/TrackingSettingsStore.kt")

        listOf("精度门槛", "门槛", "accuracyThreshold", "accuracyThresholdMeters").forEach { needle ->
            assertFalse(
                "AC-7.2：设置页不得出现用户可见的门槛设置项（命中：$needle）",
                settingsScreen.contains(needle)
            )
        }
        assertFalse(
            "AC-7.2：SettingsViewModel 不得新增门槛设置入口",
            settingsViewModel.contains("accuracyThreshold")
        )
        assertFalse(
            "AC-7.2：TrackingSettings 不得新增门槛字段（门槛固定 30 米，见 D4）",
            settingsStore.contains("accuracyThreshold")
        )
    }

    private fun fix(accuracy: Float) = RawLocationFix(
        latitude = 31.230416,
        longitude = 121.473701,
        horizontalAccuracyMeters = accuracy,
        altitudeMeters = 12.0,
        provider = "gps",
        recordedAtMillis = 1_000L,
        policyMode = "PowerSavingNormal",
        scheduleLowFrequency = false,
        motionSignal = "Unknown"
    )

    private companion object {
        /** `x = 30f` 这类门槛字面量定义（排除 `accuracy >= 30f` 这类派生比较）。 */
        val THRESHOLD_DEFINITION = Regex(
            """(?:MAX_ACCURACY_METERS_EXCLUSIVE|maxAccuracyMetersExclusive)\s*[:=]\s*\d+(?:\.\d+)?f"""
        )

        /** 用户可见文案里内联的门槛数字，例如「精度门槛 < 30m」。 */
        val HARDCODED_SCAFFOLD_THRESHOLD = Regex("""精度门槛\s*<\s*\d""")
    }
}
