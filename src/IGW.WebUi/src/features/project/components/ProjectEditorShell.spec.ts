import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import ProjectEditorShell from './ProjectEditorShell.vue'
import type { ProjectTagRowDto, ProjectTreeNodeDto } from '../types'

const nodes: ProjectTreeNodeDto[] = [
  { id: 'g1', kind: 'group', name: 'Station', description: '进站', actualPath: 'PLC1.DB1.Station', hasChildren: true, directTagCount: 2, rowVersion: 1, schemaVersion: 1 },
  { id: 'g2', kind: 'group', name: 'Station', description: '出站', actualPath: 'PLC1.DB2.Station', hasChildren: true, directTagCount: 2, rowVersion: 1, schemaVersion: 1 },
]
const rows: ProjectTagRowDto[] = [
  { id: 't1', name: 'Flag', actualPath: 'PLC1.DB1.Station.Flag', address: 'DB1.DBW0', dataType: 'int16', access: 'readWrite', scanRateMs: 100, description: '进站标志', engUnit: null, stringLength: null, array: null, metadata: null, rowVersion: 1 },
  { id: 't2', name: 'Flag', actualPath: 'PLC1.DB2.Station.Flag', address: 'DB2.DBW0', dataType: 'int16', access: 'readWrite', scanRateMs: 100, description: '出站标志', engUnit: null, stringLength: null, array: null, metadata: null, rowVersion: 1 },
]

describe('ProjectEditorShell', () => {
  it('renders descriptions and complete paths and gives selected rows an explicit style', async () => {
    const wrapper = mount(ProjectEditorShell, { props: { nodes, rows, selectedIds: ['t2'] } })
    expect(wrapper.text()).toContain('进站')
    expect(wrapper.text()).toContain('PLC1.DB1.Station')
    const selected = wrapper.get('[data-row-id="t2"]')
    expect(selected.classes()).toContain('is-selected')
    expect(selected.attributes('title')).toContain('PLC1.DB2.Station.Flag')
  })

  it('persists resized pane width and closes a context menu when another node is clicked', async () => {
    localStorage.clear()
    const wrapper = mount(ProjectEditorShell, { attachTo: document.body, props: { nodes, rows, selectedIds: [] } })
    await wrapper.get('[data-node-id="g1"]').trigger('contextmenu')
    expect(wrapper.find('.project-context-menu').exists()).toBe(true)
    await wrapper.get('[data-node-id="g2"]').trigger('click')
    expect(wrapper.find('.project-context-menu').exists()).toBe(false)

    await wrapper.get('[data-resizer="tree"]').trigger('mousedown', { clientX: 360 })
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 430 }))
    window.dispatchEvent(new MouseEvent('mouseup'))
    expect(Number(localStorage.getItem('igw.project.editor.tree-width'))).toBeGreaterThan(360)
    wrapper.unmount()
  })
})
