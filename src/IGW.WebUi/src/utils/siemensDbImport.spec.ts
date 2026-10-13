import { describe, expect, it } from 'vitest'
import { mergeSiemensDbImport } from './siemensDbImport'
import { assignLazyIds, buildLazyMutations, captureBaseline, createLazyState } from '../features/project/lazyProject'

const tag = (name: string, extra: any = {}) => ({
  id: '', name, address: 'DB1.DBW0', dataType: 'int16', isArray: false, array: null,
  stringLength: null, description: '', metadata: null, access: 'readWrite', scanRateMs: 100, ...extra,
})
const group = (name: string, tags: any[] = [], subGroups: any[] = [], extra: any = {}) => ({
  id: '', name, description: '', tags, subGroups, ...extra,
})
const array = (extra: any = {}) => ({
  elementType: 'int16', dimensions: [4], lowerBounds: [1], elementStrideBytes: 2,
  elementStrideBits: 0, stringLength: null, struct: null, sourceShape: 'ARRAY[1..4] OF INT', ...extra,
})

describe('Siemens DB-specific upsert field ownership', () => {
  it('creates a new DB once with fresh entity IDs and leaves other DBs alone', () => {
    const other = group('DB2', [tag('Keep', { id: 'keep' })], [], { id: 'other' })
    const device = { groups: [other], tags: [] }
    const root = group('root', [tag('New')], [group('Nested', [tag('Member')])])
    const original = JSON.stringify(root)
    const first = mergeSiemensDbImport(device, 'device', 'DB1', root)
    expect(first).toMatchObject({ added: 2, updated: 0, group: { name: 'DB1' } })
    expect(device.groups[0]).toBe(other)
    const ids = [first.group.id, first.group.tags[0].id, first.group.subGroups[0].id, first.group.subGroups[0].tags[0].id]
    expect(ids.every(Boolean)).toBe(true)
    expect(new Set(ids).size).toBe(4)
    const snapshot = JSON.stringify(device)
    expect(mergeSiemensDbImport(device, 'device', 'db1', root)).toMatchObject({ added: 0, updated: 2 })
    expect(JSON.stringify(device)).toBe(snapshot)
    expect(JSON.stringify(root)).toBe(original)
  })

  it('matches the full relative name path, not same-named points in another subfolder', () => {
    const outside = group('DB1', [tag('Value', { id: 'outside' })], [], { id: 'outside-db' })
    const inside = group('DB1', [tag('Value', { id: 'inside' })], [], { id: 'inside-db' })
    const folder = group('Folder', [], [inside], { id: 'folder' })
    const device = { groups: [outside, folder] }
    mergeSiemensDbImport(folder, 'group', 'DB1', group('root', [tag('Value', { address: 'DB1.DBW8' })]))
    expect(outside.tags[0]).toMatchObject({ id: 'outside', address: 'DB1.DBW0' })
    expect(inside.tags[0]).toMatchObject({ id: 'inside', address: 'DB1.DBW8' })
    expect(device.groups).toHaveLength(2)
    expect(folder.subGroups).toHaveLength(1)
  })

  it('updates array shape, source address and string capacity without clearing site metadata or access', () => {
    const old = tag('Text', {
      id: 'stable', dataType: 'string', stringLength: 10, textEncoding: 'utf8', access: 'read',
      metadata: { custom: 'site', 'array.sourceAddress': 'old', 'array.sourceFormat': 'old' },
    })
    const db = group('DB1', [old], [], { id: 'g1' })
    const source = tag('Text', {
      dataType: 'wString', address: 'DB1.DBB6', isArray: true, stringLength: 40,
      array: array({ elementType: 'wString', stringLength: 40, elementStrideBytes: 84 }),
      metadata: { 'array.sourceAddress': 'DB1.DBB6', 'array.sourceFormat': 'Siemens DB' },
    })
    mergeSiemensDbImport(db, 'group', 'DB1', group('root', [source]))
    expect(db.tags[0]).toMatchObject({
      id: 'stable', access: 'read', dataType: 'wString', isArray: true, stringLength: 40, textEncoding: 'utf8',
      array: { dimensions: [4], lowerBounds: [1], stringLength: 40, elementStrideBytes: 84 },
      metadata: { custom: 'site', 'array.sourceAddress': 'DB1.DBB6', 'array.sourceFormat': 'Siemens DB' },
    })
    const once = JSON.stringify(db)
    mergeSiemensDbImport(db, 'group', 'DB1', group('root', [source]))
    expect(JSON.stringify(db)).toBe(once)
  })

  it('keeps site JSON field names and schema IDs inside structured arrays while updating their DB layout', () => {
    const oldSchema = { id: 'stable-schema', name: 'Row', sizeBytes: 2, fields: [
      { name: 'Value', jsonName: 'siteValue', dataType: 'int16', byteOffset: 0, bitOffset: -1,
        array: array({ struct: { id: 'nested-schema', name: 'Nested', sizeBytes: 2, fields: [
          { name: 'Count', jsonName: 'siteCount', dataType: 'int16', byteOffset: 0 },
        ] } }),
      },
    ] }
    const db = group('DB1', [tag('Rows', { id: 'rows', isArray: true, array: array({ struct: oldSchema }) })])
    const sourceSchema = { id: 'parser-schema', name: 'Row', sizeBytes: 8, fields: [
      { name: 'value', jsonName: null, dataType: 'int32', byteOffset: 4, bitOffset: -1,
        array: array({ struct: { id: 'parser-nested', name: 'Nested', sizeBytes: 4, fields: [
          { name: 'Count', jsonName: null, dataType: 'int32', byteOffset: 0 },
        ] } }),
      },
      { name: 'Added', jsonName: null, dataType: 'int16', byteOffset: 6 },
    ] }
    const source = group('root', [tag('Rows', { isArray: true, array: array({ struct: sourceSchema }) })])
    mergeSiemensDbImport(db, 'group', 'DB1', source)
    expect(db.tags[0].array.struct).toMatchObject({
      id: 'stable-schema', sizeBytes: 8, fields: [
        { name: 'value', jsonName: 'siteValue', dataType: 'int32', byteOffset: 4,
          array: { struct: { id: 'nested-schema', sizeBytes: 4, fields: [{ name: 'Count', jsonName: 'siteCount', dataType: 'int32' }] } },
        },
        { name: 'Added', jsonName: null },
      ],
    })
    const once = JSON.stringify(db)
    mergeSiemensDbImport(db, 'group', 'DB1', source)
    expect(JSON.stringify(db)).toBe(once)
  })

  it.each([
    group('root', [tag('x'), tag('X')]),
    group('root', [tag('x')], [group('X', [tag('child')])]),
  ])('rejects duplicate/colliding source paths atomically', root => {
    const db = group('DB1', [tag('Keep', { id: 'keep' })])
    const before = JSON.stringify(db)
    expect(() => mergeSiemensDbImport(db, 'group', 'DB1', root)).toThrow('路径冲突')
    expect(JSON.stringify(db)).toBe(before)
  })

  it.each([false, true])('is idempotent with the API omit-null serialization (save/reopen=%s)', reopen => {
    // ProjectStore.Options uses WhenWritingNull for both parser previews and saved project responses.
    const wire = <T,>(value: T): T => JSON.parse(JSON.stringify(value, (_key, field) => field === null ? undefined : field))
    let project: any = wire({ channels: [{ id: 'c', devices: [{ id: 'd', endpoint: 'offline', tags: [], groups: [
      group('DB1', [tag('Value', { id: 'value', isArray: true, array: array(), stringLength: 20, description: 'old', metadata: { 'array.sourceFormat': 'Siemens DB' } })], [], { id: 'db' }),
    ] }] }], mesUpload: { tasks: [] } })
    const root = wire(group('root', [tag('Value'), tag('Added', { description: null })]))
    const state = createLazyState()
    captureBaseline(state, project)
    mergeSiemensDbImport(project.channels[0].devices[0], 'device', 'DB1', root)
    assignLazyIds(state, project)
    expect(buildLazyMutations(state, project)).toHaveLength(2)
    const tags = project.channels[0].devices[0].groups[0].tags
    expect(tags[0].array).toBeUndefined()
    expect(tags[0].stringLength).toBeUndefined()
    expect(tags[0].metadata).toBeUndefined()
    expect(tags[0].description).toBe('')
    expect(tags[1].description).toBeUndefined()
    if (reopen) project = wire(project)
    captureBaseline(state, project)
    const before = JSON.stringify(project)
    mergeSiemensDbImport(project.channels[0].devices[0], 'device', 'DB1', root)
    assignLazyIds(state, project)
    expect(buildLazyMutations(state, project)).toEqual([])
    expect(JSON.stringify(project)).toBe(before)
  })

  it('ignores an empty parsed tree without adding or editing a group', () => {
    const device = { groups: [] }
    expect(mergeSiemensDbImport(device, 'device', 'DB1', group('root'))).toMatchObject({ added: 0, updated: 0 })
    expect(device.groups).toEqual([])
  })
})
