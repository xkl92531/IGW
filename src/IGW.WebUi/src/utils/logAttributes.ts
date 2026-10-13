const preferredOrder = [
  'igw_module', 'igw_feature', 'igw_event_kind', 'igw_logger',
  'igw_task_name', 'igw_task_id', 'igw_task_names', 'igw_task_ids', 'igw_node_name', 'igw_node_id',
  'igw_interface_name', 'igw_direction', 'igw_protocol', 'igw_operation', 'igw_route', 'igw_target', 'igw_method',
  'igw_channel', 'igw_device', 'igw_db', 'igw_scope', 'igw_collection_source', 'igw_collection_sources', 'igw_owner_count', 'igw_owner_list_truncated', 'igw_status',
  'igw_duration_ms', 'igw_configured_period_ms', 'igw_actual_interval_ms', 'igw_driver_ms',
  'igw_local_ms', 'igw_tag_count', 'igw_sample_count', 'igw_bad_count', 'igw_http_status_code', 'igw_request_bytes', 'igw_response_bytes',
  'igw_transport_status', 'igw_business_status', 'igw_trace_id', 'trace_id', 'span_id',
]

export interface LogAttributeRow { key: string; label: string; value: string }

const labels: Readonly<Record<string, string>> = {
  igw_module: '模块', igw_feature: '功能', igw_event_kind: '事件类型', igw_logger: '日志来源',
  igw_channel: '通道', igw_device: '设备', igw_db: '数据块', igw_task_id: '任务编号', igw_node_id: '节点编号',
  igw_task_name: '任务名称', igw_task_names: '任务列表', igw_task_ids: '任务编号列表', igw_node_name: '节点名称',
  igw_interface_name: '接口名称', igw_direction: '方向', igw_protocol: '协议', igw_operation: '操作', igw_route: '路由',
  igw_scope: '数据范围', igw_collection_source: '采集来源', igw_collection_sources: '采集来源列表', igw_owner_count: '关联任务数',
  igw_owner_list_truncated: '任务列表已截断', igw_tag_id: '标签编号', igw_tag_values: '点位值', igw_block_id: '数据块编号',
  igw_driver_id: '驱动编号', igw_driver_ids: '驱动编号列表', igw_missed_periods: '漏期次数',
  igw_status: '状态', igw_duration_ms: '耗时', igw_configured_period_ms: '设定周期',
  igw_actual_interval_ms: '实际间隔', igw_driver_ms: '驱动耗时', igw_local_ms: '本地耗时',
  igw_tag_count: '标签数', igw_sample_count: '样本数', igw_bad_count: '异常样本数',
  igw_http_status_code: '接口状态码', igw_request_bytes: '发送字节数', igw_response_bytes: '响应字节数',
  igw_transport_status: '传输结果', igw_business_status: '业务结果',
  igw_target: '目标', igw_method: '方法', igw_error_type: '错误类型', igw_error_code: '错误代码',
  igw_trace_id: '排障编号', trace_id: '排障编号', span_id: '排障分段编号',

  endpoint: '目标地址', url: '目标地址', host: '主机', port: '端口', ip: '网络地址',
  operation: '操作', action: '动作', method: '方法', route: '路由', target: '目标', protocol: '协议',
  task: '任务', taskId: '任务编号', node: '节点', nodeId: '节点编号', tag: '标签', tagId: '标签编号',
  channel: '通道', device: '设备', dev: '设备', database: '数据块', db: '数据块',
  status: '状态', result: '结果', reason: '原因', mode: '模式', source: '来源', scope: '范围',
  count: '数量', n: '数量', total: '总数', size: '大小', length: '长度', value: '值', name: '名称',
  elapsed: '耗时', ms: '耗时毫秒', milliseconds: '耗时毫秒', period: '周期', delay: '延迟',
  driverMs: '驱动耗时', localMs: '本地耗时', queueDelayMs: '排队延迟毫秒', rpcMs: '协议调用耗时', rtt: '往返耗时',
  path: '路径', fullPath: '完整路径', p: '路径', policy: '安全策略', login: '登录方式', reused: '会话已复用',
  stage: '阶段', phase: '阶段', attempt: '尝试次数', attempts: '尝试次数', retry: '重试次数', retries: '重试次数',
  truncated: '是否截断', omitted: '省略数量', skipped: '跳过数量', suppressed: '抑制数量',
  diagnostic: '诊断详情', detail: '详情', details: '详情', info: '信息', message: '消息', msg: '消息', warning: '警告',
  error: '错误', err: '错误', errorType: '错误类型', errorCode: '错误代码', exceptionType: '异常类型', exceptionMessage: '异常消息',
  httpStatusCode: '接口状态码', request: '请求', response: '响应', payload: '报文', payloadLength: '报文长度',
  quality: '质量', good: '正常数量', bad: '异常数量', online: '在线数量', available: '是否可用',
  configured: '设定值', actual: '实际值', discovered: '发现数量', scanned: '扫描数量',

  'exception.type': '异常类型', 'exception.message': '异常消息', 'exception.stacktrace': '异常堆栈',
  exception_type: '异常类型', exception_message: '异常消息', exception_stacktrace: '异常堆栈',
  service_name: '服务名称', service_version: '服务版本', service_instance_id: '服务实例编号',
  scope_name: '日志作用域', scope_version: '作用域版本', resource_attributes: '服务属性', attributes: '附加属性',
  event_name: '事件名称', event_id: '事件编号', process_id: '进程编号', thread_id: '线程编号',
  'code.file.path': '代码文件', 'code.function.name': '代码函数', 'code.line.number': '代码行号',
  code_filepath: '代码文件', code_function: '代码函数', code_line_number: '代码行号',
}

