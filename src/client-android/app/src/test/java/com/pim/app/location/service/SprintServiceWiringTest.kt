package com.pim.app.location.service

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * WO-ANDROID-GATE-20260926 REQ-2 / REQ-6 / REQ-10 —— **服务接线**必须正确。
 *
 * 独立 review（round 4）指出：前几轮只在**单元级**验证了
 * `SprintPeriodGate` / `LocationSprintRuntime` / `startManualSprint`，
 * 但**生产调用点**是否真的用了它们没有任何测试守着 ——
 * `maybeRunManualSprint` 曾经仍走 `onPeriod`（把周期门控的锚点污染成 1000ms），
 * 而全部测试仍然全绿。
 *
 * 这是「源码谓词」测试（沿用仓库既有约定，如 `AndroidV2CollectionControlContractTest`）：
 * 它断言的是**接线**，而不是行为 —— 行为已由各自的单元测试覆盖。
 */
class SprintServiceWiringTest {

    private fun serviceSource(): String =
        repoSource("src/main/java/com/pim/app/location/service/ForegroundLocationService.kt")

    private fun repoSource(relativePath: String): String {
        var current: File? = File("").canonicalFile
        while (current != null) {
            val candidate = current.resolve(relativePath)
            if (candidate.isFile) return candidate.readText(Charsets.UTF_8)
            current = current.parentFile
        }
        error("$relativePath not found")
    }

    /** I-1：手动冲刺必须走 `onManualSession`（不经周期门控、不改锚点）。 */
    @Test
    fun `手动冲刺走 onManualSession 而不是 onPeriod`() {
        val source = serviceSource()
        val manualBlock = source
            .substringAfter("private fun maybeRunManualSprint()")
            .substringBefore("\n    }")

        assertTrue(
            "I-1：手动冲刺必须调用 locationSprintRuntime.onManualSession()",
            manualBlock.contains("locationSprintRuntime.onManualSession()")
        )
        assertFalse(
            "I-1：手动冲刺**不得**走 onPeriod（它会把周期门控锚点污染成手动节奏）",
            manualBlock.contains("onPeriod(")
        )
    }

    /** M-2：手动会话终结/取消时必须中止冲刺窗口（控制器跑在自己的 scope 上）。 */
    @Test
    fun `手动会话终结时中止冲刺`() {
        val source = serviceSource()

        assertTrue(
            "M-2：会话终结路径必须调用 locationSprintRuntime.abort()",
            source.contains("locationSprintRuntime.abort()")
        )
        // 采集停止路径同样要中止
        val stopBlock = source
            .substringAfter("private fun stopCollection()")
            .substringBefore("\n    }")
        assertTrue(
            "停止采集必须中止冲刺窗口",
            stopBlock.contains("locationSprintRuntime.abort()")
        )
    }

    /**
     * 本次新增的接线必须放在 `startForeground` **之后**。
     *
     * 被动监听要经 `LocationManager` 注册，可能较慢；若排在 `startForeground` 之前，
     * 会挤占 `startForegroundService()` 之后进入前台的时限。
     *
     * 注意：**不**要求把 startForeground 提到权限检查之前 —— 那个顺序是既有约定
     * （由 `AndroidV2CollectionControlContractTest` 与 `ForegroundLocationServiceTest`
     * 明确守住），属本工单范围外。
     */
    @Test
    fun `新增接线位于 startForeground 之后`() {
        val source = serviceSource()

        val foreground = source.indexOf(
            "startForeground(LocationNotificationRenderer.NOTIFICATION_ID, notification())"
        )
        val automaticRuntime = source.indexOf("initializeAutomaticRuntime(settings)")
        assertTrue("必须都能找到", foreground > 0 && automaticRuntime > 0)
        assertTrue(
            "被动监听/冲刺接线（initializeAutomaticRuntime）必须在进入前台之后",
            foreground < automaticRuntime
        )
    }

