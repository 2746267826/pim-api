# 统一回收站 API/Web 边界

## 覆盖范围

`/api/v1/recycle-bin` 聚合当前已确认可安全恢复或永久清理的对象：

- **原生笔记**：软删除只把笔记移出笔记库和全局搜索，原稿、材料版本、ASR 检查点和派生结果仍可按已知 `versionId`/任务标识只读查看；恢复后仍可打开原版本。软删期间不能新启动 ASR、派生、处理编排、Batch 或任务建议提取，后台任务也不会继续外发或写入新结果。永久删除会清理材料、ASR/派生文件、处理编排记录、ASR routing attempt/lease/ack receipt/segment evidence/Batch 行（含 `ResultJson`、转写文本和受保护凭据）、任务建议的源正文/标题/描述及关联 AI 调用与音频/图片记录。
- **内置文件与文件夹**：沿用内置存储目录清单的软删/恢复；永久删除同时移除目录清单和已无其他目录项引用的内容 blob。删除文件夹会连同其子树进入回收站。
- **日程、任务、日历、任务本**：复用既有日历回收站规则，包括冲突预览、恢复为副本、父本必须存在等约束；统一 API 只增加跨域列出、批量回执和永久清理。

统一列表提供笔记/文件/任务/日历四组计数、搜索、删除时间范围和分页。批量恢复/清理最多 200 项，重复项按 `(domain, id)` 去重，并逐项返回 `success`、`failed` 或 `source_pending`。

## 并发、幂等与错误恢复

- Web 为每个项目回传列表中的 `deletedAt` 作为 `expectedDeletedAt` CAS 令牌；服务端在对应领域锁内重新读取并校验后才执行恢复或永久删除。令牌过期返回 `40901`，客户端需刷新回收站。
- 笔记使用账号+笔记 advisory lock，内置文件使用账号+来源 advisory lock，日历/任务沿用任务簿与任务行锁；外部副作用在锁内复核状态。
- 笔记永久清理先把仍在执行的 ASR attempt、有效租约、未停止的排队 attempt 或非终态 Batch 任务作为 `40901` 阻断；清理可重试。软删笔记的后台任务在发现笔记已进入回收站时会停止检查点，避免继续产生新结果，同时让用户可以选择永久清理。
- **中断回执对账 / crash-window reconciliation**：进程可能在逐项提交破坏性工作之后、批次 `CompleteOperationAsync` 写入回执之前退出。租约过期后同一 `operationId` 重试会先按持久状态对账，而不是把整批重新当作首次执行：笔记复用自身 `completed`/`db_committed` purge intent（并由领域服务完成 material-ledger tombstone 收尾）；内置文件与日历项复用与删除同一次 PostgreSQL `SaveChanges` 提交的 `completed` per-generation 回执。已证明完成的项返回 `success`（“无需重复提交”），其余项照常执行。只有此前确实领取过同一 `operationId` 且 payload 相同的 operation 才进入此路径；没有持久回执证明的缺失对象仍返回 `40401`，不会伪报成功。
- 笔记清理意图与 operation 回执的完成/释放都校验处理令牌；operation 在批次逐项执行前续租，note purge 在持久化清单前后、删除文件前和最终 PostgreSQL 提交前续租/复核租约。租约过期后旧请求不能续租、完成或清除新回执。恢复在删除台账 tombstone 前拒绝活跃清理租约；只有已过期的租约才可被恢复请求条件回收，旧 worker 随后续租会失败。恢复成功会废弃尚未完成的旧清理意图；若预检失败后用户已恢复笔记，旧 `expectedDeletedAt` 或无令牌重试只会返回 `40901`，不会删除 active note 的材料、head 或 ASR/派生数据。
- 收件箱任务允许 `calendar_id` 为空；永久清理按任务行自身定位，不再把空日历误判为“任务不存在”。重复提交仅在同一 `operationId` 的完整回执可重放时返回幂等成功。
- Outlook 绑定日历永久清理先删除 `outlook_calendar_bindings`，再删除日程、任务和日历，避免 `Restrict` 外键阻塞；Outlook 连接本身保留。过期软删数据的 GC 路径也先清理绑定行。
- 重复恢复已恢复项目仍返回幂等成功。永久清理只有在域级回执（笔记 purge intent，或文件/日历 per-generation purge 回执）已证明完成，或同一 `operationId` 的完整回执已持久化时才返回幂等成功；缺失的文件/日历对象若没有匹配的回执，仍返回 `40401`，他人对象或已恢复对象返回 `40401`/`40901`。回执按 `(owner, domain, item, generation)` 精确匹配 `deletedAt` 世代，物化相同的 id 在不同回收周期不会被混用。
- 批次允许部分成功；网络断开时 Web 不把任何项目标记为成功，而是显示“结果未确认”，提示刷新后核对。
- 内置文件的内容 blob 只在目录清单删除前清理，且仅删除没有其他目录项引用的键；失败后仍保留目录项以便重试。文件/文件夹子树永久清理显式处理 `file_index_jobs.version_id` 与 `file_items.current_version_id` 两条 `Restrict` 外键：先删除索引任务、再断开当前版本引用、再删除版本，最后在同一 `SaveChanges` 中删除 `file_items` 行并写入 per-generation 回执，因此真实 PostgreSQL 上不依赖触发器/EF 语句顺序。

