## ADDED Requirements

### Requirement: 等待类节点的逐条件归因
当 Trigger/Condition/握手等待节点处于 wait 状态时,节点状态快照 SHALL 携带结构化 `WaitDetail`:每个条件一条记录,含 `TagId`、比较符、期望值、当前实际值、是否命中(`Met`)。实际值 SHALL 在条件求值现场捕获(不得为此额外发起设备读)。

#### Scenario: 未命中条件被标出
- **WHEN** 某 Trigger 节点等待条件「工位状态=3(实际 3)」与「PLC完成标志=1(实际 0)」
- **THEN** 快照 `WaitDetail` 中第一条 `Met = true`、第二条 `Met = false` 且携带实际值 `0`

#### Scenario: 点位不可读时的归因
- **WHEN** 某等待条件引用的点位当前质量为 Bad
- **THEN** 该条件记录的实际值 SHALL 标明质量异常(而非展示陈旧值),`Met = false`

### Requirement: HTTP 上报失败归因
Upload 节点进入 fail 状态时,快照 SHALL 携带 `HttpFailDetail`:HTTP 状态码(网络层失败时为 0 并给出异常摘要)、响应体片段(截断至 512 字符)、当前重试次数、目标 URL。

#### Scenario: MES 返回业务失败
- **WHEN** 上报收到 HTTP 200 但响应 `code != 0` 判为失败
- **THEN** `HttpFailDetail` 含状态码 200、响应片段(含 code 字段)与重试次数

#### Scenario: 网络超时
- **WHEN** 上报请求超时无响应
- **THEN** `HttpFailDetail` 状态码为 0,异常摘要注明超时,含目标 URL

### Requirement: 回写失败归因
Writeback 节点进入 fail 状态时,快照 SHALL 携带 `WriteFailDetail`:失败点位 `TagId`、下发值、读回确认值(若执行了读回)、当前重试次数。

#### Scenario: 读回不一致
- **WHEN** 向 `PLC.Result` 写入 `1`,读回确认得到 `0`
- **THEN** `WriteFailDetail` 含 `TagId = PLC.Result`、下发值 `1`、读回值 `0`

### Requirement: 归因快照的兼容性
`MesNodeStatus` 快照 SHALL 以新增字段方式携带 Detail(多态,以 `kind` 区分类型);既有字段(Id/State/Info/AgeMs/Hits)的名称与语义 SHALL 保持不变;节点状态为 ok/idle 时 Detail SHALL 为空(不增加正常态快照体积)。

#### Scenario: 正常节点不带归因载荷
- **WHEN** 某节点状态为 ok
- **THEN** 其快照条目不含 Detail 字段

### Requirement: 画布归因面板
画布 SHALL 在用户点击异常节点(或经异常中心跳转聚焦)时展示归因面板:等待类展示逐条件"期望 vs 实际"并高亮未命中条件;HTTP 失败展示状态码/响应片段/重试次数;回写失败展示点位与读回差异。

#### Scenario: 点击等待节点看到凶手条件
- **WHEN** 用户点击一个 `stuck` 的 Trigger 节点
- **THEN** 面板逐行列出各条件的期望值与当前实际值,未命中行以醒目样式标出
