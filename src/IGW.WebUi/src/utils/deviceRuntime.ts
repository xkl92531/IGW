export type RuntimeTone = 'online' | 'standby' | 'connecting' | 'offline' | 'fault' | 'unknown'

export interface RuntimeDeviceStatus {
  name: string
  state?: string
  detailState?: string | null
  detailMessage?: string | null
  diagnostic?: {
    device?: string
    channel?: string
    protocol?: string
    code: string
    category: string
    scope: 'Transport' | 'Session' | 'DataAccess' | string
    state: string
    availability?: string
    cause: string
    suggestion: string
    rawError: string
    firstOccurredAt?: string
    lastOccurredAt?: string
    recoveredAt?: string | null
    occurrenceCount: number
    retrying: boolean
    nextRetryMs: number
    active: boolean
  } | null
  tagCount?: number
  activeTags?: number
  demandActive?: boolean
  stats?: {
    readsOk?: number
    readsErr?: number
    successRate?: number
    lastError?: string
    configuredMinPeriodMs?: number
    actualP95PeriodMs?: number
    actualP99PeriodMs?: number
    overduePeriods?: number
    samplesPerSecond?: number
    periodHealthy?: boolean
    arrayTags?: number
    arrayElements?: number
    arrayGood?: number
    arrayUncertain?: number
    arrayBad?: number
    arrayBytes?: number
    arrayBlocks?: number
    arrayFailedRanges?: string
  } | null
}

export interface RuntimeMark {
  text: string
  tone: RuntimeTone
  title: string
}

export type RuntimeTranslate = (
  source: string,
  params?: Record<string, string | number>,
) => string

export type RuntimeDeviceIndex = Record<string, RuntimeDeviceStatus>

export function indexRuntimeDevices(devices: RuntimeDeviceStatus[] | null | undefined): RuntimeDeviceIndex {
  const result: RuntimeDeviceIndex = {}
  for (const device of devices || []) {
    if (device?.name) result[device.name] = device
  }
  return result
}

export function findRuntimeDevice(
  devices: RuntimeDeviceIndex,
  channelName: string,
  deviceName: string,
): RuntimeDeviceStatus | undefined {
  const fullName = channelName ? `${channelName}.${deviceName}` : deviceName
  if (devices[fullName]) return devices[fullName]
  if (devices[deviceName]) return devices[deviceName]

  // 兼容旧运行时只返回设备短名的情况；多个通道存在同名设备时不猜测，避免串状态。
  const matches = Object.values(devices).filter((device) =>
    device.name === deviceName || device.name.endsWith(`.${deviceName}`),
  )
  return matches.length === 1 ? matches[0] : undefined
}

function defaultTranslate(source: string, params: Record<string, string | number> = {}): string {
  return source.replace(/\{(\w+)\}/g, (_, key: string) => String(params[key] ?? `{${key}}`))
}

function errorSuffix(status: RuntimeDeviceStatus, translate: RuntimeTranslate): string {
  const error = status.stats?.lastError?.trim()
  return error ? ` · ${translate('原因：{error}', { error })}` : ''
}

export function effectiveRuntimeState(status: RuntimeDeviceStatus): string {
  if (status.diagnostic?.active
    && !(status.diagnostic.scope === 'DataAccess' && status.state === 'Connected'))
    return 'Faulted'
  // 兼容旧版 EtherNet/IP 运行时：全点读取失败时统计已明确为 0% 成功，但 state 仍错误停在 Connected。
  if (status.state === 'Connected'
    && (status.stats?.readsErr || 0) > 0
    && (status.stats?.readsOk || 0) === 0
    && status.stats?.successRate === 0)
    return 'Faulted'
  return status.state || ''
}

