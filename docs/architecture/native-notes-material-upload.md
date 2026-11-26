# 原生笔记固定材料收包

本文件说明 `NativeNotes` 收包实现及部署边界，功能范围与缺口继续以 [总路线图](../product/native-notes-roadmap.md) 为唯一索引。收包接受完整原材料；ASR、整理和分析尚未在此实现。

## 提交流程

1. Android 先固定现有 schema-v1 的 `NoteMaterialInput` 与 `NoteMaterialManifest`。`POST /api/v1/native-notes/versions` 提交版本/笔记 ID、所基于的服务器版本、原始输入 JSON、材料清单 JSON及本次上传同意。
2. 服务端对照原稿块和每个录音会话推导所需文件，校验版本身份、输入摘要、原稿/导出 Markdown/录音清单摘要与大小、照片关联、向量尺寸和每个音频分片描述。缺少照片、手写图、录音或夹带未知文件均不接受为完整清单。录音分片可声明 `format`/`mimeType`（见“音频格式与解码边界”），未声明时按旧 WAV 兼容。JSON 原文和未知扩展字段保留，未知块类型明确拒绝，不能假装已经完整兼容。
3. 客户端按返回的固定 `partSizeBytes`，用 `PUT .../files/{fileIndex}/parts/{partIndex}?sha256=...` 上传二进制分块。每块独立校验长度与 SHA-256，重复相同内容幂等；同位置不同内容返回 409。状态返回已收分块，新进程可继续上传。未完成版本可清除某块后重传，或取消整个上传。
4. `POST .../{versionId}/complete` 流式校验全部文件的完整摘要、长度、WAV 头或 Ogg Opus 容器结构、声明样本数和图片类型签名。全部齐备后才发布版本；未齐备返回 409，损坏或格式不符返回 400。发布状态仅为 `Uploaded` 或 `Conflict`，不表示 ASR 或 AI 已开始。
5. 保存校验 `baseVersionId`。当前版本已改变时，保留新旧双方全部材料，新版标为 `Conflict`；不能自动替换当前版本。用户明确选择后，用 `POST .../{versionId}/promote` 携带重新核对的 `expectedCurrentVersionId`，再次校验后才更新当前版本。原有版本仍可读取。

`GET /api/v1/native-notes/upload-limits`、单版本状态和笔记版本列表均要求现有 JWT 鉴权。所有磁盘位置只由已验证用户 ID、版本 ID及整数文件/分块索引生成；清单中的路径只用于核对与展示，不作为磁盘访问路径。文件下载由服务端流式提供，返回预期 SHA-256；不发私有文件 URL、第三方授权直链或账号凭据。

## 音频格式与解码边界

录音会话的每个 `chunks[]` 可带可选 `format` 与 `mimeType`。两者都省略时按旧客户端兼容为 WAV；显式值必须一致，未知值或互相矛盾会在创建上传会话时以 `40001` 拒绝，不会静默当 WAV。

| 声明 | 接受值 | 固定材料路径 |
| --- | --- | --- |
| `format` | `wav`、`opus`（大小写不敏感） | 由服务端推导 |
| `mimeType` | `audio/wav`、`audio/x-wav`、`audio/ogg`、`audio/opus`；允许 `; codecs=...` 参数 | 由服务端推导 |
| WAV 路径 | 旧格式，无额外声明 | `recordings/{recordingId}/s{segment:D4}-c{index:D6}.wav` |
| Opus 路径 | Ogg 容器，编码必须为 Opus | `recordings/{recordingId}/s{segment:D4}-c{index:D6}.opus` |

Opus 分片的规范扩展名是 `.opus`，媒体类型是 `audio/ogg`；客户端本地文件叫 `.ogg` 不影响上传，清单必须使用服务端推导的 `.opus` 路径。单分片必须大于 0 字节且不超过 64 MiB，声明样本数必须大于 0；每份清单仍受 `NativeNotes:MaxFiles`（默认 20,000）和单版本/用户配额约束。同一 `segment`+`index` 只能声明一次，不能借不同格式重复计时间轴。

`chunks[].samples` 固定表示**解码后的 16 kHz 单声道样本数**，不是 Ogg Opus 内部的 48 kHz granule。Opus 分片必须：

- 是完整 Ogg 流，首包包含 `OpusHead`，声道数为 1；
- 有结束页（EOS），且最终 granule 减去 pre-skip 后能被 3 整除；
- 换算出的 16 kHz 样本数与清单声明完全一致。

