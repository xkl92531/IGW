import { ref, shallowRef } from 'vue'
import type { ProjectPageDto, ProjectTagQuery, ProjectTagRowDto } from './types'

interface ProjectTagApi {
  tags(query: ProjectTagQuery, signal?: AbortSignal): Promise<ProjectPageDto<ProjectTagRowDto>>
}

interface ProjectTagPageOptions {
  pageSize?: number
  debounceMs?: number
}

export function useTagPage(api: ProjectTagApi, options: ProjectTagPageOptions = {}) {
  const pageSize = options.pageSize ?? 200
  const debounceMs = options.debounceMs ?? 200
  const items = shallowRef<ProjectTagRowDto[]>([])
  const total = ref(0)
  const offset = ref(0)
  const groupId = ref<string>()
  const deviceId = ref<string>()
  const query = ref('')
  const loading = ref(false)
  const error = shallowRef<unknown>(null)
  let generation = 0
  let controller: AbortController | null = null
  let debounceTimer: ReturnType<typeof setTimeout> | null = null
  let disposed = false

  async function load() {
    if (disposed) return
    const requestGeneration = ++generation
    controller?.abort()
    controller = new AbortController()
    loading.value = true
    error.value = null
    try {
      const page = await api.tags({
        groupId: groupId.value,
        deviceId: deviceId.value,
        q: query.value || undefined,
        offset: offset.value,
        limit: pageSize,
      }, controller.signal)
      if (disposed || requestGeneration !== generation) return
      items.value = page.items.slice(0, pageSize)
      total.value = page.total
      offset.value = page.offset
    } catch (reason) {
      if (disposed || requestGeneration !== generation || controller.signal.aborted) return
      error.value = reason
    } finally {
      if (requestGeneration === generation) loading.value = false
    }
  }

  function scheduleLoad() {
    if (debounceTimer) clearTimeout(debounceTimer)
    debounceTimer = setTimeout(() => {
      debounceTimer = null
      void load()
    }, debounceMs)
  }

  function search(value: string) {
    query.value = value.trim()
    offset.value = 0
    controller?.abort()
    generation++
    scheduleLoad()
  }

  function selectGroup(value?: string) {
    groupId.value = value
    deviceId.value = undefined
    offset.value = 0
    controller?.abort()
    generation++
    void load()
  }

  // Kepware 导入常把点位直挂在设备下（组为空）：选中设备也要能翻这些点位。
  function selectDevice(value?: string) {
    deviceId.value = value
    groupId.value = undefined
    offset.value = 0
    controller?.abort()
    generation++
    void load()
  }

  function goToPage(pageNumber: number) {
    offset.value = Math.max(0, pageNumber - 1) * pageSize
    void load()
  }

  function dispose() {
    disposed = true
    generation++
    controller?.abort()
    if (debounceTimer) clearTimeout(debounceTimer)
    debounceTimer = null
  }

  return {
    items,
    total,
    offset,
    pageSize,
    groupId,
    deviceId,
    query,
    loading,
    error,
    load,
    search,
    selectGroup,
    selectDevice,
    goToPage,
    dispose,
  }
}