export function deviceRuntimeMark(
  status?: RuntimeDeviceStatus,
  runtimeAvailable = true,
  translate: RuntimeTranslate = defaultTranslate,
): RuntimeMark {
  if (!runtimeAvailable)
    return { text: translate('状态未知'), tone: 'unknown', title: translate('运行状态接口暂时不可用') }
  if (!status)
    return { text: translate('未下发'), tone: 'unknown', title: translate('运行时没有找到该设备；请保存并应用工程配置') }

  if (status.diagnostic?.active) {
    const diagnostic = status.diagnostic
    const category = translate(diagnostic.category || diagnostic.code)
    const details = [
      diagnostic.cause,
      diagnostic.suggestion ? translate('处理建议：{suggestion}', { suggestion: diagnostic.suggestion }) : '',
      diagnostic.rawError ? translate('原始错误：{error}', { error: diagnostic.rawError }) : '',
      diagnostic.occurrenceCount > 1
        ? translate('已发生 {count} 次', { count: diagnostic.occurrenceCount })
        : '',
      diagnostic.retrying && diagnostic.nextRetryMs > 0
        ? translate('{seconds} 秒后重试', { seconds: Math.max(1, Math.ceil(diagnostic.nextRetryMs / 1000)) })
        : '',
    ].filter(Boolean)
    return { text: category, tone: 'fault', title: [category, ...details].join(' · ') }
  }

  if (status.detailState === 'AuthenticationRequired' || status.detailState === 'AuthenticationFailed')
    return {
      text: translate('需验证'),
      tone: 'fault',
      title: status.detailMessage || translate('PLC 要求身份验证，请在设备右键菜单中配置凭据'),
    }
  if (status.detailState === 'PermissionDenied')
    return { text: translate('权限不足'), tone: 'fault', title: status.detailMessage || translate('当前 PLC 账户权限不足') }
  if (['NetworkAdapterUnavailable', 'NetworkConnectFailure', 'NetworkBindingFailure', 'TransportFailure'].includes(status.detailState || ''))
    return { text: translate('网络异常'), tone: 'fault', title: status.detailMessage || translate('PLC 网络连接失败') }
  if (['SymbolMismatch', 'DbAddressMismatch'].includes(status.detailState || ''))
    return { text: translate('DB/协议不匹配'), tone: 'fault', title: status.detailMessage || translate('S7/S7 Plus 协议、DB 或符号地址与 PLC 不一致') }
  if (status.detailState === 'UnsupportedFirmware')
    return { text: translate('设备不兼容'), tone: 'fault', title: status.detailMessage || translate('当前 PLC 固件或型号不支持所选协议') }
  if (status.detailState === 'PartialOnline')
    return { text: translate('部分在线'), tone: 'fault', title: status.detailMessage || translate('设备只有部分数据通道或点位可用') }

  switch (effectiveRuntimeState(status)) {
    case 'Connected': {
      const idle = status.demandActive === false && (status.activeTags || 0) === 0
        ? ` · ${translate('采集待机（会话在线，当前不读取业务点位）')}`
        : ''
      const counts = status.activeTags != null && status.tagCount != null
        ? ` · ${translate('活动点位：{active} / {total}', { active: status.activeTags, total: status.tagCount })}`
        : ''
      const sampling = status.stats?.configuredMinPeriodMs
        ? ` · ${translate('采集：目标 {target}ms，P95 {p95}ms，{rate} 点/秒', {
          target: status.stats.configuredMinPeriodMs,
          p95: status.stats.actualP95PeriodMs || 0,
          rate: Math.round(status.stats.samplesPerSecond || 0),
        })}`
        : ''
      const overdue = status.stats?.periodHealthy === false
        ? ` · ${translate('最近存在周期超时（累计跳过 {count} 个过期周期）', { count: status.stats.overduePeriods || 0 })}`
        : ''
      const state = translate('在线')
      return { text: state, tone: 'online', title: `${translate('运行状态：{state}', { state })}${idle}${counts}${sampling}${overdue}` }
    }
    case 'Standby':
      return { text: translate('待机'), tone: 'standby', title: translate('当前没有采集任务或数据消费者，设备保持断开以节省资源；产生需求后会自动连接') }
    case 'Connecting': {
      const state = translate('连接中')
      return { text: state, tone: 'connecting', title: `${translate('运行状态：{state}', { state })}${errorSuffix(status, translate)}` }
    }
    case 'Disconnected': {
      const state = translate('离线')
      return { text: state, tone: 'offline', title: `${translate('运行状态：{state}', { state })}${errorSuffix(status, translate)}` }
    }
    case 'Faulted': {
      const state = translate('故障')
      return { text: state, tone: 'fault', title: `${translate('运行状态：{state}', { state })}${errorSuffix(status, translate)}` }
    }
    default:
      {
        const state = status.state || translate('未知')
      return {
        text: status.state || translate('状态未知'),
        tone: 'unknown',
          title: `${translate('运行状态：{state}', { state })}${errorSuffix(status, translate)}`,
      }
      }
  }
}

