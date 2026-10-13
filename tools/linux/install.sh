#!/usr/bin/env bash
set -euo pipefail

if [[ "${EUID:-$(id -u)}" -ne 0 ]]; then
  echo "请使用 root 权限运行: sudo ./install.sh" >&2
  exit 1
fi

HERE="$(cd "$(dirname "$0")" && pwd)"
INSTALL_ROOT="/opt/igw"
DATA_ROOT="/var/lib/igw"
LOG_ROOT="${IGW_LOG_DIR:-$DATA_ROOT/observability}"
SERVICE_USER="igw"

if ! id "$SERVICE_USER" >/dev/null 2>&1; then
  useradd --system --home-dir "$DATA_ROOT" --shell /usr/sbin/nologin "$SERVICE_USER"
fi

systemctl stop igw.service 2>/dev/null || true
# 旧版的独立日志服务(OpenObserve)已退役：日志改由网关自身按时间分段压缩存储。
if systemctl list-unit-files igw-log.service >/dev/null 2>&1; then
  systemctl disable --now igw-log.service 2>/dev/null || true
  rm -f /etc/systemd/system/igw-log.service
fi
install -d -m 0755 "$INSTALL_ROOT"
rm -rf "$INSTALL_ROOT/app" "$INSTALL_ROOT/logservice"
install -d -m 0755 "$INSTALL_ROOT/app"
cp -a "$HERE/app/." "$INSTALL_ROOT/app/"
chmod 0755 "$INSTALL_ROOT/app/IGW.Runtime"
chmod 0755 "$INSTALL_ROOT/app/questdb/bin/java"

# /var/lib/igw contains project, licensing and log data and is never removed by this installer.
install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_USER" "$DATA_ROOT" "$LOG_ROOT"
install -d -m 0750 -o "$SERVICE_USER" -g "$SERVICE_USER" /usr/share/IGW/secrets
chown -R "$SERVICE_USER:$SERVICE_USER" "$DATA_ROOT"

install -m 0644 "$HERE/igw.service" /etc/systemd/system/igw.service
install -m 0755 "$HERE/igwctl" /usr/local/bin/igwctl

runuser -u "$SERVICE_USER" -- env IGW_DATA_DIR="$DATA_ROOT" "$INSTALL_ROOT/app/IGW.Runtime" \
  --initialize-logs --data-dir "$DATA_ROOT" --log-dir "$LOG_ROOT"
systemctl daemon-reload
systemctl enable igw.service
systemctl restart igw.service

echo "IGW 安装完成。日志目录: $LOG_ROOT"
echo "服务状态: igwctl status"
