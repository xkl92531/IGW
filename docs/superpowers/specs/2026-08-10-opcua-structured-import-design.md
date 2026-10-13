# OPC UA 结构化浏览导入设计

## 目标

IGW 浏览上游 OPC UA 服务器时，必须把结构变量的组件层级原样带入导入预览和工程树；用户可以修改导入后的结构名称与点位名称，但底层 OPC UA NodeId 保持不变。

## 后端浏览模型

- `OpcUaBrowseWalker` 不再把所有 `NodeClass.Variable` 都视为叶子。
- Variable 节点存在非属性的层级子节点时，将它作为结构容器继续递归；没有结构子节点时才生成 `BrowsedTag`。
- `HasProperty` 元数据节点不参与结构导入，防止把 `EngineeringUnits`、`EURange` 等属性误当工艺字段。
- `BrowsedTag` 增加 `PathSegments`，保存服务器返回的原始路径段；保留现有 `Path` 兼容旧调用方。
- 遍历继续执行循环检测、32 层深度保护、10000 点截断和取消检查。

## 导入预览与重命名

- Web UI 优先使用 `pathSegments` 构建树；旧运行时没有该字段时才回退解析 `path`。
- 每个结构节点和点位节点拥有独立的“目标名称”，默认等于服务器 DisplayName。
- 修改结构节点目标名称后，预览中全部后代的 `groupPath` 自动使用新名称。
- 修改点位目标名称只影响该点位的工程 `name`。
- NodeId 始终保存在 `address`，任何重命名都不得修改它。
- 目标名称不能为空、不能包含 `.`，同级名称按现有导入冲突逻辑校验。
- 搜索和勾选使用源节点身份，不受目标名称变化影响。

## 应用与兼容

- `buildImportPreview` 和 `applyTagImport` 继续作为唯一工程写入路径。
- 导入设备时写入 `device.groups`；导入组时写入 `group.subGroups`，不得创建不会被工程树读取的错误属性。
- 未修改名称时，导入结果与服务器层级一致。
- 旧版 REST 响应仅包含 `path` 时仍可导入。

## 测试与验收

- 后端单元测试覆盖：普通叶子、Variable 结构体递归、嵌套结构、属性过滤、路径段中包含点号、截断保护。
- Web UI 测试覆盖：显式路径段优先、结构重命名级联、点位重命名、NodeId 不变、设备/组两种目标容器正确落盘。
- 现有 IGW 测试、Web UI 构建和 OPC UA 导入相关测试全部通过。
