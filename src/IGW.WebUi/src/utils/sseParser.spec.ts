import { describe, expect, it } from 'vitest'
import { parseSseChunks, readableStreamChunks } from './sseParser'

async function* chunks(parts: Uint8Array[]) {
  for (const part of parts) yield part
}

describe('SSE parser', () => {
  it('parses fields and UTF-8 split across arbitrary network chunks', async () => {
    const bytes = new TextEncoder().encode('event: tag-delta\nid: stream:2\ndata: {"值":"中文"}\n\n')
    const frames = []
    for await (const frame of parseSseChunks(chunks([
      bytes.slice(0, 13), bytes.slice(13, 42), bytes.slice(42, 45), bytes.slice(45),
    ]))) frames.push(frame)

    expect(frames).toEqual([{ event: 'tag-delta', id: 'stream:2', data: '{"值":"中文"}' }])
  })

  it('joins repeated data fields and accepts CRLF', async () => {
    const bytes = new TextEncoder().encode('event: runtime-event\r\ndata: one\r\ndata: two\r\n\r\n')
    const frames = []
    for await (const frame of parseSseChunks(chunks([bytes]))) frames.push(frame)
    expect(frames[0].data).toBe('one\ntwo')
  })

  it('cancels the browser reader when its consumer stops before EOF', async () => {
    let cancelled = false
    const source = new ReadableStream<Uint8Array>({
      start(controller) { controller.enqueue(new Uint8Array([1])) },
      cancel() { cancelled = true },
    })

    for await (const _ of readableStreamChunks(source)) break

    expect(cancelled).toBe(true)
  })
})
