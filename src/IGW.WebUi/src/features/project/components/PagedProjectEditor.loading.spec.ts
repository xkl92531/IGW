import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import type { ProjectApi } from '../api'
import PagedProjectEditor from './PagedProjectEditor.vue'

function deferred<T>() {
  let resolve!: (value: T) => void, reject!: (reason: Error) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}
const root = { id: 'root', kind: 'project', name: 'Project', actualPath: 'Project', hasChildren: true, rowVersion: 1, schemaVersion: 1, directTagCount: 0 }
const group = { ...root, id: 'group', kind: 'group', name: 'Group', actualPath: 'Line.Plc.Group', hasChildren: false }

describe('paged project foreground feedback', () => {
  it('shows tree and property loading, deduplicates expand, and restores save after failure without discarding edits', async () => {
    const branch = deferred<any[]>(), document = deferred<any>(), save = deferred<any>()
    const service = {
      revision: () => 'rev-1', summary: vi.fn().mockResolvedValue({ root }),
      children: vi.fn(() => branch.promise), entity: vi.fn(() => document.promise),
      tags: vi.fn().mockResolvedValue({ items: [], total: 0, offset: 0, limit: 200 }),
      mutate: vi.fn(() => save.promise), exportUrl: () => '',
    } as unknown as ProjectApi
    const wrapper = mount(PagedProjectEditor, { props: { service } })
    await flushPromises()
    await wrapper.get('[data-node-id="root"]').trigger('dblclick')
    await wrapper.get('[data-node-id="root"]').trigger('dblclick')
    expect(service.children).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('正在展开工程树')
    branch.resolve([group]); await flushPromises()
    await wrapper.get('[data-node-id="group"]').trigger('click')
    expect(wrapper.get('.property-editor').attributes('aria-busy')).toBe('true')
    expect(wrapper.get('[data-save-property]').attributes('disabled')).toBeDefined()
    document.resolve({ name: 'Group', description: 'keep me' }); await flushPromises()
    const editor = wrapper.get('.property-editor textarea')
    await editor.setValue('{"name":"Group","description":"edited locally"}')
    await wrapper.get('[data-save-property]').trigger('click')
    await wrapper.get('[data-save-property]').trigger('click')
    expect(service.mutate).toHaveBeenCalledTimes(1)
    expect(wrapper.text()).toContain('正在保存属性')
    save.reject(new Error('save offline')); await flushPromises()
    expect(wrapper.text()).toContain('save offline')
    expect((editor.element as HTMLTextAreaElement).value).toContain('edited locally')
    expect(wrapper.get('[data-save-property]').attributes('disabled')).toBeUndefined()
    expect(wrapper.find('.operation-overlay').exists()).toBe(false)
  })

  it('clears a failed initial load and leaves a retry action available', async () => {
    const initial = deferred<any>()
    const service = { revision: () => '', summary: vi.fn(() => initial.promise) } as unknown as ProjectApi
    const wrapper = mount(PagedProjectEditor, { props: { service } })
    await wrapper.vm.$nextTick()
    expect(wrapper.text()).toContain('正在载入工程')
    initial.reject(new Error('cannot load')); await flushPromises()
    expect(wrapper.get('[role="alert"]').text()).toContain('cannot load')
    expect(wrapper.find('.operation-overlay').exists()).toBe(false)
    expect(wrapper.get('.project-toolbar button').attributes('disabled')).toBeUndefined()
  })

  it('loads device-direct tags when a device node is selected', async () => {
    const device = { ...root, id: 'device', kind: 'device', name: 'PLC', actualPath: 'Line.PLC', hasChildren: false, directTagCount: 229_998 }
    const branch = deferred<any[]>(), document = deferred<any>()
    const service = {
      revision: () => 'rev-1', summary: vi.fn().mockResolvedValue({ root }),
      children: vi.fn(() => branch.promise), entity: vi.fn(() => document.promise),
      tags: vi.fn().mockResolvedValue({ items: [], total: 0, offset: 0, limit: 200 }),
      mutate: vi.fn(), exportUrl: () => '',
    } as unknown as ProjectApi
    const wrapper = mount(PagedProjectEditor, { props: { service } })
    await flushPromises()
    await wrapper.get('[data-node-id="root"]').trigger('dblclick')
    branch.resolve([device]); await flushPromises()
    await wrapper.get('[data-node-id="device"]').trigger('click')
    expect(service.tags).toHaveBeenCalledWith(expect.objectContaining({ deviceId: 'device' }), expect.anything())
    document.resolve({ name: 'PLC' }); await flushPromises()
  })
})
