import { flushPromises, mount } from '@vue/test-utils'
import { defineComponent, h, nextTick } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import Points from './Points.vue'
import { api } from '../api'

vi.mock('../api', () => ({
  api: {
    projectTagScope: vi.fn(),
    write: vi.fn(),
  },
}))

const AutoResizerStub = defineComponent({
  name: 'ElAutoResizer',
  setup(_, { slots }) {
    return () => h('div', slots.default?.({ height: 500, width: 900 }))
  },
})

const TreeStub = defineComponent({
  name: 'ElTreeV2',
  props: {
    data: { type: Array, default: () => [] },
    defaultExpandedKeys: { type: Array, default: () => [] },
  },
  emits: ['node-click', 'node-expand', 'node-collapse', 'node-contextmenu'],
  setup(_, { expose }) {
    expose({ setCurrentKey: vi.fn() })
    return () => h('div', { class: 'tree-stub' })
  },
})

function definition(id: string) {
  const segments = id.split('.')
  return {
    id,
    name: segments[segments.length - 1],
    address: id,
    dataType: 'Int16',
    access: 'Read',
    description: null,
    engUnit: null,
    stringLength: null,
    array: null,
  }
}

describe('Quick Client tree expansion state', () => {
  beforeEach(() => {
    vi.mocked(api.projectTagScope).mockImplementation(async ({ root }) => {
      const path = root || ''
      if (path === 'PLC.Device.Root') {
        return {
          projectRevision: 'project-1',
          realtimeRevision: 'realtime-1',
          root: path,
          groups: [{ name: 'Child', path: 'PLC.Device.Root.Child', kind: 'Group', description: null }],
          definitions: [definition('PLC.Device.Root.Tag')],
          nextCursor: null,
          total: 1,
        }
      }
      return {
        projectRevision: 'project-1',
        realtimeRevision: 'realtime-1',
        root: path,
        groups: [],
        definitions: [definition(`${path}.Tag`)],
        nextCursor: null,
        total: 1,
      }
    })
  })

  it('keeps an expanded parent open when clicking a child refreshes lazy tree data', async () => {
    const wrapper = mount(Points, {
      props: { q: 'PLC.Device.Root', suspended: true },
      global: {
        stubs: {
          ElAutoResizer: AutoResizerStub,
          ElTreeV2: TreeStub,
          ElTableV2: true,
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

    const tree = wrapper.findComponent(TreeStub)
    const server = (tree.props('data') as any[])[0]
    const parent = server.children[0]
    const child = parent.children[0]

    tree.vm.$emit('node-expand', parent)
    await nextTick()
    tree.vm.$emit('node-click', child)
    await flushPromises()

    expect(tree.props('defaultExpandedKeys')).toEqual(expect.arrayContaining(['__srv', parent.key]))
    wrapper.unmount()
  })

  it('forgets a collapsed branch and all descendant expansion state', async () => {
    const wrapper = mount(Points, {
      props: { q: 'PLC.Device.Root', suspended: true },
      global: {
        stubs: {
          ElAutoResizer: AutoResizerStub,
          ElTreeV2: TreeStub,
          ElTableV2: true,
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

    const tree = wrapper.findComponent(TreeStub)
    const parent = (tree.props('data') as any[])[0].children[0]
    const child = parent.children[0]
    child.children = [{
      key: `${child.key}.Grandchild`,
      label: 'Grandchild',
      path: `${child.path}.Grandchild`,
      kind: 'Group',
      children: [],
    }]

    tree.vm.$emit('node-expand', parent)
    tree.vm.$emit('node-expand', child)
    tree.vm.$emit('node-expand', child.children[0])
    await nextTick()
    tree.vm.$emit('node-collapse', parent)
    await nextTick()

    expect(tree.props('defaultExpandedKeys')).toEqual(['__srv'])
    wrapper.unmount()
  })
})
