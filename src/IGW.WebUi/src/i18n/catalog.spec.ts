import { describe, expect, it } from 'vitest'
import { enUSMessages } from './catalog'

describe('layered acquisition diagnostic translations', () => {
  it('translates protocol editor guidance', () => {
    for (const source of ['区域与协议地址', '协议偏移（从 0 开始）', '寄存器位（0–15）', '旧数字地址起点', '写入功能码策略', '其他属性（JSON）']) {
      expect(enUSMessages[source], source).toBeTruthy()
    }
  })
  it('provides English catalog entries for every operator-facing diagnostic label', () => {
    const sources = [
      '导出诊断包', '正在导出…', '诊断包导出失败', 'IGW 排队 P95', '批次请求最大耗时 P95',
      '最近 / 总体请求最大', '网络 RTT P95 / 丢包', 'IGW 本地 P95', '最近归因 / 置信度',
      '疑似', '已确认', '低', '中', '证据', '影响因素', '探测状态', '源地址 / 远端地址',
      '同会话重试 / 批次降级', '请求数 / 失败 / 取消', '当前网络证据不可用，无法区分网络链路与 PLC/OPC UA 服务器',
      '当前批次未提供源/远端地址；可从诊断包 network.csv 查看。', '诊断快照已过期：{seconds} 秒前更新',
      '网络 RTT 最新 / P95 / 基线 P95', '探测成功 / 失败 / 丢包', '当前批次没有网络探测窗口',
      '网络探测状态为 {status}', 'IGW本地处理', 'IGW共享队列', '网络链路疑似',
      'PLC/OPC UA服务器疑似', '请求拆分或重试',
      '证据不足', '健康', '退化', '不可用', '成功', '超时', '已取消', '诊断导出响应不是 ZIP 文件',
    ]
    for (const source of sources) {
      expect(enUSMessages[source], source).toBeTruthy()
      expect(enUSMessages[source], source).not.toBe(source)
    }
  })
})

it('translates all new acquisition and business field editor labels', () => {
  for (const source of [
    "固定值按类型填写；点位选择实时采集值；Input 读取原始接口消息。必填字段缺失或类型不符会阻止发送并保留事件。",
    "业务字段",
    "业务字段样例预览",
    "可选",
    "字段来源",
    "必填",
    "此处使用样例数据，不读取设备；模板可用 ${fields.product.code} 引用业务字段。",
    "添加业务字段",
    "添加字段",
    "输出字段路径",
  ]) expect(enUSMessages[source], source).toBeTruthy()
})
