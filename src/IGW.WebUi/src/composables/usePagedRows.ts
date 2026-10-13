import { computed, ref, watch, type Ref } from 'vue'

/**
 * 大表格分页：几百台设备的表格一次全部渲染会生成上万个元素，每次定时刷新都整表重画导致页面卡顿。
 * 只渲染当前页；数据变少时自动退回最后一页，筛选条件变化时回到第一页。
 */
export function usePagedRows<T>(rows: Ref<readonly T[]>, options: { pageSize?: number; resetOn?: () => unknown } = {}) {
  const page = ref(1)
  const pageSize = ref(options.pageSize ?? 50)
  const total = computed(() => rows.value.length)
  const pageCount = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
  const pagedRows = computed(() => {
    const start = (Math.min(page.value, pageCount.value) - 1) * pageSize.value
    return rows.value.slice(start, start + pageSize.value)
  })
  watch(pageCount, (count) => { if (page.value > count) page.value = count })
  if (options.resetOn) watch(options.resetOn, () => { page.value = 1 })
  return { page, pageSize, total, pageCount, pagedRows }
}
