import { createPinia } from 'pinia'
import { shallowMount } from '@vue/test-utils'
import { ElMessageBox } from 'element-plus'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { nextTick } from 'vue'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

const mocks = vi.hoisted(() => ({
  health: vi.fn(),
  project: vi.fn(),
  summary: vi.fn(),
  shell: vi.fn(),
  reconnect: vi.fn(),
}))

vi.mock('../api', async importOriginal => {
  const original = await importOriginal<typeof import('../api')>()
  return {
    ...original,
    api: {
      ...original.api,
      health: mocks.health,
      project: mocks.project,
      reconnectChannel: mocks.reconnect,
      drivers: vi.fn().mockResolvedValue([{ driverId: 'Siemens.S7', displayName: 'Siemens', deviceSettings: [], channelSettings: [] }]),
      networkAdapters: vi.fn().mockResolvedValue([]),
    },
  }
})

vi.mock('../features/project/api', async importOriginal => {
  const original = await importOriginal<typeof import('../features/project/api')>()
  return {
    ...original,
    projectApi: { ...original.projectApi, summary: mocks.summary, shell: mocks.shell, mutate: vi.fn(() => new Promise(() => {})) },
  }
})

import { clearSession, session, type AccountDto } from '../auth'
import Project from './Project.vue'

function account(permissions: Record<string, any>, isAdmin = false): AccountDto {
  return { username: 'op', displayName: '', isAdmin, mustChangePassword: false, disabled: false, createdUtc: '', permissions }
}

async function settle() {
  for (let index = 0; index < 6; index++) {
    await Promise.resolve()
    await nextTick()
  }
}

function sampleProject() {
  return {
    name: '工程', settings: {}, opcUa: {}, history: {}, mqttPublish: {}, mesUpload: { tasks: [] },
    channels: [{ id: 'c1', name: 'C', driverId: 'Siemens.S7', settings: {}, devices: [
      { id: 'd1', name: 'D', endpoint: '10.0.0.1', settings: {}, groups: [
        { id: 'g1', name: 'DB1', subGroups: [], tags: [{ id: 't1', name: 'T1', address: 'DB1.DBW0', dataType: 'int16', access: 'read' }] },
      ], tags: [{ id: 't2', name: 'T2', address: 'DB1.DBW2', dataType: 'int16', access: 'read' }] },
    ] }],
  }
}

async function mountProject() {
  mocks.health.mockResolvedValue({ tags: 2, devices: 1 })
  mocks.summary.mockResolvedValue({ totalTagCount: 2, fullProjectSupported: false })
  mocks.shell.mockResolvedValue({ project: sampleProject(), lazy: {}, rev: '"r1"' })
  const wrapper = shallowMount(Project, {
    global: {
      plugins: [createPinia()],
      mocks: { $t: (key: string) => key },
      stubs: { ElAutoResizer: { template: '<div />' }, ElTableColumn: { template: '<div />' } },
    },
  })
  await settle()
  await vi.waitFor(() => expect(mocks.shell).toHaveBeenCalledOnce())
  await settle()
  const state = (wrapper.vm.$ as any).setupState
  return { wrapper, state }
}

function nodeOf(state: any, kind: string): any {
  const walk = (nodes: any[]): any => {
    for (const node of nodes) {
      if (node.kind === kind) return node
      const hit = walk(node.children || [])
      if (hit) return hit
    }
    return null
  }
  return walk(state.treeData)
}

const contextEvent = () => ({ preventDefault: vi.fn(), clientX: 10, clientY: 10 }) as unknown as MouseEvent
const labels = (state: any) => state.menu.items.filter((item: any) => !item.sep).map((item: any) => item.label)

afterEach(() => { clearSession(); vi.clearAllMocks(); vi.restoreAllMocks() })

