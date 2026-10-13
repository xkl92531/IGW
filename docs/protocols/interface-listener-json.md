# 后台接口监听与自定义 JSON

IGW 原有 `/hubs/igw-realtime` 是网页连接 IGW 的实时通道。这次新增的是 IGW Runtime 作为客户端连接业务后台；两条连接分别管理，关闭网页不影响后台监听。

## SignalR 示例

在 MES 流程的触发节点选择“接口监听”，协议选择 SignalR：

| 配置 | 示例 |
|---|---|
| Hub 地址 | `https://mes.example.com/hubs/production` |
| 接收方法 | `ReceiveBarcode` |
| 订阅方法 | `SubscribeStation`；后台无需订阅调用时留空 |
| 订阅 JSON | `[{"station":"OP010","consumer":"${taskId}"}]` |
| 消息编号路径 | `data.taskNo` |
| 变化字段 | `data.taskNo` |
| 回执方法 | `Acknowledge` |
| 处理完成回执 JSON | `[{"task":"${input.data.taskNo}","state":"done"}]` |

这是对接配置示例，方法名由实际后台决定。客户端先注册接收处理器，再建立连接，然后调用订阅方法；每次重连都会重新订阅。SignalR 使用官方 .NET Client 10.0.9，面向 ASP.NET Core SignalR JSON Hub 协议。首版接收方法要求一个 JSON 对象参数或一个内容为 JSON 的字符串参数；不支持多个接收参数或旧版 ASP.NET SignalR 2。

后台推送一条消息：

```json
{"data":{"taskNo":"OP010-000001","serialNo":"SN000001","quantity":4}}
```

订阅和回执 JSON 是 **Hub 方法参数数组**。例如 `[{"station":"OP010"}]` 表示一个对象参数，`["OP010",4]` 表示两个参数。接收端看到的是业务参数，不需要填写 SignalR 内部帧。

请求头可设置 Authorization、Cookie、自定义 API Key 等。常见凭据头沿用工程密钥保护和编辑脱敏规则。订阅模板中只有 `taskId`、`lastEventId` 可用；订阅发生在收到业务消息之前，不能引用尚未收到的消息字段。

## 变化判断

编号和变化字段都读取**原始消息**。同一消息编号重复投递不会重复执行；同编号不同内容报冲突。选择业务任务编号作为变化字段，可以区分同一产品的两次独立任务；仅选择产品序列号时，同序列号的新消息会被视为未变化。

首次消息默认触发；关闭“首条消息触发”则仅建立变化基线。之后只比较指定字段，时间戳或其他未选字段变化不触发。缺字段、错误类型会保留消息并显示错误，不继续写 PLC。

“处理完成回执”在本轮配置流程完成并提交后发送，不表示 MES 全部完成。对于仅建立基线、字段未变化的消息，不执行普通回执模板；可单独配置“未变化回执”，例如 SignalR 参数数组：

```json
[{"task":"${input.data.taskNo}","state":"ignored"}]
```

未配置忽略回执时不发送这类回执。

## 通用 JSON 模板和响应映射

上报节点的“类型化 JSON 模板”优先于旧模板；留空时沿用既有上报格式。占位符放在 JSON 字符串值中：

```json
{
  "product": "${input.data.serialNo}",
  "quantity": "${input.data.quantity}",
  "torque": "${fields.Torque}",
  "description": "工位 OP010 / ${input.data.serialNo}"
}
```

完整 `${...}` 保留原始 JSON 类型，上例 quantity 为数字。嵌入文字的占位符变成字符串并正确转义。数组、对象、布尔值和 null 可整值传递；缺失路径报错，不用空串代替。模板仅做 JSON 映射，不执行脚本。

可用上下文：`input` 原始监听消息；`mapped` 输入字段映射得到的对象；`fields` 按上报点位映射构建的对象；`vars` 前序节点捕获的变量（既有变量保持字符串）。周期上报没有接口监听 input，可用 fields/vars。回执没有上报字段上下文，fields 为 null。

路径支持 `data.serialNo`、`items[0].value`、可选 `$.` 前缀，以及读取字面字段名的 JSON Pointer。新路径区分大小写。输入/响应映射均为：

```json
[
  {"sourcePath":"result.code","targetPath":"code"},
  {"sourcePath":"result.plcValue","targetPath":"plc.result"}
]
```

配置响应映射后，上报返回 JSON 被转换为内部对象；业务判断、响应变量和 PLC 回写选择映射后的 `code`、`plc.result`。无需后台采用 `parameterDic` 包装。响应映射为空时保留原有解析。输入映射同理，结果用于 `${mapped...}`，原始 input 保留。

旧版 BodyTemplate 继续保留，新的类型化模板不改变旧模板语法。

## WebSocket 与 SSE

- WebSocket：地址为 ws/wss，连接后发送订阅 JSON，后续按一条文本消息一个 JSON 接收；回执为普通 JSON，非 SignalR 参数数组。
- SSE：地址为 http/https，HTTP GET 加 `Accept: text/event-stream`，支持事件名过滤、多行 data、id 和 Last-Event-ID 重连。SSE 不发送订阅或回执 JSON，界面保留的其他协议模板在 SSE 模式不执行。
- 两者与 SignalR 都有消息大小、空闲超时、重连退避配置。后台需提供心跳；单纯 HTTP keep-alive 不能让普通请求变成变化推送。

## 持久化、重试与恢复

收到的有效消息先写入数据目录 `mes-interface-inbox.db`，SQLite WAL/FULL。接收与流程独立，单任务顺序处理；失败保持当前消息，上报重试使用同一冻结请求 JSON，并带 `X-IGW-Message-Id` 与 `X-IGW-Step-Id`。上报与本地提交不构成分布式事务，后台需按消息和步骤编号去重。

队列满时暂停接收，不删除已有消息。SignalR 还限制尚未入库的回调队列，超过限制会显式报错并断线。**后台必须保留未确认消息并支持按编号重发/重新订阅补发**，才能覆盖断线、满队列或进程异常期间尚未落盘的数据；网关无法补回后台未保留的推送。SSE 的 Last-Event-ID 需要后台支持重放。

已提交回执也会保存。连接断开导致回执发送失败时，后台重复投递同编号同内容消息可取得既有回执，不重复执行流程。

Inbox 以稳定任务 Id 为入口；移动画布节点、调整连接请求头不重置消息。积压未处理时更改来源、业务映射或执行流程会停下该监听并提示恢复原配置，避免按新规则执行旧消息。历史消息和回执当前不自动清理，需规划磁盘容量。

## 验证范围

通过本地 SignalR Hub、WebSocket/SSE 服务和模拟 PLC 写入验证；尚未部署，未对接实际业务后台，未做 Linux 实机验证。

2026-09-11 最终验证：后端 1388/1388、前端 53 个文件 244/244 通过，无跳过；Web 类型检查和生产构建通过，Runtime/Windows 配置器构建均 0 警告、0 错误。后端报告位于 `artifacts/protocol-qa/interface-full-regression.trx`。新增验证覆盖 SignalR Hub 订阅/回执/过载、SSE/WS 边界、JSON 类型、持久化重开、条件阻塞、未变化回执及 PLC 回写。Web 支持样例预览，WPF 提供字段和 JSON 编辑；本轮未做界面截图验收。

官方协议用法：[SignalR .NET 客户端](https://learn.microsoft.com/en-us/aspnet/core/signalr/dotnet-client?view=aspnetcore-10.0)。
