import type { LogCenterEntryDto } from '../api'

export interface LogPresentation {
  module: string
  feature: string
  task: string
  interfaceOperation: string
  plcScope: string
  source: string
}

function text(value: unknown): string {
  if (value === null || value === undefined) return ''
  if (Array.isArray(value)) return value.map(text).filter(Boolean).join(', ')
  return String(value).trim()
}

export function buildLogPresentation(entry: LogCenterEntryDto): LogPresentation {
  const attributes = entry.attributes || {}
  const taskName = text(attributes.igw_task_name || attributes.igw_task_names)
  const taskId = text(attributes.igw_task_id || attributes.igw_task_ids)
  const method = text(attributes.igw_method)
  const target = text(attributes.igw_target || attributes.igw_route || attributes.igw_interface_name)
  const operation = text(attributes.igw_operation)
  return {
    module: text(attributes.igw_module) || 'unknown',
    feature: text(attributes.igw_feature) || 'unknown',
    task: taskName ? `${taskName}${taskId ? ` (${taskId})` : ''}` : taskId,
    interfaceOperation: method && target ? `${method} ${target}` : operation || target,
    plcScope: [attributes.igw_channel, attributes.igw_device, attributes.igw_db]
      .map(text).filter(Boolean).join(' · '),
    source: text(attributes.igw_collection_source || attributes.igw_collection_sources),
  }
}

export function logModuleLabel(value: string): string {
  return ({
    acquisition: '采集引擎', mes: 'MES 流程', northbound: '网关接口', integration: '接口集成',
    runtime: '网关运行', history: '历史记录', automation: '自动任务', observability: '日志服务',
    unknown: '未知',
  } as Record<string, string>)[value] || value
}

export function logFeatureLabel(value: string): string {
  return ({
    plc_scan: 'PLC 批次读取', device_connection: '设备连接', data_quality: '数据质量',
    http_upload: 'HTTP 上报', business_result: '业务结果判定', flow_engine: '采集流程',
    rest_api: 'REST 接口', signalr_request: '实时请求', signalr_stream: '实时数据流',
    runtime_engine: '运行引擎', device_loop: '设备循环', gateway_host: '网关生命周期',
    data_logger: '历史落盘', scheduler: '计划任务', mqtt_publish: 'MQTT 发布',
    computed_tags: '计算标签', network_probe: '网络诊断', device_state: '设备状态',
    scan_performance: '扫描性能', signalr: 'SignalR 通信', log_center: '日志查询',
    openobserve: '日志存储服务',
    interface_call: '接口调用', general: '常规运行',
    unknown: '未知',
  } as Record<string, string>)[value] || value
}
