import { describe, expect, it } from 'vitest'
import * as lazyBrowse from './opcUaLazyBrowse'
import {
  browseOperationElapsed, browseResultMetrics,
  buildSelectedBranchRows,
  flattenSelectedBranchTags,
  isLazyBrowseRootNode,
  normalizeOpcUaPathSegments,
  toLazyBrowseTreeNode,
} from './opcUaLazyBrowse'

describe('OPC UA lazy browser state', () => {
  it('prepares every selected tag and conflict across cooperative browser yields', async () => {
    const prepareOpcUaImport = (lazyBrowse as any).prepareOpcUaImport
    expect(prepareOpcUaImport).toBeTypeOf('function')
    const shared = {
      name: 'Value', address: 'ns=3;s=PLC.DB1.Value', path: 'PLC.DB1',
      pathSegments: ['PLC', 'DB1'], dataType: 17, isArray: false, access: 2,
    }
    let yields = 0
    let settled = false

    const preparation = prepareOpcUaImport({
      selectedBranches: {
        parent: [shared, {
          name: 'State', address: 'ns=3;s=PLC.DB1.State', path: 'PLC.DB1',
          pathSegments: ['PLC', 'DB1'], dataType: 'int16', isArray: false,
        }],
        overlappingChild: [shared],
      },
      aliases: {
        rootGroupName: '',
        folderNames: { ['PLC\u0000DB1']: 'Process' },
        tagNames: { ['ns=3;s=PLC.DB1.State\u0000PLC\u0000DB1\u0000State']: 'Mode' },
      },
      target: {
        name: 'Device', groups: [{ name: 'Process', subGroups: [], tags: [{
          name: 'Mode', address: 'ns=3;s=Existing.Mode', access: 'read',
        }] }], tags: [],
      },
    }, {
      batchSize: 1,
      yieldControl: async () => { yields++ },
    })
    preparation.then(() => { settled = true })

    expect(settled).toBe(false)
    const result = await preparation

    expect(yields).toBeGreaterThan(2)
    expect(result.rawTags).toHaveLength(2)
    expect(result.sourceDeviceRoots).toEqual(['PLC'])
    expect(result.tags).toEqual(expect.arrayContaining([
      expect.objectContaining({ name: 'Value', groupPath: ['Process'], dataType: 'dtl', access: 'readWrite' }),
      expect.objectContaining({ name: 'Mode', groupPath: ['Process'], dataType: 'int16' }),
    ]))
    expect(result.preview.tags).toHaveLength(2)
    expect(result.preview.conflicts).toEqual([
      expect.objectContaining({ path: 'Process.Mode', kind: 'tag' }),
    ])
  })

  it('abandons cooperative preparation before obsolete selections can publish', async () => {
    const prepareOpcUaImport = (lazyBrowse as any).prepareOpcUaImport
    expect(prepareOpcUaImport).toBeTypeOf('function')
    let cancelled = false

    const result = await prepareOpcUaImport({
      selectedBranches: {
        selected: Array.from({ length: 20 }, (_, index) => ({
          name: `Tag${index}`, address: `ns=3;s=PLC.DB.Tag${index}`, path: 'PLC.DB',
          pathSegments: ['PLC', 'DB'], dataType: 'int16', isArray: false,
        })),
      },
      aliases: { rootGroupName: '', folderNames: {}, tagNames: {} },
      target: { name: 'Device', groups: [], tags: [] },
    }, {
      batchSize: 2,
      isCancelled: () => cancelled,
      yieldControl: async () => { cancelled = true },
    })

    expect(result).toBeNull()
  })

  it('indexes a large existing target cooperatively before checking imported conflicts', async () => {
    const existingTags = Array.from({ length: 5_000 }, (_, index) => ({
      name: `Existing${index}`,
      address: `ns=3;s=PLC.Process.Existing${index}`,
      access: 'read',
    }))
    let yields = 0

    const result = await lazyBrowse.prepareOpcUaImport({
      selectedBranches: { selected: [{
        name: 'Existing4999', address: 'ns=3;s=PLC.Process.Existing4999', path: 'PLC.Process',
        pathSegments: ['PLC', 'Process'], dataType: 'int16', isArray: false, access: 'read',
      }] },
      aliases: { rootGroupName: '', folderNames: {}, tagNames: {} },
      target: { name: 'Device', groups: [{ name: 'Process', subGroups: [], tags: existingTags }], tags: [] },
    }, {
      batchSize: 100,
      yieldControl: async () => { yields++ },
    })

    expect(yields).toBeGreaterThanOrEqual(50)
    expect(result?.preview).toEqual({ tags: [], conflicts: [], groupsToCreate: 0, skipped: 1 })
  })

  it('uses only returned metrics and freezes completed request elapsed time', () => {
    expect(browseResultMetrics({ tags: [{}, {}], scannedNodeCount: 8, elapsedMilliseconds: 240 }))
      .toEqual({ discoveredTagCount: 2, scannedNodeCount: 8, serverElapsedMilliseconds: 240 })
    expect(browseResultMetrics({ nodes: [{}] }).scannedNodeCount).toBeUndefined()
    expect(browseResultMetrics({ scannedNodeCount: -1, elapsedMilliseconds: NaN }).scannedNodeCount).toBeUndefined()
    expect(browseOperationElapsed({ id: 1, stage: '扫描', label: 'DB', startedAt: 1000 }, 2250)).toBe('1.25 秒')
    expect(browseOperationElapsed({ id: 1, stage: '扫描', label: 'DB', startedAt: 1000, finishedAt: 2250, failed: true }, 9999)).toBe('1.25 秒')
  })

  it('maps a direct server child without inventing descendants', () => {
    const node = toLazyBrowseTreeNode({
      nodeId: 'ns=3;s=DB2100',
      name: 'DB2100',
      isVariable: false,
      mayHaveChildren: true,
      pathSegments: ['PLC8', 'DataBlocksGlobal', 'DB2100'],
    })

    expect(node).toEqual({
      id: 'node:ns=3;s=DB2100|path:["PLC8","DataBlocksGlobal","DB2100"]',
      label: 'DB2100',
      path: 'PLC8.DataBlocksGlobal.DB2100',
      sourceSegments: ['PLC8', 'DataBlocksGlobal', 'DB2100'],
      folder: true,
      isLeaf: false,
      nodeId: 'ns=3;s=DB2100',
      isVariable: false,
    })
    expect('children' in node).toBe(false)
  })

  it('combines selected branches by address so overlapping selections do not duplicate tags', () => {
    const a = { name: 'MESFlag', address: 'ns=3;s=DB1.MESFlag', path: 'PLC8.DB1', pathSegments: ['PLC8', 'DB1'], dataType: 'int16', isArray: false }
    const b = { name: 'PLCFlag', address: 'ns=3;s=DB1.PLCFlag', path: 'PLC8.DB1', pathSegments: ['PLC8', 'DB1'], dataType: 'int16', isArray: false }

    const result = flattenSelectedBranchTags({ parent: [a, b], child: [a] })

    expect(result.map(tag => tag.address)).toEqual(['ns=3;s=DB1.MESFlag', 'ns=3;s=DB1.PLCFlag'])
  })

  it('recognizes the Element Plus virtual root and never treats its data array as a browse node', () => {
    expect(isLazyBrowseRootNode({ level: 0, data: [{ nodeId: 'root-child' }] })).toBe(true)
    expect(isLazyBrowseRootNode({ data: [{ nodeId: 'root-child' }] })).toBe(true)
    expect(isLazyBrowseRootNode({ level: 1, data: { nodeId: 'root-child' } })).toBe(false)
  })

  it('normalizes missing and malformed path segments without throwing', () => {
    expect(normalizeOpcUaPathSegments(undefined, 'PLC8')).toEqual(['PLC8'])
    expect(normalizeOpcUaPathSegments(null, 'PLC8')).toEqual(['PLC8'])
    expect(normalizeOpcUaPathSegments({ bad: true }, 'PLC8')).toEqual(['PLC8'])
    expect(normalizeOpcUaPathSegments('DeviceSet', 'PLC8')).toEqual(['DeviceSet'])
    expect(normalizeOpcUaPathSegments(['DeviceSet', null, 'PLC8'] as any, 'fallback')).toEqual(['DeviceSet', 'PLC8'])
  })

  it('maps a field response with absent pathSegments to a safe tree node', () => {
    expect(() => toLazyBrowseTreeNode({
      nodeId: 'ns=3;s=PLC',
      name: 'PLC8',
      isVariable: false,
      mayHaveChildren: true,
      pathSegments: undefined as any,
    })).not.toThrow()
    expect(toLazyBrowseTreeNode({
      nodeId: 'ns=3;s=PLC',
      name: 'PLC8',
      isVariable: false,
      mayHaveChildren: true,
      pathSegments: undefined as any,
    }).sourceSegments).toEqual(['PLC8'])
  })

  it('uses NodeId and actual browse path together as the lazy tree identity', () => {
    const first = toLazyBrowseTreeNode({
      nodeId: 'ns=3;s=PLC', name: 'PLC', isVariable: false, mayHaveChildren: true,
      pathSegments: ['DeviceSet', 'PLC'],
    })
    const second = toLazyBrowseTreeNode({
      nodeId: 'ns=3;s=PLC', name: 'PLC', isVariable: false, mayHaveChildren: true,
      pathSegments: ['L1_PLC08', 'PLC'],
    })

    expect(first.id).not.toBe(second.id)
  })

  it('keeps the same NodeId when it is imported through different browse paths', () => {
    const shared = {
      name: 'MESFlag', address: 'ns=3;s=Shared.MESFlag', dataType: 'int16', isArray: false,
    }
    const result = flattenSelectedBranchTags({
      first: [{ ...shared, path: 'DeviceSet.PLC.DB', pathSegments: ['DeviceSet', 'PLC', 'DB'] }],
      second: [{ ...shared, path: 'L1_PLC08.PLC.DB', pathSegments: ['L1_PLC08', 'PLC', 'DB'] }],
    })

    expect(result).toHaveLength(2)
  })

  it('builds immediate removable selection rows before branch scanning finishes', () => {
    const node = toLazyBrowseTreeNode({
      nodeId: 'ns=3;s=DB2100', name: 'DB2100', isVariable: false,
      mayHaveChildren: true, pathSegments: ['PLC8', 'DB2100'],
    })
    const rows = buildSelectedBranchRows(
      [node.id],
      { [node.id]: node },
      {},
      [node.id],
      {},
    )

    expect(rows).toEqual([{
      id: node.id,
      label: 'DB2100',
      path: 'PLC8.DB2100',
      status: 'loading',
      tagCount: 0,
      error: '',
    }])
  })
})
