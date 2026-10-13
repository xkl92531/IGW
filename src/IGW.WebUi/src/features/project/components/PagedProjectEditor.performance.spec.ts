import { flushPromises, mount } from '@vue/test-utils'
import { describe, expect, it, vi } from 'vitest'
import type { ProjectApi } from '../api'
import type { ProjectTagRowDto, ProjectTreeNodeDto } from '../types'
import PagedProjectEditor from './PagedProjectEditor.vue'

const root: ProjectTreeNodeDto = {
  id: 'project-1', kind: 'project', name: '百万点工程', description: null,
  actualPath: '百万点工程', hasChildren: true, directTagCount: 0, rowVersion: 1, schemaVersion: 1,
}
const group: ProjectTreeNodeDto = {
  id: 'group-1', kind: 'group', name: 'DB100', description: '生产数据',
  actualPath: 'PLC1.DB100', hasChildren: false, directTagCount: 1_000_000, rowVersion: 1, schemaVersion: 1,
}
const targetGroup: ProjectTreeNodeDto = {
  id: 'group-2', kind: 'group', name: 'Archive', description: '移动目标',
  actualPath: 'PLC1.Archive', hasChildren: false, directTagCount: 0, rowVersion: 1, schemaVersion: 1,
}

function row(index: number): ProjectTagRowDto {
  return {
    id: `tag-${index}`, name: `Tag${index}`, description: `点位${index}`,
    actualPath: `PLC1.DB100.Tag${index}`, address: `DB100.DBW${index * 2}`,
    dataType: 'Int16', access: 'ReadWrite', scanRateMs: 100, engUnit: null,
    stringLength: null, array: null, metadata: null, rowVersion: 1,
  }
}

describe('PagedProjectEditor million-point boundary', () => {
  it('loads hierarchy on demand and renders at most one 200-row tag page', async () => {
    const tags = vi.fn().mockResolvedValue({ items: Array.from({ length: 200 }, (_, i) => row(i)), total: 1_000_000, offset: 0, limit: 200 })
    const service = {
      revision: () => 'rev-1',
      summary: vi.fn().mockResolvedValue({ root, totalTagCount: 1_000_000, fullProjectSupported: false, configurationDegraded: false, configurationDegradedMessage: null }),
      children: vi.fn().mockResolvedValue([group]),
      tags,
      mesTasks: vi.fn(),
      entity: vi.fn().mockResolvedValue({ id: group.id, name: group.name }),
      mutate: vi.fn(),
      inspectImport: vi.fn(),
      commitImport: vi.fn(),
      exportUrl: vi.fn().mockReturnValue('/api/v1/project/export'),
    } as unknown as ProjectApi

    const wrapper = mount(PagedProjectEditor, { props: { service } })
    await flushPromises()
    expect(service.children).not.toHaveBeenCalled()
    expect(tags).not.toHaveBeenCalled()

    await wrapper.get('[data-node-id="project-1"]').trigger('dblclick')
    await flushPromises()
    const groupNode = wrapper.get('[data-node-id="group-1"]')
    await groupNode.trigger('click')
    await groupNode.trigger('click')
    await flushPromises()

    expect(service.children).toHaveBeenCalledTimes(1)
    expect(service.entity).toHaveBeenCalledTimes(1)
    expect(tags).toHaveBeenCalledTimes(1)
    expect(tags).toHaveBeenCalledWith(expect.objectContaining({ groupId: 'group-1', offset: 0, limit: 200 }), expect.any(AbortSignal))
    expect(wrapper.findAll('[data-row-id]')).toHaveLength(200)
    expect(wrapper.text()).toContain('共 1000000 条')
  })

  it('moves a million-point group by id without loading its tag contents', async () => {
    let revision = 'rev-1'
    const tags = vi.fn()
    const mutate = vi.fn().mockImplementation(async () => {
      revision = 'rev-2'
      return { applied: true, revision }
    })
    const service = {
      revision: () => revision,
      summary: vi.fn().mockResolvedValue({ root, totalTagCount: 1_000_000, fullProjectSupported: false, configurationDegraded: false, configurationDegradedMessage: null }),
      children: vi.fn().mockResolvedValue([group, targetGroup]),
      tags,
      mesTasks: vi.fn(),
      entity: vi.fn(),
      mutate,
      inspectImport: vi.fn(),
      commitImport: vi.fn(),
      exportUrl: vi.fn().mockReturnValue('/api/v1/project/export'),
    } as unknown as ProjectApi

    const wrapper = mount(PagedProjectEditor, { props: { service } })
    await flushPromises()
    await wrapper.get('[data-node-id="project-1"]').trigger('dblclick')
    await flushPromises()

    const source = wrapper.get('[data-node-id="group-1"]')
    const target = wrapper.get('[data-node-id="group-2"]')
    await source.trigger('dragstart')
    await target.trigger('dragover')
    await target.trigger('drop')
    await flushPromises()

    expect(mutate).toHaveBeenCalledOnce()
    expect(mutate).toHaveBeenCalledWith({
      kind: 'moveEntities', entityKind: 'group', entityIds: ['group-1'],
      targetId: 'group-2', documentJson: null,
    }, 'rev-1')
    expect(tags).not.toHaveBeenCalled()
    expect(service.entity).not.toHaveBeenCalled()
  })
})
