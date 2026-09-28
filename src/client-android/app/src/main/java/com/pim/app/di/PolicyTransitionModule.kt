package com.pim.app.di

import android.content.Context
import android.content.SharedPreferences
import com.pim.app.location.LocationQueueRepository
import com.pim.app.location.PolicyTransitionRecorder
import com.pim.app.location.PolicyTransitionWriteFailureSource
import com.pim.app.location.PolicyTransitionWriteFailureStore
import com.pim.app.status.PolicyTransitionHistoryRepository
import com.pim.app.status.PolicyTransitionHistorySource
import dagger.Binds
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import javax.inject.Qualifier
import javax.inject.Singleton

@Qualifier
@Retention(AnnotationRetention.BINARY)
annotation class PolicyTransitionPreferences

/**
 * 界面层只依赖只读接口（`PolicyTransitionHistorySource` /
 * `PolicyTransitionWriteFailureSource`），生产实现仍绑定到真实 Room / SharedPreferences。
 */
@Module
@InstallIn(SingletonComponent::class)
abstract class PolicyTransitionReadModule {
    @Binds
    @Singleton
    abstract fun bindPolicyTransitionHistorySource(
        impl: PolicyTransitionHistoryRepository
    ): PolicyTransitionHistorySource

    @Binds
    @Singleton
    abstract fun bindPolicyTransitionWriteFailureSource(
        impl: PolicyTransitionWriteFailureStore
    ): PolicyTransitionWriteFailureSource
}

/**
 * WO-ANDROID-POLICY-TRANSITION-20260928 REQ-1：策略切换写入依赖的**生产装配**。
 *
 * 这是本工单修的核心断点——缺陷版本把写入委托给一个从未被赋值的**可空** lambda 字段，
 * 字段为空时静默跳过，一个半月零写库而 CI 全绿。现在写入依赖是**非空**的
 * [PolicyTransitionRecorder]，由这里绑定到真实仓库 [LocationQueueRepository]
 * （它再写 `mobile_location_policy_transitions` 表）。
 *
 * 删掉这个绑定就是 Hilt 的编译期装配错误——不会再出现「能编译、能跑、但静默不写」；
 * 运行期若依赖未注入，`ForegroundLocationService` 会以「装配错误」显式失败（AC-1.5）。
 *
 * 装配实现提取成 [PolicyTransitionProductionWiring.recorder]，好让**真实接线测试**
 * （`PolicyTransitionRecordingWiringTest`）执行**同一段**生产装配代码，而不是另写一份
 * 等价的测试替身（REQ-2「不注入写入替身、走生产初始化路径」）。
 */
@Module
@InstallIn(SingletonComponent::class)
object PolicyTransitionModule {
    @Provides
    @Singleton
    fun providePolicyTransitionRecorder(
        repository: LocationQueueRepository
    ): PolicyTransitionRecorder = PolicyTransitionProductionWiring.recorder(repository)
}

/** 生产装配的唯一实现处：Hilt 模块与真实接线测试共用。 */
object PolicyTransitionProductionWiring {
    fun recorder(repository: LocationQueueRepository): PolicyTransitionRecorder = repository
}

@Module
@InstallIn(SingletonComponent::class)
object PolicyTransitionPreferencesModule {
    @Provides
    @Singleton
    @PolicyTransitionPreferences
    fun providePolicyTransitionPreferences(
        @ApplicationContext context: Context
    ): SharedPreferences {
        return context.getSharedPreferences("pim_policy_transition", Context.MODE_PRIVATE)
    }
}
