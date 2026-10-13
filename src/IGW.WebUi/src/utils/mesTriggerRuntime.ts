export const RECENT_COLLECTION_CYCLE_MS = 2500

export function findMesRuntimeTask(
  runtimeTasks: any[],
  task: { id?: string; name?: string; groupRef?: string },
) {
  const tasks = Array.isArray(runtimeTasks) ? runtimeTasks : []
  const id = String(task?.id || '').trim()
  if (id) {
    const exact = tasks.find((item) => String(item?.id || '') === id)
    if (exact) return exact
  }

  const group = String(task?.groupRef || '').trim().toLowerCase()
  if (group) {
    const exact = tasks.find((item) => String(item?.group || '').trim().toLowerCase() === group)
    if (exact) return exact
  }

  const name = String(task?.name || '')
  if (!name) return undefined
  const sameName = tasks.filter((item) => String(item?.name || '') === name)
  return sameName.length === 1 ? sameName[0] : undefined
}

export function presentMesRuntimeNode(
  nodeType: string,
  snapshot: any,
  recentWindowMs = RECENT_COLLECTION_CYCLE_MS,
) {
  if (!snapshot || nodeType !== 'Trigger' || snapshot.state !== 'wait') return snapshot

  const hits = Number(snapshot.hits || 0)
  const ageMs = Number(snapshot.ageMs)
  if (hits <= 0 || !Number.isFinite(ageMs) || ageMs < 0 || ageMs > recentWindowMs) return snapshot

  return {
    ...snapshot,
    state: 'ok',
    info: '采集周期已执行',
  }
}
