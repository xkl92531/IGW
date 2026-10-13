import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { nextTick } from 'vue'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import MesFlow from './MesFlow.vue'
import { api, http } from '../api'
import { i18n } from '../i18n'
import { clearSession, session, type AccountDto } from '../auth'

const project = {
  channels: [{
    name: 'Line', driver: 'SiemensTcp',
    devices: [{ name: 'PLC', groups: [{ name: 'OP200Data', tags: [{ name: 'DataReady', dataType: 'Bool' }], subGroups: [] }] }],
  }],
  mesUpload: {
    tasks: [{
      id: 'task-1', name: 'OP200Data', groupRef: 'Line.PLC.OP200Data', deviceRef: 'Line.PLC', enabled: false,
      flow: {
        nodes: [
          { id: 'trigger-1', type: 'Trigger', title: '采集周期', x: 0, y: 80, inSide: 'Left', outSide: 'Right' },
          {
            id: 'upload-1', type: 'Upload', title: '上报', x: 300, y: 80, inSide: 'Left', outSide: 'Right',
            payload: { mode: 'FieldMapping', fields: [{ tagId: 'Line.PLC.OP200Data.DataReady', fieldName: 'DataReady' }], structs: [], append: {} },
            http: { url: '', method: 'POST', timeoutSec: 30, successKey: 'httpStatusCode', successValue: '200', bodyTemplate: '' },
            captures: [], writeBack: { fromResponse: [], manual: [] },
          },
        ],
        edges: [{ from: 'trigger-1', fromPort: 'out', to: 'upload-1' }],
      },
    }],
  },
}

vi.mock('../api', () => ({
  http: {
    get: vi.fn(async () => ({ data: { items: [], total: 0, offset: 0, limit: 100 }, headers: { etag: 'rev-1' } })),
    post: vi.fn(async () => ({ data: { applied: true, revision: '"project-2"' }, headers: { etag: '"project-2"' } })),
  },
  setActiveProjectRevision: vi.fn(),
  api: {
    projectTagScope: vi.fn(),
    project: vi.fn(async () => ({ proj: structuredClone(project), rev: 'rev-1' })),
    mes: vi.fn(async () => ({ tasks: [] })),
    mesTimeline: vi.fn(async () => []),
    parameterOptions: vi.fn(async () => ({ items: [], truncated: false })),
    putMesUpload: vi.fn(async () => ({ rev: 'rev-2' })),
  },
}))

function account(flow: 'View' | 'Edit'): AccountDto {
  return { username: 'op', displayName: '', isAdmin: false, mustChangePassword: false, disabled: false, createdUtc: '', permissions: { flow } }
}

async function view() {
  const wrapper = mount(MesFlow, {
    props: { focusGroup: 'Line.PLC.OP200Data', design: true },
    attachTo: document.body,
    global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
  })
  await flushPromises()
  return { wrapper, state: (wrapper.vm.$ as any).setupState }
}
const buttonTexts = (wrapper: any) => wrapper.findAll('button').map((button: any) => button.text())

