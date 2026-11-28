# 通知与推送 / Notifications and Web Push

## 范围

API 提供账号级站内通知偏好、设备级浏览器推送偏好与订阅、站内通知、SSE 实时事件流和持久化投递队列。当前接入的事件：

- `calendar.task.completed`：任务状态变为 `COMPLETED`（创建、更新、批量更新）。
- `calendar.reminder.due`：提醒到达 `scheduled_at`，且未因偏好被关闭。
- `note.transcription.completed`：笔记语音转写任务成功结束。
- `note.transcription.attention`：笔记语音转写失败或需要人工处理。
- `note.derived.completed`：笔记完整记录与独立分析任务成功结束。
- `note.derived.attention`：笔记整理结果不完整或需要人工复核。
- `notification.test`：设置页“发送测试通知”。

Android FCM 与 Windows WNS 不在本仓库范围内；本仓库只提供 Web Push 与 SSE。两者的真实设备/系统通道必须由各自客户端仓库验证。

### Q06 分类键

客户端筛选和展示使用以下稳定的 `categoryKey`，不要依赖内部 `eventType` 拼写：

| `categoryKey` | 来源 `eventType` | 当前切片状态 |
| --- | --- | --- |
| `notes.summary_ready` | `note.derived.completed` | 已接入扫描器与投递 |
| `notes.transcription_attention` | `note.transcription.attention` | 已接入扫描器与投递 |
| `notes.organizing_review` | `note.derived.attention` | 已接入扫描器与投递 |
| `uploads.attention_digest` | `upload.attention_digest` | 仅保留稳定契约；本切片没有上传失败生产者 |

`uploads.attention_digest` 不属于 `NotificationEventTypes.All`，不会出现在
`GET /api/v1/notifications/capabilities` 的 `eventTypes` 中，也不会被本切片实际投递。
Web Push payload 的 `data.categoryKey` 携带上述精确键；站内通知记录仍保存内部
`type`，客户端按同一映射得到分类键。QA、Android 与 Windows 对接以本表和仓库内
`NotificationCategoryKeys` / Web `categories.ts` 为准。

本 PR 是 Q06 通知 foundation 切片，不是 Q06 全部完成。通知点击/忽略（click/dismiss）
语义、provider message id、90 天保留策略和时区 DND 均未在本切片实现或验证，不能视为
已完成。

## 安全降级

未配置 `Notifications:WebPush:PublicKey` / `PrivateKey` 时：

- 站内通知照常持久化，SSE 与列表接口可用。
- 推送投递记录进入 `Skipped`，错误码为 `provider_not_configured`，不会无限重试。
- Web 设置页显示“推送通道安全降级”，并禁用启用按钮。

## 配置 Web Push

生成 VAPID 密钥对（P-256，公钥 65 字节未压缩点，私钥 32 字节，base64url 无填充），然后通过环境变量配置：

```bash
Notifications__WebPush__PublicKey=<public-key>
Notifications__WebPush__PrivateKey=<private-key>
Notifications__WebPush__Subject=mailto:ops@example.com
```

可选调整：

- `Notifications__WebPush__AllowedEndpointHosts__0=...` 追加允许的推送服务主机；默认只允许 FCM、Mozilla、Windows、Apple 的推送域名。
- `Notifications__Dispatcher__MaxAttempts` / `BaseDelaySeconds` / `MaxDelaySeconds` 控制指数退避与死信阈值。
- `Notifications__Dispatcher__Enabled=false` 可关闭后台分发（站内通知仍会写入）。

浏览器侧需要 HTTPS（localhost 例外）、通知权限和 Service Worker 支持。设备级推送偏好通过 `X-Pim-Device-Id` 请求头传递（Web 设置页自动保存并使用同一设备标识）；未带该请求头时读写账号级兜底偏好。站内通知开关为账号级，共享通知中心。设备级类别只过滤该设备的浏览器推送，不会删除服务器事件或站内通知。

订阅端点和密钥在服务端使用 Data Protection 加密存储；接口只返回 endpoint 主机名，不返回 endpoint、`p256dh`、`auth` 或 provider secret。

### 订阅所有权

同一 `channel + endpoint_hash` 一旦绑定即只归属该账号（无论订阅状态为 `Active` 还是 `Revoked`）。若该浏览器推送地址已绑定其他账号，`POST /api/v1/notifications/subscriptions` 返回 HTTP `409` / 业务码 `40906`，不会转移订阅、改写原账号的 `user_id`、密钥、`created_at` 或 `version`，也不会抑制原账号仍处于 `Pending` / `Retrying` / `Processing` 的投递。当前版本不提供跨账号换绑或释放路径：原账号撤销订阅只会把订阅标记为 `Revoked` 并抑制未发送投递，不会清空 `endpoint_hash`，其他账号仍不能绑定同一推送地址；只有原账号可以再次提交同一 endpoint 重新启用订阅。冲突响应只包含通用错误码和安全文案，不回显 endpoint、订阅密钥或原账号信息。

