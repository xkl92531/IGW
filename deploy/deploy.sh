#!/usr/bin/env bash
# IGW 工业网关 · Ubuntu Docker 一键部署脚本(deploy\ 套件,随 zip 一起分发)
# 用法A(已解压,推荐): cd igw-docker && sudo bash deploy.sh
# 用法B(免解压一行命令):
#   sudo bash <(unzip -p igw-<版本>-docker-linux-x64.zip igw-docker/deploy.sh) igw-<版本>-docker-linux-x64.zip
# 自动完成:安装 Docker(如缺)→ 构建镜像 → 启动容器 → 健康检查
set -euo pipefail

RED=$'\033[0;31m'; GRN=$'\033[0;32m'; YLW=$'\033[1;33m'; RST=$'\033[0m'
info() { printf '%s[信息]%s %s\n' "$GRN" "$RST" "$*"; }
warn() { printf '%s[警告]%s %s\n' "$YLW" "$RST" "$*"; }
die()  { printf '%s[错误]%s %s\n' "$RED" "$RST" "$*" >&2; exit 1; }

# ---------- 0. 提权(兼容 bash <(unzip -p ...) 进程替换:$0 不是普通文件) ----------
if [[ $EUID -ne 0 ]]; then
  if [[ -f "$0" ]]; then
    exec sudo bash "$0" "$@"
  else
    SELF="$(mktemp /tmp/igw-deploy.XXXXXX.sh)"
    cat "$0" > "$SELF" || die "无法缓存脚本"
    exec sudo bash "$SELF" "$@"
  fi
fi

# ---------- 1. 自解压模式:首参为 zip 路径则先解压再继续 ----------
if [[ "${1:-}" == *.zip ]]; then
  ZIP="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
  [[ -f "$ZIP" ]] || die "找不到压缩包:$ZIP"
  if ! command -v unzip >/dev/null 2>&1; then
    info "安装 unzip ..."
    apt-get update -y >/dev/null 2>&1 || die "apt-get update 失败"
    apt-get install -y unzip >/dev/null 2>&1 || die "unzip 安装失败"
  fi
  # 停旧容器(数据在 ../igwdata,不受解压影响)
  DEST="$(dirname "$ZIP")/igw-docker"
  if command -v docker >/dev/null 2>&1 && [[ -f "$DEST/docker-compose.yml" ]]; then
    docker compose -f "$DEST/docker-compose.yml" down >/dev/null 2>&1 || true
  fi
  # 只删/重建程序目录 igw-docker;数据目录 ../igwdata 永不触碰(人工管理)
  info "解压 $ZIP ..."
  rm -rf "$DEST"
  unzip -q "$ZIP" -d "$(dirname "$ZIP")"
  cd "$DEST"
fi

[[ -f Dockerfile && -d app ]] || die "请在解压后的 igw-docker 目录里运行(Dockerfile 与 app/ 须在当前目录)"

# ---------- 2. Docker / Compose 就绪 ----------
if ! command -v docker >/dev/null 2>&1; then
  info "未检测到 Docker,开始在线安装(约 1-3 分钟)..."
  if ! command -v curl >/dev/null 2>&1; then
    apt-get update -y >/dev/null 2>&1 || true
    apt-get install -y curl >/dev/null 2>&1 || die "curl 安装失败"
  fi
  curl -fsSL https://get.docker.com -o /tmp/get-docker.sh || die "下载 get.docker.com 失败(离线环境请先手工安装 Docker 并重跑)"
  sh /tmp/get-docker.sh >/dev/null 2>&1 || die "Docker 安装失败,请查看 /tmp/get-docker.sh 手工执行"
fi

# 守护进程(systemd 正常环境;WSL 等无 systemd 时回退 service/dockerd)
if ! docker info >/dev/null 2>&1; then
  systemctl enable --now docker >/dev/null 2>&1 \
    || service docker start >/dev/null 2>&1 \
    || { nohup dockerd >/var/log/igw-dockerd.log 2>&1 & sleep 3; }
  docker info >/dev/null 2>&1 || die "Docker 守护进程未运行:systemctl status docker(WSL 见 /var/log/igw-dockerd.log)"
fi

if docker compose version >/dev/null 2>&1; then
  DC=(docker compose)
elif command -v docker-compose >/dev/null 2>&1; then
  DC=(docker-compose)
else
  info "安装 docker compose 插件..."
  { apt-get update -y >/dev/null 2>&1 && apt-get install -y docker-compose-v2 >/dev/null 2>&1; } \
    || { apt-get update -y >/dev/null 2>&1 && apt-get install -y docker-compose-plugin >/dev/null 2>&1; } \
    || die "无法安装 docker compose,请手工安装后重跑"
  if docker compose version >/dev/null 2>&1; then DC=(docker compose); else DC=(docker-compose); fi
fi