describe('工程配置只能看', () => {
  it('工具栏只保留重新载入/导出/复制/属性/QC，不显示任何修改按钮', async () => {
    session.account = account({ project: 'View', monitor: 'View' })
    const { wrapper, state } = await mountProject()
    expect(state.projectEditable).toBe(false)
    state.pickNode(nodeOf(state, 'device'))
    await nextTick()
    const titles = wrapper.findAll('.bar button').map(button => button.attributes('title'))
    for (const hidden of ['新建工程', '导入 JSON 配置', '保存并应用', '新建通道', '新建设备', '新建标记组', '新建标记', '剪切 (Ctrl+X)', '粘贴 (Ctrl+V)', '删除 (Del)'])
      expect(titles).not.toContain(hidden)
    for (const kept of ['重新载入工程数据（不刷新网页）', '导出 JSON 配置', '复制 (Ctrl+C)', '属性'])
      expect(titles).toContain(kept)
    wrapper.unmount()
  })

  it('右键菜单去掉新建/剪切/粘贴/删除/导入/采集开关，保留复制、属性、点位监控', async () => {
    session.account = account({ project: 'View', monitor: 'View' })
    const { wrapper, state } = await mountProject()
    await state.onTreeCtx(contextEvent(), nodeOf(state, 'device'))
    const device = labels(state)
    expect(device).toEqual(expect.arrayContaining(['复制', '属性…', '在此处打开点位监控(Quick Client)', '导出 CSV']))
    for (const hidden of ['新建标记组', '新建标记', '剪切', '粘贴', '删除', '导入 CSV…', '从 PLC 文件导入(西门子 DB)…', '一键开启直属组采集流程…'])
      expect(device).not.toContain(hidden)
    expect(state.menu.items[0].sep).toBeFalsy()
    expect(state.menu.items[state.menu.items.length - 1].sep).toBeFalsy()

    await state.onTreeCtx(contextEvent(), nodeOf(state, 'channel'))
    for (const hidden of ['新建设备…', '一键全开采集(本通道所有主标记组)', '一键全关采集(本通道)', '立即重连'])
      expect(labels(state)).not.toContain(hidden)

    state.pickNode(nodeOf(state, 'device'))
    state.hideMenu()
    state.onGridCtx(contextEvent())
    expect(state.menu.show).toBe(false)
    wrapper.unmount()
  })

  it('修改入口全部不生效：工程不变、不会变脏、不会弹确认', async () => {
    session.account = account({ project: 'View' })
    const confirm = vi.spyOn(ElMessageBox, 'confirm')
    const { wrapper, state } = await mountProject()
    const before = JSON.stringify(state.proj)
    const device = nodeOf(state, 'device')
    state.pickNode(device)

    state.onKey({ key: 'Delete', target: document.body, preventDefault: vi.fn() })
    state.onKey({ key: 'v', ctrlKey: true, target: document.body, preventDefault: vi.fn() })
    state.onKey({ key: 'x', ctrlKey: true, target: document.body, preventDefault: vi.fn() })
    await state.doDelete()
    await state.doCut()
    await state.doPaste()
    state.newChannel(); state.newDevice(); state.newGroup(); state.newTag(); state.newByView()
    await state.newProject()
    await state.enableAllCollect(nodeOf(state, 'channel'))
    await state.disableAllCollect(nodeOf(state, 'channel'))
    await state.enableDeviceFirstLevelFlows(device)
    await state.applyOpcUaTags({ tags: [{ name: 'X', address: 'ns=2;s=X', dataType: 'int16', groupPath: [] }], overwriteConflicts: true })
    state.importKepProject()
    state.markDirty()
    await settle()

    expect(confirm).not.toHaveBeenCalled()
    expect(state.wiz.open).toBe(false)
    expect(state.grpOpen).toBe(false)
    expect(state.tagOpen).toBe(false)
    expect(state.projectImportOpen).toBe(false)
    expect(state.dirty).toBe(false)
    expect(JSON.stringify(state.proj)).toBe(before)
    wrapper.unmount()
  })

  it('属性对话框能打开查看，但“确定”不写回工程', async () => {
    session.account = account({ project: 'View' })
    const { wrapper, state } = await mountProject()
    const tag = state.proj.channels[0].devices[0].tags[0]
    state.editDef(tag)
    expect(state.tagOpen).toBe(true)
    state.tagForm.name = '改名'
    await state.confirmTag()
    expect(tag.name).toBe('T2')

    const group = state.proj.channels[0].devices[0].groups[0]
    state.editDef(group)
    expect(state.grpOpen).toBe(true)
    state.grpForm.name = '改名'
    await state.confirmGroup()
    expect(group.name).toBe('DB1')

    state.openProjProps()
    state.projForm.name = '改名'
    state.confirmProjProps()
    expect(state.proj.name).toBe('工程')
    expect(state.dirty).toBe(false)
    wrapper.unmount()
  })

  it('拖拽移动标记组被禁止', async () => {
    session.account = account({ project: 'View' })
    const { wrapper, state } = await mountProject()
    const group = nodeOf(state, 'group')
    const dragEvent = { preventDefault: vi.fn(), dataTransfer: null } as unknown as DragEvent
    state.onDragStart(dragEvent, group)
    expect(dragEvent.preventDefault).toHaveBeenCalled()
    expect(state.dragData).toBeNull()
    wrapper.unmount()
  })

  it('属性对话框里的表单全部按权限禁用，确定按钮按权限隐藏', () => {
    const source = readFileSync(resolve(process.cwd(), 'src/views/Project.vue'), 'utf8')
    const template = source.slice(source.indexOf('</script>'))
    const forms = template.match(/<el-form\s[^>]*>/g) || []
    expect(forms.length).toBeGreaterThan(0)
    for (const form of forms) expect(form).toContain(':disabled="!projectEditable"')
    for (const confirm of ['confirmChProps', 'confirmDevice"', 'confirmDeviceProps', 'confirmGroup', 'confirmTag', 'confirmProjProps'])
      expect(template).toMatch(new RegExp(`v-if="projectEditable"[^>]*@click="${confirm}`))
  })
})