## 队列状态

- 事件先写入 `notification_events`（站内通知与 outbox 同一行），再由后台 fanout 为每个活跃订阅写入 `notification_deliveries`。
- 事件创建时快照当时符合偏好的订阅；之后才重新打开的设备、或关闭期间产生的事件不会补推。关闭类别期间的事件仍以抑制状态落库，用于幂等与去重。
- `notification_deliveries` 唯一键 `(event_id, subscription_id)` 保证重复 fanout 不重复推送。
- 可重试失败按指数退避进入 `Retrying`；达到上限进入 `DeadLetter`，设置页可见最近安全错误并允许手动重试。
- 手动重试只接受 `DeadLetter` / `Skipped`。PostgreSQL 路径在同一事务内先锁定用户
  拥有的 `Active` subscription，再执行带 `status IN ('DeadLetter', 'Skipped')` 条件的
  `UPDATE`；worker 抢先 claim 后条件更新影响 0 行，接口返回实际状态，绝不会把
  `Processing` / `Delivered` 写回 `Pending`。
- 订阅撤销会把未发送投递置为 `Suppressed`。
- 400/401/403/413 等永久失败进入 `DeadLetter`，404/410 会撤销订阅。
- subscription 与 delivery 的锁顺序统一为 subscription → delivery。换绑（upsert）
  会在抑制 duplicate subscription 的 delivery 前，按 id 升序锁定所有受影响
  subscription；worker 和撤销路径遵循同一顺序，避免 PostgreSQL 死锁。
- provider 调用目前仍位于持有 subscription/delivery 行锁的投递事务内。
  `Notifications__Dispatcher__ProviderTimeoutSeconds`（默认 15 秒）只限制单个 provider
  调用，超时按 `push_transport_timeout` 重试，避免无限阻塞撤销；把 provider 发送完全移出
  事务仍属未完成的后续工作，不能宣称该复审项已解决。

## 历史数据与扫描边界

- 迁移会把部署时已到期的既有提醒标记为已处理，避免首次上线补发全部历史提醒。
- 转写/整理完成事件由文件任务扫描器发现；扫描水位持久化在笔记存储根的 `notification-note-scan-watermark.txt`，首次上线只回看最近 2 分钟，之后按水位增量扫描，补扫上限 7 天，避免历史任务洪泛。服务停机超过补扫上限期间完成的笔记任务不会补发。
- 扫描器对每个笔记任务按 revision 生成稳定去重键；`Succeeded` 产生完成事件，
  `NeedsAttention` 产生 attention 事件，水位内任务不重放。入队失败时不推进水位。
  当前回归测试覆盖单进程失败不推进水位、两个扫描器共享水位和去重；真实多实例调度、
  fencing/租约、进程崩溃恢复和跨实例水位一致性仍为 NOT-VERIFIED，需要独立工单验证。

## 实时通道与登出

- SSE 与普通 API 共用统一的 401 单飞 refresh：浏览器通道刷新成功后重放一次；
  host 通道继续由宿主添加 Bearer 并走宿主刷新，不读取或清理浏览器令牌。
- 刷新最终失败时，SSE 显示“登录已过期”并停止 3 秒重连，避免用已失效凭据持续请求。
- 浏览器登出会在清理本地令牌前启动服务端撤销请求，再解绑 service worker；撤销失败时
  仅将 `{ deviceId, userId }` 放入 `pim.pendingPushRevocations`，下次对应账号登录后重试。
  队列不保存 endpoint、推送密钥或令牌。host 通道的桌面宿主登出由宿主协议负责，不在
  此浏览器队列的已验证范围内。

## 接口

- `GET /api/v1/notifications/capabilities`
- `GET|PUT /api/v1/notifications/preferences`
- `GET|POST /api/v1/notifications/subscriptions`
- `DELETE /api/v1/notifications/subscriptions/{id}`
- `GET /api/v1/notifications`
- `POST /api/v1/notifications/{id}/read`
- `POST /api/v1/notifications/read-all`
- `GET /api/v1/notifications/deliveries`
- `POST /api/v1/notifications/deliveries/{id}/retry`
- `GET /api/v1/notifications/stream`（SSE，支持 `Last-Event-ID` / `after`）
- `POST /api/v1/notifications/test`

所有接口要求登录，并按 `user_id` 隔离；设备撤销、通知已读和投递重试都不能跨用户操作。
