export interface NetworkAdapterRow {
  id: string
  name: string
  ipv4: string
  prefixLength: number
  label: string
}

export interface NetworkAdapterOption {
  value: string
  label: string
  unavailable: boolean
  adapter?: NetworkAdapterRow
}

type Settings = Record<string, string | undefined>

export const DEFAULT_ADAPTER_VALUE = ''

export function adapterValue(adapter: Pick<NetworkAdapterRow, 'id' | 'ipv4'>): string {
  return `${adapter.id}|${adapter.ipv4}`
}

export function selectedAdapterValue(settings: Settings): string {
  const name = String(settings.networkAdapter || '').trim()
  if (!name || name === '默认值' || name.toLowerCase() === 'default') return DEFAULT_ADAPTER_VALUE
  const id = String(settings.networkAdapterId || '').trim()
  const ipv4 = String(settings.networkAdapterIpv4 || '').trim()
  return id && ipv4 ? `${id}|${ipv4}` : `legacy|${name}`
}

export function adapterOptions(rows: NetworkAdapterRow[], settings: Settings): NetworkAdapterOption[] {
  const selected = selectedAdapterValue(settings)
  const legacyName = selected.startsWith('legacy|') ? selected.slice('legacy|'.length) : ''
  const uniqueLegacy = legacyName && rows.filter((row) => row.name === legacyName).length === 1
  const options: NetworkAdapterOption[] = rows.map((adapter) => ({
    value: uniqueLegacy && adapter.name === legacyName ? selected : adapterValue(adapter),
    label: adapter.label || `${adapter.name} — ${adapter.ipv4}`,
    unavailable: false,
    adapter,
  }))
  if (selected && !options.some((option) => option.value === selected)) {
    const name = String(settings.networkAdapter || '').trim()
    const ipv4 = String(settings.networkAdapterIpv4 || '').trim()
    options.push({
      value: selected,
      label: ipv4 ? `${name || settings.networkAdapterId} — ${ipv4}` : name,
      unavailable: true,
    })
  }
  return options
}

export function applyAdapterValue(settings: Settings, value: string, rows: NetworkAdapterRow[]): Settings {
  const next = { ...settings }
  if (!value) {
    next.networkAdapter = '默认值'
    delete next.networkAdapterId
    delete next.networkAdapterIpv4
    return next
  }

  const legacyName = value.startsWith('legacy|') ? value.slice('legacy|'.length) : ''
  const legacyRows = legacyName ? rows.filter((row) => row.name === legacyName) : []
  const adapter = rows.find((row) => adapterValue(row) === value)
    || (legacyRows.length === 1 ? legacyRows[0] : undefined)
  if (!adapter) return next // 保存的不可用项只展示，不静默改写
  next.networkAdapter = adapter.name
  next.networkAdapterId = adapter.id
  next.networkAdapterIpv4 = adapter.ipv4
  return next
}
