import { describe, expect, it } from 'vitest'
import { exportKepJson, importKepJson } from './interop'

describe('IGW JSON configuration array round-trip', () => {
  it('preserves driver, multidimensional structure schema and MES tasks', () => {
    const array = {
      elementType: 'struct', dimensions: [2, 3], lowerBounds: [1, -2], elementStrideBytes: 8,
      struct: {
        id: 'Cell', name: 'Cell', sizeBytes: 8,
        fields: [{ name: 'IsNG', jsonName: 'isNg', dataType: 'int16', byteOffset: 0 }],
      },
    }
    const source: any = {
      name: 'Line', mesTasks: [{ id: 'flow-1', groupRef: 'PLC.DB1' }],
      channels: [{
        id: 'channel-1', name: 'TCP', driverId: 'Siemens.S7', settings: { networkAdapter: 'WLAN' },
        devices: [{
          id: 'device-1', name: 'PLC', endpoint: '192.168.1.2:102', settings: { model: 'S7-1500' }, tags: [],
          groups: [{ id: 'group-1', name: 'DB1', subGroups: [], tags: [{
            id: 'array-1', name: 'Cells', address: 'DB1.DBB0', dataType: 'struct', isArray: true,
            array, access: 'readWrite', scanRateMs: 100, metadata: { source: 'db' },
          }] }],
        }],
      }],
    }

    const imported = importKepJson(exportKepJson(source)).proj as any
    const tag = imported.channels[0].devices[0].groups[0].tags[0]
    expect(imported.channels[0].driverId).toBe('Siemens.S7')
    expect(imported.channels[0].settings.networkAdapter).toBe('WLAN')
    expect(imported.mesTasks).toEqual(source.mesTasks)
    expect(tag.id).toBe('array-1')
    expect(tag.array).toEqual(array)
    expect(tag.metadata).toEqual({ source: 'db' })
  })
})
