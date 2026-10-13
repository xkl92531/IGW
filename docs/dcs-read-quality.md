# 逐点质量读取：`POST /Tag/ReadTagsWithQuality`

> 起始版本：IGW 1.6.78
> 本文是对外契约：字段名、`reason` 取值和映射规则保持稳定；如需变更，只新增、不修改已有含义。

## 为什么需要

`/Tag/ReadTagsAsList` 等旧接口是“全有或全无”：只要一个点没注册、没权限或质量不是 Good，整个请求就失败，只返回一条错误文字，并且只报第一个有问题的点。

S7 Plus 的坏符号现在只影响它自己一个点（质量 `BadNodeIdUnknown`，连续 3 次失败后被隔离）。如果继续用旧接口，一个坏点就会让整批数据都读不到。

新接口逐点返回值和质量，单个坏点不会影响其它点。

## 请求

- 方法与路径：`POST /Tag/ReadTagsWithQuality`，`Content-Type: application/json`。
- 请求体：别名数组，与 `ReadTagsAsList` 相同，例如 `["通用.S1500_10.0.0.1.DB1.Value"]`。
- 单次最多 1000 个。
- 权限与 `ReadTagsAsList` 相同；只读，没有副作用。
- 读取前的行为也相同：对当前没在采集的点先补读一次 PLC。

## 响应

外层结构与其它 `/Tag` 接口相同。JSON 属性名是小写开头（camelCase），建议调用方按不区分大小写的方式解析。

```json
{
  "httpStatusCode": 200,
  "isSuccess": true,
  "backResult": [
    { "key": "通用.D.G.A", "value": 123, "isGood": true, "statusCode": 0, "status": "Good",
      "timestampUtc": "2026-09-19T12:00:00.123Z", "reason": null },
    { "key": "通用.D.G.B", "value": null, "isGood": false, "statusCode": 2150891520, "status": "BadNodeIdUnknown",
      "timestampUtc": "2026-09-19T11:59:30Z", "reason": "SymbolNotFound" },
    { "key": "通用.D.G.C", "value": null, "isGood": false, "statusCode": 2150891520, "status": "BadNodeIdUnknown",
      "timestampUtc": null, "reason": "NotRegistered" }
  ],
  "errorMessage": null,
  "exceptionMessage": null
}
```

整个请求只在以下情况失败（`isSuccess=false`）：

| HTTP | 情况 |
|---|---|
| 400 | 请求体为空、包含空白别名，或超过 1000 个 |
| 503 | 网关正在热重载，稍后重试 |

其余情况一律返回 200 且 `isSuccess=true`。每个请求的别名各对应一行，顺序与请求一致；重复的别名按出现次数返回。

### 字段

| 字段 | 说明 |
|---|---|
| `key` | 请求中的别名（去掉首尾空白） |
| `value` | 仅 `isGood=true` 时有值，格式与 `ReadTagsAsList` 相同（包括数组点）；否则为 `null` |
| `isGood` | 质量是否为 Good |
| `statusCode` | OPC UA 状态码数值（uint） |
| `status` | 状态码名称，如 `Good`、`BadNodeIdUnknown`、`BadCommFailure` |
| `timestampUtc` | 该值最后一次真实采集的时间（UTC）。被隔离的点停在隔离前最后一次读取的时间，调用方可以据此判断数据陈旧程度；别名未注册时为 `null` |
| `reason` | Good 时为 `null`，否则取下表中的一个值 |

### `reason` 取值与映射规则

| reason | 对应的质量 | 含义 / 建议处理 |
|---|---|---|
| `SymbolNotFound` | `BadNodeIdUnknown`（0x8034xxxx） | 地址或符号在 PLC 程序里不存在：检查点名或 PLC 程序，不要当作通讯故障 |
| `CommFailure` | `BadCommFailure`；或 `BadNotConnected` 且设备当前未连接 | 与设备的通讯中断 |
| `Timeout` | `BadTimeout` | 单次读取超时，会话仍在线 |
| `WaitingForData` | `BadWaitingForInitialData`；或 `BadNotConnected` 且设备当前已连接 | 点已注册，但还没有采到首个值（设备刚连上首轮采集或符号解析期间，或点定义变更后） |
| `NotRegistered` | 返回 `BadNodeIdUnknown` | 别名在 IGW 里不存在（没有注册，或已被删除） |
| `AccessDenied` | 返回 `BadUserAccessDenied`（0x801F0000） | 当前账户没有该点的读取权限 |
| `DeviceError` | 其它 Bad 或 Uncertain（如 `Bad`、`BadDeviceFailure`、`BadDecodeError`） | 设备或驱动侧的其它错误。S7 Classic 读不存在的 DB/偏移通常落在这里（通用 `Bad`） |

`BadNotConnected` 的区分规则：点刚注册、还没有采集值时，质量初始为 `BadNotConnected`，和设备断开时的质量相同。IGW 按该点所属设备当前的连接状态区分：设备已连上报 `WaitingForData`，否则报 `CommFailure`。

## 版本探测

没有此接口的旧版 IGW 对 `POST /Tag/ReadTagsWithQuality` 返回 **404**，因为所有未知的 `/Tag/*` 路径都显式返回 404，不会落到网页。调用方可以据此回退到 `ReadTagsAsList`。
