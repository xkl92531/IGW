import { shallowRef } from 'vue'
import type { AxiosInstance, AxiosRequestConfig } from 'axios'

declare module 'axios' {
  interface AxiosRequestConfig { igwBackground?: boolean }
}

export const uiActivities = shallowRef<ReadonlyArray<{ id: number; label: string; startedAt: number }>>([])
let sequence = 0
export function beginUiActivity(label: string): () => void {
  const id = ++sequence
  uiActivities.value = [...uiActivities.value, { id, label, startedAt: Date.now() }]
  let ended = false
  return () => {
    if (ended) return
    ended = true
    uiActivities.value = uiActivities.value.filter(item => item.id !== id)
  }
}

// Background health, subscriptions and job polling must not look like a blocked user action.
// Labels never contain request bodies, credentials, field values or raw URLs.
export function requestActivityLabel(config: Pick<AxiosRequestConfig, 'url' | 'method' | 'igwBackground'>): string | null {
  if (config.igwBackground) return null
  const path = String(config.url || '').split('?')[0]
  const read = String(config.method || 'get').toLowerCase() === 'get'
  if (/\/hubs\//.test(path) || /\/(health|realtime|events|stream)(\/|$)/.test(path)
    || read && (/\/project\/tag-scope$/.test(path) || /\/runtime-logs$/.test(path) || /\/validation\/jobs\/[^/]+$/.test(path))) return null
  if (!path.startsWith('/api/')) return null
  if (/export/.test(path)) return '正在导出数据…'
  if (/import/.test(path)) return '正在导入数据…'
  if (/browse/.test(path)) return '正在浏览设备点位…'
  if (/preview/.test(path)) return '正在生成预览…'
  if (/reconnect/.test(path)) return '正在重新连接设备…'
  if (/authentication/.test(path)) return '正在验证设备连接…'
  if (!read) return '正在保存并应用配置…'
  if (/\/log-center\/logs$/.test(path)) return '正在查询日志…'
  if (/\/project\/(tags|parameter-options)/.test(path)) return '正在加载工程点位…'
  if (/template/.test(path)) return '正在加载模板…'
  return '正在加载配置…'
}

export function installRequestActivity(http: AxiosInstance) {
  const active = new WeakMap<object, () => void>()
  const finish = (config?: object) => {
    if (!config) return
    active.get(config)?.()
    active.delete(config)
  }
  http.interceptors.request.use(config => {
    const label = requestActivityLabel(config)
    if (label) active.set(config, beginUiActivity(label))
    return config
  })
  http.interceptors.response.use(response => {
    finish(response.config)
    return response
  }, error => {
    finish(error?.config)
    return Promise.reject(error)
  })
}
