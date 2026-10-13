# 后台长连接与自定义 JSON

用户已确定 IGW 作为客户端连接后台并接收业务字段变化，随后指出项目已有 SignalR。默认增加 SignalR 客户端选项，同时提供 WebSocket/SSE；监听与 HTTP 上报共用自定义 JSON 模板和字段路径解析。现有网页到 IGW 的 SignalR 连接保留。

MES 增加 InterfaceEvent 触发模式及独立 listener 配置。WebSocket 可发送订阅 JSON 和处理回执 JSON；SSE 使用 HTTP GET、请求头及 Last-Event-ID，不伪装成双向接口。监听连接与流程处理独立：收到消息持久化到单独 inbox 表，按任务顺序消费，失败保留消息；重连带退避，禁止满队列丢最旧消息。相同消息 ID 不重复消费，指定变化字段与最后已消费消息比较。默认首条消息触发，首次仅建基线可配置。

JSON 路径支持点分对象和数组索引；新模板用整个字符串占位符 ${path} 保留原生 JSON 类型，嵌入文本占位符按字符串替换并正确转义。已有 BodyTemplate 格式保持兼容。HTTP 响应通过显式路径映射生成内部字段，用于变量、业务判定和 PLC 写入；配置前预览，不运行任意脚本。

监听配置可指定 SetVin、条码、任务编号和数量路径。收码调用现有 TighteningJobCoordinator，保留单设备任务占位、幂等、确认后 ready、不确定禁止覆盖规则。未完成任务来的下一件在 inbox 等待；相同 ID 不同内容报冲突。事件上下文提供 input 和 job 给后续模板。仅软件 ready，无物理启动联锁。

使用 .NET 10 和现有浏览器依赖；SignalR 使用官方 Microsoft.AspNetCore.SignalR.Client 10.0.9，匹配现有服务端依赖。缓存查无此包后恢复依赖并缓存至 D:/sdk/nuget/signalr-client-10.0.9。保留周期采集及 Open Protocol 结果事件模式。通过 JSON 类型/路径测试、真实 SignalR Hub/WebSocket/SSE 测试、引擎集成和前端组件测试后再执行回归。Windows 编译与模拟测试不代表 Linux 或现场联调。

任务 Id 为稳定 Inbox 入口；积压中更改消息来源、业务映射或流程会阻止该监听按新规则执行旧消息。节点位置和鉴权请求头不影响消息入口。字段未变化时可以配置独立 IgnoreResponseTemplate，避免访问不存在的 job/vars。SSE 模式保留但不执行其他协议的订阅/回执模板。
