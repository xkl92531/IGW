## ADDED Requirements

### Requirement: 丢弃计数不静默
有界队列因背压丢弃变化事件时,系统 SHALL 计数并区分来源:每分片的总线丢弃数、每订阅的队列丢弃数。丢弃 SHALL NOT 无痕发生;计数 SHALL 累计并可随时读取。

#### Scenario: 总线丢弃可见
- **WHEN** 某分片总线队列满导致最旧事件被丢弃
- **THEN** 该分片的丢弃计数递增且可查询

#### Scenario: 订阅丢弃可见
- **WHEN** 某订阅队列满导致丢弃
- **THEN** 该订阅的丢弃计数递增且可查询,其他订阅计数不受影响

### Requirement: 数据面状态暴露
系统 SHALL 通过 `/health` 暴露数据面统计(分片数、各片队列深度与丢弃数、各订阅滞后与丢弃数),监控中心 SHALL 展示这些指标。

#### Scenario: /health 携带数据面统计
- **WHEN** 客户端请求 `/health`
- **THEN** 响应包含分片与订阅级的深度/丢弃统计字段

### Requirement: 展示不制造告警噪音
数据面背压指标 SHALL 以监控中心常驻展示呈现,SHALL NOT 因丢弃计数变化产生告警事件或弹窗打断。

#### Scenario: 有丢弃时不弹告警
- **WHEN** 出现队列丢弃
- **THEN** 监控中心指标更新,但不产生告警记录与前端弹窗
