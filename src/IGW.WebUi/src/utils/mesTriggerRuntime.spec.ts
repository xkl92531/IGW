import { describe, expect, it } from 'vitest'
import { findMesRuntimeTask, presentMesRuntimeNode } from './mesTriggerRuntime'

describe('MES collection-cycle runtime presentation', () => {
  it('presents a recently fired waiting trigger as triggered', () => {
    expect(presentMesRuntimeNode('Trigger', {
      id: 'trigger', state: 'wait', info: '等待下一采集周期', ageMs: 120, hits: 8,
    })).toMatchObject({ state: 'ok', info: '采集周期已执行' })
  })

  it('keeps an older trigger snapshot waiting for the next cycle', () => {
    expect(presentMesRuntimeNode('Trigger', {
      id: 'trigger', state: 'wait', info: '等待下一采集周期', ageMs: 3000, hits: 8,
    })).toMatchObject({ state: 'wait', info: '等待下一采集周期' })
  })

  it('does not rewrite non-trigger node states', () => {
    const condition = { id: 'condition', state: 'wait', info: '等待条件', ageMs: 120, hits: 0 }
    expect(presentMesRuntimeNode('Condition', condition)).toBe(condition)
  })

  it('matches duplicate task names by stable task id', () => {
    const runtimeTasks = [
      { id: 'plc8-op090', name: 'OP090_1', group: 'PLC8.PLC8.OP090_1', nodes: [{ id: 'wrong' }] },
      { id: 'plc7-op090', name: 'OP090_1', group: 'PLC7.PLC7.OP090_1', nodes: [{ id: 'right' }] },
    ]

    expect(findMesRuntimeTask(runtimeTasks, {
      id: 'plc7-op090', name: 'OP090_1', groupRef: 'PLC7.PLC7.OP090_1',
    })?.nodes[0].id).toBe('right')
  })

  it('does not guess when only a duplicate task name is available', () => {
    const runtimeTasks = [
      { id: 'first', name: 'OP090_1', group: 'PLC8.PLC8.OP090_1' },
      { id: 'second', name: 'OP090_1', group: 'PLC7.PLC7.OP090_1' },
    ]

    expect(findMesRuntimeTask(runtimeTasks, { name: 'OP090_1' })).toBeUndefined()
  })
})
