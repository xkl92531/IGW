import { reactive } from 'vue'
import { api } from '../api'

/** 工程配置自动保存设置(保存在网关数据目录，所有浏览器共用)。 */
export const projectAutoSaveSettings = reactive({ enabled: true, seconds: 30, loaded: false })

let loading: Promise<void> | null = null

export function loadProjectAutoSaveSettings(force = false): Promise<void> {
  if (loading && !force) return loading
  loading = api.uiPreferences().then((preferences) => {
    applyProjectAutoSavePreferences(preferences)
  }).catch(() => { /* 读取失败时保持默认：开启、30 秒 */ })
  return loading
}

export function applyProjectAutoSavePreferences(preferences: { projectAutoSave?: boolean; projectAutoSaveSeconds?: number }) {
  if (typeof preferences.projectAutoSave === 'boolean') projectAutoSaveSettings.enabled = preferences.projectAutoSave
  if (Number.isFinite(preferences.projectAutoSaveSeconds)) projectAutoSaveSettings.seconds = Number(preferences.projectAutoSaveSeconds)
  projectAutoSaveSettings.loaded = true
}
