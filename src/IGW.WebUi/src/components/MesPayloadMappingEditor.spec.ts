import { mount, flushPromises } from '@vue/test-utils'
import { beforeEach, expect, it, vi } from 'vitest'
import ElementPlus from 'element-plus'
import MesPayloadMappingEditor from './MesPayloadMappingEditor.vue'
import { i18n } from '../i18n'

const api = vi.hoisted(() => ({ mesPayloadPreview: vi.fn() }))
vi.mock('../api', () => ({ api }))

const value = (key: string, name: string, dataType = 'Int16') => ({ key, name, kind: 'value', tagId: `Line.PLC.DB10.${key}`, dataType, excludable: true, children: [] })
const preview = {
  ranges: [{
    sourcePath: 'Line.PLC.DB10', outputPath: 'PLC.DB10', scope: 'Group', tagCount: 5,
    root: { key: '', name: 'DB10', kind: 'object', excludable: false, children: [
      value('Counter', 'Counter'),
      { key: 'UDT1', name: 'UDT1', kind: 'object', excludable: false, children: [
        { ...value('UDT1.A', 'A'), excludable: false }] },
      { key: 'Bin', name: 'Bin', kind: 'array', length: 2, excludable: false, children: [
        { key: 'Bin[].Code', name: 'Code', kind: 'value', dataType: 'Int16', excludable: false, omittable: true, children: [] }] },
    ] },
  }],
  error: null,
}

beforeEach(() => { api.mesPayloadPreview.mockReset().mockResolvedValue(preview) })

async function mountEditor(payload: any) {
  const wrapper = mount(MesPayloadMappingEditor, {
    props: { payload, scopeGroup: 'Line.PLC.DB10' },
    global: { plugins: [ElementPlus], config: { globalProperties: { $t: i18n.t } as any } },
  })
  await new Promise(resolve => setTimeout(resolve, 350))
  await flushPromises()
  return wrapper
}

it('lists plain tags, structs and arrays of the configured data and records renames by structure path', async () => {
  const payload = { mode: 'DataRanges', dataRanges: [{ sourcePath: 'Line.PLC.DB10', scope: 'Group', outputPath: 'PLC.DB10', excludes: [] }] }
  const wrapper = await mountEditor(payload)
  const state = (wrapper.vm.$ as any).setupState
  expect(api.mesPayloadPreview).toHaveBeenCalledWith({ scopeGroup: 'Line.PLC.DB10', ranges: payload.dataRanges }, expect.anything())
  const rows = state.sections[0].rows
  expect(rows.map((row: any) => [row.label, row.node.kind])).toEqual([['Counter', 'value'], ['UDT1', 'object'], ['Bin', 'array']])
  expect(state.kindText(rows[2].node)).toContain('数组 ×2')

  state.rename(rows[0], 'count')
  state.rename(rows[2].children[0], 'code')
  expect(payload.dataRanges[0]).toMatchObject({ names: { Counter: 'count', 'Bin[].Code': 'code' } })
  state.rename(rows[0], 'Counter')                          // 改回原名就不再记录
  expect((payload.dataRanges[0] as any).names).toEqual({ 'Bin[].Code': 'code' })

  state.setUploaded(rows[0], false)
  expect(payload.dataRanges[0].excludes).toEqual(['Counter'])
  state.setUploaded(rows[0], true)
  expect(payload.dataRanges[0].excludes).toEqual([])

  // 数组元素里的成员：记在 omit，所有元素一起去掉
  const code = rows[2].children[0]
  expect(state.canToggle(code)).toBe(true)
  state.setUploaded(code, false)
  expect(payload.dataRanges[0]).toMatchObject({ omit: ['Bin[].Code'] })
  expect(state.isUploaded(code)).toBe(false)
  expect(state.excludedCount).toBe(1)
  state.setUploaded(code, true)
  expect((payload.dataRanges[0] as any).omit).toBeUndefined()
  expect(state.canToggle(rows[1])).toBe(false)              // 结构体本身不能勾掉，只能勾它的成员
  expect(wrapper.emitted('change')!.length).toBeGreaterThanOrEqual(4)
  wrapper.unmount()
})

it('renames a single-tag range through its output path and shows rename conflicts', async () => {
  api.mesPayloadPreview.mockResolvedValue({
    ranges: [{ sourcePath: 'Line.PLC.DB10.Counter', outputPath: 'PLC.DB10.Counter', scope: 'Tag', tagCount: 1,
      root: { key: '', name: 'Counter', kind: 'value', dataType: 'Int16', excludable: false, children: [] } }],
    error: 'MES 数据范围 Line.PLC.DB10 改名后出现重复字段:Temp。',
  })
  const payload = { mode: 'DataRanges', dataRanges: [{ sourcePath: 'Line.PLC.DB10.Counter', scope: 'Tag', outputPath: 'PLC.DB10.Counter', outputPathMode: 'Auto' }] }
  const wrapper = await mountEditor(payload)
  const state = (wrapper.vm.$ as any).setupState
  expect(wrapper.find('[data-testid="mapping-error"]').text()).toContain('重复字段')
  state.rename(state.sections[0].rows[0], 'count')
  expect(payload.dataRanges[0]).toMatchObject({ outputPath: 'PLC.DB10.count', outputPathMode: 'Custom' })
  wrapper.unmount()
})
