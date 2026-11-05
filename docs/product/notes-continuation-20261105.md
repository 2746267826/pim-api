# Notes continuation — 2026-11-05

## 当前阶段

本轮前三步已完成本地候选收口，不扩张为 M0–M5 全部完成：

1. API #560/#561 收口为单一候选，采用 discovery、current、clone 三个端点；processing fixed-version 保留。
2. API 主线契约后，推进 Android 官方 fetch/generate/freshness 与精确 CI。
3. 用同一 Android production builder 的合成混合 bundle，走 API/Web 保全、续写与冲突 replay。

## 已知事实

- API master 已真实联网核实为 `622382f4`。
- #561 Draft 远端 `faa6ab` 的旧 Gate PASS 不覆盖本地候选；#560 `c20dac` 有冲突；当前推荐 #561。
- `pim-api` 脏树不动。
- API 原 `27` 项加同一样本 `3` 项 Store/TestServer，共 `30 PASS / 0 fail / 0 skip`。
- Android #70 父 `3f6b1d4` freshness 为红，缺 3 paths / 4 schemas。
- Android 精确结果来自代码 `dfa9558` 加 3 个 test-fixture 补丁：全 App JVM `193 XML / 2258 tests / 0 fail / 0 error / 0 skip`，`compileDebugKotlin` 均 `BUILD SUCCESSFUL`；receipt 为 `scratch/notes-continuation-20261105/android-jvm/receipt.txt`，含 3 个 source hash。旧 `2256` 不沿用。fixture `inputSha` 为 `67d62e...930b`，manifest 为 `31389e...b5d8`，共 9 文件，含 2 个 WAV。
- Web 完整 `68 files / 586 PASS`；最终类型/ctor 小修后的定向 raw/model/golden `14 PASS`，build PASS，lint PASS。raw `320.0` 与 known-null metadata 已修复，普通用户 UI 未新增配置。
- Android 先前全量 JVM `2258` + compile PASS；另有生产 `CloudNoteRepository` refresh -> restore 读回真正 Web 续写的 `1 case PASS`，不相加写成 `2259` 全套。原/新图、ink、录音 hash 相同，已有本机 `noteId` 拒绝覆盖。
- 原 Android `inputSha` `67d62e...930b` / manifest `31389e...b5d8`；Web `inputSha` `19916b...77c3` / manifest `5cbf921733156fe44daba52ce291f09fcc85671e64ce6a032765810f51cf492a`。
- 收据：`scratch/notes-continuation-20261105/verification.json`；两视口报告：`scratch/notes-continuation-20261105/browser/run-2026-11-05T05-44-31-260Z/report.json`，dist 离线 + API 合成 mock，no errors/overflow。

## 交付边界

Scope 4 真实 provider、计费、物理设备、OEM、生产部署与生产验证由用户接手，不阻塞本地候选。稳定 45 IDs、AC1–19、M0–M5 与 #516 范围保持不变。

## 下一次回写

本地候选已冻结，交回父提交 #561；master `622`、远端 `faa` 旧 Draft 仅作当前基线。远端 CI/merge 尚待推进，CI 通过后再推进主线与 Android 官方刷新；在父提供最终事实前不写已合并。
