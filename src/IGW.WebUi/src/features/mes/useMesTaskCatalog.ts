import { computed, ref, shallowRef } from 'vue'
import type { ProjectMesTaskQuery, ProjectMesTaskRowDto, ProjectPageDto } from '../project/types'

type CatalogScope = 'device' | 'channel'
type CatalogState = 'all' | 'running' | 'stopped'

interface MesTaskCatalogApi {
  mesTasks(query: ProjectMesTaskQuery, signal?: AbortSignal): Promise<ProjectPageDto<ProjectMesTaskRowDto>>
}

interface MesTaskCatalogOptions {
  deviceId: string
  channelId: string
  pageSize?: number
  debounceMs?: number
  runtime?: ReadonlyMap<string, unknown>
}

export function useMesTaskCatalog(api: MesTaskCatalogApi, options: MesTaskCatalogOptions) {
  const items = shallowRef<ProjectMesTaskRowDto[]>([])
  const total = ref(0)
  const offset = ref(0)
  const scope = ref<CatalogScope>('device')
  const state = ref<CatalogState>('all')
  const search = ref('')
  const loading = ref(false)
  const error = shallowRef<unknown>(null)
  const deviceId = ref(options.deviceId)
  const channelId = ref(options.channelId)
  const pageSize = options.pageSize ?? 200
  const debounceMs = options.debounceMs ?? 200
  let controller: AbortController | null = null
  let generation = 0
  let timer: ReturnType<typeof setTimeout> | null = null
  let disposed = false

  const viewItems = computed(() => items.value.map(item => ({
    ...item,
    runtime: options.runtime?.get(item.id),
  })))

  async function load() {
    if (disposed) return
    const requestGeneration = ++generation
    controller?.abort()
    const requestController = new AbortController()
    controller = requestController
    loading.value = true
    error.value = null
    const enabled = state.value === 'all' ? undefined : state.value === 'running'
    try {
      const page = await api.mesTasks({
        scope: scope.value,
        deviceId: scope.value === 'device' ? deviceId.value : undefined,
        channelId: scope.value === 'channel' ? channelId.value : undefined,
        q: search.value.trim() || undefined,
        offset: offset.value,
        limit: pageSize,
        enabled,
      }, requestController.signal)
      if (disposed || generation !== requestGeneration) return
      // 服务端已经过滤；这里再次防御旧服务端或缓存返回的禁用功能组。
      items.value = page.items.filter(item => item.flowAvailable).slice(0, pageSize)
      total.value = page.total
      offset.value = page.offset
    } catch (reason) {
      if (disposed || generation !== requestGeneration || requestController.signal.aborted) return
      error.value = reason
    } finally {
      if (generation === requestGeneration) loading.value = false
    }
  }

  async function setScope(next: CatalogScope) {
    scope.value = next
    offset.value = 0
    await load()
  }

  async function setState(next: CatalogState) {
    state.value = next
    offset.value = 0
    await load()
  }

  function setContext(nextDeviceId: string, nextChannelId: string) {
    deviceId.value = nextDeviceId
    channelId.value = nextChannelId
    offset.value = 0
  }

  function scheduleSearch(value: string) {
    search.value = value
    offset.value = 0
    controller?.abort()
    generation++
    if (timer) clearTimeout(timer)
    timer = setTimeout(() => { timer = null; void load() }, debounceMs)
  }

  function goToPage(pageNumber: number) {
    offset.value = Math.max(0, pageNumber - 1) * pageSize
    void load()
  }

  function dispose() {
    disposed = true
    generation++
    controller?.abort()
    if (timer) clearTimeout(timer)
    timer = null
  }

  return {
    items,
    viewItems,
    total,
    offset,
    pageSize,
    scope,
    state,
    search,
    loading,
    error,
    load,
    setScope,
    setState,
    setContext,
    scheduleSearch,
    goToPage,
    dispose,
  }
}
