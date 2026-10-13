# IGW 1.6.40 近期修改交接

更新时间：2026-09-07
基线提交：`575c074`
目标版本：`1.6.40`

本文用于在另一台电脑继续开发、验证或打包。工程配置、现场日志、安装包和 `dist` 构建目录没有纳入 Git 提交。

## 主要修改

### OPC UA 与西门子数据类型

- 自动浏览保留服务器返回的真实数据类型、数组维度、下界、字符串容量和读写权限，避免把 Byte Array、Short、DWord 等误显示为 Double。
- 增加 S7-1500 常用类型：SINT、WORD、DWORD、LWORD、CHAR、WCHAR、DATE、TOD、LTOD、TIME、LTIME、S5TIME、DT、LDT、DTL、TIMER、COUNTER 及对应数组。
- DTL 支持 ExtensionObject 解码、REST/MES 显示和写回；DT 的 8 字节传输形状按日期标量处理。
- 普通 Byte Array 保持字节数组，不再仅凭长度猜测为日期类型。
- OPC UA 写入按目标类型归一化，并保留字符串写入兼容回退。

详细类型范围见 [siemens-opcua-types.md](siemens-opcua-types.md)。

### OPC UA 连接与采集稳定性

- 同一端点、账号、安全策略、超时和网卡配置的逻辑设备复用一条 OPC UA 物理会话；每个设备保留独立租约和订阅。
- 自动生成标签的连续浏览复用预览会话，避免反复建立连接。
- 增加写优先、有公平让行和队列上限的请求调度；连接失败按 1、2、4、8、16、30 秒退避。
- 连续三次确认 `BadNodeIdUnknown` 的点位进入约 30 秒一次的低频复查，正常点继续按原周期采集。
- 读取错误日志合并重复信息，恢复时输出恢复记录；心跳成功日志按时间窗口汇总。
- 修复保存工程碰上其他 PLC 硬重连时，QC 长时间收到 `503 project_reloading` 和 `BadNotConnected`：等待设备生命周期锁期间保持旧运行态，取得锁后才进入实际切换窗口。

详细策略见 [opcua-stability.md](opcua-stability.md)。

### 采集性能与 QC

- 新增“采集性能”页面，显示设备设定周期、平均/P95 完成间隔、最近批次耗时、驱动耗时、本地处理、实际请求点数、协议请求数、排队时间、失败/取消次数、吞吐和漏期。
- 每台设备在内存中保留最近 200 批采集记录；诊断页面读取缓存，不会额外读取 PLC。
- QC 增加“最近批次耗时”，并显示批次开始时间、驱动/本地耗时、周期、实际间隔和批次点数。
- 自动生成标签窗口显示连接、展开和扫描分支的耗时及失败阶段。

指标口径见 [scan-performance.md](scan-performance.md)。

### 日志中心

- 修复日志中心无数据的问题，兼容当前日志格式、日期筛选和运行目录。
- 增加日志存储目录选择；空值继续使用默认数据目录下的 `logs`。
- 日志设置只应用设置范围，不重启 PLC、MES 或北向服务。
- Runtime 启动时记录程序集版本、构建标识、模块标识、程序路径、PID 和启动 ID，方便确认现场实际运行版本。

### MES 采集流程

- 数组范围长度增加“固定长度”和“PLC 点位长度”两种互斥模式；PLC 点位模式按每一维配置的整数点位动态截取。
- 附加字段恢复为“字段名 + 固定值”两个输入，提交时一起写入上报 JSON。
- 增加流程批量管理：可一次保存全部流程，也可勾选多条流程统一修改采集周期后保存。
- 批量保存使用单次原子项目变更，保留流程启用状态并处理版本冲突。
- 修复从其他流程保存时，当前流程采集周期没有真正持久化的问题。

## 验证记录

- 后端：`dotnet test tests/IGW.Tests/IGW.Tests.csproj --no-restore`，847 项通过。
- Web Vitest：43 个文件、177 项通过。
- Web Node 契约测试：227 项通过。
- `vue-tsc --noEmit` 通过。
- Vite 生产构建通过。
- Inno Setup 安装包编译通过，Runtime 文件版本为 `1.6.40.0`。
- 本轮没有修改 `native/igw-s7plus` 源码；安装包沿用现有 Release 原生 DLL。

## 另一台电脑接续方式

如果能访问 Git 远程仓库，拉取包含本提交的分支后直接继续开发。若暂时不能访问远程仓库，可复制随本次交付生成的 `IGW-1.6.40.bundle`，在目标电脑执行：

```powershell
git clone .\IGW-1.6.40.bundle IGW
Set-Location .\IGW
git status
```

如果目标电脑已经有仓库：

```powershell
git fetch C:\传输目录\IGW-1.6.40.bundle master:refs/remotes/transfer/igw-1.6.40
git checkout -b codex/igw-1.6.40 transfer/igw-1.6.40
```

安装包属于构建产物，没有纳入 Git。当前生成位置为 `installer/out/IGW-Setup-1.6.40.exe`。

## 现场继续关注

- PLC5、PLC7 日志仍出现远端主动关闭 OPC UA 会话的 `BadConnectionClosed`。1.6.40 已限制其对其他 PLC/QC 的连带影响，但远端关闭原因仍需结合交换机、PLC OPC UA 会话资源和现场后续日志确认。
- 升级后无需重新保存工程即可获得会话复用、重连隔离、性能监控和 QC 重载窗口修复。已有点位若历史上导入了错误类型，需要重新自动导入或手工改为服务器真实类型。
