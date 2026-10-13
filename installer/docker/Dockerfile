# IGW 工业网关 · Docker 镜像(自包含发布,无需 .NET 运行时)
# 构建上下文:发布产物所在目录(app/ + 本文件 + docker-compose.yml)
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-noble

# 时区(日志时间戳用本地时间;镜像自带 tzdata)
ENV TZ=Asia/Shanghai
RUN test -f /usr/share/zoneinfo/$TZ \
    && ln -sf /usr/share/zoneinfo/$TZ /etc/localtime \
    && echo "$TZ" > /etc/timezone || true

WORKDIR /app
COPY app/ ./
RUN chmod +x /app/IGW.Runtime /app/questdb/bin/java

ENV IGW_DATA_DIR=/var/lib/igw \
    DOTNET_EnableDiagnostics=0

# 48020 = Web 管理端 + REST API;4840 = OPC UA Server(opc.tcp)
EXPOSE 48020 4840
VOLUME ["/var/lib/igw"]

ENTRYPOINT ["/app/IGW.Runtime"]
