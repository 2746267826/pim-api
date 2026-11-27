# 通知与推送 / Notifications and Web Push

## 范围

API 提供账号级站内通知偏好、设备级浏览器推送偏好与订阅、站内通知、SSE 实时事件流和持久化投递队列。当前接入的事件：

- `calendar.task.completed`：任务状态变为 `COMPLETED`（创建、更新、批量更新）。
- `calendar.reminder.due`：提醒到达 `scheduled_at`，且未因偏好被关闭。
- `note.transcription.completed`：笔记语音转写任务成功结束。
- `note.derived.completed`：笔记完整记录与独立分析任务成功结束。
- `notification.test`：设置页“发送测试通知”。

Android FCM 与 Windows WNS 不在本仓库范围内；本仓库只提供 Web Push 与 SSE。两者的真实设备/系统通道必须由各自客户端仓库验证。

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

同一 `channel + endpoint_hash` 只归属一个账号。若该浏览器推送地址已绑定其他账号，`POST /api/v1/notifications/subscriptions` 返回 HTTP `409` / 业务码 `40906`，不会转移订阅、改写原账号的 `user_id`、密钥、`created_at` 或 `version`，也不会抑制原账号仍处于 `Pending` / `Retrying` / `Processing` 的投递。需要换绑时，先在原账号撤销该订阅。冲突响应只包含通用错误码和安全文案，不回显 endpoint、订阅密钥或原账号信息。

## 队列状态

- 事件先写入 `notification_events`（站内通知与 outbox 同一行），再由后台 fanout 为每个活跃订阅写入 `notification_deliveries`。
- 事件创建时快照当时符合偏好的订阅；之后才重新打开的设备、或关闭期间产生的事件不会补推。关闭类别期间的事件仍以抑制状态落库，用于幂等与去重。
- `notification_deliveries` 唯一键 `(event_id, subscription_id)` 保证重复 fanout 不重复推送。
- 可重试失败按指数退避进入 `Retrying`；达到上限进入 `DeadLetter`，设置页可见最近安全错误并允许手动重试。
- 订阅撤销会把未发送投递置为 `Suppressed`。
- 400/401/403/413 等永久失败进入 `DeadLetter`，404/410 会撤销订阅。

## 历史数据与扫描边界

- 迁移会把部署时已到期的既有提醒标记为已处理，避免首次上线补发全部历史提醒。
- 转写/整理完成事件由文件任务扫描器发现；扫描水位持久化在笔记存储根的 `notification-note-scan-watermark.txt`，首次上线只回看最近 2 分钟，之后按水位增量扫描，补扫上限 7 天，避免历史任务洪泛。服务停机超过补扫上限期间完成的笔记任务不会补发。

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
