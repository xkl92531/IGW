import { flushPromises, mount } from '@vue/test-utils'
import { expect, it, vi } from 'vitest'
import type { ProjectApi } from '../api'
import PagedProjectEditor from './PagedProjectEditor.vue'

function fixture(driverId = 'Modbus.Tcp', tagOverrides: Record<string, any> = {}) {
  const node = (id: string, kind: string, path: string, hasChildren = true) => ({ id, kind, name: id, actualPath: path, hasChildren, description: '', directTagCount: 0, rowVersion: 1, schemaVersion: 1 })
  const root = node('root', 'project', 'project')
  const channel = node('ch', 'channel', 'Channel')
  const device = node('dev', 'device', 'Channel.Device')
  const group = node('grp', 'group', 'Channel.Device.Group', false)
  const row = { id: 'tag', name: 'Pressure', address: '40001', dataType: 'int16', access: 'readWrite', actualPath: 'Channel.Device.Group.Pressure', rowVersion: 1, ...tagOverrides }
  const docs: Record<string, any> = { ch: { id: 'ch', driverId }, dev: { id: 'dev', endpoint: '127.0.0.1:502', settings: { zeroBased: '禁用' } }, grp: { id: 'grp' }, tag: { ...row, vendorExtension: { retained: true } } }
  const service = {
    revision: () => 'rev-1', summary: vi.fn().mockResolvedValue({ root }),
    children: vi.fn(async (_kind, id) => ({ root: [channel], ch: [device], dev: [group] } as any)[id] || []),
    entity: vi.fn(async (_kind, id) => docs[id]),
    tags: vi.fn().mockResolvedValue({ items: [row], total: 100000, offset: 0, limit: 200 }),
    mutate: vi.fn().mockResolvedValue({ applied: true, revision: 'rev-2' }), exportUrl: () => '',
  } as unknown as ProjectApi
  return { service }
}

it('edits a paged Modbus point using its loaded ancestry and preserves unknown document properties', async () => {
  const { service } = fixture()
  const wrapper = mount(PagedProjectEditor, { props: { service } })
  await flushPromises()
  for (const id of ['root', 'ch', 'dev']) { await wrapper.get(`[data-node-id="${id}"]`).trigger('dblclick'); await flushPromises() }
  await wrapper.get('[data-node-id="grp"]').trigger('click'); await flushPromises()
  await wrapper.get('[data-row-id="tag"]').trigger('dblclick'); await flushPromises()
  expect((wrapper.get('[aria-label="Modbus offset"]').element as HTMLInputElement).value).toBe('0')
  await wrapper.get('[aria-label="Modbus offset"]').setValue('12')
  await wrapper.get('[data-save-property]').trigger('click'); await flushPromises()
  const request = vi.mocked(service.mutate).mock.calls[0][0]
  expect(request.entityKind).toBe('tag')
  expect(JSON.parse(request.documentJson!)).toMatchObject({ address: 'HR:12', vendorExtension: { retained: true } })
  expect(service.tags).toHaveBeenCalledTimes(1)
})

it('shows a device of an unsupported driver as JSON without protocol editors', async () => {
  const { service } = fixture('Legacy.Tcp')
  const wrapper = mount(PagedProjectEditor, { props: { service } })
  await flushPromises()
  for (const id of ['root', 'ch']) { await wrapper.get(`[data-node-id="${id}"]`).trigger('dblclick'); await flushPromises() }
  await wrapper.get('[data-node-id="dev"]').trigger('click'); await flushPromises()
  expect(wrapper.find('[aria-label="unitId"]').exists()).toBe(false)
  expect(wrapper.find('[aria-label="Protocol endpoint"]').exists()).toBe(false)
  expect((wrapper.get('.property-editor textarea').element as HTMLTextAreaElement).value).toContain('127.0.0.1:502')
})

it('keeps a point of an unsupported driver editable as JSON and saves it unchanged', async () => {
  const { service } = fixture('Legacy.Tcp', { address: 'Code', dataType: 'string', access: 'write', stringLength: 25, writeConfirmation: { mode: 'protocolAcknowledged' } })
  const wrapper = mount(PagedProjectEditor, { props: { service } })
  await flushPromises()
  for (const id of ['root', 'ch', 'dev']) { await wrapper.get(`[data-node-id="${id}"]`).trigger('dblclick'); await flushPromises() }
  await wrapper.get('[data-node-id="grp"]').trigger('click'); await flushPromises()
  await wrapper.get('[data-row-id="tag"]').trigger('dblclick'); await flushPromises()
  expect(wrapper.find('[aria-label="Modbus offset"]').exists()).toBe(false)
  await wrapper.get('[data-save-property]').trigger('click'); await flushPromises()
  expect(JSON.parse(vi.mocked(service.mutate).mock.calls[0][0].documentJson!))
    .toMatchObject({ address: 'Code', dataType: 'string', stringLength: 25 })
})
