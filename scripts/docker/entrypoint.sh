#!/bin/bash
set -euo pipefail

# 1. authorized_keys：PIM_SSH_AUTHORIZED_KEYS 为 base64 单行，解码后逐行写入
if [[ -n "${PIM_SSH_AUTHORIZED_KEYS:-}" ]]; then
    mkdir -p /home/pimlog/.ssh
    echo "$PIM_SSH_AUTHORIZED_KEYS" | base64 -d > /home/pimlog/.ssh/authorized_keys
    chown -R pimlog:pimlog /home/pimlog/.ssh
    chmod 700 /home/pimlog/.ssh
    chmod 600 /home/pimlog/.ssh/authorized_keys
fi

# 2. SSH host keys（/etc/pim/ssh 为持久化卷；缺失才生成，重建容器指纹不变）
mkdir -p /etc/pim/ssh
if [[ ! -s /etc/pim/ssh/ssh_host_ed25519_key ]]; then
    ssh-keygen -A -f /etc/pim/ssh/ssh_host_
fi

# 3. sshd 运行所需目录
mkdir -p /run/sshd

# 4. 日志目录归属（只 chown 目录本身，不递归；文件 0644 世界可读兜底）
install -d -o pimlog -g pimlog /data/pim/logs

# 5. 启动 supervisord（tini 为 PID 1，负责回收僵尸与转发信号）
exec supervisord -n -c /etc/supervisor/supervisord.conf
