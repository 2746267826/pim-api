# 固定笔记材料的持久分段 ASR

更新日期：2026-10-29。本片在已上传的不可变材料版本上实现任务、逐片进度与恢复，已随#520合并；普通账号授权与安全 destinations 已在#528合并到 API master `a3b35dc`，Release`api-v26.10.995`保留为#528历史；P0 API #529现已在`c5ff06fe`完成PR/master CI与Release[api-v26.10.997](https://github.com/2746267826/pim-api/releases/tag/api-v26.10.997)。GitHub Release不等于production，生产仍NOT-VERIFIED。Android #48另行接入存储上传；Android ASR PR[#51](https://github.com/2746267826/pim-android/pull/51)已普通MERGED exact head`8b1d091`→`93d934379b9a0e10318a9369e4d9d80a0c47e5e2`，tree`6294c2ab...`与test source相同；CI40874958748全SUCCESS/CLEAN，QA FINAL33/33PASS0FAIL，App source`1eaa`/sha`001546`不变，2024JVM同product lineage保留。Source组件3按同源R5 lineage另记，不拼成36 PASS。Windows bot #37 masterCI/Release成功；Android bot #49 Release`android-v26.10.1112`历史，P0#50 current Release`android-v26.10.1116`。完整记录/独立分析、人工修订保护、P2 UI余项、流式和真实课堂质量仍未交付。准确提交和证据以各片PR及[唯一总路线图](../product/native-notes-roadmap.md)为准。

## 使用入口与授权

管理员进入“设置 → AI 设置 → 笔记录音任务”，配置全局共享模型/运行时。普通账号只能处理自己的固定材料/任务，通过 `GET /api/v1/native-notes/asr-tasks/destinations` 选择管理员已配置并启用的共享转写目标，核对实际地址/预设/语言并明确同意；目录为空时先由管理员配置，不提供普通账号 BYOK 或 admin CRUD。owner 隔离约束材料/任务/调用账号，不表示模型配置有 owner。无材料时仍显示空态；系统不会因上传自动发起处理。任务、列表、详情、停止和恢复重新校验 owner、账号状态和固定版本。Android PR#51已普通MERGED`93d93437`，CI40874958748全SUCCESS/CLEAN，QA FINAL33/33PASS0FAIL；Source组件3按同源R5另记。API current Release `api-v26.10.997`已发布，生产仍未验证。

任务固定 owner、材料版本、输入/清单 SHA256、模型 ID/revision、厂商预设版本、实际操作地址和语言。后续编辑/新上传版本、默认模型切换均不把原任务改成另一份材料。配置修改或账号停用/失去权限会阻止后续发送；切换目标需要新建明确授权的任务。

## 分片和覆盖

复用 Android 原生材料的 PCM WAV（16kHz、16bit、单声道）。按照固定输入中的录音/分块顺序遍历所有音频；每块再按最多 960,000 样本（一分钟）拆成有效 WAV，最后一片保留实际长度，零样本分块不产生空调用。原录音不修改、不经过有损重编码，也不套用单片入口的4MiB上限限制整堂课。

每片保存录音ID、产品录音段/分块号、源文件索引/路径/摘要、源内偏移、录音内偏移、全篇拼接时间轴偏移及样本数。偏移全部以样本计数；显示时间才换算。暂停空隙不计入有效录音时长；拼接时间轴不是墙上时间，也不是模型提供的逐字时间戳。每次新外发前重新校验版本指纹、原文件完整摘要和 WAV 格式，以有界范围读取构造分片，不把整条大文件装入内存。

进度按成功片段的样本总数计算。失败片不计入成功覆盖，即使后面的片成功也能看见缺口。逐片页面保留实际转写、全部尝试与调用记录入口；空转写明确提示试听，不将它当作语音质量通过。

## 持久调度与恢复

复用现有 Hangfire/PostgreSQL：每分钟扫描固定任务检查点，向既有后台队列派发工作。检查点位于 `NativeNotes:StorageRoot/asr-tasks/<owner>/<task>.json`，与材料共用持久卷。原子替换前 flush 到磁盘；账号状态锁保护创建/更新，独立文件执行锁跨进程排除同任务并跑。一次执行最多64片，后续扫描继续；不另建通用任务平台或工作流引擎。

HTTP 请求只保存意图。前端丢失响应后保留同一任务标识，先查已有任务，或用原标识再次保存；变更输入不能复用原标识。后台身份直接来自持久化授权，不伪造 HTTP 登录上下文；使用内部 owner 方法复用原有 SDK、并发门限、原音频保存、实际请求/回复与用量记录。

| 窗口 | 恢复行为 |
| --- | --- |
| 任务保存、尚未外发即重启 | 重开相同计划和请求标识，继续未发送片 |
| 本片为 Running，调用已保存成功、检查点未更新 | 按 owner + 请求标识核对调用记录，补记进度，不重复外发 |
| 调用仍为 Running | 等待原执行期限；不分配新请求标识 |
| 执行过期但结果未知 | 记为未完整确认，保留旧记录；不自动重发此片 |
| 超时、失败、未完整或未发送 | 其余成功片保留；用户选择具体失败片、核对原目标并确认可能重复计费后恢复 |
| 停止任务 | 快速持久化停止标记，不再发送下一片；已经发送的片仍核对并保存结果，不保证撤销上游费用 |
| 已删除/改动材料、配置变化、权限失效 | 停止后续发送，保留已有记录和转写 |

