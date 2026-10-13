## ADDED Requirements

### Requirement: 执行停滞的自学习基线判定
系统 SHALL 为每个流程节点在线维护"单次执行耗时"基线(P95,P² 分位数估计,O(1) 内存;样本 = 进入 run 到 ok/fail 的毫秒数)。当节点处于 **run** 状态且持续时长超过 `P95 × 3` 时,系统 SHALL 在状态快照中将该节点标记为 `stuck`,并同时输出所用基线值。

#### Scenario: 执行节点超过基线判卡住
- **WHEN** 某 Upload 节点历史执行耗时 P95 为 2 秒,本次进入 run 状态已持续 7 秒
- **THEN** 状态快照中该节点 `stuck = true`,且携带 `baselineMs = 2000`

#### Scenario: 执行中未超基线不误报
- **WHEN** 某 Upload 节点执行耗时 P95 为 2 秒,本次 run 已持续 5 秒(< 3 倍)
- **THEN** 该节点 `stuck = false`

### Requirement: 等待永不判卡
等待(wait)状态 SHALL NOT 参与卡住判定——等待触发/等待复位是产线常态("在等活干"),不是异常;无论等待多久、无论是否配置手工阈值,wait 状态的节点 SHALL 保持 `stuck = false` 且 `baselineMs = 0`(不产生"慢于基线"预警)。等待节点的画布标注 SHALL 以中性信息样式呈现(在等什么、差哪个条件),SHALL NOT 使用警示色。触发间隔(两次触发的间隔)SHALL 仍被采集(供后续"常态节奏"展示),但 SHALL NOT 用于判定。

#### Scenario: 等待触发不是异常
- **WHEN** 某 Trigger 节点等待条件「完成标志=1」已持续 10 分钟
- **THEN** 该节点 `stuck = false`,不出现在异常中心,画布标注为中性信息「⏳ 等待 + 差哪个条件」

#### Scenario: 手工阈值对等待无效
- **WHEN** 某 Trigger 节点配置了 `StuckThresholdMs = 5000`,等待条件已持续 8 秒
- **THEN** 该节点 `stuck = false`(手工阈值仅作用于 run 状态)

### Requirement: 冷启动保护
节点执行耗时基线样本数不足 20 时,系统 SHALL NOT 依据自学习基线判定卡住;此时仅当该节点配置了手工阈值且 run 超时才判 `stuck`。

#### Scenario: 样本不足不判卡住
- **WHEN** 引擎刚启动,某节点仅积累 5 个耗时样本,当前 run 已持续 10 分钟,且未配置手工阈值
- **THEN** 该节点 `stuck = false`

### Requirement: 手工阈值覆盖
`MesNode` SHALL 支持可选字段 `StuckThresholdMs`(缺省/0 表示仅用自动基线),仅对 **run** 状态生效:配置后判定以手工阈值优先(run 持续超过该值即判 `stuck`,不等自学习就绪)。该字段 SHALL 随 `.igwproj` 序列化且向后兼容(旧工程文件缺失该字段可正常加载)。

#### Scenario: 手工阈值优先于自动基线
- **WHEN** 某 Upload 节点配置 `StuckThresholdMs = 5000`,自学习基线 P95×3 为 60 秒,当前 run 持续 8 秒
- **THEN** 该节点 `stuck = true`,`baselineMs = 5000`

#### Scenario: 旧工程文件兼容
- **WHEN** 加载一个不含 `StuckThresholdMs` 字段的既有 `.igwproj`
- **THEN** 工程正常加载,所有节点按上述默认规则判定

### Requirement: 卡住状态的解除
节点离开 run 超时状态(执行完成 ok/fail、或新一轮开始)后,系统 SHALL 在下一次状态快照中清除其 `stuck` 标记。

#### Scenario: 执行完成后解除
- **WHEN** 一个因执行超时被判 `stuck` 的 Upload 节点最终返回并进入 ok
- **THEN** 随后的状态快照中该节点 `stuck = false`
