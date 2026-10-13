# JSON 配置工程交换设计

## 目标

Web 工程配置中的整体文件导入和导出统一使用 JSON 配置。用户可导入 KEPServer 导出的 JSON，也可在两台 IGW 之间迁移包含采集流程的完整工程配置；界面不再提供 `.igwproj` 文件导出。

## 边界

- IGW 后台的工程保存、自动保存和运行时加载机制保持不变。
- 根对象 `project` 承载 KEP 能表达的连接性内容：工程名称、通道、设备、嵌套标记组、标记、地址、数据类型、访问权限、扫描周期和说明。
- 根对象 `igw` 是 IGW 扩展，保存 MES 流程、流程模板、OPC UA 服务端、历史、授权与其他工程级配置。
- `igw.project` 不重复保存 `channels`，避免百万点工程的导出文件和浏览器内存占用翻倍。
- 导入有 `igw` 扩展的文件时恢复完整 IGW 工程级配置；没有扩展的原生 KEP JSON 仍按连接性工程导入。

## 映射

- 通道：`common.ALLTYPES_NAME`、`common.ALLTYPES_DESCRIPTION`、`servermain.MULTIPLE_TYPES_DEVICE_DRIVER`、`devices`。
- 设备：名称、说明、同一 KEP 驱动名、`servermain.DEVICE_ID_STRING`、`tags`、`tag_groups`。设备地址拆分为主机和端口；已明确的驱动专属参数使用该驱动的 KEP 属性（当前包含 Siemens 型号、端口、Rack 和 Slot）。
- 组：名称、说明、`tags`、`tag_groups`。
- 标记：名称、说明、地址、KEP 数据类型码、读写权限、扫描周期。
- 驱动：对 IGW 已知驱动输出 KEP 标准显示名；未知驱动保留原 `driverId`，不静默替换。
- 数据类型按 KEP 官方枚举映射，包含标量和数组。导入端同步修正 LLong/QWord 与数组枚举，保证往返一致。

## 交互

- 工具栏入口显示“导入 JSON 配置”和“导出 JSON 配置”。
- 导出文件名为 `<工程名>.json`，不再生成 `.igwproj`。
- KEP 无法表达的工程级字段写入 `igw` 扩展，不影响 KEP 连接性结构。
- 导入继续执行数量预览和替换确认。

## 验证

- 单元测试校验 KEP `project` 结构和 IGW 扩展字段。
- 使用包含设备、嵌套组、采集流程、流程节点、连线、模板、说明、权限和扫描周期的代表工程执行导出后再导入，核对结构与关键属性不丢失。
- 页面测试确保不存在 `.igwproj` 导出，并且下载扩展名为 `.json`。
- 运行完整 Web 测试、类型检查和生产构建。
