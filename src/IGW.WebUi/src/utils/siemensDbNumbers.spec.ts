import { beforeEach, expect, it } from 'vitest'
import { findDbNumber, rememberDbNumber, rememberedDbNumber } from './siemensDbNumbers'

beforeEach(() => localStorage.clear())

it('finds the DB number of a block by name, ignoring case', () => {
  const blocks = [{ name: 'OP051_MES', number: 51 }, { name: 'ST430_Data Collection_A(数据收集)', number: 4306 }]
  expect(findDbNumber(blocks, 'st430_data collection_a(数据收集)')).toBe(4306)
  expect(findDbNumber(blocks, 'ST999')).toBeNull()
  expect(findDbNumber(undefined, 'ST430')).toBeNull()
})

it('remembers the number used for a block so the next import fills it in', () => {
  expect(rememberedDbNumber('ST430_Data Collection_A(数据收集)')).toBeNull()
  rememberDbNumber('ST430_Data Collection_A(数据收集)', 4306)
  expect(rememberedDbNumber(' ST430_DATA COLLECTION_A(数据收集) ')).toBe(4306)
  rememberDbNumber('ST430_Data Collection_A(数据收集)', 0)          // 无效的号不覆盖
  expect(rememberedDbNumber('ST430_Data Collection_A(数据收集)')).toBe(4306)
})