describe('工程配置可修改', () => {
  it('管理员保留全部修改入口', async () => {
    session.account = account({}, true)
    vi.spyOn(ElMessageBox, 'confirm').mockRejectedValue('cancel')
    const { wrapper, state } = await mountProject()
    expect(state.projectEditable).toBe(true)
    await state.onTreeCtx(contextEvent(), nodeOf(state, 'device'))
    expect(labels(state)).toEqual(expect.arrayContaining(['新建标记组', '新建标记', '剪切', '粘贴', '删除', '导入 CSV…']))
    await state.onTreeCtx(contextEvent(), nodeOf(state, 'channel'))
    expect(labels(state)).toContain('立即重连')

    state.pickNode(nodeOf(state, 'device'))
    state.newGroup()
    expect(state.grpOpen).toBe(true)
    state.grpForm.name = 'DB2'
    await state.confirmGroup()
    expect(state.proj.channels[0].devices[0].groups.map((group: any) => group.name)).toContain('DB2')
    expect(state.dirty).toBe(true)

    await state.doDelete()
    expect(ElMessageBox.confirm).toHaveBeenCalled()
    wrapper.unmount()
  })

  it('工程可修改但监控中心只能看时不显示“立即重连”', async () => {
    session.account = account({ project: 'Edit', monitor: 'View' })
    const { wrapper, state } = await mountProject()
    await state.onTreeCtx(contextEvent(), nodeOf(state, 'channel'))
    expect(labels(state)).toContain('新建设备…')
    expect(labels(state)).not.toContain('立即重连')
    await state.reconnectChannel(nodeOf(state, 'channel'))
    expect(mocks.reconnect).not.toHaveBeenCalled()
    wrapper.unmount()
  })
})
