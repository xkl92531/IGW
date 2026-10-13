# IGW OPC UA 只读信息采集器

用于现场采集 OPC UA Server 公开的 NodeId、Namespace URI、完整浏览路径、数据类型、数组维度、访问权限、角色权限、访问限制、DataTypeDefinition、服务状态、拒绝计数和标准服务器能力值。

工具不会读取业务变量值，不创建订阅、不调用 Method，也不执行 Write。服务器标准能力值（例如 `MaxNodesPerWrite`）会被读取，用于分析批量写入限制；同时会从本机 `%ProgramData%\IGW\logs` 提取最近的 OPC UA/写入失败日志并脱敏，保留真实 `StatusCode` 与异常栈供定位。

遇到西门子服务器 `BadNoContinuationPoints` 时，工具会先释放当前续传点、自动缩小 Browse 并发窗口并重试，不会因为节点较多直接终止整次采集。

双击 `IGW.OpcUaProbe.exe` 后按提示操作。完成后会在指定目录生成 `IGW-OPCUA-Probe-<IP>-<时间>.zip`，将该 ZIP 文件发回分析即可。

命令行示例：

```powershell
.\IGW.OpcUaProbe.exe --url opc.tcp://10.217.152.80:4840 --output C:\Probe
```

现场电脑有多个网卡时，请双击后从列表选择 PLC 所在网卡，或显式指定本机 IPv4：

```powershell
.\IGW.OpcUaProbe.exe --url opc.tcp://10.217.152.80:4840 --local-ip 10.217.152.221 --output C:\Probe
```
