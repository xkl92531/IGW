# IGW OpenObserve 日志中心验收记录

验收日期：2026-09-13
基础版本：1.6.47；当前验收版本：1.6.50
分支：`codex/openobserve-log-center`

## 交付产物

- Windows 安装包：`installer/out/IGW-Setup-1.6.50.exe`
- 文件大小：175,377,735 字节（167.25 MiB）
- SHA-256：`EDA8594ECF0B4312FFC2D18DC4E04911D0272FE169786F3783B11F6DD7EF7DCF`
- 内置 OpenObserve：1.0.0，Windows 和 Linux 压缩包均由 Git LFS 管理。
- Windows OpenObserve SHA-256：`c9b4710873bedd297475657074fd75da9cfeaf6372ea962fcb6157d9102fdf5c`
- Linux OpenObserve SHA-256：`debba8c9267498bff9ca0994b1b96f9a27bab9e9b1cba1941b3ad89c3db4a1e8`

## 自动化验证

- 后端共 1590 项。首轮 1586 项通过；2 项因本机 OpenObserve 占用默认端口而得到动态端口，另 2 项并行时序断言单独复跑通过。排除两项实时端口环境断言后，1588/1588 通过。
- 前端 Vitest：71 个文件、320 项测试全部通过。
- 前端类型检查：`vue-tsc --noEmit` 通过。
- 前端生产构建：Vite 构建通过；仅有第三方包的 Rollup PURE 注释位置警告。
- 仓库 CJS 契约：245/245 通过。
- Windows 发布布局：Runtime、LogService、OpenObserve 和许可证校验通过。
- 安装器：Inno Setup 6.7.3 编译成功。

## 1.6.49 安装页修复验证

- 1.6.48 证明即使换用 MiB/`Cardinal` 并捕获异常，部分电脑仍会在 Inno Pascal 中返回 `Type Mismatch`，因此 1.6.49 完全移除了安装页的 Pascal 目录校验。
- 安装器契约确认脚本不再包含 `NextButtonClick`、`GetSpaceOnDisk` 或 `SaveStringToFile` 日志目录校验。
- .NET 预检测试覆盖目录创建、写入探测、临时文件清理和低于 1 GiB 时拒绝。
- 使用当前源码和隔离配置目录，对截图中的 `C:\ProgramData\IGW\observability` 执行日志服务初始化；返回码为 0。

## 1.6.50 日志上下文与界面验证

- 普通 `ILogger` 记录由统一提供器补充模块、功能、事件类型和 logger 来源，结构化业务日志保留自己更精确的字段。
- 采集任务归属按注册任务紧凑保存，在扫描计划重建时预计算到批次；采集热路径不遍历标签或任务。共享批次只生成一条日志，任务展示列表最多 32 项，同时保存真实任务总数、全部来源类型和截断标记。
- 10,000 点紧凑归属测试和 33 个共享任务测试通过；后者确认超过展示上限时总数与不同来源仍准确。
- MES HTTP、REST 和 SignalR 测试确认任务、节点、方向、协议、稳定路由、目标接口、操作、结果与耗时字段写入正确；异常、取消与超时不会再误记为传输完成。
- 日志查询 API 的集成测试确认 `module`、`feature`、`task` 和 `interface` 参数进入 OpenObserve SQL；返回记录优先使用结构化分类，不再被 `Device:...` logger 名覆盖。OpenObserve 返回缺失字段错误时会自动执行旧结构安全查询。
- 主搜索框覆盖消息、设备、任务名称/ID 和稳定 Trace ID；统一日志提供器为所有可查询字段建立稳定字符串列，空值不会显示在详情中。
- 日志筛选栏拆成常用和高级两层，响应式布局在窄屏自动换行；记录行和详情抽屉都能直接识别模块、功能、任务、接口和 PLC 范围。
- 后端完整运行共 1590 项：1586 项通过；2 项默认端口断言因本机正在运行的 OpenObserve 占用 `28020/28021` 而得到 `28022/28023`，另 2 项并行时序断言单独复跑通过。排除两项实时端口环境断言后，1588/1588 通过；端口选择算法的占用测试仍通过。
- 前端 Vitest：71 个文件、320 项测试全部通过；`vue-tsc --noEmit`、Vite 生产构建及 245 项 CJS 契约全部通过。
- Runtime、LogService、OpenObserve 1.0.0 发布布局、许可证和可执行文件校验通过；安装器 ProductVersion 为 `1.6.50`。

## 端口占用验证

使用发布目录中的 `IGW.LogService.exe`，先在 `127.0.0.1:28020` 建立独占监听，再执行首次初始化。结果为：

- 首选 HTTP：28020（已占用）
- 自动选择 HTTP：28022
- 自动选择 gRPC：28023
- 配置正常写入包含中文和空格的日志目录。

单元测试同时覆盖 HTTP 或 gRPC 任一端口被占用时跳过整组端口。OpenObserve 的 HTTP 和 gRPC 地址都固定为 `127.0.0.1`，安装器不给 OpenObserve 创建入站防火墙规则。

## 回环集成验证

在隔离目录启动真实 OpenObserve 1.0.0、LogService 和 IGW Runtime，并使用 Simulator 工程进行只读采集：

- OpenObserve `/healthz` 返回 200，IGW `/api/v1/log-center/status` 返回 `ready`。
- 日志查询返回 Runtime、采集和性能记录；采集记录包含设定周期、实际间隔、总耗时、驱动耗时、标签/样本数和 trace ID。
- 8 个模拟标签、200 ms 周期下，读取成功 28 次、失败 0 次；实际平均间隔 199.81 ms，漏期 0；最近批次 16 ms。
- 停止 LogService 后，Runtime 健康接口和 Simulator 采集继续运行，查询按约定返回 503，磁盘队列从 0 增长且失败/丢弃均为 0。
- 重启 LogService 后队列回放到 0，日志重新可查询。
- 日志存储迁移的复制、状态、失败保留原目录由自动化测试覆盖；本轮未复制真实 2 GiB WAL 做完整现场迁移。

## 构建环境说明

当前电脑的 Visual Studio C++ 安装缺少 `vcvarsall.bat`、标准 MSVC 头文件和库，Rust 原生组件无法在本轮重新链接。日志中心分支未修改 `native/igw-s7plus` 源码，因此安装包复用了上一版已校验的 DLL；源文件差异为空，复用 DLL 的 SHA-256 为 `11364AE4AF70DC3A18B4E4C554F2B7E2211383FB26F3286F33B2F42AACCFD615`。Runtime 和 LogService 均从当前源码重新发布。

## 原创性和验收边界

- 日志页面的模板结构、组件拆分、CSS、文案、趋势图和交互均在 IGW 中独立实现；只复用了 OpenObserve 官方第三方发布物及许可证。
- 未连接真实 PLC，未执行现场 clean-machine 安装、长时间稳定性或真实磁盘完整迁移。
- 用户提供的 `模拟器驱动程序演示 (10).json` 在只读导入检查中发现 `PLC9.OP020.PackGlue.GlueData.0.TimeStamp` 与现有工程的地址、类型、数组或权限冲突，因此未强制导入。
