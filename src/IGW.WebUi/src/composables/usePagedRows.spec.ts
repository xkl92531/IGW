import { describe, expect, it } from 'vitest'
import { nextTick, ref } from 'vue'
import { usePagedRows } from './usePagedRows'

describe('usePagedRows', () => {
  it('只返回当前页，数据变少时退回最后一页，筛选变化时回到第一页', async () => {
    const rows = ref(Array.from({ length: 380 }, (_, index) => index))
    const filter = ref('')
    const paged = usePagedRows(rows, { resetOn: () => filter.value })

    expect(paged.pagedRows.value).toHaveLength(50)
    paged.page.value = 8
    expect(paged.pagedRows.value).toEqual(Array.from({ length: 30 }, (_, index) => 350 + index))

    rows.value = rows.value.slice(0, 120)
    await nextTick()
    expect(paged.page.value).toBe(3)

    filter.value = 'PLC1'
    await nextTick()
    expect(paged.page.value).toBe(1)
  })
})
