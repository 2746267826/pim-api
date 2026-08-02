#!/bin/bash
# =============================================================
# PIM Production Log Reader — 服务端部署脚本
# 在 Ubuntu 生产服务器上以 root 执行
# =============================================================
set -euo pipefail

echo "=== PIM Log Reader 服务端部署 ==="

# --- 1. 创建 logreader 用户 ---
echo "--- 创建 logreader 用户 ---"
if id "logreader" &>/dev/null; then
    echo "用户 logreader 已存在，跳过"
else
    sudo useradd -r -m -s /bin/bash logreader
    echo "用户 logreader 已创建"
fi

# --- 2. 授权日志目录读取权限 ---
echo "--- 配置日志目录权限 ---"
sudo mkdir -p /data/pim/logs
# 将 logreader 加入 adm 组（如果日志使用系统权限）
sudo usermod -aG adm logreader 2>/dev/null || true
# 给 logreader 读取 /data/pim/logs 的权限（通过 ACL 或组权限）
sudo setfacl -R -m u:logreader:rx /data/pim/logs/ 2>/dev/null || \
    sudo chmod -R o+r /data/pim/logs/ 2>/dev/null || \
    echo "警告: 无法设置 ACL/权限，请手动检查 /data/pim/logs 的读取权限"

# journalctl 权限（通过 systemd-journal 组）
sudo usermod -aG systemd-journal logreader 2>/dev/null || true

# --- 3. 部署 log-reader.sh ---
echo "--- 部署 log-reader.sh ---"
sudo cp "$(dirname "$0")/log-reader.sh" /usr/local/bin/pim-log-reader.sh
sudo chmod 755 /usr/local/bin/pim-log-reader.sh
sudo chown root:root /usr/local/bin/pim-log-reader.sh
echo "已部署到 /usr/local/bin/pim-log-reader.sh"

# --- 4. 创建 logreader 的 ~/.ssh 目录 ---
echo "--- 配置 SSH authorized_keys ---"
sudo mkdir -p /home/logreader/.ssh
sudo touch /home/logreader/.ssh/authorized_keys
sudo chmod 700 /home/logreader/.ssh
sudo chmod 600 /home/logreader/.ssh/authorized_keys
sudo chown -R logreader:logreader /home/logreader/.ssh

# --- 5. 提示用户添加公钥 ---
echo ""
echo "==========================================================="
echo "  部署完成!"
echo "==========================================================="
echo ""
echo "下一步: 将公钥添加到 /home/logreader/.ssh/authorized_keys"
echo ""
echo "在 authorized_keys 中，在公钥前加上以下限制:"
echo ""
echo '  command="/usr/local/bin/pim-log-reader.sh",restrict,no-agent-forwarding,no-port-forwarding,no-pty,no-user-rc,no-X11-forwarding'
echo ""
echo "完整的 authorized_keys 行示例:"
echo ""
echo 'command="/usr/local/bin/pim-log-reader.sh",restrict,no-agent-forwarding,no-port-forwarding,no-pty,no-user-rc,no-X11-forwarding ssh-ed25519 AAA... pim-log-reader-20260802'
echo ""
echo "然后在本机测试连接:"
echo "  ssh -i .reasonix/production-log-key logreader@<server-ip>"
echo ""
echo "本机 SSH 配置建议 (~/.ssh/config):"
echo ""
echo "  Host pim-log-prod"
echo "      HostName <server-ip>"
echo "      Port 22"
echo "      User logreader"
echo "      IdentityFile ~/.ssh/pim-log-reader-key"
echo "      StrictHostKeyChecking ask"
echo ""
echo "==========================================================="