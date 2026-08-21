#!/bin/bash
set -euo pipefail

# 日志目录（保持 root 属主；文件 0644 世界可读兜底）
install -d -m 0755 /data/pim/logs

# 启动 supervisord（tini 为 PID 1，负责回收僵尸与转发信号）
exec supervisord -n -c /etc/supervisor/supervisord.conf
