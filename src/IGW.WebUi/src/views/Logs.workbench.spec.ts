import { flushPromises, shallowMount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import { api } from '../api'
import { createMemoryHistory, createRouter } from 'vue-router'
import { logCenterLink } from '../utils/logCenter'
import Logs from './Logs.vue'

vi.mock('../api', () => ({ api: {
  logCenterStatus: vi.fn(), logCenterLogs: vi.fn(), updateLogRetention: vi.fn(), changeLogStorage: vi.fn(),
  logPayloadExchange: vi.fn(), downloadLogPayload: vi.fn(), exportLogs: vi.fn(),
  logCleanupStatus: vi.fn(), startLogCleanup: vi.fn(), logFilterOptions: vi.fn(),
} }))

const response = {
  items: [{ timestamp: '2026-09-13T08:00:00Z', level: 'Information', category: 'collection', message: '采集批次完成',
    traceId: 'abc', payloadId: null, attributes: { igw_module: 'acquisition', igw_feature: 'plc_scan',
      igw_task_name: 'OP140 出站', igw_task_id: 'task-140', igw_device: 'PLC8.OP140', igw_duration_ms: 12 } }],
  total: 1, tookMs: 3,
  histogram: [{ start: '2026-09-13T08:00:00Z', total: 1, warnings: 0, errors: 0 }],
  devices: [{ device: 'PLC8.OP140', status: 'online', timestamp: '2026-09-13T08:00:00Z' }], outages: [],
}
const status = { available: true, state: 'running', port: 28020, retentionDays: 30, storageDirectory: 'D:\\IGW-Logs',
  spool: { pending: 0, failed: 0, dropped: 0 }, migrationStatus: null, migrationTotalBytes: 0, migrationCopiedBytes: 0, migrationFailure: null }

const options = { global: { stubs: [
  'ElButton', 'ElDialog', 'ElInput', 'ElInputNumber', 'ElSelect', 'ElOption', 'ElDatePicker', 'ElAlert', 'ElPagination',
  'RouterLink',
] } }

describe('IGW log workbench', () => {
  it('exports structured QuestDB logs by default and lets operators choose CSV', async () => {
    vi.mocked(api.logCenterStatus).mockResolvedValue({ ...status, engine: 'questdb', historyImported: true } as any)
    const wrapper = shallowMount(Logs, options); await flushPromises()
    const vm = wrapper.vm as any
    expect(vm.exportFormat).toBe('jsonl')
    expect(vm.exportFilters()).toMatchObject({ category: 'all' })
    vm.exportFormat = 'csv'
    expect(vm.exportFormat).toBe('csv')
    wrapper.unmount()
  })
  beforeEach(() => {
    vi.mocked(api.logCenterStatus).mockReset().mockResolvedValue(status as any)
    vi.mocked(api.logCenterLogs).mockReset().mockResolvedValue(response as any)
    vi.mocked(api.logFilterOptions).mockReset().mockResolvedValue({
      protocols: [{ id: 'Siemens.S7', name: '西门子 S7' }], channels: [{ name: 'PLC8', protocol: 'Siemens.S7' }],
      devices: [{ channel: 'PLC8', name: 'OP140', path: 'PLC8.OP140' }],
      groups: [{ name: 'OP140 出站', path: 'PLC8.OP140.Exit', channel: 'PLC8', device: 'PLC8.OP140', taskId: 'task-140', taskName: 'OP140 出站' }],
    })
    vi.mocked(api.logPayloadExchange).mockReset().mockResolvedValue({ id: 'payload-1', request: '{"sn":"A1"}',
      response: '{"code":0}', requestTruncated: false, responseTruncated: false, split: true })
  })

  it('shows the scope rail, status strip, trend, corridor and pagination', async () => {
    const wrapper = shallowMount(Logs, options)
    await flushPromises()
    expect(wrapper.find('[data-testid="log-workbench"]').exists()).toBe(true)
    expect(wrapper.findComponent({ name: 'LogScopeRail' }).exists()).toBe(true)
    expect(wrapper.findComponent({ name: 'LogTrendPanel' }).exists()).toBe(true)
    expect(wrapper.findComponent({ name: 'LogTimeCorridor' }).props('items')).toHaveLength(1)
    expect(wrapper.text()).not.toContain('OpenObserve')
    expect(wrapper.text()).toContain('D:\\IGW-Logs')
    expect(wrapper.find('[data-testid="result-pagination"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('applies draft filters as chips and preserves last results when a refresh fails', async () => {
    const wrapper = shallowMount(Logs, options)
    await flushPromises()
    const vm = wrapper.vm as any
    vm.draft.channel = 'PLC8'
    vm.draft.device = 'PLC8.OP140'
    await vm.applyFilters()
    expect(wrapper.findAll('[data-testid="active-filter-chip"]').map(chip => chip.text().replace(/\s*×$/, ''))).toEqual(['通道: PLC8', '设备: OP140'])
    vi.mocked(api.logCenterLogs).mockRejectedValueOnce(new Error('offline'))
    await vm.reload('manual')
    expect(vm.rows).toHaveLength(1)
    expect(wrapper.find('[data-testid="stale-results"]').exists()).toBe(true)
    wrapper.unmount()
  })

  it('sends a project context path even when the frontend task catalogue is not loaded', async () => {
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/logs', component: Logs }] })
    await router.push(logCenterLink({ path: 'PLC6.D1.OP051.Sub' }))
    await router.isReady()
    vi.mocked(api.logFilterOptions).mockResolvedValue({ protocols: [], channels: [], devices: [], groups: [] })
    const wrapper = shallowMount(Logs, { global: { ...options.global, plugins: [router] } })
    try {
      await flushPromises()
      const query = vi.mocked(api.logCenterLogs).mock.calls[0][0]
      expect(query).toMatchObject({ category: 'all', path: 'PLC6.D1.OP051.Sub' })
      expect(query).not.toHaveProperty('taskId')
      expect(query).not.toHaveProperty('task')
      expect(Date.parse(String(query.to)) - Date.parse(String(query.from))).toBe(86_400_000)
      expect(wrapper.find('[data-testid="active-filter-chip"]').text()).toContain('PLC6.D1.OP051.Sub')
    } finally { wrapper.unmount() }
  })

  it('applies and clears a partial URL without retaining an earlier task filter', async () => {
    const wrapper = shallowMount(Logs, options)
    try {
      await flushPromises()
      const vm = wrapper.vm as any
      Object.assign(vm.draft, { group: '/api/report', task: '' })
      await vm.applyFilters()
      const calls = vi.mocked(api.logCenterLogs).mock.calls
      expect(calls[calls.length - 1][0]).toMatchObject({ taskOrInterface: '/api/report' })
      expect(calls[calls.length - 1][0]).not.toHaveProperty('task')
      expect(wrapper.find('[data-testid="active-filter-chip"]').text()).toContain('标记组 / 接口 URL: /api/report')
      await vm.removeChip('group')
      expect(calls[calls.length - 1][0]).not.toHaveProperty('taskOrInterface')
      expect(calls[calls.length - 1][0]).not.toHaveProperty('task')
      expect(wrapper.findAll('[data-testid="active-filter-chip"]')).toHaveLength(0)
    } finally { wrapper.unmount() }
  })

  it('opens structured details from a corridor event', async () => {
    const wrapper = shallowMount(Logs, options)
    await flushPromises()
    wrapper.findComponent({ name: 'LogTimeCorridor' }).vm.$emit('select', response.items[0])
    await wrapper.vm.$nextTick()
    expect(wrapper.findComponent({ name: 'LogDetailDrawer' }).props('entry')).toMatchObject({ traceId: 'abc' })
    wrapper.unmount()
  })

  it('loads the actual interface exchange when opening an entry with a payload reference', async () => {
    const wrapper = shallowMount(Logs, options)
    await flushPromises()
    const entry = { ...response.items[0], category: 'interface', payloadId: 'payload-1' }
    wrapper.findComponent({ name: 'LogTimeCorridor' }).vm.$emit('select', entry)
    await flushPromises()
    expect(api.logPayloadExchange).toHaveBeenCalledWith('payload-1')
    expect(wrapper.findComponent({ name: 'LogDetailDrawer' }).props('payload')).toMatchObject({ request: '{"sn":"A1"}', response: '{"code":0}' })
    wrapper.unmount()
  })

  it('explains a missing log-center endpoint as a backend version mismatch', async () => {
    vi.mocked(api.logCenterLogs).mockRejectedValueOnce({
      response: { status: 404, data: {} },
      message: 'Request failed with status code 404',
    })

    const wrapper = shallowMount(Logs, options)
    await flushPromises()

    expect(wrapper.find('[data-testid="query-error"]').text()).toContain('当前 IGW 后端未提供新版日志接口')
    expect(wrapper.text()).not.toContain('Request failed with status code 404')
    wrapper.unmount()
  })

  it('searches every disk when the cleanup dialog opens and cleans only the ticked items', async () => {
    const idle = { state: 'Idle', residues: [], deletedFiles: 0, deletedBytes: 0, skipped: [] }
    const found = { ...idle, residues: [
      { path: 'E:\IGW\igw-logs', kind: '其它位置的旧日志' },
      { path: 'C:\ProgramData\IDAP\logs', kind: '旧版文本日志', filePattern: 'igw-*.log' },
    ] }
    vi.mocked(api.logCleanupStatus).mockReset()
      .mockResolvedValueOnce(idle as any)                           // 页面加载：不查找
      .mockResolvedValueOnce({ ...idle, scanning: true } as any)    // 打开窗口：开始查找
      .mockResolvedValue(found as any)
    vi.mocked(api.startLogCleanup).mockReset().mockResolvedValue({ ...idle, state: 'Running' } as any)
    vi.useFakeTimers()
    try {
      const wrapper = shallowMount(Logs, options)
      await flushPromises()
      expect(vi.mocked(api.logCleanupStatus).mock.calls[0]).toEqual([false])
      const state = (wrapper.vm.$ as any).setupState
      await state.openCleanup()
      expect(vi.mocked(api.logCleanupStatus).mock.calls[1]).toEqual([true])
      expect(state.cleanupScanning).toBe(true)
      await vi.advanceTimersByTimeAsync(2000)
      expect(state.cleanupScanning).toBe(false)
      expect(state.cleanupChecked).toEqual(['E:\IGW\igw-logs', 'C:\ProgramData\IDAP\logs'])
      state.cleanupChecked = ['E:\IGW\igw-logs']                 // 不是网关的那项取消勾选
      await state.startCleanup()
      expect(api.startLogCleanup).toHaveBeenCalledWith(['E:\IGW\igw-logs'])
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })

  it('exports the current filters as a zip without paging parameters', async () => {
    vi.mocked(api.exportLogs).mockReset().mockResolvedValue({ blob: new Blob(['zip']), fileName: 'igw-logs.zip', entries: 12, truncated: false })
    const createObjectURL = vi.fn(() => 'blob:igw'), revokeObjectURL = vi.fn()
    Object.assign(URL, { createObjectURL, revokeObjectURL })
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {})
    try {
      const wrapper = shallowMount(Logs, options)
      await flushPromises()
      const state = (wrapper.vm.$ as any).setupState
      state.exportPayloads = false
      await state.runExport()
      const [params, includePayloads] = vi.mocked(api.exportLogs).mock.calls[0]
      expect(includePayloads).toBe(false)
      expect(params).toHaveProperty('from')
      expect(params).toHaveProperty('to')
      expect(params).not.toHaveProperty('page')
      expect(params).not.toHaveProperty('pageSize')
      expect(click).toHaveBeenCalledTimes(1)
      expect(state.exporting).toBe(false)
    } finally { click.mockRestore() }
  })

  it('refreshes in the background without the busy banner and never cancels a query the user started', async () => {
    vi.useFakeTimers()
    try {
      const wrapper = shallowMount(Logs, options)
      await flushPromises()
      const calls = vi.mocked(api.logCenterLogs).mock.calls
      expect(calls[0][2]).toBeUndefined()                         // 打开页面的第一次查询：前台
      expect(vi.mocked(api.logCenterStatus).mock.calls[0][1]).toBe(true)   // 状态轮询：后台

      await vi.advanceTimersByTimeAsync(10_000)
      expect(calls[calls.length - 1][2]).toEqual({ background: true })   // 每 10 秒自动刷新：后台，不出「正在…」提示

      // 用户点了查询，网关还没返回(现场大范围查询要十几秒)：自动刷新不能把它取消掉
      let finish!: (value: unknown) => void
      vi.mocked(api.logCenterLogs).mockImplementationOnce(() => new Promise(resolve => { finish = resolve }) as any)
      const vm = wrapper.vm as any
      const query = vm.applyFilters()
      const before = calls.length
      await vi.advanceTimersByTimeAsync(25_000)
      expect(calls.length).toBe(before)
      const signal = calls[before - 1][1] as AbortSignal
      expect(signal.aborted).toBe(false)
      finish(response); await query
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })

  it('keeps unsubmitted filter edits when the live window refreshes, then queries with all of them combined', async () => {
    vi.useFakeTimers()
    const lastQuery = () => { const calls = vi.mocked(api.logCenterLogs).mock.calls; return calls[calls.length - 1][0] }
    try {
      const wrapper = shallowMount(Logs, options)
      await flushPromises()
      const vm = wrapper.vm as any
      // 先组合好几个条件(还没点「查询日志」)，期间到了 10 秒自动刷新
      Object.assign(vm.draft, { protocol: 'Siemens.S7', channel: 'PLC8', device: 'PLC8.OP140', group: 'OP140 出站', task: 'task-140' })
      await vi.advanceTimersByTimeAsync(10_000)
      expect(vm.draft).toMatchObject({ protocol: 'Siemens.S7', channel: 'PLC8', device: 'PLC8.OP140', group: 'OP140 出站', task: 'task-140' })

      await vm.applyFilters()
      expect(lastQuery()).toMatchObject({ task: 'task-140' })   // 标记组最具体：按它绑定的任务查
      expect(wrapper.findAll('[data-testid="active-filter-chip"]').map(chip => chip.text().replace(/\s*×$/, '')))
        .toEqual(['协议: 西门子 S7', '通道: PLC8', '设备: OP140', '标记组: OP140 出站'])
      // 之后的自动刷新沿用这组条件，只把时间窗口滑到现在
      await vi.advanceTimersByTimeAsync(10_000)
      expect(lastQuery()).toMatchObject({ task: 'task-140' })
      wrapper.unmount()
    } finally { vi.useRealTimers() }
  })
})