## 永久清理后仍保留的数据

“永久删除”指该对象不再出现在回收站、领域列表、搜索入口，且本 PR 明确清理的材料/转写/派生/blob 已删除；它**不是**所有历史记录的彻底擦除。以下数据仍会保留，产品尚未给出保留期限或清理机制：

- **标题与对象标识 / Titles and object ids**：`recycle_bin_operations.receipt_json` 保存批次逐项回执（含标题），`recycle_bin_purge_intents` 保存每个已清理世代的 `title`、`item_id`、`generation`、阶段与计数。两者当前都没有定期清理任务，因此永久清理后标题仍长期存在。
- **审计日志 / Audit logs**：`audit_logs` 保留 `recycle_bin.note.purge`、`files.recycle_bin.purge`、`calendar.recycle_bin.purge` 的记录（资源 id、动作、计数、时间等），不含笔记正文。
- **Outlook 快照 / Outlook snapshots**：日历永久清理删除 `outlook_calendar_bindings`、日程、任务和日历行，但保留 `outlook_connections`；`outlook_writeback_operations`（`result_json`、`previous_draft_json`、请求 hash）以及 `sync_conflicts`（`pim_snapshot_json`、`external_snapshot_json`）不随日历/日程删除，仍可能包含事件标题、时间、描述等快照内容。`outlook_operation_executions.payload_json` 也可能保留类似草稿。
- **非笔记 AI 请求日志 / Non-note AI request logs**：笔记永久清理只删除属于该笔记 ASR/派生来源及 contributor 明确登记的 AI 日志；文件 AI、聊天、模型测试等其它 `ai_request_logs` 不因回收站清理而删除。是否保留 prompt/响应正文取决于各自日志写入策略。
- **未被引用的历史材料 tombstone**：笔记材料 ledger 的 `DeletedNotes` 记录在被删除笔记仍处于回收站或清理进行中时承载恢复锚点；成功完成后才移除。若进程在完成回执前退出，该 tombstone 会保留到同一 `operationId` 重试收尾，或由人工排查处理。

保留期限、清理任务与“彻底擦除”承诺目前**没有产品依据**，在对应需求确认前不得声称已实现；相关验收项应标为待决/未验证。

## 明确不支持或不由 PIM 冒充完成的边界

- **旧“快速记录”** 的归档/恢复仍是独立能力，不并入此统一回收站。
- **提醒、习惯、报告、位置、设备等其它对象** 目前没有确认过的统一软删除语义，不出现在列表中。
- **OneDrive / Google Drive** 的远端内容不由 PIM 本地恢复或永久删除。统一列表可以显示 PIM 索引中的已删项，并在恢复/清理时返回 `source_pending`，用户需到对应网盘网页端确认。
- 统一回收站不提供“恢复为副本”的笔记或文件实现；批量请求中选择该选项时，笔记和文件会明确失败，而不是静默改回原位恢复。
- 本仓库只交付 API 与 Web 入口；Android/Windows 原生回收站页面、真实浏览器端到端、真实外部网盘回收站行为和 production 均需单独验证。

## 相关入口

- Web：`/recycle-bin`（系统导航、设置中心、文件页、原生笔记页均可达）；旧 `/settings/recycle-bin` 重定向到统一页面。
- API：`GET /api/v1/recycle-bin`、`POST /api/v1/recycle-bin/restore`、`POST /api/v1/recycle-bin/purge`。
