---
name: production-log-reader
description: 通过受限 SSH 只读访问生产服务器日志（journal + /data/pim/logs/*.jsonl），支持 tail/cat/ls/journal/du/find 命令，内置 10MB/会话流量上限
runAs: inline
---

# PIM Production Log Reader

通过受限 SSH 密钥访问生产服务器日志，只读、限流、有审计。

## 前置条件

- 本机有 `.reasonix/production-log-key` 私钥文件（已生成）
- 服务器端已部署 `log-reader.sh` 并配置 `authorized_keys` 的 `command=` 限制
- 服务器 SSH 主机密钥已确认

## 快速使用

```bash
# 查看可用命令
ssh -i .reasonix/production-log-key logreader@<server-ip>

# 查看最近日志
ssh -i .reasonix/production-log-key logreader@<server-ip> tail pim-api-20260802.jsonl lines=100

# 搜索关键词
ssh -i .reasonix/production-log-key logreader@<server-ip> find 'ERROR'

# 查看 systemd journal
ssh -i .reasonix/production-log-key logreader@<server-ip> journal '2h ago' lines=50

# 查看日志目录大小
ssh -i .reasonix/production-log-key logreader@<server-ip> du

# 列出日志文件
ssh -i .reasonix/production-log-key logreader@<server-ip> ls
```

如果配置了 `~/.ssh/config` 的 Host 别名，可简化为：

```bash
ssh pim-log-prod tail pim-api-20260802.jsonl lines=100
```

## 安全限制

- 每次 SSH 会话最多读取 **10MB** 数据（由服务器端 `log-reader.sh` 强制限制）
- 每个命令最多返回 **200 行**（防止意外大量输出）
- 仅允许读取 `/data/pim/logs/` 下的 `.jsonl` 文件
- 文件名不允许路径穿越（`/`、`..` 等字符被拒绝）
- 文件名仅允许字母、数字、`_`、`.`、`-` 字符
- 服务器端 `authorized_keys` 使用 `command=` 和 `restrict` 强制仅执行此脚本

## 支持的命令

| 命令 | 用途 | 示例 |
|------|------|------|
| `tail` | 查看日志文件尾部 | `tail pim-api-20260802.jsonl lines=100` |
| `cat` | 查看日志文件开头 | `cat pim-api-20260802.jsonl lines=50` |
| `ls` | 列出日志目录文件 | `ls` 或 `ls pim-api-*.jsonl` |
| `journal` | 查看 systemd journal | `journal '30 min ago' lines=50` |
| `du` | 查看日志目录大小 | `du` |
| `find` | 搜索关键词 | `find 'ERROR'` 或 `find 'TimeoutException' pim-api-20260802.jsonl` |
| `help` | 显示帮助 | 无参数连接 |

## 注意

- 不要尝试全量下载日志文件。服务器端有限流限制，但请自觉遵守。
- 调试时优先使用 `find` 命令搜索关键词，再使用 `tail` 查看上下文。
- 如需查看历史日志，先通过 `ls` 确定文件名，再用 `tail` 或 `cat` 查看。
- 如果服务器端 `log-reader.sh` 需要更新，请在服务器上以 root 重新部署脚本文件。