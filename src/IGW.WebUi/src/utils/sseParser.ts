export interface SseFrame {
  event: string
  id: string
  data: string
}

/** Incremental SSE parser. It preserves split UTF-8 code points and incomplete lines. */
export async function* parseSseChunks(chunks: AsyncIterable<Uint8Array>): AsyncGenerator<SseFrame> {
  const decoder = new TextDecoder('utf-8', { fatal: true })
  let pending = ''
  let event = ''
  let id = ''
  let data: string[] = []

  const dispatch = () => {
    if (data.length === 0) {
      event = ''
      return null
    }
    const frame: SseFrame = { event: event || 'message', id, data: data.join('\n') }
    event = ''
    data = []
    return frame
  }

  const processLine = (rawLine: string) => {
    const line = rawLine.endsWith('\r') ? rawLine.slice(0, -1) : rawLine
    if (line === '') return dispatch()
    if (line.startsWith(':')) return null
    const separator = line.indexOf(':')
    const field = separator < 0 ? line : line.slice(0, separator)
    let value = separator < 0 ? '' : line.slice(separator + 1)
    if (value.startsWith(' ')) value = value.slice(1)
    if (field === 'event') event = value
    else if (field === 'id' && !value.includes('\0')) id = value
    else if (field === 'data') data.push(value)
    return null
  }

  for await (const chunk of chunks) {
    pending += decoder.decode(chunk, { stream: true })
    let newline = pending.indexOf('\n')
    while (newline >= 0) {
      const frame = processLine(pending.slice(0, newline))
      pending = pending.slice(newline + 1)
      if (frame) yield frame
      newline = pending.indexOf('\n')
    }
  }

  pending += decoder.decode()
  if (pending.length > 0) processLine(pending)
}

export async function* readableStreamChunks(
  stream: ReadableStream<Uint8Array>,
): AsyncGenerator<Uint8Array> {
  const reader = stream.getReader()
  let completed = false
  try {
    while (true) {
      const result = await reader.read()
      if (result.done) {
        completed = true
        return
      }
      if (result.value) yield result.value
    }
  } finally {
    if (!completed) {
      try { await reader.cancel('sse_consumer_stopped') }
      catch { /* 请求 Abort 与 reader.cancel 可能并发；两者都表示旧流已经不可复用。 */ }
    }
    reader.releaseLock()
  }
}
