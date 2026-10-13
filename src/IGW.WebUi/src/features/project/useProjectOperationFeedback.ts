import { computed, nextTick, shallowRef } from 'vue'

/** Yield before large in-memory project work so the operation label can paint. */
export async function paintProjectFeedback() {
  await nextTick()
  await new Promise<void>(resolve => {
    const fallback = setTimeout(resolve, 50)
    if (typeof requestAnimationFrame === 'function') requestAnimationFrame(() => {
      setTimeout(() => { clearTimeout(fallback); resolve() }, 0)
    })
  })
}

/** Explicit user actions only; background refreshes must not enter this scope. */
export function useProjectOperationFeedback(paint = paintProjectFeedback) {
  const operations = shallowRef(new Map<string, string>())
  const pending = new Map<string, Promise<unknown>>()
  const busy = computed(() => operations.value.size > 0)
  const label = computed(() => { const labels = [...operations.value.values()]; return labels[labels.length - 1] || '' })
  function run<T>(key: string, message: string, action: () => T | Promise<T>): Promise<T> {
    const existing = pending.get(key)
    if (existing) return existing as Promise<T>
    operations.value = new Map(operations.value).set(key, message)
    const promise = (async () => {
      try { await paint(); return await action() }
      finally {
        const remaining = new Map(operations.value); remaining.delete(key); operations.value = remaining
        pending.delete(key)
      }
    })()
    pending.set(key, promise)
    return promise
  }
  return { busy, label, run }
}
