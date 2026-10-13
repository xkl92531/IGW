import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import ArrayValueViewer from './ArrayValueViewer.vue'
import { api } from '../api'
import { i18n } from '../i18n'
vi.mock('../api', () => ({ api: { arrayInfo: vi.fn(), arraySlice: vi.fn(), writeArraySlice: vi.fn() } }))
const info = { id:'A', elementType:'Int16', dimensions:[1], lowerBounds:[0], totalElements:1, good:1, bad:0, uncertain:0, overallQuality:'Good' as const }
const slice = (value: number) => ({ ...info, start:[0], length:[1], values:[value], qualityCodes:[0] })
function view() { return mount(ArrayValueViewer, { props: { tagId:'A', definition:{elementType:'Int16', dimensions:[1], lowerBounds:[0]}, writable:true }, global: { plugins:[ElementPlus], config:{globalProperties:{$t:i18n.t} as any} } }) }
beforeEach(() => { vi.resetAllMocks(); vi.mocked(api.arrayInfo).mockResolvedValue(info); vi.mocked(api.arraySlice).mockResolvedValue(slice(1)) })
describe('array loading ownership', () => {
  it('ignores an old response after switching to another array', async () => {
    let finish!: (value: any) => void
    vi.mocked(api.arrayInfo).mockImplementationOnce(() => new Promise(resolve => { finish=resolve }))
    const wrapper=view(); await flushPromises()
    expect(wrapper.attributes('aria-busy')).toBe('true')
    await wrapper.setProps({tagId:'B'}); await flushPromises()
    expect(wrapper.attributes('aria-busy')).toBe('false')
    finish(info); await flushPromises()
    expect(api.arraySlice).toHaveBeenCalledTimes(1)
    expect(api.arraySlice).toHaveBeenCalledWith('B',[0],[1])
    wrapper.unmount()
  })
  it('shows pending writes in the dialog, rejects duplicate submits, and recovers on failure', async () => {
    let fail!: (reason: any) => void
    vi.mocked(api.writeArraySlice).mockImplementationOnce(() => new Promise((_, reject) => { fail=reject }))
    const wrapper=view(); await flushPromises()
    await wrapper.findAll('button').find(button => button.text()==='写当前切片')!.trigger('click'); await flushPromises()
    const vm=wrapper.vm as any
    const first=vm.saveWrite(); const second=vm.saveWrite()
    await flushPromises()
    expect(api.writeArraySlice).toHaveBeenCalledTimes(1)
    expect(wrapper.attributes('aria-busy')).toBe('true')
    fail(new Error('write failed')); await first; await second; await flushPromises()
    expect(wrapper.attributes('aria-busy')).toBe('false')
    expect(vm.editOpen).toBe(true)
    wrapper.unmount()
  })
})