恢复操作同时绑定 retryId、任务 revision、分片集合和原目标，同一恢复标识绑定不同输入返回冲突。成功/执行中分片不能选择重跑。每次明确重试分配新的调用标识，旧尝试仍可核对；原结果未知时明确可能重复计费。页面刷新可核对已有回执，不能用“没有收到回复”推断模型没有执行。

## 接口与资源边界

唯一契约包含 `/api/v1/native-notes/asr-tasks` 下的任务创建/列表、`sources` 固定版本选择、`destinations` 普通账号安全目标、单任务、分页 `segments`、`stop` 和 `retry`，与Web类型同片从实际Test API导出生成。#528把 contract SHA256 `eff87d962f5a935148c7f74ef1d73793d534719c4493397fd8a8247567d644c0` 合并进 `a3b35dc`；Android bot #49 merge `e629febe`、Windows bot #37 merge `94384c9e`，准确PR CI全SUCCESS。Android #48只启用四个材料存储operation；Android PR#51已包含 bot官方generated contract、source semantics fix与QA assets，不手写猜测接口；exact head`8b1d091`/sha`67f0`的CI40874958748全SUCCESS/CLEAN，并已普通MERGED`93d93437`。

当前每账号最多200任务、10个创建时待处理任务；单任务最多5000片、50次恢复意图、每片最多10次尝试，分片每页最多20条。现有 SDK 总并发4、ASR并发2、单片时限/原音频默认文件来源容量仍生效。没有任何同篇产品录音时长新承诺；任务操作受当前资源限额约束。任务历史归档/删除与文件回收尚未提供产品入口，不自动删除用户记录来掩盖限额。

后台未配置/显式停用时，任务可以保存，但页面明确显示后台调度不可用，不宣称正在执行。无队列客户端时不注册派发器，开发模式的容器启动校验仍须通过。当前标志反映配置而非实时工作节点健康；后台故障需结合现有系统状态排查。任务检查点、材料卷、AI调用/音频数据库及默认文件来源需一致备份。本批沿用单个持久本地文件系统的部署边界，不声称支持无共享卷多实例、分布式文件锁或真实断电恢复。

## 验证与剩余范围

永久回归使用隔离 Test API/JWT、真实本机 SDK HTTP、内置文件存储与合成 PCM；涵盖精确拆分/字节覆盖、多个录音/产品段、100/180分钟合成材料、失败片恢复、同任务互斥、停止、任务重开/回执恢复、未知结果、配置/权限/原材料保护和所有者隔离。#528准确head另覆盖管理员配置的共享目标安全目录、普通账号消费、非owner任务/停用/配置变化拒绝与任务授权快照；QA host 实际经 admin API 建立合成共享模型，再以 Alice 消费，不是 DB 种子私有 model。完整PR CI测试6122通过/15跳过。早期长时fixture的材料摘要不一致失败保留，修正fixture序列化，不放宽材料完整性校验。

Web 有明确同意、丢失响应核对、只选失败片恢复的交互测试；`web/client-web/scripts/qa-note-asr-tasks.mjs` 使用构建产物和隔离合成接口验证桌面/390px窄屏空态、部分成功和恢复，无外部网络请求。各项最终执行结果见PR，合成API页面不等于真实后台调度或模型质量验收。

Android PR#51已普通MERGED exact head`8b1d091`→`93d934379b9a0e10318a9369e4d9d80a0c47e5e2`，tree与test source相同；产品App source`1eaa`/sha`001546`不变，current test source`8b1d091`/R8 sha`67f0`仅test helper变化。CI40874958748全SUCCESS/CLEAN；QA FINAL33/33PASS0FAIL：phone12+narrow12+tablet9，narrow ASR6 `OK6 @20:39:58`、Legacy3 `OK3 @20:41:09`、tablet ASR6 `OK6 114.559s @20:44:47`。Source组件3为同源R5 lineage，不冒称R6/R8重跑，也不拼成36 PASS。历史旧失败逐条保留，包括R7 narrow 3/6 selector fail及`18ce5` case3/2、`5da86` case3/3、`b97/8bff6` case3/3、`577/e0816` case4/2、`R5test220a6` case5/1 fail。P2 UI余项：窄Legacy四字段/取消确认实见，说明尾部截于“拒绝”、浅过渡帧不足，fullBody NOT-VERIFIED。仍待：master CI/release、真实Hangfire进程崩溃/重启与部署恢复演练、old queue upgrade E2E、真实100/180分钟课堂和分片边缘质量、长时后台/OEM、流式/中断部分输出、完整结果界面、全材料双产物/人工修订保护、旧调用迁移与LiteLLM清理及OPPO验收。GitHub Release不代表生产升级。