const hiddenEnvelopeFields = new Set([
  '{OriginalFormat}', '_timestamp', 'timestamp', 'observed_timestamp', 'body', 'message',
  'severity', 'severity_text', 'severity_number', 'category_name', 'igw_category',
  'igw_payload_id', 'igw_trace_id', 'trace_id',
])

const tokenLabels: Readonly<Record<string, string>> = {
  queue: '排队', delay: '延迟', ms: '毫秒', service: '服务', instance: '实例', id: '编号',
  request: '请求', response: '响应', bytes: '字节数', status: '状态', code: '代码', http: '接口',
  task: '任务', node: '节点', tag: '标签', device: '设备', channel: '通道', driver: '驱动',
  count: '数量', duration: '耗时', actual: '实际', configured: '设定', period: '周期',
  error: '错误', exception: '异常', type: '类型', message: '消息', name: '名称',
  local: '本地', business: '业务', transport: '传输', collection: '采集', source: '来源',
  interface: '接口', route: '路由', method: '方法', operation: '操作', target: '目标',
}

export function logAttributeLabel(key: string): string {
  if (labels[key]) return labels[key]
  if (/\p{Script=Han}/u.test(key)) return key
  const tokens = key.replace(/^igw[_.]/i, '').replace(/([a-z0-9])([A-Z])/g, '$1_$2')
    .split(/[._\-\s]+/).filter(Boolean).map(token => tokenLabels[token.toLowerCase()])
  return tokens.length > 0 && tokens.every(Boolean) ? tokens.join('') : key
}

export function logAttributeRows(attributes: Record<string, unknown> | null | undefined): LogAttributeRow[] {
  if (!attributes) return []
  return Object.entries(attributes)
    .filter(([key, value]) => !hiddenEnvelopeFields.has(key)
      && !(typeof value === 'string' && value.trim() === ''))
    .sort(([left], [right]) => {
      const li = preferredOrder.indexOf(left), ri = preferredOrder.indexOf(right)
      return (li < 0 ? 10_000 : li) - (ri < 0 ? 10_000 : ri) || left.localeCompare(right)
    })
    .map(([key, value]) => ({ key, label: logAttributeLabel(key), value: displayAttribute(value) }))
}

export function displayAttribute(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'string') return value
  if (typeof value === 'number' || typeof value === 'boolean') return String(value)
  try {
    const json = JSON.stringify(value)
    return json
  } catch { return String(value) }
}
