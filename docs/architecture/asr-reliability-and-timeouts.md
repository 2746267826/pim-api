# ASR 调用可靠性与部署超时

更新日期：2026-11-01。本文补充单片 ASR 同步调用的超时边界、持久异步入口和中断诊断；不替代 [ASR 预设说明](ai-asr-presets.md) 或长材料分段任务说明。

## 已知问题与证据

Issue #552 的生产复现为 507.924 秒、1,015,848 字节 MP3。直连 MiMo 在 69.0 秒返回 HTTP 200、`finish_reason=stop`、2,318 completion tokens、3,669 字符；原 PIM 请求在 60 秒被 1Panel/nginx 默认 `proxy_read_timeout` 截断为 504。把模型配置超时从 60 秒改成 600 秒仍在 60 秒失败，因为反向代理比服务端和 Provider 更早结束请求。随后把该 vhost 的 `proxy_read_timeout` 和 `proxy_send_timeout` 临时改为 600 秒后，同一 69 秒调用成功。该成功是用户报告的部署验证，不是本仓可复现的生产验收；不能将异步接口说成所有部署都不需要配置代理。

## 入口与兼容性

- Web 单片文件 ASR 的新默认路径为 `POST /api/v1/ai/asr-jobs`。服务端先持久化 owner-scoped job、审计记录、输入指纹和音频引用，再交给 Hangfire；POST 只需等待接受，不等待模型完整推理。音频仍在请求体中上传，反向代理的 body 大小、上传/请求头时限和服务端 admission 限制仍适用。
- 以同一 `clientRequestId` 调用 `GET /api/v1/ai/asr-jobs/{clientRequestId}` 查询；只有用户明确点击“取消任务”才用 `DELETE /api/v1/ai/asr-jobs/{clientRequestId}`。关闭页面、停止浏览器等待或轮询网络断开不会取消已接受的 job。相同 owner/ID/输入返回既有记录；相同 owner/ID 的不同输入冲突；其他 owner 看不到记录。
- 旧 `POST/GET /api/v1/ai/asr-calls` 和 `POST/GET /api/v1/ai/asr-url-calls` 保持同步兼容，旧调用方不会自动转换为后台任务。旧同步调用仍须为真实最长服务时间配置整条等待链路。
- 异步不是运行依赖的替代：部署必须配置可用 Hangfire/持久存储和 ASR 音频存储；Hangfire 不可用时 API 明确拒绝新 job，不退化成进程内队列或悄悄同步等待。已接受 job 的状态与 intent 在数据库，dispatcher 定期补投；进程重启后 running 租约过期记为结果未知，不自动再次调用 Provider。
- job 与审计行采用同事务状态转换：`preparing` 音频落盘成功才 CAS 为 `queued`；10 分钟准备租约过期则回收为 `preparation_interrupted`（未调用、无服务商费用）。取消/claim 竞争以数据库 CAS 决胜；恢复通过条件 `UPDATE ... RETURNING` 只回收实际命中的 job，并在同事务同步相应审计行，避免双写中断留下矛盾状态。
- Web 在异步提交、轮询和旧 URL 同步调用中，只有浏览器实际收到 `502/503/504/524` 时才调用 `POST /api/v1/ai/asr-transport-observations` 回填网关状态；可同时保存有界的响应头网关名。`fetch` 的 `TypeError`/socket 失败只记为 `client_network_failure`，不得推断为网关 `504`。该观察表按 owner、请求标识、传输方式和来源分开持久化，异步 job GET 与旧同步结果 GET 均可回读；它不会改写已成功或已终态的 Provider 结果，也不会触发新请求。

## 超时逐层配置

`timeout_seconds` 是 Provider HTTP 请求预算，不是用户浏览器、API host 或反向代理的统一超时。检查/配置所有外部层，避免任何必要层先于目标操作终止：

