import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus, { ElMessageBox } from 'element-plus'
import { ArrowDown } from '@element-plus/icons-vue'
import { nextTick } from 'vue'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import MesFlow from './MesFlow.vue'
import MesParameterPicker from '../components/MesParameterPicker.vue'
import MesResponseFieldPicker from '../components/MesResponseFieldPicker.vue'
import MesBatchSaveDialog from '../components/MesBatchSaveDialog.vue'
import { api, http } from '../api'
import { i18n } from '../i18n'
import { session } from '../auth'

const confirmed = 'confirm' as Awaited<ReturnType<typeof ElMessageBox.confirm>>

const project = {
  channels: [{
    name: 'Line',
    driver: 'SiemensTcp',
    devices: [{
      name: 'PLC',
      groups: [{
        name: 'OP200Data',
        tags: [{ name: 'DataReady', dataType: 'Bool' }],
        subGroups: [],
      }, {
        name: 'OP090_1',
        tags: [{ name: 'Request', dataType: 'Bool' }],
        subGroups: [],
      }],
    }],
  }],
  mesUpload: {
    tasks: [{
      id: 'task-1',
      name: 'OP200Data',
      groupRef: 'Line.PLC.OP200Data',
      deviceRef: 'Line.PLC',
      enabled: false,
      flow: {
        nodes: [{
          id: 'upload-1',
          type: 'Upload',
          title: '上报',
          x: 80,
          y: 80,
          inSide: 'Left',
          outSide: 'Right',
          payload: {
            mode: 'FieldMapping',
            fields: [{ tagId: 'Line.PLC.OP200Data.DataReady', fieldName: 'DataReady' }],
            structs: [],
            append: {},
          },
          http: {
            url: '',
            method: 'POST',
            timeoutSec: 30,
            successKey: 'httpStatusCode',
            successValue: '200',
            bodyTemplate: '',
          },
          captures: [],
          writeBack: { fromResponse: [], manual: [] },
        }],
        edges: [],
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
    mesFieldMapping: vi.fn(),
    project: vi.fn(async () => ({ proj: structuredClone(project), rev: 'rev-1' })),
    mes: vi.fn(async () => ({ tasks: [] })),
    mesTimeline: vi.fn(async () => []),
    parameterOptions: vi.fn(async () => ({ items: [], truncated: false })),
    putMesUpload: vi.fn(async () => ({ rev: 'rev-2' })),
  },
}))

describe('MES upload ordinary-point entry', () => {
  it.each(['cancel', 'apply', 'switch'])('opens fill-all in a draft dialog and protects the original node (%s)', async action => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue(confirmed)
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      const state = (wrapper.vm.$ as any).setupState, node = state.selNode
      state.pagedProjectMode = true
      vi.mocked(api.projectTagScope).mockResolvedValueOnce({ definitions: [
        { id: 'Line.PLC.OP200Data.DataReady', name: 'DataReady' },
        { id: 'Line.PLC.OP200Data.New', name: 'New' },
      ], groups: [], nextCursor: null } as any)
      const before = JSON.stringify(node.payload)
      await state.fillAllFields(node)
      expect(JSON.stringify(node.payload)).toBe(before)
      const dialog = wrapper.findComponent({ name: 'MesUploadMappingDialog' })
      expect(dialog.exists()).toBe(true)
      expect(dialog.props('modelValue')).toBe(true)
      expect(api.putMesUpload).not.toHaveBeenCalled()
      if (action === 'cancel') {
        dialog.vm.$emit('update:modelValue', false)
        await nextTick()
        expect(JSON.stringify(node.payload)).toBe(before)
      } else if (action === 'switch') {
        state.selIds = new Set()
        await nextTick()
        dialog.vm.$emit('apply', { fields: [{ tagId: 'Line.PLC.OP200Data.New' }], structs: [] })
        await nextTick()
        expect(JSON.stringify(node.payload)).toBe(before)
      } else {
        const draft = structuredClone(dialog.props('payload')) as any
        draft.fields[1].parameterCode = 'P_NEW'
        dialog.vm.$emit('apply', draft)
        await nextTick()
        expect(node.payload.fields).toHaveLength(2)
        expect(node.payload.fields[1]).toMatchObject({ tagId: 'Line.PLC.OP200Data.New', parameterCode: 'P_NEW' })
        expect(state.dirty).toBe(true)
      }
    } finally { confirm.mockRestore(); wrapper.unmount() }
  })

  it('clears only the selected node fields or structs independently', async () => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      const state = (wrapper.vm.$ as any).setupState, node = state.selNode
      node.payload.structs = [{ kind: 'Object', baseGroup: 'Line.PLC.OP200Data.Header', aliasName: 'header', members: [{ tagId: 'Code' }] }]
      const structure = JSON.stringify(node.payload.structs)
      await wrapper.get('[data-testid="upload-clear-fields"]').trigger('click')
      expect(node.payload.fields).toEqual([])
      expect(JSON.stringify(node.payload.structs)).toBe(structure)
      node.payload.fields = [{ tagId: 'Line.PLC.OP200Data.DataReady', parameterCode: 'READY' }]
      await wrapper.get('[data-testid="upload-clear-structs"]').trigger('click')
      expect(node.payload.structs).toEqual([])
      expect(node.payload.fields[0].parameterCode).toBe('READY')
      expect(state.dirty).toBe(true)
    } finally { wrapper.unmount() }
  })

  it.each(['conflict', 'version'])('does not apply a dialog draft after validation failure or concurrent edits (%s)', async reason => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      const state = (wrapper.vm.$ as any).setupState, node = state.selNode
      state.pagedProjectMode = true
      vi.mocked(api.projectTagScope).mockResolvedValueOnce({ definitions: [{ id: 'Line.PLC.OP200Data.New', name: 'New' }], groups: [], nextCursor: null } as any)
      const before = JSON.stringify(node.payload)
      await state.fillAllFields(node)
      const dialog = wrapper.findComponent({ name: 'MesUploadMappingDialog' })
      if (reason === 'version') state.md()
      const payload = structuredClone(dialog.props('payload')) as any
      if (reason === 'conflict') payload.fields = [{ tagId: 'Line.PLC.OP200Data.New', parameterCode: 'SAME' }, { tagId: 'Line.PLC.OP200Data.DataReady', parameterCode: 'SAME' }]
      dialog.vm.$emit('apply', payload)
      await nextTick()
      expect(JSON.stringify(node.payload)).toBe(before)
      expect(wrapper.findComponent({ name: 'MesUploadMappingDialog' }).exists()).toBe(reason === 'conflict')
    } finally { wrapper.unmount() }
  })

  it.each([true, false])('detects cell arrays inside their slots (direct slot member: %s)', async ownMember => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      const state = (wrapper.vm.$ as any).setupState
      const node = state.selNode
      node.payload.fields = [1, 2].flatMap(slot => [
        ...(ownMember ? [{ tagId: `Line.PLC.OP200Data.Slot_Data_${slot}.StructCount`, fieldName: '' }] : []),
        ...[3, 4].flatMap(cell => ['BBWeldWid_Min', 'BBTestResult'].map(member => ({
          tagId: `Line.PLC.OP200Data.Slot_Data_${slot}.Cell_Date_1st_${cell}.${member}`, fieldName: '',
        }))),
      ])
      node.payload.structs = []
      await state.detectStructsWithFeedback(node)
      expect(node.payload.fields).toEqual([])
      expect(node.payload.structs).toHaveLength(1)
      expect(node.payload.structs[0]).toMatchObject({ kind: 'Array', baseGroup: 'Line.PLC.OP200Data.Slot_Data', startIndex: 1, fixedCount: 2 })
      expect(node.payload.structs[0].members.map((member: any) => member.tagId)).toEqual(ownMember ? ['StructCount'] : [])
      expect(node.payload.structs[0].structs).toHaveLength(1)
      expect(node.payload.structs[0].structs[0]).toMatchObject({ kind: 'Array', baseGroup: 'Cell_Date_1st', startIndex: 3, fixedCount: 2 })
      expect(node.payload.structs[0].structs[0].members.map((member: any) => member.tagId)).toEqual(['BBWeldWid_Min', 'BBTestResult'])
    } finally { wrapper.unmount() }
  })

  it('repairs previously flattened arrays even when no ordinary fields remain', async () => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      const state = (wrapper.vm.$ as any).setupState, node = state.selNode
      node.payload.fields = []
      node.payload.structs = [{ kind: 'Array', baseGroup: 'Line.PLC.OP200Data.Slot_Data', aliasName: 'slots', startIndex: 1, fixedCount: 2, members: [{ tagId: 'StructCount' }] },
        ...[1, 2].map(slot => ({ kind: 'Array', baseGroup: `Line.PLC.OP200Data.Slot_Data[${slot}].Cell_Date_1st`, aliasName: 'cells', startIndex: 3, fixedCount: 2, members: [{ tagId: 'BBWeldWid_Min', fieldName: 'width', scale: 2 }] }))]
      await state.detectStructsWithFeedback(node)
      expect(node.payload.structs).toHaveLength(1)
      expect(node.payload.structs[0].aliasName).toBe('slots')
      expect(node.payload.structs[0].structs[0]).toMatchObject({ baseGroup: 'Cell_Date_1st', aliasName: 'cells', members: [{ tagId: 'BBWeldWid_Min', fieldName: 'width', scale: 2 }] })
      expect(state.dirty).toBe(true)
    } finally { wrapper.unmount() }
  })

  it.each(['detect', 'fill', 'conflict'])('retains selected single-cell member contracts or rejects conflicts (%s)', async operation => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    const confirm = vi.spyOn(ElMessageBox, 'confirm').mockResolvedValue(confirmed)
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      const state = (wrapper.vm.$ as any).setupState, node = state.selNode
      node.payload.fields = [1, 2].map(slot => ({ tagId: `Line.PLC.OP200Data.Slots[${slot}].Cells[5].Value`,
        fieldName: operation === 'conflict' && slot === 2 ? 'otherValue' : 'wireValue', scale: 2, format: 'F2', parameterCode: 'V' }))
      node.payload.structs = []
      const before = JSON.stringify(node.payload)
      if (operation === 'fill') {
        state.pagedProjectMode = true
        vi.mocked(api.projectTagScope).mockResolvedValueOnce({ definitions: node.payload.fields.map((field: any) => ({ id: field.tagId, name: 'Value' })), groups: [], nextCursor: null } as any)
        await state.fillAllFields(node)
        const dialog = wrapper.findComponent({ name: 'MesUploadMappingDialog' })
        dialog.vm.$emit('apply', structuredClone(dialog.props('payload')))
        await nextTick()
      } else await state.detectStructsWithFeedback(node)
      if (operation === 'conflict') expect(JSON.stringify(node.payload)).toBe(before)
      else {
        expect(node.payload.fields).toEqual([])
        expect(node.payload.structs).toHaveLength(1)
        expect(node.payload.structs[0].structs[0].members[0]).toMatchObject({ tagId: 'Value', fieldName: 'wireValue', scale: 2, format: 'F2', parameterCode: 'V' })
      }
    } finally { confirm.mockRestore(); wrapper.unmount() }
  })

  it('removes the global report format and edits parameter codes without regenerating data', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].flow.nodes[0].payload.fieldKeyMode = 'ItemId'
    const payload = configured.mesUpload.tasks[0].flow.nodes[0].payload
    payload.fields[0].fieldName = 'ready'
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' })
    const mountFlow = () => mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    let wrapper = mountFlow()
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      expect(wrapper.find('[data-testid="upload-field-key-mode"]').exists()).toBe(false)
      expect(wrapper.text()).not.toContain('切换报文格式')
      expect((wrapper.get('input[aria-label="JSON 字段名"]').element as HTMLInputElement).value).toBe('ready')
      await wrapper.get('input[aria-label="参数码（填写后转字典）"]').setValue('P.READY')
      const state = (wrapper.vm.$ as any).setupState
      expect(state.selNode.payload.fields[0]).toMatchObject({ tagId: 'Line.PLC.OP200Data.DataReady', fieldName: 'ready', parameterCode: 'P.READY' })
      expect(api.mesFieldMapping).not.toHaveBeenCalled()
      await wrapper.findAll('button').find(button => button.text() === '保存并应用')!.trigger('click')
      await vi.waitFor(() => expect(api.putMesUpload).toHaveBeenCalled())
      const calls = vi.mocked(api.putMesUpload).mock.calls
      const saved = structuredClone(calls[calls.length - 1]![0])
      wrapper.unmount()
      vi.mocked(api.project).mockResolvedValueOnce({ proj: { ...structuredClone(project), mesUpload: saved }, rev: 'rev-2' } as any)
      wrapper = mountFlow()
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      expect((wrapper.get('input[aria-label="参数码（填写后转字典）"]').element as HTMLInputElement).value).toBe('P.READY')
      await wrapper.get('input[aria-label="参数码（填写后转字典）"]').setValue('')
      expect((wrapper.vm.$ as any).setupState.selNode.payload.fields[0].fieldName).toBe('ready')
    } finally { wrapper.unmount() }
  })

  it('saves and reopens a structure dictionary selection without adding parameter codes or generating mappings', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].flow.nodes[0].payload.structs = [{ kind: 'Array', baseGroup: 'Line.PLC.OP200Data.Slots', aliasName: 'slots',
      countMode: 'Enum', startIndex: 2, fixedCount: 3, members: [{ tagId: 'StructCount', fieldName: 'count', scale: 2 }],
      structs: [{ kind: 'Array', baseGroup: 'Cells', aliasName: 'cells', countMode: 'Enum', startIndex: 5, fixedCount: 40, members: [{ tagId: 'Value', fieldName: 'width' }] }] }]
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' })
    const mountFlow = () => mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    let wrapper = mountFlow()
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      const state = (wrapper.vm.$ as any).setupState
      const structure = state.selNode.payload.structs[0]
      expect(structure.asDictionary).not.toBe(true)
      await wrapper.findAll('[data-testid="struct-dictionary-toggle"]')[0].get('input').setValue(true)
      expect(structure.asDictionary).toBe(true)
      expect(structure.members[0].parameterCode).toBeUndefined()
      expect(structure.structs[0].members[0].parameterCode).toBeUndefined()
      expect(structure).toMatchObject({ aliasName: 'slots', startIndex: 2, fixedCount: 3, members: [{ tagId: 'StructCount', fieldName: 'count', scale: 2 }] })
      expect(api.mesFieldMapping).not.toHaveBeenCalled()
      await wrapper.findAll('button').find(button => button.text() === '保存并应用')!.trigger('click')
      await vi.waitFor(() => expect(api.putMesUpload).toHaveBeenCalled())
      const calls = vi.mocked(api.putMesUpload).mock.calls
      const saved = structuredClone(calls[calls.length - 1]![0])
      wrapper.unmount()
      vi.mocked(api.project).mockResolvedValueOnce({ proj: { ...structuredClone(project), mesUpload: saved }, rev: 'rev-2' } as any)
      wrapper = mountFlow()
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      expect((wrapper.findAll('[data-testid="struct-dictionary-toggle"]')[0].get('input').element as HTMLInputElement).checked).toBe(true)
    } finally { wrapper.unmount() }
  })

  it('keeps parameter-code and struct dictionary settings read-only for view accounts', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].flow.nodes[0].payload.structs = [{ kind: 'Object', baseGroup: 'Line.PLC.OP200Data.Header', aliasName: 'header', members: [{ tagId: 'Code' }] }]
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' })
    session.account = { username: 'viewer', displayName: '', isAdmin: false, mustChangePassword: false, disabled: false, createdUtc: '', permissions: { flow: 'View' } }
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    try {
      await flushPromises()
      await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
      expect((wrapper.get('input[aria-label="参数码（填写后转字典）"]').element as HTMLInputElement).disabled).toBe(true)
      expect((wrapper.findAll('[data-testid="struct-dictionary-toggle"]')[0].get('input').element as HTMLInputElement).disabled).toBe(true)
      expect(wrapper.find('[data-testid="upload-clear-fields"]').exists()).toBe(false)
      expect(wrapper.find('[data-testid="upload-clear-structs"]').exists()).toBe(false)
      const before = JSON.stringify((wrapper.vm.$ as any).setupState.selNode.payload)
      ;(wrapper.vm.$ as any).setupState.clearUploadMappings((wrapper.vm.$ as any).setupState.selNode, 'fields')
      expect(JSON.stringify((wrapper.vm.$ as any).setupState.selNode.payload)).toBe(before)
      expect(api.mesFieldMapping).not.toHaveBeenCalled()
    } finally { wrapper.unmount() }
  })
  it('bounds a single 100000-member structure and deletes by its original index on later pages', async () => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    const state = (wrapper.vm.$ as any).setupState
    state.selNode.payload.structs = [{ kind: 'List', baseGroup: 'Line.PLC.OP200Data.Huge', aliasName: 'Huge', members: Array.from({ length: 100000 }, (_, index) => ({ tagId: `Value${index}`, fieldName: '' })) }]
    await nextTick()
    expect(wrapper.findAll('.struct-member-row')).toHaveLength(50)
    const struct = state.selNode.payload.structs[0]
    // 成员分页在结构体卡片组件里
    ;(wrapper.findComponent({ name: 'MesStructCard' }).vm.$ as any).setupState.page = 3
    await nextTick()
    expect((wrapper.find('.struct-member-row input').element as HTMLInputElement).value).toBe('Value100')
    await wrapper.find('.struct-member-row button').trigger('click')
    expect(struct.members).toHaveLength(99999)
    expect(struct.members[0].tagId).toBe('Value0')
    expect(struct.members[100].tagId).toBe('Value101')
    wrapper.unmount()
  }, 15000)
  it('never creates unsupported nodes and cancels chunked structure detection without partial edits', async () => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    const state = (wrapper.vm.$ as any).setupState
    const count = state.flow.nodes.length
    state.newNode('LegacyCommand')
    expect(state.flow.nodes).toHaveLength(count)
    const node = state.selNode
    state.uploadMappingSearch = '__hidden_scan_fixture__'
    node.payload.fields = Array.from({ length: 1024 }, (_, index) => ({ tagId: `Line.PLC.OP200Data.Struct.Value${index}`, fieldName: `v${index}` }))
    node.payload.structs = []
    const frames: FrameRequestCallback[] = []
    const frameSpy = vi.spyOn(globalThis, 'requestAnimationFrame').mockImplementation(callback => { frames.push(callback); return frames.length })
    try {
      const pending = state.detectStructsWithFeedback(node)
      await nextTick()
      expect(state.visibleUploadFieldRows).toEqual([])
      const button = () => wrapper.findAll('button').find(item => item.text().includes('一键识别结构体'))!
      expect(button().classes()).toContain('is-loading')
      expect(frames).toHaveLength(1)
      frames.shift()!(0)
      await new Promise(resolve => setTimeout(resolve, 0))
      await nextTick()
      // The first 512-field checkpoint is explicitly held; cancellation does not depend on elapsed time.
      expect(frames).toHaveLength(1)
      expect(node.payload.structs).toEqual([])
      state.md()
      frames.shift()!(16)
      await pending
      await nextTick()
      expect(button().classes()).not.toContain('is-loading')
      expect(node.payload.structs).toEqual([])
      expect(node.payload.fields).toHaveLength(1024)
    } finally {
      frameSpy.mockRestore()
      wrapper.unmount()
    }
  })
  it('shows fill progress, blocks duplicate requests, clears loading after failure, and ignores a switched node', async () => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    const state = (wrapper.vm.$ as any).setupState
    state.pagedProjectMode = true
    let reject!: (reason: Error) => void
    vi.mocked(api.projectTagScope).mockReset().mockImplementationOnce(() => new Promise((_, fail) => { reject = fail }))
    const fill = wrapper.findAll('button').find(button => button.text().includes('填入本组全部点'))!
    await fill.trigger('click')
    await vi.waitFor(() => expect(api.projectTagScope).toHaveBeenCalledTimes(1))
    expect(fill.classes()).toContain('is-loading')
    await state.fillAllFields(state.selNode)
    expect(api.projectTagScope).toHaveBeenCalledTimes(1)
    reject(new Error('scope unavailable'))
    await vi.waitFor(() => expect(fill.classes()).not.toContain('is-loading'))
    expect(document.body.textContent).toContain('scope unavailable')
    let finish!: (result: any) => void
    vi.mocked(api.projectTagScope).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }))
    const original = state.selNode
    const before = JSON.stringify(original.payload)
    await fill.trigger('click')
    await vi.waitFor(() => expect(api.projectTagScope).toHaveBeenCalledTimes(2))
    state.selIds = new Set()
    finish({ definitions: [{ id: 'Line.PLC.OP200Data.New', name: 'New' }], groups: [], nextCursor: null })
    await flushPromises()
    expect(JSON.stringify(original.payload)).toBe(before)
    wrapper.unmount()
  })
  beforeEach(() => {
    setActivePinia(createPinia())
    session.account = { username: 'admin', displayName: '', isAdmin: true, mustChangePassword: false, disabled: false, createdUtc: '', permissions: {} }
    i18n.setLocale('zh-CN')
    vi.mocked(api.parameterOptions).mockClear()
    vi.mocked(api.mesFieldMapping).mockReset()
    vi.mocked(api.putMesUpload).mockClear()
    vi.mocked(http.get).mockReset()
    // These fixtures exercise the legacy server fallback. Modern task APIs are tested below.
    vi.mocked(http.get).mockRejectedValue({ response: { status: 404 } })
    vi.mocked(http.post).mockClear()
  })

  it.each([73, 1_000_000])('loads one task document with %i project tags and saves a local mutation', async (total) => {
    vi.mocked(api.project).mockClear()
    vi.mocked(http.get).mockImplementation(async (url: string) => {
      if (url === '/api/v1/project/tags') return {
        data: { items: [], total, offset: 0, limit: 1 },
        headers: { etag: '"project-1"' },
      } as any
      if (url === '/api/v1/project/mes-tasks') return {
        data: {
          items: [{
            id: 'task-1', name: 'OP200Data', groupId: 'group-1', groupRef: 'Line.PLC.OP200Data',
            deviceRef: 'Line.PLC', deviceId: 'device-1', channelId: 'channel-1', description: '电芯数据',
            enabled: false, flowAvailable: true, rowVersion: 1,
          }],
          total: 1, offset: 0, limit: 200,
        },
        headers: { etag: '"project-1"' },
      } as any
      if (url === '/api/v1/project/mes-tasks/task-1') return {
        data: structuredClone(project.mesUpload.tasks[0]),
        headers: { etag: '"project-1"' },
      } as any
      return { data: { items: [], total: 0, offset: 0, limit: 100 }, headers: { etag: '"project-1"' } } as any
    })

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus], components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()

    expect(wrapper.text()).toContain('OP200Data')
    expect(api.project).not.toHaveBeenCalled()
    expect(http.get).toHaveBeenCalledWith('/api/v1/project/mes-tasks/task-1', { signal: undefined })
    const saveButton = wrapper.findAll('button').find(button => button.text().includes('保存并应用'))
    expect(saveButton).toBeTruthy()
    await saveButton!.trigger('click')
    await vi.waitFor(() => expect(http.post).toHaveBeenCalled())

    expect(api.putMesUpload).not.toHaveBeenCalled()
    expect(http.post).toHaveBeenCalledWith('/api/v1/project/mutations', expect.objectContaining({
      kind: 'replaceMesTask', entityIds: ['task-1'],
    }), expect.objectContaining({ headers: { 'If-Match': '"project-1"' } }))
    wrapper.unmount()
  })

  it('loads modern anomaly targets and template results outside the initially loaded task', async () => {
    const second = structuredClone(project.mesUpload.tasks[0])
    second.id = 'task-2'; second.name = 'OP090_1'; second.groupRef = 'Line.PLC.OP090_1'
    second.flow.nodes[0].id = 'upload-2'
    const docs = [structuredClone(project.mesUpload.tasks[0]), second]
    vi.mocked(http.get).mockImplementation(async (url: string) => ({
      data: url === '/api/v1/project/mes-tasks'
        ? { items: docs.map(d => ({ ...d, flowAvailable: true })), total: 2, offset: 0, limit: 200 }
        : docs.find(d => url.endsWith('/' + d.id)),
      headers: { etag: 'rev-1' },
    }) as any)
    const wrapper = mount(MesFlow, {
      props: { focusTask: 'OP200Data', focusNode: 'upload-1', design: true },
      global: { plugins: [ElementPlus], components: { ArrowDown }, config: { globalProperties: { $t: i18n.t } as any } },
    })
    await flushPromises()
    const state = (wrapper.vm as any).$.setupState
    expect(state.selIds.has('upload-1')).toBe(true)
    await wrapper.setProps({ focusTask: 'OP090_1', focusNode: 'upload-2' })
    await flushPromises()
    expect(state.task.id).toBe('task-2')
    expect(state.selIds.has('upload-2')).toBe(true)
    await state.onTplApplied('OP200Data')
    expect(state.task.id).toBe('task-1')
    await state.onTplApplied('OP090_1')
    expect(state.task.id).toBe('task-2')
    await state.onTplRefreshed()
    expect(state.task.id).toBe('task-2')
    vi.mocked(api.projectTagScope).mockImplementation(async ({ root }) => ({
      root, definitions: [{ id: root + '.Value', name: 'Value' }],
      groups: root === second.groupRef ? [{ kind: 'Group', path: root + '.Child' }] : [],
    }) as any)
    expect(await state.groupTagPaths()).toEqual([
      { path: second.groupRef + '.Value', name: 'Value' },
      { path: second.groupRef + '.Child.Value', name: 'Value' },
    ])
    wrapper.unmount()
  })

  it('keeps the original fill-all entry and does not add a second pull entry', async () => {
    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()

    const uploadNode = wrapper.find('g.node')
    expect(uploadNode.exists()).toBe(true)
    await uploadNode.trigger('mousedown', { clientX: 90, clientY: 90 })
    await nextTick()

    expect(wrapper.text()).toContain('填入本组全部点')
    expect(wrapper.text()).not.toContain('拉取主标记组普通点')

    wrapper.unmount()
  })

  it('saves and reopens additional field names together with their fixed values', async () => {
    const mountFlow = () => mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus], components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    const wrapper = mountFlow()
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    await nextTick()
    const add = wrapper.findAll('button').find(button => button.text() === '+ 固定值')!
    await add.trigger('click')
    const row = wrapper.find('.fixed-row')
    await row.find('input[aria-label="字段名"]').setValue('WorkStationCode')
    await row.find('input[aria-label="值"]').setValue('OP200 / 工位 "A"')
    await add.trigger('click')
    const second = wrapper.findAll('.fixed-row')[1]!
    await second.find('input[aria-label="字段名"]').setValue('Temporary')
    await second.find('input[aria-label="值"]').setValue('001')
    await second.find('input[aria-label="字段名"]').setValue('LineCode')

    await wrapper.findAll('button').find(button => button.text() === '保存并应用')!.trigger('click')
    await vi.waitFor(() => expect(api.putMesUpload).toHaveBeenCalled())
    const saves = vi.mocked(api.putMesUpload).mock.calls
    const saved = structuredClone(saves[saves.length - 1]![0])
    const savedNode = saved.tasks[0].flow!.nodes[0] as any
    // 固定值按类型保存(默认文本)，文本仍按字符串上报
    expect(savedNode.payload.fixedValues).toEqual([
      { name: 'WorkStationCode', dataType: 'Text', value: 'OP200 / 工位 "A"' }, { name: 'LineCode', dataType: 'Text', value: '001' },
    ])
    expect(savedNode.payload.append).toEqual({})
    expect(savedNode).not.toHaveProperty('_append')
    wrapper.unmount()

    vi.mocked(api.project).mockResolvedValueOnce({ proj: { ...structuredClone(project), mesUpload: saved }, rev: 'rev-2' } as any)
    const reopened = mountFlow()
    await flushPromises()
    await reopened.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    await nextTick()
    const rows = reopened.findAll('.fixed-row')
    expect(rows.map(item => ['字段名', '值'].map(label => (item.find(`input[aria-label="${label}"]`).element as HTMLInputElement).value))).toEqual([
      ['WorkStationCode', 'OP200 / 工位 "A"'], ['LineCode', '001'],
    ])
    await rows[1]!.find('button').trigger('click')
    await reopened.findAll('button').find(button => button.text() === '保存并应用')!.trigger('click')
    await vi.waitFor(() => expect(api.putMesUpload).toHaveBeenCalledTimes(2))
    expect((saves[saves.length - 1]![0].tasks[0].flow!.nodes[0]!.payload as any).fixedValues)
      .toEqual([{ name: 'WorkStationCode', dataType: 'Text', value: 'OP200 / 工位 "A"' }])
    reopened.unmount()
  })

  it('shows an unsupported trigger with a warning, and keeps it on save', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].collectIntervalMs = 5000
    configured.mesUpload.tasks[0].trigger = { mode: 'LegacyEvent', tagId: 'old-condition' }
    configured.mesUpload.tasks[0].flow.nodes = [{ id: 'trigger-event', type: 'Trigger', title: '采集周期', x: 80, y: 80 }]
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' } as any)
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], components: { ArrowDown }, config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    expect(wrapper.find('g.node').text()).toContain('不支持的触发方式')
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 }); await nextTick()
    expect(wrapper.find('[aria-label="MES trigger mode"]').exists()).toBe(false)
    const note = wrapper.get('[data-testid="unsupported-trigger-note"]')
    expect(note.text()).toContain('不支持的触发方式：LegacyEvent')
    expect(note.text()).toContain('网关会停用此任务')
    expect(wrapper.find('input[role="spinbutton"]').exists()).toBe(false)
    const state = (wrapper.vm.$ as any).setupState
    const alert = vi.spyOn(ElMessageBox, 'alert').mockResolvedValue(undefined as any)
    state.validateFlow()
    expect(alert.mock.calls[0]![0]).toContain('触发方式「LegacyEvent」本版本不支持')
    alert.mockRestore()
    await wrapper.findAll('button').find(button => button.text() === '保存并应用')!.trigger('click'); await flushPromises()
    await vi.waitFor(() => expect(api.putMesUpload).toHaveBeenCalled())
    const saves = vi.mocked(api.putMesUpload).mock.calls
    expect(saves[saves.length - 1][0].tasks[0]).toMatchObject({ collectIntervalMs: 5000, trigger: { mode: 'LegacyEvent', tagId: 'old-condition' } })
    expect(note.find('button').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows a node of an unsupported type and lets the user delete it', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].flow.nodes = [{
      id: 'command-1', type: 'LegacyCommand', title: '设备指令', x: 80, y: 80, inSide: 'Left', outSide: 'Right',
      command: { action: 'Send' },
    }]
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' } as any)
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], components: { ArrowDown }, config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    expect(wrapper.find('g.node').text()).toContain('不支持的节点')
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 }); await nextTick()
    const note = wrapper.get('[data-testid="unsupported-node-note"]')
    expect(note.text()).toContain('不支持的节点：LegacyCommand')
    expect(note.text()).toContain('网关会停用含此节点的任务，请删除该节点')
    expect(wrapper.find('.cfghead').text()).toContain('不支持的节点')
    const state = (wrapper.vm.$ as any).setupState
    const alert = vi.spyOn(ElMessageBox, 'alert').mockResolvedValue(undefined as any)
    state.validateFlow()
    expect(alert.mock.calls[0]![0]).toContain('节点「设备指令」的类型「LegacyCommand」本版本不支持')
    alert.mockRestore()
    expect(note.find('button').exists()).toBe(false)
    await wrapper.findAll('button').find(button => button.text() === '删除该节点')!.trigger('click'); await nextTick()
    expect(state.flow.nodes).toHaveLength(0)
    expect(wrapper.find('[data-testid="unsupported-node-note"]').exists()).toBe(false)
    wrapper.unmount()
  })

  it('shows the saved enabled state of tasks that have not been opened yet', async () => {
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], components: { ArrowDown }, config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    const state = (wrapper.vm.$ as any).setupState
    state.mesCatalog.items.value = [
      { id: 'task-1', name: 'OP200Data', groupRef: 'Line.PLC.OP200Data', deviceRef: 'Line.PLC', enabled: false, flowAvailable: true, rowVersion: 1 },
      { id: 'task-2', name: 'OP090_1', groupRef: 'Line.PLC.OP090_1', deviceRef: 'Line.PLC', enabled: true, flowAvailable: true, rowVersion: 1 },
    ]
    await nextTick()
    expect(wrapper.findAll('[data-testid="task-status"]').map(item => item.text())).toEqual(['停用', '运行'])
    expect(wrapper.text()).not.toContain('未配置')
    wrapper.unmount()
  })

  it('deletes a node from the hover cross on its top-right corner', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].flow.nodes.push({ id: 'delay-1', type: 'Delay', title: '延时', x: 320, y: 80, inSide: 'Left', outSide: 'Right', delayMs: 100 })
    configured.mesUpload.tasks[0].flow.edges = [{ from: 'upload-1', fromPort: 'out', to: 'delay-1' }]
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' } as any)
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], components: { ArrowDown }, config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    const state = (wrapper.vm.$ as any).setupState
    expect(wrapper.findAll('[data-testid="node-delete"]')).toHaveLength(2)
    await wrapper.findAll('g.node')[1]!.find('[data-testid="node-delete"]').trigger('click'); await nextTick()
    expect(state.flow.nodes.map((n: any) => n.id)).toEqual(['upload-1'])
    expect(state.flow.edges).toHaveLength(0)
    expect(state.dirty).toBe(true)
    wrapper.unmount()
  })

  it('keeps writeback targets inside the bound group', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].flow.nodes = [{
      id: 'write-event', type: 'ResponseWriteback', title: '回写 PLC', x: 80, y: 80,
      writeBack: { fromResponse: [{ responseField: 'status', tagId: '' }], manual: [{ tagId: '', copyFromTagId: 'Line.PLC.OP200Data.DataReady', value: '' }] },
    }]
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' } as any)
    const wrapper = mount(MesFlow, { props: { focusGroup: 'Line.PLC.OP200Data', design: true }, global: { plugins: [ElementPlus], components: { ArrowDown }, config: { globalProperties: { $t: i18n.t } as any } } })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 }); await nextTick()
    const writePickers = wrapper.findAllComponents(MesParameterPicker).filter(picker => picker.props('access') === 'write')
    expect(writePickers).toHaveLength(2)
    for (const picker of writePickers) expect(picker.props('scopeGroup')).toBe('Line.PLC.OP200Data')
    expect(wrapper.find('[data-testid="add-api-barcode"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="add-plc-barcode"]').exists()).toBe(false)
    expect(wrapper.findAll('.manual-write-row')).toHaveLength(1)
    // 上报不再拦路后，回写节点的说明对各触发方式一致
    expect(wrapper.text()).toContain('不区分业务成败')
    expect(wrapper.text()).toContain('「判成败」节点')
    expect(wrapper.text()).not.toContain('isSuccess=false')
    expect(wrapper.text()).not.toContain('SetVin')
    wrapper.unmount()
  })

  it('persists an edited collection interval when saving from another task', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].collectIntervalMs = 5000
    configured.mesUpload.tasks[0].flow.nodes = [
      { id: 'trigger-1', type: 'Trigger', title: '采集周期', x: 80, y: 80 },
    ]
    configured.mesUpload.tasks.push({
      id: 'task-2', name: 'OP090', groupRef: 'Line.PLC.OP090_1', deviceRef: 'Line.PLC',
      enabled: false, collectIntervalMs: 5000,
      flow: { nodes: [{ id: 'trigger-2', type: 'Trigger', title: '采集周期', x: 80, y: 80 }], edges: [] },
    })
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' } as any)
    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus], components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    await nextTick()
    const interval = wrapper.find('input[role="spinbutton"]')
    expect((interval.element as HTMLInputElement).value).toBe('5000')
    await interval.setValue('200')
    await interval.trigger('change')
    await wrapper.findAll('.titem').find(item => item.text().includes('OP090'))!.trigger('click')
    await nextTick()
    await wrapper.findAll('button').find(button => button.text() === '保存并应用')!.trigger('click')
    await vi.waitFor(() => expect(api.putMesUpload).toHaveBeenCalled())
    const saves = vi.mocked(api.putMesUpload).mock.calls
    const saved = structuredClone(saves[saves.length - 1]![0])
    expect(saved.tasks[0].collectIntervalMs).toBe(200)
    expect(saved.tasks[1].collectIntervalMs).toBe(5000)
    wrapper.unmount()
  })

  it('sends every cached draft in one batch and retains drafts when the batch conflicts', async () => {
    const configured = structuredClone(project) as any
    configured.mesUpload.tasks[0].collectIntervalMs = 200
    configured.mesUpload.tasks.push({
      id: 'task-2', name: 'Heartbeat', deviceRef: 'Line.PLC', groupRef: 'Line.PLC.OP090_1',
      enabled: true, collectIntervalMs: 5000, flow: { nodes: [], edges: [] },
    })
    vi.mocked(api.project).mockResolvedValueOnce({ proj: configured, rev: 'rev-1' } as any)
    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: { plugins: [ElementPlus], components: { ArrowDown }, config: { globalProperties: { $t: i18n.t } as any } },
    })
    await flushPromises()
    await wrapper.findAll('button').find(button => button.text() === '流程批量管理')!.trigger('click')
    const dialog = wrapper.findComponent(MesBatchSaveDialog)
    expect(dialog.props('modelValue')).toBe(true)
    await dialog.props('saveBatch')({ periodTaskIds: [] }, 'rev-1')
    const posted = vi.mocked(http.post).mock.calls[vi.mocked(http.post).mock.calls.length - 1]!
    expect(posted[0]).toBe('/api/v1/project/mutations')
    expect((posted[1] as any).kind).toBe('saveMesTasks')
    const batch = JSON.parse((posted[1] as any).documentJson)
    expect(batch.tasks.map((item: any) => [item.id, item.collectIntervalMs, item.enabled]))
      .toEqual([['task-1', 200, false], ['task-2', 5000, true]])
    expect(batch.periodTaskIds).toEqual([])
    expect(batch).not.toHaveProperty('collectIntervalMs')
    vi.mocked(http.post).mockRejectedValueOnce({ response: { status: 409, data: { error: 'conflict' } } })
    await expect(dialog.props('saveBatch')({ periodTaskIds: ['task-1'], collectIntervalMs: 300 }, '"project-2"')).rejects.toBeTruthy()
    expect(dialog.props('tasks')[0].collectIntervalMs).toBe(200)
    expect(wrapper.text()).toContain('自动保存冲突')
    wrapper.unmount()
  })

  it('shows the current group description in both the header and task list', async () => {
    const describedProject = structuredClone(project) as any
    describedProject.channels[0].devices[0].groups[0].description = '电芯数据采集与上报'
    vi.mocked(api.project).mockResolvedValueOnce({ proj: describedProject, rev: 'rev-description' } as any)

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()

    const headerDescription = wrapper.find('.flowdescription')
    expect(headerDescription.text()).toBe('电芯数据采集与上报')
    expect(headerDescription.attributes('title')).toBe('电芯数据采集与上报')
    const activeTaskDescription = wrapper.find('.titem.on .taskdescription')
    expect(activeTaskDescription.text()).toBe('电芯数据采集与上报')
    expect(activeTaskDescription.attributes('title')).toBe('电芯数据采集与上报')
    expect(wrapper.findAll('.taskdescription')).toHaveLength(1)

    wrapper.unmount()
  })

  it('shows only flow-enabled tasks and filters them without switching the active flow', async () => {
    const searchableProject = structuredClone(project) as any
    searchableProject.channels[0].devices[0].groups.push({
      name: 'OP140',
      tags: [{ name: 'MESFlag', dataType: 'Int16' }],
      subGroups: [],
    })
    searchableProject.mesUpload.tasks.push({
      id: 'task-running',
      name: 'OP140 进站',
      groupRef: 'Line.PLC.OP140',
      deviceRef: 'Line.PLC',
      flowAvailable: true,
      enabled: true,
      flow: { nodes: [], edges: [] },
    })
    searchableProject.channels[0].devices[0].groups.push({
      name: 'OP160',
      tags: [{ name: 'PLCFlag', dataType: 'Int16' }],
      subGroups: [],
    })
    searchableProject.mesUpload.tasks.push({
      id: 'task-closed',
      name: 'OP160 已关闭',
      groupRef: 'Line.PLC.OP160',
      deviceRef: 'Line.PLC',
      flowAvailable: false,
      enabled: false,
      flow: { nodes: [], edges: [] },
    })
    vi.mocked(api.project).mockResolvedValueOnce({ proj: searchableProject, rev: 'rev-search' } as any)

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()

    const fixedControls = wrapper.get('.task-sidebar-fixed')
    const scrollingList = wrapper.get('.task-list-scroll')
    expect(fixedControls.find('.task-filters').exists()).toBe(true)
    expect(fixedControls.find('.titem').exists()).toBe(false)
    expect(scrollingList.find('.task-filters').exists()).toBe(false)
    expect(scrollingList.findAll('.titem')).toHaveLength(2)
    expect(wrapper.findAll('.titem')).toHaveLength(2)
    expect(wrapper.text()).toContain('OP200Data')
    expect(wrapper.text()).toContain('OP140 进站')
    expect(wrapper.text()).not.toContain('OP090_1')
    expect(wrapper.text()).not.toContain('OP160 已关闭')
    expect(wrapper.findAll('.task-state-filter').map((item) => item.text())).toEqual(['全部', '运行', '停用'])

    const search = wrapper.find('input[placeholder="搜索任务名称或数据块名称"]')
    expect(search.exists()).toBe(true)
    await search.setValue('140')
    await nextTick()
    expect(wrapper.findAll('.titem')).toHaveLength(1)
    expect(wrapper.find('.titem').text()).toContain('OP140 进站')
    expect(wrapper.find('.taskcount').text()).toBe('1/2')
    expect(wrapper.find('.flowidentity strong').text()).toBe('OP200Data')

    await search.setValue('')
    const runningFilter = wrapper.findAll('.task-state-filter').find((item) => item.text() === '运行')
    expect(runningFilter).toBeTruthy()
    await runningFilter!.trigger('click')
    await nextTick()
    expect(wrapper.findAll('.titem')).toHaveLength(1)
    expect(wrapper.find('.titem').text()).toContain('OP140 进站')
    expect(wrapper.find('.taskcount').text()).toBe('1/2')
    expect(wrapper.find('.flowidentity strong').text()).toBe('OP200Data')

    wrapper.unmount()
  })

  it('defaults to the current device and can expand only to the current channel', async () => {
    const scopedProject = structuredClone(project) as any
    scopedProject.mesUpload.tasks.push({
      id: 'task-current-2', name: 'OP090 当前设备', groupRef: 'Line.PLC.OP090_1', deviceRef: 'Line.PLC',
      flowAvailable: true, enabled: false, flow: { nodes: [], edges: [] },
    })
    scopedProject.channels[0].devices.push({
      name: 'PLC2', groups: [{ name: 'DB2', tags: [{ name: 'Ready', dataType: 'Bool' }], subGroups: [] }],
    })
    scopedProject.mesUpload.tasks.push({
      id: 'task-plc2', name: 'PLC2 任务', groupRef: 'Line.PLC2.DB2', deviceRef: 'Line.PLC2',
      flowAvailable: true, enabled: false, flow: { nodes: [], edges: [] },
    })
    scopedProject.channels.push({
      name: 'OtherLine', driver: 'SiemensTcp', devices: [{
        name: 'PLC3', groups: [{ name: 'DB3', tags: [{ name: 'Ready', dataType: 'Bool' }], subGroups: [] }],
      }],
    })
    scopedProject.mesUpload.tasks.push({
      id: 'task-other-channel', name: '其他通道任务', groupRef: 'OtherLine.PLC3.DB3', deviceRef: 'OtherLine.PLC3',
      flowAvailable: true, enabled: false, flow: { nodes: [], edges: [] },
    })
    vi.mocked(api.project).mockResolvedValueOnce({ proj: scopedProject, rev: 'rev-scope' } as any)

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()

    expect(wrapper.findAll('.task-scope-filter').map((item) => item.text())).toEqual(['当前设备', '当前通道'])
    expect(wrapper.findAll('.titem')).toHaveLength(2)
    expect(wrapper.text()).toContain('OP200Data')
    expect(wrapper.text()).toContain('OP090 当前设备')
    expect(wrapper.text()).not.toContain('PLC2 任务')
    expect(wrapper.text()).not.toContain('其他通道任务')

    await wrapper.findAll('.task-scope-filter').find((item) => item.text() === '当前通道')!.trigger('click')
    await nextTick()
    expect(wrapper.findAll('.titem')).toHaveLength(3)
    expect(wrapper.text()).toContain('PLC2 任务')
    expect(wrapper.text()).not.toContain('其他通道任务')

    await wrapper.findAll('.titem').find((item) => item.text().includes('PLC2 任务'))!.trigger('click')
    await wrapper.findAll('.task-scope-filter').find((item) => item.text() === '当前设备')!.trigger('click')
    await nextTick()
    expect(wrapper.findAll('.titem')).toHaveLength(1)
    expect(wrapper.find('.titem').text()).toContain('PLC2 任务')
    wrapper.unmount()
  })

  it('links the current-task run switch without changing another task that has the same id', async () => {
    const linkedProject = structuredClone(project) as any
    linkedProject.mesUpload.tasks[0].id = 'duplicate-task-id'
    linkedProject.channels[0].devices[0].groups.push({
      name: 'OP090_1',
      tags: [{ name: 'Request', dataType: 'Bool' }],
      subGroups: [],
    })
    linkedProject.mesUpload.tasks.push({
      id: 'duplicate-task-id',
      name: 'OP090 保持运行',
      groupRef: 'Line.PLC.OP090_1',
      deviceRef: 'Line.PLC',
      flowAvailable: true,
      enabled: true,
      flow: { nodes: [], edges: [] },
    })
    vi.mocked(api.project).mockResolvedValueOnce({ proj: linkedProject, rev: 'rev-linked-run' } as any)

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()

    const runSwitch = wrapper.find('.task-run-switch input[type="checkbox"]')
    expect(runSwitch.exists()).toBe(true)
    expect((runSwitch.element as HTMLInputElement).checked).toBe(false)
    await runSwitch.setValue(true)
    await nextTick()

    expect((runSwitch.element as HTMLInputElement).checked).toBe(true)
    expect(wrapper.findAll('.titem').find((item) => item.text().includes('OP200Data'))!.text()).toContain('运行')
    expect(wrapper.findAll('.titem').find((item) => item.text().includes('OP090 保持运行'))!.text()).toContain('运行')
    const emittedChanges = wrapper.emitted('task-enabled-change') || []
    expect(emittedChanges[emittedChanges.length - 1]).toEqual(['Line.PLC.OP200Data', true])
    wrapper.unmount()
  })

  it('creates and selects an explicitly focused task when randomUUID is unavailable', async () => {
    vi.stubGlobal('crypto', {
      getRandomValues(bytes: Uint8Array) {
        for (let index = 0; index < bytes.length; index++) bytes[index] = index + 1
        return bytes
      },
    })
    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP090_1', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()

    const activeGroupEvents = wrapper.emitted('active-group-change') || []
    expect(activeGroupEvents[activeGroupEvents.length - 1]).toEqual(['Line.PLC.OP090_1'])
    expect(wrapper.find('.titem.on').text()).toContain('OP090_1')

    wrapper.unmount()
    vi.unstubAllGlobals()
  })

  it('creates the focused first-level task without listing unopened second-level groups', async () => {
    const nestedProject = structuredClone(project) as any
    nestedProject.channels[0].driverId = 'OpcUa.Client'
    nestedProject.channels[0].devices[0].groups = [{
      name: 'OP030',
      tags: [{ name: 'RootState', dataType: 'Bool' }],
      subGroups: [
        { name: '进站', tags: [{ name: 'MESFlag', dataType: 'Int16' }], subGroups: [] },
        { name: '出站', tags: [{ name: 'PLCFlag', dataType: 'Int16' }], subGroups: [] },
      ],
    }]
    nestedProject.mesUpload.tasks = []
    vi.mocked(api.project).mockResolvedValueOnce({ proj: nestedProject, rev: 'rev-nested' } as any)

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP030', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()

    expect(wrapper.find('.flowidentity small').text()).toBe('Line.PLC.OP030')
    expect(wrapper.findAll('.titem')).toHaveLength(1)
    expect(wrapper.text()).not.toContain('进站')
    expect(wrapper.text()).not.toContain('出站')
    wrapper.unmount()
  })

  it('fills a second-level task with only its direct and descendant tags', async () => {
    const nestedProject = structuredClone(project) as any
    nestedProject.channels[0].driverId = 'OpcUa.Client'
    nestedProject.channels[0].devices[0].groups = [{
      name: 'OP030',
      tags: [{ name: 'RootState', dataType: 'Bool' }],
      subGroups: [
        {
          name: '进站', tags: [{ name: 'MESFlag', dataType: 'Int16' }], subGroups: [
            { name: '扫码', tags: [{ name: 'SN', dataType: 'String' }], subGroups: [] },
          ],
        },
        { name: '出站', tags: [{ name: 'PLCFlag', dataType: 'Int16' }], subGroups: [] },
      ],
    }]
    nestedProject.mesUpload.tasks = [{
      id: 'entry-task', name: '进站', groupRef: 'Line.PLC.OP030.进站', deviceRef: 'Line.PLC', enabled: false,
      flow: { nodes: [{
        id: 'entry-upload', type: 'Upload', title: '上报', x: 80, y: 80,
        payload: { mode: 'FieldMapping', fields: [], structs: [], append: {} },
        http: { url: '', method: 'POST', timeoutSec: 30, bodyTemplate: '' },
      }], edges: [] },
    }]
    vi.mocked(api.project).mockResolvedValueOnce({ proj: nestedProject, rev: 'rev-entry' } as any)

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP030.进站', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    await nextTick()
    const fill = wrapper.findAll('button').find((button) => button.text().includes('填入本组全部点'))
    await fill!.trigger('click')
    await vi.waitFor(() => expect(fill!.classes()).not.toContain('is-loading'))

    const dialog = wrapper.findComponent({ name: 'MesUploadMappingDialog' })
    dialog.vm.$emit('apply', structuredClone(dialog.props('payload')))
    await nextTick()

    expect(wrapper.findAllComponents(MesParameterPicker).map((picker) => picker.props('modelValue'))).toEqual([
      'Line.PLC.OP030.进站.MESFlag',
      'Line.PLC.OP030.进站.扫码.SN',
    ])
    wrapper.unmount()
  })

  it('fills the current field mapping without switching it to data-range mode', async () => {
    const upload = project.mesUpload.tasks[0].flow.nodes[0] as any
    const originalPayload = structuredClone(upload.payload)
    upload.payload = { mode: 'FieldMapping', fields: [], structs: [], append: {} }
    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    await nextTick()
    const fill = wrapper.findAll('button').find((button) => button.text().includes('填入本组全部点'))
    expect(fill).toBeTruthy()
    await fill!.trigger('click')
    await vi.waitFor(() => expect(fill!.classes()).not.toContain('is-loading'))
    const dialog = wrapper.findComponent({ name: 'MesUploadMappingDialog' })
    dialog.vm.$emit('apply', structuredClone(dialog.props('payload')))
    await nextTick()
    expect(wrapper.text()).toContain('字段/结构映射')
    expect(wrapper.text()).toContain('共 1 个字段')
    expect(wrapper.text()).toContain('填入本组全部点')
    expect(wrapper.findAllComponents(MesParameterPicker)).toHaveLength(1)
    expect((wrapper.findComponent(MesParameterPicker).props('modelValue') as string)).toBe('Line.PLC.OP200Data.DataReady')
    expect(wrapper.text()).not.toContain('1 个数据范围')
    wrapper.unmount()
    upload.payload = originalPayload
  })

  it('renders only the first 50 field editors and makes no option requests for a 20000-point upload mapping', async () => {
    const upload = project.mesUpload.tasks[0].flow.nodes[0] as any
    const originalFields = upload.payload.fields
    upload.payload.fields = Array.from({ length: 20_000 }, (_, index) => ({
      tagId: `Line.PLC.OP200Data.Point${index}`,
      fieldName: `Point${index}`,
    }))

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    await nextTick()

    expect(wrapper.findAllComponents(MesParameterPicker)).toHaveLength(50)
    expect(wrapper.text()).toContain('共 20000 个字段')
    expect(api.parameterOptions).not.toHaveBeenCalled()

    await wrapper.find('input[placeholder="搜索上报字段、JSON 名称、结构路径或成员…"]').setValue('Point19999')
    await nextTick()
    expect(wrapper.findAllComponents(MesParameterPicker)).toHaveLength(1)
    expect(wrapper.text()).toContain('共 1 个字段')
    expect(api.parameterOptions).not.toHaveBeenCalled()

    wrapper.unmount()
    upload.payload.fields = originalFields
  }, 15_000)

  it('renders only the first 10 structure editors for a large manual mapping', async () => {
    const upload = project.mesUpload.tasks[0].flow.nodes[0] as any
    const originalStructs = upload.payload.structs
    upload.payload.structs = Array.from({ length: 25 }, (_, index) => ({
      kind: 'Object',
      baseGroup: `Line.PLC.OP200Data.Manual${index}`,
      aliasName: `Manual${index}`,
      countMode: 'Fixed',
      fixedCount: 1,
      members: [{ tagId: 'Value', fieldName: 'Value' }],
    }))

    const wrapper = mount(MesFlow, {
      props: { focusGroup: 'Line.PLC.OP200Data', design: true },
      global: {
        plugins: [ElementPlus],
        components: { ArrowDown },
        config: { globalProperties: { $t: i18n.t } as any },
      },
    })
    await flushPromises()
    await wrapper.find('g.node').trigger('mousedown', { clientX: 90, clientY: 90 })
    await nextTick()

    expect(wrapper.findAll('.structcard')).toHaveLength(10)
    expect(wrapper.text()).toContain('共 25 个结构体')

    wrapper.unmount()
    upload.payload.structs = originalStructs
  })

})
