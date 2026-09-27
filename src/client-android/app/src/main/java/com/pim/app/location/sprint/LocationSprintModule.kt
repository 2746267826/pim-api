package com.pim.app.location.sprint

import dagger.Binds
import dagger.Module
import dagger.hilt.InstallIn
import dagger.hilt.components.SingletonComponent
import javax.inject.Singleton

/**
 * 冲刺与被动定位的依赖绑定（WO-ANDROID-GATE-20260926 REQ-8 / REQ-14）。
 *
 * 两个台账都是「可替换的外部世界读数」的写入口，绑成接口是为了让窗口判定与开关语义
 * 能在 JVM 单测里用注入的假件复现（沿用 `ForensicModule` 的约定）。
 */
@Module
@InstallIn(SingletonComponent::class)
abstract class LocationSprintModuleBinds {

    @Binds
    @Singleton
    abstract fun bindSprintLedger(impl: LocationSprintLedger): SprintLedgerPort

    /**
     * 冲刺使用**独立**的注册源（AC-5.6）：同一个底层 provider，
     * 但注册与取消与主流完全分开。
     */
    @Binds
    @Singleton
    abstract fun bindSprintUpdateSource(impl: FusedSprintUpdateSource): SprintUpdateSource
}