# ---------- 3. 数据目录(zip 内容之外;默认 ../igwdata 平级,可 IGW_HOST_DATA 指定绝对路径;
#              脚本只创建/读取,永不覆盖/删除,人工管理) ----------
IGWDATA="${IGW_HOST_DATA:-../igwdata}"
mkdir -p "$IGWDATA/data"
if [[ ! -s "$IGWDATA/machine-id" ]]; then
  tr -d '-' < /proc/sys/kernel/random/boot_id > "$IGWDATA/machine-id"
  info "已生成 $IGWDATA/machine-id(容器机器码标识,授权绑定,勿删)"
fi
# 旧布局(./data、./machine-id 在程序目录内)迁移提示:不自动动,人工处理
if [[ -d ./data || -e ./machine-id ]]; then
  warn "检测到旧版数据 ./data 或 ./machine-id(新版数据目录为 $IGWDATA)。如需保留旧数据,手工执行后重跑:"
  echo "  mkdir -p $IGWDATA && mv ./data $IGWDATA/data; mv ./machine-id $IGWDATA/machine-id"
fi

# ---------- 4. 基础镜像:本地已有 -> 包内 basedeps.tar 离线加载 -> 联网拉取 ----------
BASE="${IGW_BASE_IMAGE:-mcr.microsoft.com/dotnet/runtime-deps:10.0-noble}"
if ! docker image inspect "$BASE" >/dev/null 2>&1 && [[ -f basedeps.tar ]]; then
  info "基础镜像本地无,从包内 basedeps.tar 离线加载(无需外网)..."
  docker load -i basedeps.tar >/dev/null 2>&1 || die "basedeps.tar 加载失败(文件损坏?)"
fi
if ! docker image inspect "$BASE" >/dev/null 2>&1; then
  info "拉取基础镜像 $BASE(包内无 basedeps.tar,需联网)..."
  if ! docker pull "$BASE" >/dev/null 2>&1; then
    warn "基础镜像拉取失败,按顺序排查:"
    echo "  1) 有无外网(不经过DNS)   : ping -c2 223.5.5.5"
    echo "  2) DNS 是否正常           : getent hosts mcr.microsoft.com"
    echo "  3) DNS 修复(127.0.0.53 超时常见): sudo systemctl restart systemd-resolved"
    echo "     仍不行则配上游: sudo mkdir -p /etc/systemd/resolved.conf.d &&"
    echo "       printf '[Resolve]\nDNS=223.5.5.5 119.29.29.29\n' | sudo tee /etc/systemd/resolved.conf.d/dns.conf &&"
    echo "       sudo systemctl restart systemd-resolved"
    echo "  4) mcr 不可达/走加速(可选): IGW_BASE_IMAGE=<可达的同内容镜像地址> sudo bash deploy.sh"
    echo "  5) 完全离线:找台有网机器 docker pull $BASE && docker save $BASE | gzip > basedeps.tar.gz,"
    echo "     拷来本机 docker load < basedeps.tar.gz 后重跑本脚本"
    die "基础镜像不可用,无法构建"
  fi
else
  info "基础镜像已就绪: $BASE"
fi

# ---------- 5. 构建镜像并启动 ----------
info "构建镜像并启动容器..."
"${DC[@]}" up -d --build || die "容器启动失败"

# ---------- 6. 健康检查(REST /api/v1/health，不需要登录) ----------
info "等待网关就绪(最长 90 秒)..."
http_ok() {
  curl -fsS -o /dev/null --max-time 3 "$1" 2>/dev/null \
    || wget -q -O /dev/null --timeout=3 "$1" 2>/dev/null
}
ok=""
for _ in $(seq 1 45); do
  if http_ok http://127.0.0.1:48020/api/v1/health; then ok=1; break; fi
  sleep 2
done
if [[ -z "$ok" ]]; then
  warn "网关 90 秒内未就绪,最近 50 行日志:"
  docker logs --tail 50 igw 2>&1 || true
  die "请根据上方日志排查(docker logs -f igw)"
fi

IP="$(hostname -I 2>/dev/null | awk '{print $1}')"
IP="${IP:-<服务器IP>}"
echo
info "部署成功!"
echo "  管理界面  : http://$IP:48020/"
echo "  REST API : http://$IP:48020/api/v1"
echo "  OPC UA   : opc.tcp://$IP:4840"
echo "  数据目录  : $(cd "$IGWDATA" && pwd)/data      (独立于程序目录,脚本永不覆盖/删除)"
echo "  机器码    : $(cd "$IGWDATA" && pwd)/machine-id (授权绑定,勿删)"
echo
echo "常用命令:"
echo "  查看日志 : ${DC[*]} logs -f igw"
echo "  重启     : ${DC[*]} restart igw"
echo "  停止     : ${DC[*]} down"
echo "  升级     : 重跑一键命令(程序目录随便删换,../igwdata 不受影响)"
