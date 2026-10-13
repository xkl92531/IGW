# deploy\ · IGW Docker 部署套件(自包含,不改仓库其它文件)

一键「Windows 打 zip → 上传 Ubuntu → Docker 运行」。

## 文件

| 文件 | 作用 |
|---|---|
| `package-docker.ps1` | Windows 打包脚本(构建 WebUI + linux-x64 自包含发布 → 组装 → zip) |
| `build-s7plus-linux.ps1` | 交叉编译 `libigw_s7plus.so`(S7CommPlus/S7-1500 符号地址),编完重打包自动带上 |
| `pull-base-image.py` | 免守护进程直拉基础镜像(注册表 v2 API → docker-archive tar);podman 不可用时的兜底 |
| `Dockerfile` | 镜像定义(基于 runtime-deps:10.0-noble;比 `installer\docker` 版多 `chmod +x`,兼容 zip 丢执行位) |
| `docker-compose.yml` | 容器编排(48020 Web/REST、4840 OPC UA、data 与 machine-id 持久化) |
| `deploy.sh` | Ubuntu 一键部署脚本(随 zip 分发):装 Docker(如缺)→ 建镜像 → 起容器 → 健康检查 |
| `out\` | 打包输出(可删,自动重建) |

## Windows 打包

前置:dotnet SDK 10、node + pnpm(WebUI 依赖已装过;缺依赖时先在 `src\IGW.WebUi` 执行 `$env:CI='true'; pnpm install`)。

```powershell
powershell -ExecutionPolicy Bypass -File deploy\package-docker.ps1            # 完整构建(含基础镜像离线包)
powershell -ExecutionPolicy Bypass -File deploy\package-docker.ps1 -SkipWebUi # 复用已构建的网页端
# 其它:-SkipBaseImage 不捆绑基础镜像;-RefreshBaseImage 强制重拉(否则复用 rundata\image-cache\basedeps.tar)
```

产物:`deploy\out\igw-<版本>-docker-linux-x64.zip`(版本号取 `installer\version.txt`)。

**基础镜像离线包**:`mcr.microsoft.com/dotnet/runtime-deps:10.0-noble` 会被拉成包内 `basedeps.tar`(默认 podman 拉取+导出;podman machine 不可用时自动回退 `pull-base-image.py` 注册表直拉)。服务器部署时 deploy.sh 按「本地已有 → 包内 tar 离线加载 → 联网拉取」取用——服务器无外网/DNS 坏也能装。

## Ubuntu 一键部署

上传 zip 后**一条命令**(自动安装 Docker[如缺]→构建镜像→启动→健康检查):

```bash
sudo bash <(unzip -p igw-<版本>-docker-linux-x64.zip igw-docker/deploy.sh) igw-<版本>-docker-linux-x64.zip
```

或分步:

```bash
unzip igw-<版本>-docker-linux-x64.zip -d /opt
cd /opt/igw-docker
sudo bash deploy.sh
```

- 管理界面 `http://<IP>:48020/`;OPC UA `opc.tcp://<IP>:4840`;REST `http://<IP>:48020/api/v1`。
- **数据独立存放**在与 `igw-docker` 平级的 `igwdata/`(`data/`=工程/授权/日志/证书 → 容器 `/var/lib/igw`;`machine-id`=机器码标识):**脚本永不覆盖/删除,由人工管理**(备份=整目录拷走)。注意 `igw-docker` 与 `igwdata` 须保持平级(compose 用 `../igwdata` 相对挂载)。
- 常用命令:`docker compose logs -f igw` / `restart igw` / `down`。
- 升级:重跑一键命令即可——程序目录 `igw-docker` 随便删换,`igwdata` 不受影响。

## 故障排查

- **`lookup mcr.microsoft.com on 127.0.0.53:53: i/o timeout`**(服务器 DNS 坏,最常见):`ping -c2 223.5.5.5` 通则只需修 DNS——`sudo systemctl restart systemd-resolved`,不行再配上游 `DNS=223.5.5.5 119.29.29.29`(写入 `/etc/systemd/resolved.conf.d/dns.conf` 后重启 resolved);ping 也不通=无外网,走下面的离线方案。
- **基础镜像拉不动/想走加速**:`IGW_BASE_IMAGE=<可达镜像地址> sudo bash deploy.sh`(Dockerfile 的 FROM 经 ARG 可替换);deploy.sh 已内置预拉取与逐条排查提示。
- **完全离线部署**:新包已内置 `basedeps.tar`,deploy.sh 自动 `docker load`,服务器无需外网;旧包或 `-SkipBaseImage` 打的包,按「找台有网机器 `docker pull` + `docker save` 拷来 `docker load`」处理。
- `WARN: ... buildx isn't installed`:无害(回退传统构建);想消除可 `apt-get install -y docker-buildx-plugin`。

## 注意

- **S7CommPlus(符号地址)支持**:`libigw_s7plus.so` 随包发布。它没有现成二进制可下载(本项目自研 Rust 组件),由 `deploy\build-s7plus-linux.ps1` 在本机交叉编译产出(离线 Rust 工具链在 `rundata\`,zig 经 pip `ziglang` 提供);`native\igw-s7plus` 源码变更后先跑它、再跑 `package-docker.ps1`。若包内无此文件,S7-1500 符号地址不可用,Classic 绝对地址不受影响(打包时会警告)。
- LogService / OpenObserve(观测栈)不在 Docker 套件内,按需单独部署。
