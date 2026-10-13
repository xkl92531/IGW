import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, h } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import Points from './Points.vue'
import { api } from '../api'

const streamHarness = vi.hoisted(() => ({
  onState: null as null | ((state: string) => void),
  onFrame: null as null | ((frame: { event: string; id: string; data: string }) => void),
}))

vi.mock('../api', () => ({
  api: {
    projectTagScope: vi.fn(),
    write: vi.fn(),
  },
}))

vi.mock('../utils/realtimeStream', () => ({
  RealtimeStreamClient: class {
    constructor(options: { onState?: (state: string) => void; onFrame?: (frame: { event: string; id: string; data: string }) => void }) {
      streamHarness.onState = options.onState || null
      streamHarness.onFrame = options.onFrame || null
    }
    start() { streamHarness.onState?.('connecting') }
    stop() {}
    requestSnapshot() {}
  },
}))

const AutoResizerStub = defineComponent({
  name: 'ElAutoResizer',
  setup(_, { slots }) {
    return () => h('div', slots.default?.({ height: 500, width: 900 }))
  },
})

const GridStub = defineComponent({
  name: 'ElTableV2',
  props: { data: { type: Array, default: () => [] } },
  setup(props) {
    return () => h('div', { class: 'grid-stub' }, (props.data as any[])
      .map(row => `${row.itemId}|${row.address}|${row.quality}`).join('\n'))
  },
})

const TreeStub = defineComponent({
  name: 'ElTreeV2',
  setup(_, { expose }) {
    expose({ setCurrentKey: vi.fn() })
    return () => h('div')
  },
})

function scope(realtimeRevision: string, tagId: string) {
  const segments = tagId.split('.')
  return {
    projectRevision: `project-${realtimeRevision}`,
    realtimeRevision,
    root: 'PLC7.OP120.StationEntrance',
    groups: [],
    definitions: [{
      id: tagId,
      name: segments[segments.length - 1] || tagId,
      address: tagId,
      dataType: 'Int16',
      access: 'Read',
      description: null,
      engUnit: null,
      stringLength: null,
      array: null,
    }],
    nextCursor: null,
    total: 1,
  }
}

describe('Quick Client project generation recovery', () => {
  beforeEach(() => {
    vi.useFakeTimers()
    streamHarness.onState = null
    streamHarness.onFrame = null
    vi.mocked(api.projectTagScope)
      .mockResolvedValueOnce(scope('realtime-1', 'PLC7.OP120.StationEntrance.OldTag'))
      .mockResolvedValueOnce(scope('realtime-2', 'PLC7.OP120.StationEntrance.NewTag'))
  })

  afterEach(() => {
    vi.useRealTimers()
    vi.clearAllMocks()
  })

  it('reloads the selected scope after the server retires the old project generation', async () => {
    const wrapper = mount(Points, {
      props: { q: 'PLC7.OP120.StationEntrance' },
      global: {
        stubs: {
          ElAutoResizer: AutoResizerStub,
          ElTreeV2: TreeStub,
          ElTableV2: GridStub,
          ElTable: true,
          ElTableColumn: true,
          ElDialog: true,
          ElInput: true,
          ElButton: true,
          ArrayValueViewer: true,
        },
      },
    })
    await flushPromises()
    expect(wrapper.find('.grid-stub').text()).toContain('OldTag')

    streamHarness.onState?.('project-changed')
    await vi.advanceTimersByTimeAsync(1_000)
    await flushPromises()

    expect(wrapper.find('.grid-stub').text()).toContain('NewTag')
    expect(wrapper.find('.grid-stub').text()).not.toContain('OldTag')
    wrapper.unmount()
  })

  it('shows the actual address and exact OPC UA failure instead of only Bad', async () => {
    vi.mocked(api.projectTagScope).mockReset()
    const failureScope = scope('realtime-1', 'PLC7.OP120.StationEntrance.MESFlag')
    failureScope.definitions[0].address = 'ns=3;s=OP120.StationEntrance.MESFlag'
    vi.mocked(api.projectTagScope).mockResolvedValue(failureScope)
    const wrapper = mount(Points, {
      props: { q: 'PLC7.OP120.StationEntrance' },
      global: {
        stubs: {
          ElAutoResizer: AutoResizerStub,
          ElTreeV2: TreeStub,
          ElTableV2: GridStub,
          ElTable: true,
          ElTableColumn: true,
          ElDialog: true,
          ElInput: true,
          ElButton: true,
          ArrayValueViewer: true,
        },
      },
    })
    await flushPromises()

    streamHarness.onFrame?.({
      event: 'hello',
      id: 'stream-1:1',
      data: '{"streamId":"stream-1","projectRevision":"realtime-1"}',
    })
    streamHarness.onFrame?.({
      event: 'tag-delta',
      id: 'stream-1:2',
      data: JSON.stringify({ values: [{
        id: 'PLC7.OP120.StationEntrance.MESFlag',
        value: null,
        quality: 'BadRequestTimeout',
        qualityCode: 0x8085_0000,
        good: false,
        sourceTs: '2026-09-02T17:03:21.183Z',
      }] }),
    })
    await vi.advanceTimersByTimeAsync(20)

    const visible = wrapper.find('.grid-stub').text()
    expect(visible).toContain('ns=3;s=OP120.StationEntrance.MESFlag')
    expect(visible).toContain('BadRequestTimeout')
    expect(visible).toContain('0x80850000')
    wrapper.unmount()
  })
})
