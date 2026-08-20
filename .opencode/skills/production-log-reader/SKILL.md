---
name: production-log-reader
description: Read PIM production logs via restricted SSH. Use when debugging production issues, verifying bug fixes, or investigating reported errors. Trigger: production logs, prod logs, read logs, production bug, 查日志, 生产日志.
---

# PIM Production Log Reader

通过受限 SSH 读取生产服务器日志。只读、限流、有审计。

## 前置条件

- 私钥: `/root/.ssh/pimlog`（opencode 容器内）
- SSH: `ssh -i /root/.ssh/pimlog -p 2223 -o StrictHostKeyChecking=accept-new pimlog@127.0.0.1`
- 容器: `pim-pim-1`，端口 `127.0.0.1:2223→22`
- 网络: opencode 使用 host 网络，直连 127.0.0.1

## 何时使用

**必须使用的场景：**
- 修 production bug 时，先看日志确认真实错误
- 用户报告异常时，查日志定位时间点和上下文
- CI 通过但生产出问题时，验证实际运行状态
- PR 涉及生产数据逻辑时，查看实际数据

**不要滥用：**
- 纯代码审查（看源码即可）
- 本地调试（用本地日志）

## SSH 命令模板

```bash
# 通用前缀
S="ssh -i /root/.ssh/pimlog -p 2223 -o StrictHostKeyChecking=accept-new pimlog@127.0.0.1"

# 列出日志文件
$S "ls"

# 查看目录大小
$S "du"

# 查看今天日志最后 50 行
$S "tail pim-api-$(date +%Y%m%d).jsonl"

# 指定行数
$S "tail pim-api-20260820.jsonl lines=100"

# 查看文件开头
$S "cat pim-api-20260820.jsonl lines=20"

# 搜索关键词（默认搜最近 3 个文件）
$S "find 'Exception'"

# 搜索指定文件
$S "find 'TimeoutException' pim-api-20260820.jsonl"
```

## 安全限制

- 每会话最多 **10MB** 数据
- 每命令最多 **200 行**
- 文件名仅允许 `字母数字._-`，禁止路径穿越
- `ForceCommand` 锁死为 `pim-log-cat.sh`，无法获得 bash
- 不允许写入、端口转发、agent 转发

## 日志格式

Serilog JSONL，每行一个 JSON：

```json
{
  "@t": "2026-08-20T03:00:03Z",
  "@mt": "Start processing HTTP request {HttpMethod} {Uri}",
  "@l": "Debug",
  "Service": "pim-api"
}
```

级别: `Error`/`Fatal`(关注) > `Warning`(潜在问题) > `Information`(正常) > `Debug`(调试)

## 调试工作流

### Step 1: 定位错误

```bash
$S "find 'Error' pim-api-$(date +%Y%m%d).jsonl"
$S "find 'Error' pim-api-$(date -d yesterday +%Y%m%d).jsonl"
```

### Step 2: 看上下文

```bash
# 根据 Step 1 的关键词，用 tail 看前后文
$S "tail pim-api-20260820.jsonl lines=200"
```

### Step 3: 分析模式

```bash
# 统计错误次数
$S "find 'TimeoutException' | wc -l"

# 查某个 API 的调用
$S "find '/api/v1/location' pim-api-20260820.jsonl"
```

## 日志轮转

- 文件名: `pim-api-YYYYMMDD.jsonl`
- 保留最近 2 天（`PIM_LOG_RETAINED_FILES=2`）
- 单文件约 1GB

## 常见问题

**SSH 连不上？**
- `docker ps | grep pim` 检查容器
- `docker exec pim-pim-1 cat /home/pimlog/.ssh/authorized_keys` 检查密钥
- `ls -la /root/.ssh/pimlog` 检查私钥权限（应为 600）

**输出太多？**
- 先 `find` 过滤关键词，不要直接 `tail` 大文件
- 缩小 lines: `tail xxx.jsonl lines=30`
- 指定具体文件
