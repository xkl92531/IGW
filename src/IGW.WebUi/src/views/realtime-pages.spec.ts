import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

const read = (file: string) => readFileSync(resolve(process.cwd(), file), 'utf8')

describe('runtime pages use shared realtime state', () => {
  it.each(['src/views/Overview.vue', 'src/views/Monitor.vue', 'src/views/Mes.vue'])(
    '%s does not poll high-frequency device or MES state over HTTP', file => {
      const source = read(file)
      expect(source).toContain('useRealtimeStore')
      expect(source).not.toContain('api.devices()')
      expect(source).not.toContain('api.mes()')
    },
  )

  it('MES timeline loads one authoritative history snapshot then consumes ordered stream events', () => {
    const source = read('src/components/MesTimeline.vue')
    expect(source).toContain('runtimeEvents')
    expect(source).toContain('api.mesTimeline(')
    expect(source).not.toContain('setInterval(')
  })

  it('the application owns one shared runtime stream', () => {
    const source = read('src/App.vue')
    expect(source).toContain('startRuntimeRealtime')
    expect(source).toContain('scheduleRuntimeRestart')
    expect(source).toContain('topics: globalRuntimeTopics()')
    expect(source).not.toContain("topics: ['devices', 'mes']")
  })

  it('project device status consumes the shared device SSE state instead of polling devices', () => {
    const source = read('src/views/Project.vue')
    expect(source).toContain('useRealtimeStore')
    expect(source).toContain('deviceStates')
    expect(source).not.toContain('api.devices()')
  })

  it('gateway settings reads and saves only local setting sections', () => {
    const source = read('src/views/GatewaySettings.vue')
    expect(source).toContain('projectApi.settings()')
    expect(source).toContain("kind: 'replaceProjectSections'")
    expect(source).not.toContain('api.project()')
    expect(source).not.toContain('api.putProject(')
  })

  it('MES flow uses a task-scoped state and transition stream with one-shot HTTP fallback', () => {
    const source = read('src/views/MesFlow.vue')
    expect(source).toContain('new RealtimeStreamClient')
    expect(source).toContain('topics: mesFlowTopics(taskId)')
    expect(source).toContain("onState: state => realtime.setConnectionState(state, activeTaskStreamKey.value)")
    expect(source).toContain('taskRealtimeStatus.value === \'live\'')
    expect(source).not.toContain("status: realtimeStatus")
    expect(source).toContain('api.mes()')
    expect(source).not.toContain('fallbackLiveTimer = setInterval')
  })

  it('MES status creates the all-task stream only inside the MES page', () => {
    const source = read('src/views/Mes.vue')
    expect(source).toContain('topics: mesStatusTopics()')
  })

  it('idle canvas mouse movement does not force layout or reactive pointer writes', () => {
    const source = read('src/views/MesFlow.vue')
    expect(source).toMatch(/function onMove\(ev: MouseEvent\) \{\s*if \(!drag\.mode && !connecting\.value\) return/)
  })
})
