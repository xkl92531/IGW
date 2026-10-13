import { describe, expect, it } from 'vitest'
import { globalRuntimeTopics, mesFlowTopics, mesStatusTopics } from './runtimeTopics'

describe('runtime SSE topic scopes', () => {
  it('keeps the application stream free of all-task MES state fan-out', () => {
    expect(globalRuntimeTopics()).toEqual(['devices', 'mes-availability'])
  })

  it('subscribes a flow to only its selected task state and transitions', () => {
    expect(mesFlowTopics('task-17')).toEqual(['mes-state:task-17', 'mes-task:task-17'])
  })

  it('loads all task states only while the MES status page is visible', () => {
    expect(mesStatusTopics()).toEqual(['mes-state'])
  })
})