这些条件在 `complete` 时校验，非法或截断文件返回 `40001`，不会发布为完整版本。Android 侧把录音分片调整为 15 秒只是客户端采集策略；服务端不把 15 秒写进协议或 ASR 计划，而是继续按声明样本数切片，单次 ASR 预算仍由服务端现有 60 秒常量（或已解析路由的预算）决定。15 秒对应 240,000 个 16 kHz 样本，服务端会按实际样本数处理，不假定分片长度。

原始 Opus/Ogg 字节按上传内容原样保存、下载和导出，不做有损再编码；WAV 用户和历史数据继续走原有直接读取路径。进入 ASR、Batch 或派生音频上下文前，服务端用 `ffmpeg` 把**整个 Opus 文件**严格解码为 16 kHz 单声道 PCM，再按请求的样本偏移包装成现有 WAV 输入，provider 不会收到压缩容器。解码输出字节数必须与声明样本数完全一致，否则以 `40001` 拒绝且不发送 provider 请求。FFmpeg stderr 只留在内存中丢弃，不写入日志、任务错误或客户端响应。运行镜像安装 `ffmpeg`；可用 `NativeNotes:FfmpegPath`（默认 `ffmpeg`）、`NativeNotes:AudioDecodeTimeoutSeconds`（默认 120 秒）和 `NativeNotes:MaxConcurrentAudioDecodes`（默认 2，范围 1–16）配置。解码器缺失、路径非法、超时配置越界或进程启动失败返回 `50303`（HTTP 503），任务进入可解释的阻断状态，WAV 不受影响；人工重试前应先修复运行环境。

长单文件不再按分片重复哈希、复制和从头解码。文件内容按 `sha256 + samples` 只解码一次，PCM 写入 `NativeNotes:StorageRoot/audio-cache/<sha256>-<samples>.pcm`，同一内容的并发读取由进程内单飞门闩串行化；后续分片只按偏移读取缓存。校验结果按不可变分块的长度与修改时间签名缓存，内容未变化时不重复计算全文件 SHA-256。解码前先把分块复制到 `NativeNotes:StorageRoot/audio-work` 下的随机私有文件，解码结束立即删除；崩溃残留超过 1 小时会在后续账号操作或解码时清理。缓存按 `NativeNotes:AudioCacheMaxAgeHours`（默认 24 小时）和 `NativeNotes:AudioCacheMaxBytes`（默认 2 GiB，且必须不小于单个最大解码文件 345,600,000 字节）清理，每 5 分钟最多执行一次，并在每次解码后强制执行；清理不会删除刚写入的当前缓存。单文件仍受 64 MiB 容器上限与 172,800,000 个 16 kHz 样本（约 3 小时）上限约束。

隔离测试覆盖格式白名单、MIME 参数与冲突、旧 WAV 默认兼容、Opus 容器结构/样本数/granule 边界、64 MiB 边界、15 秒样本切片、长文件一次解码与偏移读取、FFmpeg 并发上限、崩溃残留/缓存清理、解码失败安全信息、配置修复后的恢复，以及人工重试与在途自适应调用之间的 epoch 竞态。真实 FFmpeg 解码用例需要在 CI runner 上安装 `ffmpeg`；当前 workflow 未安装时会在 shard 日志中显式 `[SKIP]`，PR 同时记录 waiver。真实 Android/Windows 采集器、真实 Opus 编码器输出及第三方 ASR provider 的端到端链路尚未在本机验证；包含恶意压缩音频沙箱逃逸、FFmpeg 漏洞面或长期 DoS 的结论也不能由这些测试替代。

## 存储与限额

新业务按用户“上传到服务器”的流程使用服务器持久数据卷。版本清单/账本默认 Linux 路径为 `/data/pim/native-notes`，Windows 为服务账号 LocalAppData 下 `PIM/server/native-notes`；`NativeNotes:StorageRoot` 可改为绝对路径。新增分块使用文件系统的共享 `ManagedBinaryStore`，默认 `/data/pim/storage`（Windows 为 `PIM/server/storage`），与普通文件/暂存共享 20 GiB 用户字节额度但使用独立 `note-parts` 命名空间。旧版本 `.part` 保留读取与删除兼容，不自动迁移。文件系统新增无需云盘的保底来源；既有 QuickNotes 云盘默认/引用保持，未绑定云盘时新附件可使用内置盘。详见[内置存储说明](builtin-local-files.md)。现有容器 `/data` 卷覆盖两处默认目录；自定义目录必须另行持久化。服务账号须有读写权限，备份需同时包含账本、字节、索引与授权密钥。

