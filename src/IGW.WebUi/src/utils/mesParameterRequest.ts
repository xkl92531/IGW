export interface MesParameterRequestCoordinator<T> {
  search: (
    query: string,
    onResult: (value: T) => void,
    onError: (error: unknown) => void,
    immediate?: boolean,
  ) => void
  cancel: () => void
}

/** Coordinates debounced remote searches so stale or cancelled responses cannot update the UI. */
export function createMesParameterRequestCoordinator<T>(
  fetcher: (query: string, signal: AbortSignal) => Promise<T>,
  delayMs = 250,
): MesParameterRequestCoordinator<T> {
  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined
  let sequence = 0

  const cancel = () => {
    sequence++
    if (timer) clearTimeout(timer)
    timer = undefined
    controller?.abort()
    controller = undefined
  }

  const search = (
    query: string,
    onResult: (value: T) => void,
    onError: (error: unknown) => void,
    immediate = false,
  ) => {
    cancel()
    const request = sequence
    const current = new AbortController()
    controller = current
    timer = setTimeout(async () => {
      timer = undefined
      try {
        const value = await fetcher(query, current.signal)
        if (request === sequence && !current.signal.aborted) onResult(value)
      } catch (error) {
        if (request === sequence && !current.signal.aborted) onError(error)
      }
    }, immediate ? 0 : Math.max(0, delayMs))
  }

  return { search, cancel }
}
