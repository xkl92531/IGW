import { flushPromises, mount } from '@vue/test-utils'
import ElementPlus from 'element-plus'
import { nextTick } from 'vue'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import OpcUaTagBrowserDialog from './OpcUaTagBrowserDialog.vue'

const connection = {
  url: 'opc.tcp://10.217.152.80:4840',
  securityPolicy: 'None',
  anonymous: true,
  username: '',
  password: '',
  timeoutMs: 120_000,
}

describe('OPC UA tag browser dialog', () => {
  it('keeps a large branch removable while the complete import preview is prepared cooperatively', async () => {
    const scheduledFrames: FrameRequestCallback[] = []
    vi.stubGlobal('requestAnimationFrame', vi.fn((callback: FrameRequestCallback) => {
      scheduledFrames.push(callback)
      return scheduledFrames.length
    }))
    const node = { id: 'node:DB', label: 'DB', path: 'PLC.DB', sourceSegments: ['PLC', 'DB'], folder: true, isLeaf: false, nodeId: 'ns=3;s=DB', isVariable: false }
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({
      nodes: [{ nodeId: node.nodeId, name: node.label, isVariable: false, mayHaveChildren: true, pathSegments: node.sourceSegments }],
    })
    vi.spyOn(api, 'browseOpcUaBranch').mockResolvedValue({
      tags: Array.from({ length: 600 }, (_, index) => ({
        name: `Tag${index}`, address: `ns=3;s=PLC.DB.Tag${index}`, path: 'PLC.DB',
        pathSegments: ['PLC', 'DB'], dataType: index === 299 ? 17 : 'int16', isArray: false,
      })),
      truncated: false,
    })
    const wrapper = mount(OpcUaTagBrowserDialog, { attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } } })

    await wrapper.setProps({ modelValue: true })
    await flushPromises()
    wrapper.findComponent({ name: 'ElTree' }).vm.$emit('check', node, { checkedKeys: [node.id] })
    await flushPromises()

    expect(scheduledFrames.length).toBeGreaterThan(0)
    expect(document.body.textContent).toContain('正在整理完整导入预览…')
    expect([...document.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.includes('清空选择'))?.disabled).toBe(false)

    while (scheduledFrames.length) {
      scheduledFrames.shift()!(performance.now())
      await flushPromises()
    }

    expect(document.querySelectorAll('.selected-row')).toHaveLength(300)
    expect(document.body.textContent).toContain('其余 {count} 个已选择，导入时会一并处理。')
    expect(document.body.textContent).toContain('dtl')
    const apply = [...document.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.includes('导入 {count} 个'))!
    expect(apply.disabled).toBe(false)
    apply.click()
    expect(wrapper.emitted('apply')?.[0]?.[0]).toEqual(expect.objectContaining({
      tags: expect.arrayContaining([expect.objectContaining({ name: 'Tag599' })]),
    }))
    expect((wrapper.emitted('apply')?.[0]?.[0] as any).tags).toHaveLength(600)
    wrapper.unmount()
  })

  it('ignores a root browse response from a closed and replaced connection', async () => {
    let resolveFirst!: (value: any) => void
    let resolveSecond!: (value: any) => void
    vi.spyOn(api, 'browseOpcUaChildren')
      .mockReturnValueOnce(new Promise(resolve => { resolveFirst = resolve }))
      .mockReturnValueOnce(new Promise(resolve => { resolveSecond = resolve }))
    const wrapper = mount(OpcUaTagBrowserDialog, { attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } } })

    await wrapper.setProps({ modelValue: true })
    await nextTick()
    await wrapper.setProps({ modelValue: false })
    await wrapper.setProps({
      connection: { ...connection, url: 'opc.tcp://fresh-server:4840' },
      modelValue: true,
    })
    resolveSecond({ nodes: [{
      nodeId: 'ns=3;s=Fresh', name: 'FreshServer', isVariable: false,
      mayHaveChildren: true, pathSegments: ['FreshServer'],
    }] })
    await flushPromises()
    resolveFirst({ nodes: [{
      nodeId: 'ns=3;s=Stale', name: 'StaleServer', isVariable: false,
      mayHaveChildren: true, pathSegments: ['StaleServer'],
    }] })
    await flushPromises()

    expect(document.body.textContent).toContain('FreshServer')
    expect(document.body.textContent).not.toContain('StaleServer')
    wrapper.unmount()
  })

  it('keeps an exact scalar browse node rename attached to the imported tag', async () => {
    const node = {
      id: 'node:Value', label: 'Value', path: 'PLC.DB.Value', sourceSegments: ['PLC', 'DB', 'Value'],
      folder: false, isLeaf: true, nodeId: 'ns=3;s=PLC.DB.Value', isVariable: true,
    }
    const tag = {
      name: 'Value', address: node.nodeId, path: 'PLC.DB', pathSegments: ['PLC', 'DB'],
      dataType: 'int16', isArray: false,
    }
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({ nodes: [{
      nodeId: node.nodeId, name: node.label, isVariable: true, mayHaveChildren: false,
      pathSegments: node.sourceSegments,
    }] })
    vi.spyOn(api, 'browseOpcUaBranch').mockResolvedValue({ tags: [tag], truncated: false })
    const wrapper = mount(OpcUaTagBrowserDialog, { attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } } })
    try {
      await wrapper.setProps({ modelValue: true })
      await flushPromises()
      wrapper.findComponent({ name: 'ElTree' }).vm.$emit('check', node, { checkedKeys: [node.id] })
      await flushPromises()

      const input = document.querySelector<HTMLInputElement>('.target-input input')!
      expect(input).not.toBeNull()
      input.value = 'RenamedValue'
      input.dispatchEvent(new Event('input', { bubbles: true }))
      await flushPromises()
      const apply = [...document.querySelectorAll<HTMLButtonElement>('button')]
        .find(button => button.textContent?.includes('导入 {count} 个'))!
      apply.click()

      expect((wrapper.emitted('apply')?.[0]?.[0] as any).tags).toEqual([
        expect.objectContaining({ name: 'RenamedValue', address: node.nodeId, groupPath: ['DB'] }),
      ])
    } finally { wrapper.unmount() }
  })

  it('renames only the selected browse-path alias when two scalar nodes share a NodeId', async () => {
    const nodeId = 'ns=3;s=Shared.Value'
    const nodeA = {
      id: 'node:AliasA', label: 'Value', path: 'PLC.AliasA.Value', sourceSegments: ['PLC', 'AliasA', 'Value'],
      folder: false, isLeaf: true, nodeId, isVariable: true,
    }
    const nodeB = {
      id: 'node:AliasB', label: 'Value', path: 'PLC.AliasB.Value', sourceSegments: ['PLC', 'AliasB', 'Value'],
      folder: false, isLeaf: true, nodeId, isVariable: true,
    }
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({ nodes: [nodeA, nodeB].map(node => ({
      nodeId: node.nodeId, name: node.label, isVariable: true, mayHaveChildren: false,
      pathSegments: node.sourceSegments,
    })) })
    vi.spyOn(api, 'browseOpcUaBranch').mockImplementation(async body => ({
      tags: [{
        name: 'Value', address: nodeId,
        path: body.pathSegments.slice(0, -1).join('.'),
        pathSegments: body.pathSegments.slice(0, -1),
        dataType: 'int16', isArray: false,
      }],
      truncated: false,
    }))
    const wrapper = mount(OpcUaTagBrowserDialog, { attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } } })
    try {
      await wrapper.setProps({ modelValue: true })
      await flushPromises()
      const tree = wrapper.findComponent({ name: 'ElTree' })
      tree.vm.$emit('check', nodeA, { checkedKeys: [nodeA.id] })
      await flushPromises()
      tree.vm.$emit('check', nodeB, { checkedKeys: [nodeB.id] })
      await flushPromises()

      const input = document.querySelector<HTMLInputElement>('.target-input input')!
      input.value = 'SecondAliasValue'
      input.dispatchEvent(new Event('input', { bubbles: true }))
      await flushPromises()
      const apply = [...document.querySelectorAll<HTMLButtonElement>('button')]
        .find(button => button.textContent?.includes('导入 {count} 个'))!
      apply.click()

      const imported = (wrapper.emitted('apply')?.[0]?.[0] as any).tags
      expect(imported).toEqual(expect.arrayContaining([
        expect.objectContaining({ name: 'Value', address: nodeId, groupPath: ['AliasA'] }),
        expect.objectContaining({ name: 'SecondAliasValue', address: nodeId, groupPath: ['AliasB'] }),
      ]))
      expect(imported).toHaveLength(2)
    } finally { wrapper.unmount() }
  })

  it.each(['success', 'failure'] as const)('keeps another path alias of the same NodeId unchanged after retry %s', async outcome => {
    const node = { id: 'node:DB', label: 'DB', path: 'PLC.DB', sourceSegments: ['PLC', 'DB'], folder: true, isLeaf: false, nodeId: 'ns=3;s=DB', isVariable: false }
    const first = { nodeId: 'ns=3;s=Shared', name: 'Value', pathSegments: ['PLC', 'DB', 'AliasA', 'Value'], reason: 'AliasA type failure' }
    const second = { ...first, pathSegments: ['PLC', 'DB', 'AliasB', 'Value'], reason: 'AliasB type failure' }
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({ nodes: [{ nodeId: node.nodeId, name: node.label, isVariable: false, mayHaveChildren: true, pathSegments: node.sourceSegments }] })
    const branch = vi.spyOn(api, 'browseOpcUaBranch').mockResolvedValueOnce({ tags: [], issues: [first, second], truncated: false } as any)
    if (outcome === 'failure') branch.mockRejectedValueOnce(new Error('AliasA retry timed out'))
    else branch.mockResolvedValueOnce({ tags: [{ name: 'Value', address: first.nodeId, path: 'PLC.DB.AliasA', pathSegments: ['PLC', 'DB', 'AliasA'], dataType: 'int16', isArray: false }], truncated: false })
    const wrapper = mount(OpcUaTagBrowserDialog, { attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } } })
    try {
      await wrapper.setProps({ modelValue: true })
      await flushPromises()
      wrapper.findComponent({ name: 'ElTree' }).vm.$emit('check', node, { checkedKeys: [node.id] })
      await flushPromises()
      expect(document.querySelectorAll('.browse-issue-row')).toHaveLength(2)
      document.querySelector<HTMLButtonElement>('.browse-issue-row button')!.click()
      await flushPromises()
      expect(branch).toHaveBeenLastCalledWith(expect.objectContaining({ nodeId: first.nodeId, pathSegments: first.pathSegments }))
      const rows = [...document.querySelectorAll('.browse-issue-row')].map(row => row.textContent || '')
      expect(rows).toHaveLength(outcome === 'success' ? 1 : 2)
      expect(rows.filter(row => row.includes('AliasB type failure'))).toHaveLength(1)
      expect(rows.filter(row => row.includes('AliasA retry timed out'))).toHaveLength(outcome === 'failure' ? 1 : 0)
    } finally { wrapper.unmount() }
  })

  it('keeps the rendered issue list bounded for a 100000-item response', async () => {
    const node = { id: 'node:DB', label: 'DB', path: 'PLC.DB', sourceSegments: ['PLC', 'DB'], folder: true, isLeaf: false, nodeId: 'ns=3;s=DB', isVariable: false }
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({ nodes: [{ nodeId: node.nodeId, name: node.label, isVariable: false, mayHaveChildren: true, pathSegments: node.sourceSegments }] })
    const branch = vi.spyOn(api, 'browseOpcUaBranch').mockResolvedValue({ tags: [], truncated: false,
      issues: Array.from({ length: 100_000 }, (_, i) => ({ nodeId: `ns=3;s=Bad${i}`, name: `Bad${i}`, pathSegments: ['PLC', 'DB', `Bad${i}`], reason: '不支持类型' })),
    } as any)
    const wrapper = mount(OpcUaTagBrowserDialog, { attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } } })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()
    wrapper.findComponent({ name: 'ElTree' }).vm.$emit('check', node, { checkedKeys: [node.id] })
    await flushPromises()
    expect(document.querySelectorAll('.browse-issues code')).toHaveLength(50)
    expect(document.querySelector('.browse-issues')?.textContent).toContain('100000 项')
    expect(document.querySelector('.browse-issues')?.textContent).toContain('第 1 / 2000 页')
    expect(branch).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

  it('renders at most 50 issues and clamps the page after retry or clearing the selected range', async () => {
    const node = { id: 'node:DB', label: 'DB', path: 'PLC.DB', sourceSegments: ['PLC', 'DB'], folder: true, isLeaf: false, nodeId: 'ns=3;s=DB', isVariable: false }
    const issues = Array.from({ length: 101 }, (_, i) => ({ nodeId: `ns=3;s=DB.Bad${i}`, name: `Bad${i}`, pathSegments: ['PLC', 'DB', `Bad${i}`], reason: '不支持类型' }))
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({ nodes: [{ nodeId: node.nodeId, name: node.label, isVariable: false, mayHaveChildren: true, pathSegments: node.sourceSegments }] })
    const branch = vi.spyOn(api, 'browseOpcUaBranch').mockResolvedValueOnce({ tags: [], issues, truncated: false } as any)
      .mockResolvedValueOnce({ tags: [{ name: 'Bad100', address: issues[100]!.nodeId, path: 'PLC.DB', pathSegments: ['PLC', 'DB'], dataType: 'int16', isArray: false }], truncated: false })
      .mockResolvedValueOnce({ tags: [], issues, truncated: false } as any)
    const wrapper = mount(OpcUaTagBrowserDialog, { attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } } })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()
    wrapper.findComponent({ name: 'ElTree' }).vm.$emit('check', node, { checkedKeys: [node.id] })
    await flushPromises()
    expect(document.querySelectorAll('.browse-issues code')).toHaveLength(50)
    expect(document.querySelector('.browse-issues')?.textContent).toContain('101 项')
    const next = () => [...document.querySelectorAll<HTMLButtonElement>('.browse-issues button')].find(button => button.textContent?.includes('下一页'))!
    next().click(); await nextTick()
    next().click(); await nextTick()
    expect(document.querySelectorAll('.browse-issues code')).toHaveLength(1)
    expect(document.querySelector('.browse-issues code')?.textContent).toBe(issues[100]!.nodeId)
    expect(branch).toHaveBeenCalledTimes(1)
    document.querySelector<HTMLButtonElement>('.browse-issues button')!.click()
    await flushPromises()
    expect(branch).toHaveBeenLastCalledWith(expect.objectContaining({ nodeId: issues[100]!.nodeId, pathSegments: issues[100]!.pathSegments }))
    expect(document.querySelector('.browse-issues')?.textContent).toContain('第 2 / 2 页')
    expect(document.querySelectorAll('.browse-issues code')).toHaveLength(50)
    wrapper.findComponent({ name: 'ElTree' }).vm.$emit('check', node, { checkedKeys: [] })
    await nextTick()
    expect(document.querySelector('.browse-issues')).toBeNull()
    wrapper.findComponent({ name: 'ElTree' }).vm.$emit('check', node, { checkedKeys: [node.id] })
    await flushPromises()
    expect(document.querySelector('.browse-issues')?.textContent).toContain('第 1 / 3 页')
    wrapper.unmount()
  })

  it('keeps successful tags importable and retries only the exact failed node', async () => {
    const node = { id: 'node:DB', label: 'DB', path: 'PLC.DB', sourceSegments: ['PLC', 'DB'], folder: true, isLeaf: false, nodeId: 'ns=3;s=DB', isVariable: false }
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({ nodes: [{ nodeId: node.nodeId, name: node.label, isVariable: false, mayHaveChildren: true, pathSegments: node.sourceSegments }] })
    const good = { name: 'Good', address: 'ns=3;s=DB.Good', path: 'PLC.DB', pathSegments: ['PLC', 'DB'], dataType: 'int16', isArray: false }
    const branch = vi.spyOn(api, 'browseOpcUaBranch').mockResolvedValueOnce({ tags: [good], truncated: false,
      issues: [{ nodeId: 'ns=3;s=DB.Bad', name: 'Bad', pathSegments: ['PLC', 'DB', 'Bad'], reason: '类型 POINTER 不支持' }] } as any)
      .mockRejectedValueOnce(new Error('连接超时'))
      .mockResolvedValueOnce({ tags: [{ ...good, name: 'Bad', address: 'ns=3;s=DB.Bad' }], truncated: false })
    const wrapper = mount(OpcUaTagBrowserDialog, { attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } } })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()
    wrapper.findComponent({ name: 'ElTree' }).vm.$emit('check', node, { checkedKeys: [node.id] })
    await flushPromises()
    expect(document.querySelector('.browse-issues')?.textContent).toContain('ns=3;s=DB.Bad')
    expect(document.querySelector('.browse-issues')?.textContent).toContain('类型 POINTER 不支持')
    expect([...document.querySelectorAll<HTMLButtonElement>('button')].find(button => button.textContent?.includes('导入 {count} 个'))?.disabled).toBe(false)
    const retry = document.querySelector<HTMLButtonElement>('.browse-issues button')!
    retry.click()
    await flushPromises()
    expect(document.querySelector('.browse-issues')?.textContent).toContain('连接超时')
    expect(document.body.textContent).toContain('Good')
    document.querySelector<HTMLButtonElement>('.browse-issues button')!.click()
    await flushPromises()
    expect(branch).toHaveBeenLastCalledWith(expect.objectContaining({ nodeId: 'ns=3;s=DB.Bad', name: 'Bad', isVariable: true, pathSegments: ['PLC', 'DB', 'Bad'] }))
    expect(document.querySelector('.browse-issues')).toBeNull()
    expect(document.body.textContent).toContain('Good')
    wrapper.unmount()
  })

  it('keeps failed request duration and labels browse timing separately from PLC sampling', async () => {
    let rejectRequest!: (error: Error) => void
    vi.spyOn(api, 'browseOpcUaChildren').mockReturnValue(new Promise((_resolve, reject) => { rejectRequest = reject }))
    const clock = vi.spyOn(performance, 'now').mockReturnValue(1000)
    const wrapper = mount(OpcUaTagBrowserDialog, {
      attachTo: document.body,
      props: { modelValue: false, connection, target: { name: 'Device', tags: [], groups: [] } },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } },
    })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()
    expect(document.body.textContent).toContain('连接并读取根节点')
    clock.mockReturnValue(2500)
    rejectRequest(new Error('连接超时'))
    await flushPromises()
    expect(document.body.textContent).toContain('失败 · 1.50 秒')
    expect(document.body.textContent).toContain('不代表 PLC 采集耗时')
    expect(api.browseOpcUaChildren).toHaveBeenCalledTimes(1)
    wrapper.unmount()
  })

  afterEach(() => {
    vi.restoreAllMocks()
    vi.unstubAllGlobals()
    document.body.innerHTML = ''
  })

  it('loads the Element Plus virtual root without sending it as an OPC UA node', async () => {
    const browse = vi.spyOn(api, 'browseOpcUaChildren')
      .mockResolvedValueOnce({
        nodes: [{
          nodeId: 'ns=3;s=PLC', name: 'PLC8', isVariable: false,
          mayHaveChildren: true, pathSegments: ['PLC8'],
        }],
      })
      .mockResolvedValueOnce({
        nodes: [{
          nodeId: 'ns=3;s=DB2100', name: 'DB2100', isVariable: false,
          mayHaveChildren: true, pathSegments: ['PLC8', 'DB2100'],
        }],
      })

    const wrapper = mount(OpcUaTagBrowserDialog, {
      attachTo: document.body,
      props: {
        modelValue: false,
        connection,
        target: { name: 'Device', tags: [], groups: [] },
      },
      global: {
        plugins: [ElementPlus],
        mocks: { $t: (key: string, values?: Record<string, unknown>) => {
          let text = key
          for (const [name, value] of Object.entries(values || {}))
            text = text.split(`{${name}}`).join(String(value))
          return text
        } },
      },
    })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()

    expect(browse).toHaveBeenCalledTimes(1)
    expect(document.body.textContent).toContain('PLC8')
    expect(document.body.textContent).not.toContain('sourceSegments is not iterable')

    const expand = document.querySelector<HTMLElement>('.el-tree-node__expand-icon')
    expect(expand).not.toBeNull()
    expand!.click()
    await flushPromises()

    expect(browse).toHaveBeenCalledTimes(2)
    expect(browse.mock.calls[1][0]).toMatchObject({
      nodeId: 'ns=3;s=PLC',
      pathSegments: ['PLC8'],
    })
    wrapper.unmount()
  })

  it('shows a selected range immediately and lets the user remove it while scanning', async () => {
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({
      nodes: [{
        nodeId: 'ns=3;s=DB2100', name: 'DB2100', isVariable: false,
        mayHaveChildren: true, pathSegments: ['PLC8', 'DB2100'],
      }],
    })
    let finishBranch!: (value: any) => void
    const branch = vi.spyOn(api, 'browseOpcUaBranch').mockReturnValue(new Promise(resolve => { finishBranch = resolve }))
    const wrapper = mount(OpcUaTagBrowserDialog, {
      attachTo: document.body,
      props: {
        modelValue: false,
        connection,
        target: { name: 'Device', tags: [], groups: [] },
      },
      global: {
        plugins: [ElementPlus],
        mocks: { $t: (key: string) => key },
      },
    })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()

    document.querySelector<HTMLInputElement>('.el-tree-node .el-checkbox__original')!.click()
    await new Promise(resolve => setTimeout(resolve, 10))
    await nextTick()

    expect(branch).toHaveBeenCalledWith(expect.objectContaining({
      nodeId: 'ns=3;s=DB2100',
      pathSegments: ['PLC8', 'DB2100'],
    }))
    expect(document.body.textContent).toContain('已选范围')
    expect(document.body.textContent).toContain('读取中…')
    const remove = [...document.querySelectorAll<HTMLButtonElement>('button')]
      .find(button => button.textContent?.includes('移除'))
    expect(remove).toBeDefined()
    remove!.click()
    await nextTick()
    expect(document.body.textContent).not.toContain('已选范围')

    finishBranch({ tags: [{
      name: 'MESFlag', address: 'ns=3;s=DB2100.MESFlag', path: 'PLC8.DB2100',
      pathSegments: ['PLC8', 'DB2100'], dataType: 'int16', isArray: false,
    }], truncated: false, scannedNodeCount: 7, elapsedMilliseconds: 234 })
    await flushPromises()
    expect(document.body.textContent).not.toContain('MESFlag')
    expect(document.querySelector('.browse-progress')?.textContent).toContain('扫描节点 7')
    expect(document.querySelector('.browse-progress')?.textContent).toContain('服务器浏览耗时 234 ms')
    wrapper.unmount()
  })

  it('keeps previously selected lazy branches when checking a node from another branch', async () => {
    const nodeA = {
      id: 'node:ns=3;s=A', label: 'DB_A', path: 'PLC8.DB_A', sourceSegments: ['PLC8', 'DB_A'],
      folder: true, isLeaf: false, nodeId: 'ns=3;s=A', isVariable: false,
    }
    const nodeB = {
      id: 'node:ns=3;s=B', label: 'DB_B', path: 'PLC8.DB_B', sourceSegments: ['PLC8', 'DB_B'],
      folder: true, isLeaf: false, nodeId: 'ns=3;s=B', isVariable: false,
    }
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({
      nodes: [
        { nodeId: nodeA.nodeId, name: nodeA.label, isVariable: false, mayHaveChildren: true, pathSegments: nodeA.sourceSegments },
        { nodeId: nodeB.nodeId, name: nodeB.label, isVariable: false, mayHaveChildren: true, pathSegments: nodeB.sourceSegments },
      ],
    })
    vi.spyOn(api, 'browseOpcUaBranch').mockImplementation(async body => ({
      tags: [{
        name: `${body.name}_MESFlag`, address: `${body.nodeId}.MESFlag`, path: body.pathSegments.join('.'),
        pathSegments: body.pathSegments, dataType: 'int16', isArray: false,
      }],
      truncated: false,
    }))
    const wrapper = mount(OpcUaTagBrowserDialog, {
      attachTo: document.body,
      props: {
        modelValue: false,
        connection,
        target: { name: 'Device', tags: [], groups: [] },
      },
      global: {
        plugins: [ElementPlus],
        mocks: { $t: (key: string) => key },
      },
    })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()

    const tree = wrapper.findComponent({ name: 'ElTree' })
    tree.vm.$emit('check', nodeA, { checkedKeys: [nodeA.id] })
    await flushPromises()
    expect(document.querySelector('.selected-ranges-scroll')?.textContent).toContain('DB_A')

    // 懒加载树切到另一分支时，组件事件可能只返回当前挂载分支的 key。
    tree.vm.$emit('check', nodeB, { checkedKeys: [nodeB.id] })
    await flushPromises()

    const selectedRanges = document.querySelector('.selected-ranges-scroll')?.textContent || ''
    expect(selectedRanges).toContain('DB_A')
    expect(selectedRanges).toContain('DB_B')
    const actualPaths = [...document.querySelectorAll<HTMLElement>('.selected-range-path code')]
      .map(element => element.textContent)
    expect(actualPaths).toEqual(expect.arrayContaining(['PLC8.DB_A', 'PLC8.DB_B']))
    expect(document.querySelector('.selected-ranges-scroll')?.textContent).toContain('实际路径')
    wrapper.unmount()
  })

  it('does not show the rename editor for a lazy node that was only clicked but not checked', async () => {
    const node = {
      id: 'node:ns=3;s=DB|path:PLC8.DB', label: 'DB', path: 'PLC8.DB', sourceSegments: ['PLC8', 'DB'],
      folder: true, isLeaf: false, nodeId: 'ns=3;s=DB', isVariable: false,
    }
    vi.spyOn(api, 'browseOpcUaChildren').mockResolvedValue({
      nodes: [{
        nodeId: node.nodeId, name: node.label, isVariable: false,
        mayHaveChildren: true, pathSegments: node.sourceSegments,
      }],
    })
    const wrapper = mount(OpcUaTagBrowserDialog, {
      attachTo: document.body,
      props: {
        modelValue: false,
        connection,
        target: { name: 'Device', tags: [], groups: [] },
      },
      global: { plugins: [ElementPlus], mocks: { $t: (key: string) => key } },
    })
    await wrapper.setProps({ modelValue: true })
    await flushPromises()

    document.querySelector<HTMLElement>('.tree-line .label')!.click()
    await nextTick()

    expect(document.querySelector('.rename-editor .source-name')).toBeNull()
    wrapper.unmount()
  })
})
