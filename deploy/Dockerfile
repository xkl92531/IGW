# IGW 工业网关 · Docker 镜像(自包含发布,无需 .NET 运行时)
# 本文件属于 deploy\ 独立部署套件:构建上下文 = 打包产物目录(app/ + 本文件 + docker-compose.yml)
# 与 installer\docker\Dockerfile 的差异:补 chmod +x —— zip 分发会丢执行位,Linux 解压后 IGW.Runtime 不可执行
# 基础镜像可替换(镜像加速/离线场景):IGW_BASE_IMAGE=<地址> 由 deploy.sh / compose 注入
ARG BASE_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0-noble
FROM ${BASE_IMAGE}

# 时区(日志时间戳用本地时间;镜像自带 tzdata)
ENV TZ=Asia/Shanghai
RUN test -f /usr/share/zoneinfo/$TZ \
    && ln -sf /usr/share/zoneinfo/$TZ /etc/localtime \
    && echo "$TZ" > /etc/timezone || true

WORKDIR /app
COPY app/ ./
# zip 解压不带执行位,统一补齐
RUN chmod +x /app/IGW.Runtime /app/questdb/bin/java

ENV IGW_DATA_DIR=/var/lib/igw \
    DOTNET_EnableDiagnostics=0

# 48020 = Web 管理端 + REST API;4840 = OPC UA Server(opc.tcp)
EXPOSE 48020 4840
VOLUME ["/var/lib/igw"]

ENTRYPOINT ["/app/IGW.Runtime"]
