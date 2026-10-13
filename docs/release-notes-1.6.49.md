# IGW 1.6.49 发布说明

> 此安装包已由 1.6.50 替代。1.6.50 补齐了日志模块、功能、采集任务和接口调用上下文，并重做日志筛选布局。

发布日期：2026-09-13

## 安装器修复

- 日志存储位置页面不再通过 Inno Pascal Script 创建测试文件或查询磁盘空间，彻底移除触发 `Type Mismatch` 的运行路径。
- 点击“下一步”只保存选择的目录并继续安装。
- 安装文件复制完成后，由 `IGW.LogService.exe --initialize` 使用 .NET 完成目录创建、写入探测和至少 1 GiB 可用空间检查。
- 目录无效、不可写或空间不足时，日志服务返回明确中文错误，安装器不会使用 Pascal Script 类型转换。

## 验证

- 使用当前源码对 `C:\ProgramData\IGW\observability` 执行隔离初始化，日志目录预检和配置初始化成功，返回码为 0。
- .NET 日志目录预检测试覆盖目录创建、写入探测、临时文件清理和低于 1 GiB 时拒绝。
- 安装器契约测试确认 Inno 脚本不再包含 `NextButtonClick`、`GetSpaceOnDisk` 或 `SaveStringToFile` 日志目录校验。

## 保持不变

- IGW Web/API 端口保持 `48020`。
- OpenObserve 优先使用本机 `28020/28021`，遇到占用时自动选择下一组连续空闲端口。
