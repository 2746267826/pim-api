package com.pim.app.location.service

import java.io.File
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-1 / AC-1.5：**生产装配的源码谓词守卫**。
 *
 * `PolicyTransitionRecordingWiringTest` 证明「装配好的写入依赖真的会落库」，
 * 但 Robolectric 单测跑不起真实 Hilt 图（仓库未引入 `hilt-android-testing`），
 * 所以「Hilt 图里到底把这个接口绑到了谁」需要另有一条断言守着——
 * 沿用仓库既有的源码谓词范式（`SprintServiceWiringTest`、`AndroidV2CollectionControlContractTest`）。
 *
 * 它守的是三件事：
 * 1. Hilt 模块确实提供了 `PolicyTransitionRecorder`，且绑到真实仓库（而不是空实现 / 又一个可空字段）；
 * 2. 采集服务声明了**非空**的 `@Inject` 写入依赖（缺陷版本是 `internal var ... ? = null`）；
 * 3. 决策变化路径真的调用了写入函数（字段存在但没人用 = 又一次静默断链）。
 */
class PolicyTransitionModuleWiringTest {

    private fun repoSource(relativePath: String): String {
        var current: File? = File("").canonicalFile
        while (current != null) {
            val candidate = current.resolve(relativePath)
            if (candidate.isFile) return candidate.readText(Charsets.UTF_8)
            current = current.parentFile
        }
        error("$relativePath not found")
    }

    private fun moduleSource(): String =
        repoSource("src/main/java/com/pim/app/di/PolicyTransitionModule.kt")

    private fun serviceSource(): String =
        repoSource("src/main/java/com/pim/app/location/service/ForegroundLocationService.kt")

    @Test
    fun `Hilt 模块把写入依赖绑到真实仓库`() {
        val module = moduleSource()

        assertTrue(
            "Hilt 必须提供非空的 PolicyTransitionRecorder",
            module.contains("fun providePolicyTransitionRecorder(") &&
                module.contains("): PolicyTransitionRecorder")
        )
        assertTrue(
            "绑定必须走生产装配函数并指向真实仓库",
            module.contains("PolicyTransitionProductionWiring.recorder(repository)") &&
                module.contains("repository: LocationQueueRepository")
        )
    }

    @Test
    fun `采集服务持有非空的写入依赖`() {
        val service = serviceSource()

        assertTrue(
            "写入依赖必须是 @Inject 的非空属性（不是可空 lambda + 静默跳过）",
            service.contains("@Inject lateinit var policyTransitionRecorder: PolicyTransitionRecorder")
        )
        assertFalse(
            "缺陷版本的可空写入字段不得复活",
            service.contains("policyTransitionWriter")
        )
    }

    @Test
    fun `决策变化路径真的调用写入函数`() {
        val service = serviceSource()

        val applyDecision = service
            .substringAfter("private fun applyDecision(")
            .substringBefore("\n    private fun ")

        assertTrue(
            "applyDecision 在决策变化时必须解析写入依赖并调用 writePolicyTransition",
            applyDecision.contains("policyTransitionDeduper.note(decision)") &&
                applyDecision.contains("resolvePolicyTransitionRecorder()") &&
                applyDecision.contains("writePolicyTransition(recorder, transition.fromMode, transition.decision)")
        )
        assertFalse(
            "写入异常不得再被空 catch 吞掉",
            applyDecision.contains("catch (_: Exception)")
        )
    }
}