    /**
     * 循环等待**永不超过 30 秒**（行为断言，不只扫源码里的常量）。
     *
     * 独立 review（round 4）指出：改写后的契约测试只要求源码里出现常量与调用，
     * 把等待改成 60 秒仍然全绿。这里直接断言计算结果。
     */
    @Test
    fun `循环等待永不超过三十秒`() {
        assertEquals(
            "上限内的建议值原样采用（45 秒档在 30 秒唤醒点 → 等 15 秒）",
            15_000L,
            ForegroundLocationService.resolveLoopWaitMillis(15_000L)
        )
        assertEquals(
            "建议值超过上限时收敛到上限（不得放长）",
            30_000L,
            ForegroundLocationService.resolveLoopWaitMillis(60_000L)
        )
        assertEquals(
            "没有建议值时用上限",
            30_000L,
            ForegroundLocationService.resolveLoopWaitMillis(0L)
        )
        assertEquals(
            "异常大值也收敛到上限",
            30_000L,
            ForegroundLocationService.resolveLoopWaitMillis(Long.MAX_VALUE)
        )
    }

    /**
     * R5-1（Critical）：状态页必须**真的把冲刺概况传给渲染函数**。
     *
     * 独立 review round 5 发现 `StatusCenterScreen` 收了 `sprintSummary`
     * 却没传给 `StatusCenterContent`，页面永远显示默认的「暂无」——
     * 看起来像「没有采集数据」，实际是没接线。
     */
    @Test
    fun `状态页把冲刺概况传给渲染函数`() {
        val screen = repoSource(
            "src/main/java/com/pim/app/ui/status/StatusCenterScreen.kt"
        )

        assertTrue(
            "R5-1：调用 StatusCenterContent 时必须传 sprintSummary",
            screen.contains("sprintSummary = sprintSummary")
        )
        assertFalse(
            "R5-1：SprintSummary 不得再有默认值（漏传会静默显示「暂无」）",
            screen.contains("countDisplay = com.pim.app.location.sprint.SprintCountDisplay.Empty\n    ),")
        )
    }

    /**
     * R5-2：手动会话终结只在**取消**时中止自己发起的冲刺窗口。
     *
     * 无条件 abort() 会在手动正常跑满 30 秒时掐掉即将写完的窗口，
     * 让「已执行」记录永不落台账（操作卡 §6 正是查这条）；也可能取消
     * 自动采集循环正在跑的窗口并重置周期锚点。
     */
    @Test
    fun `手动终结只在取消时中止手动窗口`() {
        val source = serviceSource()

        assertTrue(
            "R5-2：只有取消分支才中止手动冲刺窗口",
            source.contains("AcquisitionPhase.Cancelled") &&
                source.contains("abortManualWindow()")
        )
        assertFalse(
            "R5-2：终结路径不得无条件 abort()（会掐掉自动循环的窗口与已执行记录）",
            source.contains("locationSprintRuntime.abort()\n            // 本实例拥有的会话已终结")
        )
    }

    /** AC-14.1：被动监听随采集服务启停。 */
    @Test
    fun `被动监听随服务启停`() {
        val source = serviceSource()

        assertTrue(
            "AC-14.1：采集运行期必须启动被动监听",
            source.contains("passiveLocationCoordinator.start(")
        )
        assertTrue(
            "AC-14.1：停止采集必须停掉被动监听（停止后不再产生被动点）",
            source.contains("passiveLocationCoordinator.stop()")
        )
    }

    /** AC-14.2：被动计数必须**周期性**落台账（不能只在停止时写）。 */
    @Test
    fun `被动计数周期性落台账`() {
        val source = serviceSource()

        assertTrue(
            "AC-14.2：必须有周期刷新任务（否则服务被杀时分母丢失）",
            source.contains("passiveCounterFlushJob") &&
                source.contains("passiveLocationCoordinator.flushWindow()")
        )
    }
}
