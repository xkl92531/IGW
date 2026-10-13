# 西门子 S7-1500 OPC UA 数据类型

IGW 1.6.34 按 S7-1500 的 OPC UA 基础类型映射表补齐类型转换。现场使用 OPC UA 连接的 PLC5—PLC9 适用；本次不改变 S7 原始地址、Modbus 等其他驱动的类型支持范围。

## 基础类型

| PLC 类型 | OPC UA 传输类型 | IGW 处理 |
|---|---|---|
| BOOL | Boolean | 布尔值 |
| BYTE、USINT | Byte | 无符号 8 位 |
| SINT | SByte | 有符号 8 位，写入不再扩大为 Int16 |
| INT、UINT | Int16、UInt16 | 对应有符号/无符号 16 位 |
| DINT、UDINT | Int32、UInt32 | 对应有符号/无符号 32 位 |
| LINT、ULINT | Int64、UInt64 | 对应 64 位；浏览器读写的大整数保留十进制文本 |
| WORD、DWORD、LWORD | UInt16、UInt32、UInt64 | 保留位模式对应的整数和独立类型名称 |
| REAL、LREAL | Float、Double | 对应 32/64 位浮点数 |
| CHAR、WCHAR | Byte、UInt16 | Windows-1252 单字符、UCS-2 单字符；与旧 KEP 有符号 Char 区分 |
| STRING、WSTRING | String | 沿用字符串容量及写入校验 |
| DATE | UInt16 | 从 1990-01-01 起的天数，显示 yyyy-MM-dd |
| TOD / TIME_OF_DAY | UInt32 | 当天毫秒，显示 HH:mm:ss.fff |
| LTOD / LTIME_OF_DAY | UInt64 | 当天纳秒，显示 HH:mm:ss.fffffffff |
| TIME | Int32 | 毫秒时长，显示 T#123ms；可输入 T#1h2m3s |
| LTIME | Int64 | 纳秒时长，显示 LT#123ns；全范围有符号 64 位 |
| S5TIME | UInt16 | BCD 数值与时间基数转换，显示 S5T#1000ms |
| DT / DATE_AND_TIME | Byte[8] | BCD 日期，显示 yyyy-MM-dd HH:mm:ss.fff |
| LDT / DATE_AND_LTIME | DateTime | 日历时间；OPC UA 传输精度为 100ns，拒绝无法无损写入的值 |
| DTL | ExtensionObject | 完整结构体；显示 yyyy-MM-dd HH:mm:ss.fffffffff |
| TIMER、COUNTER | UInt16 | 按 OPC UA 原生 UInt16 值处理 |

自动导入读取服务器 DataType、BrowseName、ValueRank、ArrayDimensions 和读写权限。服务器只声明通用 Int32、Byte[] 等基础类型时，网关不能凭字段名或数组长度推断 TIME/DT/DTL，可在点位属性中明确选择。普通 8/12 字节数组保持原类型。标准 OPC UA DateTime 使用独立 UTC 类型显示；服务器明确声明 LDT 时保留 LDT 语义。

原有点位的类型不自动覆盖。更新后重新自动导入可获得服务器明确声明的类型；也可手动修改目标点位的数据类型。DT 在 PLC 中是标量，尽管其 OPC UA 传输形状是 Byte[8]。已有错误导入为 Byte[8] 数组的 DT 点位建议重新导入，避免保留旧数组定义。

## DTL 和日期写入

示例：`2026-09-05 13:40:44.123456789` 或 `DTL#2026-09-05-13:40:44.123456789`。

- DTL 的 9 位纳秒在采集、监控、REST、MES 上报和写回之间保留，不套用网关电脑时区。
- DTL 范围为 1970-01-01 至 2262-04-11 23:47:16.854775807；写入时计算星期，检查日期及各字段范围。
- DTL 按完整结构体写入，沿用服务器的二进制编码标识。字段 YEAR 等单独节点的读写权限仍以 PLC 声明为准。
- 明确配置 DTL 的原始 12 字节缓冲区使用 PLC 大端布局；OPC UA ExtensionObject 的二进制字段使用小端布局。
- DT 为 1990 至 2089 年、毫秒精度；DATE 使用完整的无符号 16 位天数范围，最大为 2169-06-06。
- 北向 IGW OPC UA 服务将日期、时长和字符语义类型作为 String 输出，保留规范文本；整数仍使用相应整数类型。

## 数组和复杂类型

新增基础语义类型支持数组、切片、下界、逐元素质量与写回；DT 的最后一维 8 字节在导入时折叠为逻辑日期元素。

STRUCT/UDT 保留现有成员浏览和已解码结构体数组支持。任意二进制 STRUCT/UDT 数组需要真实结构定义；当前版本没有通用的未知结构字典加载器，无法确定布局时明确报错，不编造字段或退回 Double。

ANY、POINTER、BLOCK_FB、BLOCK_FC 等 PLC 专用指针和块类型不在西门子 OPC UA 可访问值范围内，不作为普通点位类型开放。DTL 是已明确实现的结构体特例，不代表所有 PLC 系统结构均可直接读写。

## 官方依据

- [S7-1500 SIMATIC 与 OPC UA 数据类型映射](https://docs.tia.siemens.cloud/r/simatic_et_200eco_pn_manual_collection_itit_20/function-manuals/communication-function-manuals/communication/opc-ua-communication/using-the-s7-1500-as-an-opc-ua-server/interesting-information-about-the-opc-ua-server-of-the-s7-1500-cpus/mapping-of-data-types)
- [DTL](https://docs.tia.siemens.cloud/r/en-us/v20/data-types/date-and-time/dtl-s7-1200-s7-1500)
- [LDT](https://docs.tia.siemens.cloud/r/en-us/v20/data-types/date-and-time/ldt-date_and_ltime-s7-1500)
- [日期类型的范围及格式](https://docs.tia.siemens.cloud/r/en-us/v20/tia-portal-test-suite/system-test/supported-data-types/date-and-time)
- [S5TIME 编码](https://docs.tia.siemens.cloud/r/en-us/v20/data-types/timers/s5time-duration-s7-300-s7-400)
- [OPC UA Binary 整数编码](https://reference.opcfoundation.org/specs/OPC-10000-6/5.2)
