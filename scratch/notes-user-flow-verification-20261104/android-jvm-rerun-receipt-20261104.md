# Android JVM Rerun Receipt / Android JVM 重跑回执

- Test target / 测试目标: Android Draft PR #70, exact HEAD `3f6b1d4`.
- Command / 命令: `:app:testDebugUnitTest --no-daemon --stacktrace`.
- Environment / 环境: Android Studio-provided JDK; installed Android SDK; Gradle 8.14. This was a local rerun, not CI.
- Result / 结果: 193 XML reports under `src/client-android/app/build/test-results/testDebugUnitTest`; 2,256 tests, 0 failures, 0 errors, 0 skipped.
- XML location / XML 路径: `src/client-android/app/build/test-results/testDebugUnitTest`.
- Execution time / 执行时间: 2026-11-04 (Asia/Shanghai); exact completion time was not included in the run summary. Receipt recorded at 2026-11-04 21:10 Asia/Shanghai.
- Prior discrepancy / 旧结果差异: An earlier summary reported 14 failures out of 2,256, but its original XML was missing. The exact-HEAD rerun did not reproduce those failures; their cause is unknown. Do not classify the old failures as pre-existing or as regressions without the missing evidence.
- Device scope / 设备范围: ADB successfully listed devices but found no connected device. Connected UI tests were not run and remain `NOT-VERIFIED`; this rerun does not establish device, CI, provider, or end-to-end cross-device acceptance.