| 配置/固定边界 | 默认值 | 说明 |
| --- | --- | --- |
| `partSizeBytes` | 1 MiB | 固定传输块；不改变用户录音段或本机音频分片 |
| `maxMetadataCharacters` | 每份 JSON 8,388,608 字符 | 输入与清单分别受限；JSON 请求另有 64 MiB HTTP 上限 |
| `NativeNotes:MaxVersionBytes` | 4 GiB | 单次固定版本的全部材料，不是整篇累计时长上限 |
| `NativeNotes:MaxUserReservedBytes` | 20 GiB | 该用户全部版本与待上传预留容量，包含请求元数据和账本开销 |
| `NativeNotes:MaxFiles` | 20,000 | 每份清单的文件数 |
| `NativeNotes:MaxPendingVersions` | 10 | 每用户未完成上传数 |
| `NativeNotes:MaxVersionsPerUser` | 2,000 | 每用户全部版本数 |
| `NativeNotes:UploadExpiryHours` | 168 小时 | 未完成上传到期后，在该账号后续操作时清理并释放预留容量 |
| `NativeNotes:FfmpegPath` | `ffmpeg` | Opus/Ogg 解码器路径；缺失或非法时 `50303`，WAV 不受影响 |
| `NativeNotes:AudioDecodeTimeoutSeconds` | 120 秒 | 单次 Opus 解码超时；越界配置按解码器不可用返回 `50303`，运行超时按 `40001` 拒绝且不发送 provider |
| `NativeNotes:MaxConcurrentAudioDecodes` | 2 | 进程内同时运行的 FFmpeg 解码数，范围 1–16 |
| `NativeNotes:AudioCacheMaxAgeHours` | 24 小时 | 解码 PCM 缓存保留时间，范围 1–720 小时 |
| `NativeNotes:AudioCacheMaxBytes` | 2 GiB | 解码 PCM 缓存字节上限；不得小于单个最大解码文件 345,600,000 字节 |
| 照片/手写派生图 | 32 / 40 MiB | 单文件固定格式上限；照片仍须与原稿关联 |
| Opus/Ogg 录音分片 | 64 MiB | 单文件上限；还须满足 Ogg Opus 结构、单声道和声明样本数 |

共享字节额度不足时会拒绝新分块并保留已收块，不因单独的笔记预留额度通过而保证全部可写；旧目录内容不计入共享字节额度，仍受笔记预留/版本上限及实际磁盘约束。已发布版本不因上传有效期自动删除。用户可删除一篇笔记的全部服务器材料；本机内容不被远程删除。删除意图先进入原子账本，文件清理中断后在下一次账号操作时继续。已经发布的材料不可通过“取消上传”或分块接口修改。

每用户通过磁盘事务锁串行修改，一次原子账本替换同时发布版本和当前版本指针。临时文件写盘同步后原子替换，未完成文件不作为完整收包。不同进程读取同一数据卷可恢复状态；此实现的部署前提是支持文件锁和原子替换的持久文件系统。尚未验收网络文件系统、多节点分散存储、真实掉电恢复或磁盘耗尽。没有全服务器总配额和后台定时扫描；每用户配额及按访问清理不能替代磁盘监控。

## 验证与后续边界

隔离测试覆盖大于 4 MB 的真实格式合成 WAV、重开续传、幂等、缺失/损坏拒收、并发配额预留、跨用户隔离、版本竞争、显式选择、删除意图恢复和限额调整。HTTP 测试使用真实 API 宿主、JWT 与隔离内存数据库。故障夹具模拟特定持久化窗口，不作为实际断电、长时录音、生产磁盘或 OPPO 真机证据。

Android #44与Windows #35已同步旧唯一契约；[Android #48](https://github.com/2746267826/pim-android/pull/48)接入正式笔记入口、明确存储同意及账号/服务器绑定的持久上传、暂停/恢复与同版本回执核对。API #528已合并普通账号ASR授权/destinations；Android ASR PR#51已普通MERGED`93d93437`，exact head`8b1d091`/R8 sha`67f0`，CI40874958748/master40877083407全SUCCESS，Release1123，QA FINAL33/33PASS0FAIL；Source组件3按同源R5 lineage另记。Android current Release`android-v26.10.1123`；bot #49 Release1112与P0#50 Release1116保留历史；Windows bot #37 masterCI/Release成功。准确提交/验收见[总路线图](../product/native-notes-roadmap.md)。真实课堂长时上传、进程/设备重启、Doze/OEM与无公网LAN仍未验收；全材料双产物及人工修订仍待开发。第三方模型同意与保留政策不由收包同意替代，上传不会自动调用模型。服务器冲突材料保全不等于跨端编辑或比较/解决UI已完成。
