import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { describe, expect, it, vi } from 'vitest'
import MoveTagDialog from './MoveTagDialog.vue'
import { i18n } from '../i18n'
import { projectApi } from '../features/project/api'

vi.mock('../features/project/api', () => ({
  projectApi: {
    resolvePaths: vi.fn(),
    shell: vi.fn(),
    lazyChildren: vi.fn(),
    mutate: vi.fn(),
  },
}))

const match = {
  path: '产线1_PLC1.S7Plus.DB110_OP010_User.Cmd.Flag',
  found: true,
  kind: 'tag',
  id: 'tag-flag',
  parentId: 'grp-db110',
  parentPath: '产线1_PLC1.S7Plus.DB110_OP010_User',
  parentKind: 'group',
}

const shell = {
  project: {
    channels: [{
      id: 'ch-1',
      name: '产线1_PLC1',
      devices: [{ id: 'dev-classic', name: 'S7' }, { id: 'dev-plus', name: 'S7Plus' }],
    }],
  },
  lazy: {},
}

async function open() {
  vi.mocked(projectApi.resolvePaths).mockResolvedValue([match] as any)
  vi.mocked(projectApi.shell).mockResolvedValue(shell as any)
  vi.mocked(projectApi.mutate).mockResolvedValue({ applied: true, conflict: false, revision: 'r2' } as any)
  const wrapper = mount(MoveTagDialog, {
    props: { modelValue: true, tagPath: match.path },
    global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
  })
  await flushPromises()
  return wrapper
}

describe('移动点位', () => {
  it('只改父节点：发出的变更是 moveEntities，带点位 id 与目标容器 id，不碰地址', async () => {
    const wrapper = await open()
    const state = (wrapper.vm.$ as any).setupState

    state.pick({ id: 'dev-classic', name: 'S7', path: '产线1_PLC1.S7', kind: 'device' })
    await state.submit()

    expect(projectApi.mutate).toHaveBeenCalledWith({
      kind: 'moveEntities',
      entityKind: 'tag',
      entityIds: ['tag-flag'],
      targetId: 'dev-classic',
      documentJson: null,
    })
    wrapper.unmount()
  }, 15000)

  it('可以整组移动：改的是这个点所在分组的父节点', async () => {
    const wrapper = await open()
    const state = (wrapper.vm.$ as any).setupState

    state.scope = 'group'
    state.pick({ id: 'dev-classic', name: 'S7', path: '产线1_PLC1.S7', kind: 'device' })
    await state.submit()

    expect(projectApi.mutate).toHaveBeenCalledWith(expect.objectContaining({
      entityKind: 'group',
      entityIds: ['grp-db110'],
      targetId: 'dev-classic',
    }))
    wrapper.unmount()
  }, 15000)

  it('通道不能当移动目标：点它不会被选中', async () => {
    const wrapper = await open()
    const state = (wrapper.vm.$ as any).setupState

    state.pick({ id: 'ch-1', name: '产线1_PLC1', path: '产线1_PLC1', kind: 'channel' })

    expect(state.targetId).toBe('')
    expect(state.canSubmit).toBe(false)
    wrapper.unmount()
  }, 15000)

  it('工程里已经没有这个点位时不发变更', async () => {
    vi.mocked(projectApi.resolvePaths).mockResolvedValue([{ ...match, found: false, id: null }] as any)
    vi.mocked(projectApi.shell).mockResolvedValue(shell as any)
    vi.mocked(projectApi.mutate).mockClear()
    const wrapper = mount(MoveTagDialog, {
      props: { modelValue: true, tagPath: match.path },
      global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
    })
    await flushPromises()
    const state = (wrapper.vm.$ as any).setupState

    state.pick({ id: 'dev-classic', name: 'S7', path: '产线1_PLC1.S7', kind: 'device' })
    await state.submit()

    expect(projectApi.mutate).not.toHaveBeenCalled()
    wrapper.unmount()
  }, 15000)
})