export function channelRuntimeMark(
  channelName: string,
  configuredDevices: Array<{ name: string }> | null | undefined,
  statuses: RuntimeDeviceIndex,
  runtimeAvailable = true,
  translate: RuntimeTranslate = defaultTranslate,
): RuntimeMark | null {
  const devices = configuredDevices || []
  if (!devices.length) return null
  if (!runtimeAvailable)
    return { text: translate('状态未知'), tone: 'unknown', title: translate('运行状态接口暂时不可用') }

  let online = 0
  let connecting = 0
  let standby = 0
  let offline = 0
  let fault = 0
  let dataIssue = 0
  let missing = 0
  let unknown = 0

  for (const device of devices) {
    const status = findRuntimeDevice(statuses, channelName, device.name)
    if (!status) { missing++; continue }
    if (status.state === 'Connected'
      && status.diagnostic?.active
      && status.diagnostic.scope === 'DataAccess')
      dataIssue++
    const state = effectiveRuntimeState(status)
    if (state === 'Connected') online++
    else if (state === 'Standby') standby++
    else if (state === 'Connecting') connecting++
    else if (state === 'Disconnected') offline++
    else if (state === 'Faulted') fault++
    else unknown++
  }

  const details = [
    online ? translate('在线 {count} 台', { count: online }) : '',
    standby ? translate('待机 {count} 台', { count: standby }) : '',
    connecting ? translate('连接中 {count} 台', { count: connecting }) : '',
    offline ? translate('离线 {count} 台', { count: offline }) : '',
    fault ? translate('故障 {count} 台', { count: fault }) : '',
    dataIssue ? translate('数据异常 {count} 台', { count: dataIssue }) : '',
    missing ? translate('未下发 {count} 台', { count: missing }) : '',
    unknown ? translate('状态未知 {count} 台', { count: unknown }) : '',
  ].filter(Boolean).join(' · ')

  const abnormal = offline + fault + unknown
  if (abnormal) {
    const text = fault && !offline && !unknown
      ? translate('{count}台故障', { count: fault })
      : offline && !fault && !unknown
        ? translate('{count}台离线', { count: offline })
        : translate('{count}台异常', { count: abnormal })
    return { text, tone: 'fault', title: `${translate('运行状态：存在异常')} · ${details}` }
  }
  if (connecting)
    return { text: translate('{count}台连接中', { count: connecting }), tone: 'connecting', title: `${translate('运行状态：正在连接')} · ${details}` }
  if (missing)
    return { text: translate('{count}台未下发', { count: missing }), tone: 'unknown', title: `${translate('运行状态：配置未完全下发')} · ${details}` }
  if (dataIssue)
    return {
      text: translate('{count}台数据异常', { count: dataIssue }),
      tone: 'fault',
      title: `${translate('运行状态：设备在线但存在数据异常')} · ${details}`,
    }
  if (standby)
    return { text: online ? translate('{count}台待机', { count: standby }) : translate('全部待机'), tone: 'standby', title: `${translate('运行状态：按需连接')} · ${details}` }
  return { text: translate('全部在线'), tone: 'online', title: translate('运行状态：{count} 台设备全部在线', { count: online }) }
}

/**
 * 兼容旧运行时把“全部读取失败”的设备误报为 Connected。
 * 监控中心必须与工程树共用 effectiveRuntimeState，否则同一设备会同时显示离线和正常。
 */
export function reconcileMonitorRuntime(
  source: any,
  devices: RuntimeDeviceStatus[] | null | undefined,
  query = '',
): any {
  const entries = [...(source?.entries || [])]
  const channels = (source?.channels || []).map((channel: any) => ({ ...channel }))
  const runtimeDevices = devices || []
  const correctionByChannel = new Map<string, number>()
  const onlineByChannel = new Map<string, number>()
  const existingOfflineRoots = new Set(
    entries
      .filter((entry: any) => entry?.severity === 'offline' && !entry?.taskId && entry?.device)
      .map((entry: any) => String(entry.device)),
  )
  const keyword = query.trim().toLocaleLowerCase('zh-CN')
  const added: any[] = []

  for (const device of runtimeDevices) {
    const channel = device.name.includes('.') ? device.name.slice(0, device.name.indexOf('.')) : device.name
    const effectiveState = effectiveRuntimeState(device)
    if (effectiveState === 'Connected') {
      onlineByChannel.set(channel, (onlineByChannel.get(channel) || 0) + 1)
      continue
    }

    // 原始状态本来就离线/故障时，后端异常接口已经计数；这里只补偿旧驱动的误报。
    if (device.state !== 'Connected') continue
    correctionByChannel.set(channel, (correctionByChannel.get(channel) || 0) + 1)
    if (existingOfflineRoots.has(device.name)) continue

    const info = device.stats?.lastError?.trim() || '设备通信失败'
    const searchable = `${device.name} ${channel} ${info}`.toLocaleLowerCase('zh-CN')
    if (keyword && !searchable.includes(keyword)) continue
    added.push({
      severity: 'offline',
      title: `设备 ${device.name} 离线`,
      device: device.name,
      channel,
      info,
      ageMs: 0,
      impactedCount: 0,
      impacted: [],
    })
  }

  for (const channel of channels) {
    const correction = correctionByChannel.get(String(channel.name)) || 0
    channel.online = onlineByChannel.get(String(channel.name)) || 0
    if (correction) {
      channel.level = 'offline'
      channel.count = Number(channel.count || 0) + correction
    }
  }

  const reconciledEntries = [...added, ...entries]
  return { ...source, total: reconciledEntries.length, entries: reconciledEntries, channels }
}

/** 比较监控中心真正可见的状态；持续时间每次轮询都会增长，不应导致整块界面重绘。 */
export function sameMonitorPresentation(current: any, next: any): boolean {
  if (current == null || next == null) return current === next
  const presentationJson = (value: any) => JSON.stringify(value, (key, item) =>
    key === 'ageMs' ? undefined : item)
  return presentationJson(current) === presentationJson(next)
}
