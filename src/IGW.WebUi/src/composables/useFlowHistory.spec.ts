import { ref } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { useFlowHistory } from './useFlowHistory'

type Task = { flow: { nodes: any[]; edges: any[] } }
const task = (): Task => ({ flow: { nodes: [], edges: [] } })

function setup(owner = ref<Task | null>(task())) {
  const history = useFlowHistory<Task>({
    owner,
    snapshot: t => JSON.stringify(t.flow),
    apply: (t, json) => { t.flow = JSON.parse(json) },
    commitDelayMs: 100,
    mergeMs: 1000,
  })
  history.track()
  return { owner, history }
}

describe('useFlowHistory', () => {
  beforeEach(() => vi.useFakeTimers())
  afterEach(() => vi.useRealTimers())

  it('keeps only the latest five operations, newest first', () => {
    const { owner, history } = setup()
    for (let i = 0; i < 7; i++) {
      owner.value!.flow.nodes.push({ id: `n${i}` })
      history.record(`添加节点 ${i}`)
      vi.advanceTimersByTime(2000)
    }
    expect(history.entries.value.map(e => e.label)).toEqual(['添加节点 6', '添加节点 5', '添加节点 4', '添加节点 3', '添加节点 2'])
  })

  it('merges rapid edits with the same label and drops no-op edits', () => {
    const { owner, history } = setup()
    owner.value!.flow.nodes.push({ id: 'a', title: '' })
    history.record('添加节点'); vi.advanceTimersByTime(200)
    for (const ch of 'abc') { owner.value!.flow.nodes[0].title += ch; history.record('修改节点配置'); vi.advanceTimersByTime(50) }
    history.record('移动节点'); vi.advanceTimersByTime(200)   // 没有真实改动
    expect(history.entries.value.map(e => e.label)).toEqual(['修改节点配置', '添加节点'])
  })

  it('restores the state before an operation and records the restore so it can be undone', () => {
    const { owner, history } = setup()
    owner.value!.flow.nodes.push({ id: 'a' }); history.record('添加 A'); vi.advanceTimersByTime(2000)
    owner.value!.flow.nodes.push({ id: 'b' }); history.record('添加 B'); vi.advanceTimersByTime(2000)
    const addA = history.entries.value.find(e => e.label === '添加 A')!
    expect(history.restore(addA.id, '回退')).toBe(true)
    expect(owner.value!.flow.nodes).toEqual([])
    const undo = history.entries.value[0]
    expect(undo.label).toBe('回退')
    history.restore(undo.id, '再回退')
    expect(owner.value!.flow.nodes.map((n: any) => n.id)).toEqual(['a', 'b'])
  })

  it('keeps separate histories per task', () => {
    const first = task(), second = task()
    const { owner, history } = setup(ref<Task | null>(first))
    first.flow.nodes.push({ id: 'a' }); history.record('一'); vi.advanceTimersByTime(2000)
    owner.value = second; history.track()
    expect(history.entries.value).toEqual([])
    second.flow.nodes.push({ id: 'b' }); history.record('二')
    owner.value = first; history.track()   // 切换前把未提交的改动提交到原任务
    expect(history.entries.value.map(e => e.label)).toEqual(['一'])
    owner.value = second; history.track()
    expect(history.entries.value.map(e => e.label)).toEqual(['二'])
  })

  it('skips history for flows larger than the snapshot limit', () => {
    const owner = ref<Task | null>(task())
    const history = useFlowHistory<Task>({
      owner, snapshot: t => JSON.stringify(t.flow), apply: () => {}, commitDelayMs: 10, maxSnapshotChars: 200,
    })
    history.track()
    owner.value!.flow.nodes.push({ id: 'x'.repeat(300) }); history.record('添加节点'); vi.advanceTimersByTime(50)
    expect(history.entries.value).toEqual([])
    expect(history.oversized.value).toBe(true)
  })
})