| 层 | 建议/含义 |
| --- | --- |
| 浏览器到 API 的上传请求 | 只覆盖认证、上传/解码、持久化和 job 接受；仍需满足请求体上限与网关上传时限。接受后由浏览器轮询状态。 |
| nginx/1Panel 反向代理（旧同步调用） | `proxy_read_timeout`、`proxy_send_timeout` 均要大于服务端模型预算加上传/响应余量。已知 #552 的临时验证值为 `600s`；部署者若继续使用同步接口，应按实际 `timeout_seconds` 与最坏上传/处理时长设置并验证，而不是只改模型字段。也检查其前方 CDN、负载均衡、Ingress 和客户端代理的独立 idle/request timeout。 |
| nginx/1Panel 反向代理（异步入口） | POST 不等待 Provider 完成，通常无需把读超时设到音频推理时长；但上传的大型 Base64 JSON、慢客户端、body size 与接受响应仍受代理限制。GET/DELETE 应快速返回。 |
| 后台 Provider 调用 | 使用提交时冻结的模型 `timeout_seconds` 和输出 token 上限；后台执行预算另留停止/保存余量。超时不自动重试，因请求可能已到 Provider。 |
| Hangfire worker/storage | worker 进程要持续运行、成功连接持久 storage；运行租约长于 Provider timeout 与内部余量。worker/进程中止后通过租约恢复为 UNKNOWN/Incomplete，不重新发送付费调用。 |

对 69 秒样例，600 秒是 issue 所记录并成功的临时 nginx 值，不是对所有 Provider、音频大小、上游代理或未来部署的普适保证。若选择同步兼容入口，部署文档/运维配置仍必须显式延长每一层；推荐 Web 使用持久异步 job 消除长时间浏览器连接依赖。

## 取消与诊断解释

- `user`：仅代表用户对 job 发出明确取消。队列尚未 claim 时可确定未调用 Provider；运行中取消只能尽力停止 HTTP 请求，Provider 可能已处理/计费，结果必须标为未知。
- `caller_disconnect`：旧同步 HTTP 请求的浏览器断开或其间代理关闭连接。应用通常只能观察到 request cancellation，无法可靠得知断开由浏览器、nginx、Ingress、负载均衡器还是网络造成；在没有浏览器实际收到响应头的可信证据时 `gatewayStatus` 为空，不能伪造 `504`。旧同步请求只有显式声明 `explicitCancel=true` 才按用户取消记录；通用断开不推定为用户意图。job 异步路径不把停止轮询当取消，只有 DELETE 端点是显式取消。
- `client_http_response`：浏览器确实收到 `502/503/504/524` HTTP 响应，持久记录 `gatewayStatus`、可得的 `gatewayName`、观察环节与时间。它证明客户端到 API 的传输层实际收到该响应，但不证明 Provider 是否执行。
- `client_network_failure`：浏览器未收到 HTTP 响应，只能证明客户端连接失败；不得显示为“网关 504”，也不得据此取消后台 job。
- `provider_timeout` / `upstream_http`：PIM 的 Provider 请求预算耗尽或收到上游 HTTP 状态；保存 `upstreamStatus`（可得时）与下一步建议。timeout、408、429、5xx 或发送后的连接错误通常表示结果未知；不自动重试。
- `worker_timeout`、`worker_shutdown`、`worker_lost`：执行预算、worker 进程或租约中断；单独呈现，不混成用户取消。崩溃恢复不会假设 Provider 未执行。
- job 返回 `status`、`abortSource`、`outcomeKnown`、`gatewayStatus`、`gatewayName`、`gatewayEvidenceSource`、`transportObservationKind`、`upstreamStatus`、`providerAttempted`、`responseBodyComplete` 和 `nextAction`。旧同步结果也返回同一诊断对象。UNKNOWN 表示无法确认服务商最终状态，不表示肯定失败，也不允许自动重发；先核对调用记录/服务商账单，再由用户明确新建不同请求。
- Web 轮询遇到可恢复的传输失败时按 `1.5s/3s/6s/12s/20s` 有限退避继续查询，不因一次断连丢弃 job。页面只在明确点击“取消任务”时发送 DELETE；关页、刷新或轮询断网不会取消。Web 只在本机保存 owner + `clientRequestId` 回执，刷新后按同 ID GET 恢复，不自动重发音频。