describe('采集流程只读权限', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
    i18n.setLocale('zh-CN')
    vi.mocked(http.get).mockReset()
    vi.mocked(http.get).mockRejectedValue({ response: { status: 404 } })
    vi.mocked(http.post).mockClear()
    vi.mocked(api.putMesUpload).mockClear()
  })
  afterEach(() => { clearSession(); document.body.innerHTML = '' })

  it('只能看：隐藏保存/添加/排布/批量/历史，运行开关禁用，画布不能删线删点连线', async () => {
    session.account = account('View')
    const { wrapper } = await view()
    const texts = buttonTexts(wrapper)
    for (const hidden of ['保存并应用', '流程批量管理', '＋ 添加节点', '自动排布', '操作历史'])
      expect(texts.some((text: string) => text.includes(hidden)), hidden).toBe(false)
    expect(texts.some((text: string) => text.includes('校验'))).toBe(true)
    expect(texts.some((text: string) => text.includes('时间线'))).toBe(true)
    expect(texts.some((text: string) => text.includes('流程模板'))).toBe(true)   // 只保留查看/导出模板
    expect(wrapper.find('.task-run-switch').classes()).toContain('is-disabled')
    expect(wrapper.find('.task-tier-switch').classes()).toContain('is-disabled')
    expect(wrapper.find('[data-testid="node-delete"]').exists()).toBe(false)
    expect(wrapper.find('.port').exists()).toBe(false)
    expect(wrapper.find('.ewire').exists()).toBe(false)
    expect(wrapper.find('.edel').exists()).toBe(false)
    expect(wrapper.find('.ehit').exists()).toBe(false)
    wrapper.unmount()
  })

  it('只能看：点节点能选中查看配置，但不能拖动、输入框禁用、增删按钮隐藏，搜索仍可用', async () => {
    session.account = account('View')
    const { wrapper, state } = await view()
    const upload = wrapper.findAll('g.node')[1]
    await upload.trigger('mousedown', { clientX: 310, clientY: 90 })
    expect(state.drag.mode).toBe('')
    window.dispatchEvent(new MouseEvent('mouseup'))
    await nextTick()
    const cfg = wrapper.find('.cfg')
    expect(cfg.exists()).toBe(true)
    expect(cfg.find('input').attributes('disabled')).toBeDefined()
    const texts = buttonTexts(cfg)
    for (const hidden of ['删除该节点', '配置数据', '+ 字段', '填入本组全部点', '+ 结构体', '一键识别结构体', '+ 固定值', '+ 捕获', '填入示例'])
      expect(texts.some((text: string) => text.includes(hidden)), hidden).toBe(false)
    const search = cfg.find('.mapping-filter input')
    expect(search.exists()).toBe(true)
    expect(search.attributes('disabled')).toBeUndefined()
    wrapper.unmount()
  })

  it('只能看：Delete / Ctrl+X / Ctrl+S 都不生效，不会发保存请求', async () => {
    session.account = account('View')
    const { wrapper, state } = await view()
    state.selIds = new Set(['upload-1'])
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'Delete' }))
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'x', ctrlKey: true }))
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 's', ctrlKey: true }))
    await flushPromises()
    expect(state.flow.nodes).toHaveLength(2)
    expect(state.flow.edges).toHaveLength(1)
    state.markDirty('测试')
    expect(state.dirty).toBe(false)
    expect(await state.save(false)).toBe(true)
    expect(await wrapper.vm.flushAutoSave()).toBe(true)
    expect(api.putMesUpload).not.toHaveBeenCalled()
    expect(http.post).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('可修改：写操作全部可见，节点可拖动', async () => {
    session.account = account('Edit')
    const { wrapper, state } = await view()
    const texts = buttonTexts(wrapper)
    for (const shown of ['保存并应用', '流程批量管理', '＋ 添加节点', '自动排布', '操作历史'])
      expect(texts.some((text: string) => text.includes(shown)), shown).toBe(true)
    expect(wrapper.find('.task-run-switch').classes()).not.toContain('is-disabled')
    expect(wrapper.find('[data-testid="node-delete"]').exists()).toBe(true)
    expect(wrapper.find('.ewire').exists()).toBe(true)
    await wrapper.findAll('g.node')[1].trigger('mousedown', { clientX: 310, clientY: 90 })
    expect(state.drag.mode).toBe('node')
    window.dispatchEvent(new MouseEvent('mouseup'))
    await nextTick()
    const cfg = wrapper.find('.cfg')
    expect(cfg.find('input').attributes('disabled')).toBeUndefined()
    for (const shown of ['删除该节点', '配置数据', '+ 字段', '填入本组全部点', '+ 固定值', '+ 捕获'])
      expect(buttonTexts(cfg).some((text: string) => text.includes(shown)), shown).toBe(true)
    wrapper.unmount()
  })
})
