import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import ScanTimingPanel from './ScanTimingPanel.vue'
import { api } from '../api'
import ElementPlus from 'element-plus'
import { i18n } from '../i18n'

vi.mock('../api', () => ({ api: {
  scanTimings: vi.fn(),
  acquisitionDiagnosticsExport: vi.fn(),
  values: vi.fn(),
}, INVALID_ACQUISITION_DIAGNOSTICS_ZIP: 'invalid-acquisition-diagnostics-zip' }))

const snapshot = {
  devices: [
    { name: 'Line1.Plc1', mode: 'polling', configuredMinPeriodMs: 100, configuredMaxPeriodMs: 500, pollingTags: 2, subscribedTags: 0, plannedBlocks: 1, lastScanMs: 12, lastScanAt: '2026-09-05T04:00:00Z', actualAveragePeriodMs: 105, actualP95PeriodMs: 120, overduePeriods: 1, samplesPerSecond: 20 },
    { name: 'Line2.Plc2', mode: 'subscription', configuredMinPeriodMs: 0, configuredMaxPeriodMs: 0, pollingTags: 0, subscribedTags: 3, plannedBlocks: 0, lastScanMs: 0, lastScanAt: null, actualAveragePeriodMs: 90, actualP95PeriodMs: 100, overduePeriods: 0, samplesPerSecond: 15 },
  ],
  device: 'Line1.Plc1',
  records: [{ sequence: 3, startedAt: '2026-09-05T04:00:00Z', blockId: 'b1', configuredPeriodMs: 100, actualIntervalMs: 106, totalMs: 12, driverMs: 9, localMs: 3, tagCount: 2, sampleCount: 2, badCount: 0, status: 'good', error: null, missedPeriods: 0, tagIds: ['Line1.Plc1.A', 'Line1.Plc1.B'] }],
  capacity: 200,
}

const layeredSnapshot = {
  ...snapshot,
  devices: [{
    ...snapshot.devices[0],
    queueP95Ms: 6.5,
    protocolRequestP95Ms: 18.5,
    protocolRequestMaxMs: 25,
    localP95Ms: 3.5,
    networkP95RttMs: 8.4,
    networkLossPercent: 12.5,
    latestAttribution: '网络链路疑似',
    latestAttributionConfidence: 'suspected',
  }],
  records: [{
    ...snapshot.records[0],
    protocol: { requests: 4, failedRequests: 1, cancelledRequests: 2, requestTotalMs: 45.25, requestMaxMs: 20.5, queueMs: 7.75 },
    network: {
      from: '2026-09-05T03:59:30Z', to: '2026-09-05T04:00:00Z', count: 8, successCount: 7, failedCount: 1,
      latestRttMs: 9.2, p95RttMs: 10.4, baselineP95RttMs: 3.1, lossPercent: 12.5,
      status: 'degraded', error: null, routeVerified: true,
      sourceAddress: '192.168.1.20', remoteAddress: '192.168.1.30',
    },
    attribution: {
      code: 'network-suspected', label: '网络链路疑似', confidence: 'suspected',
      evidence: ['request-max=20.5 ms; queue=7.75 ms', 'loss=12.5%'],
      factors: ['request-split-or-retry'],
    },
    sameSessionRetries: 2,
    batchDownshifts: 1,
  }],
}

describe('ScanTimingPanel', () => {
  it('distinguishes 71 points, one logical batch, five physical requests and independent device sessions', async () => {
    const requests = Array.from({ length: 5 }, (_, i) => ({ descriptor: {
      operation: 'read', scope: `DB1 bytes ${i * 100}-${i * 100 + 99}`, tagCount: i === 4 ? 15 : 14,
      payloadBytes: 100, gapBytes: 2 }, elapsedMs: 3.5, failed: false, cancelled: false }))
    const first = { ...snapshot.devices[0], pollingTags: 71, logicalBatchCount: 1, latestProtocolRequestCount: 5,
      deviceSessionFingerprint: 'aabbcc-p', sessionGeneration: 2, sameEndpointDeviceCount: 2, sameEndpointSessionCount: 2,
      knownSessionLimit: 3, sessionLimitSource: 'fixture server evidence' }
    vi.mocked(api.scanTimings).mockResolvedValue({ ...snapshot, devices: [first, { ...first, name: 'Line1.Other', deviceSessionFingerprint: 'ddeeff-p', sessionGeneration: 1 }],
      records: [{ ...snapshot.records[0], tagCount: 71, logicalBatchCount: 1, latestProtocolRequestCount: 5,
        protocolRequestSamples: requests, omittedProtocolRequestSamples: 0, deviceSessionFingerprint: 'aabbcc-p', sessionGeneration: 2 }] } as any)
    const wrapper = mountPanel()
    await flushPromises()
    expect(wrapper.text()).toContain('轮询点位 71')
    expect(wrapper.text()).toContain('逻辑批次 1')
    expect(wrapper.text()).toContain('实际协议请求 5')
    expect(wrapper.text()).not.toContain('轮询 / 订阅')
    expect(wrapper.text()).toContain('aabbcc-p / 2')
    expect(wrapper.text()).toContain('ddeeff-p / 1')
    expect(wrapper.text()).toContain('活动设备会话 2')
    expect(wrapper.text()).toContain('活动设备会话接近已知上限')
    expect(wrapper.text()).toContain('fixture server evidence')
    await wrapper.find('.el-table__expand-icon').trigger('click')
    expect(wrapper.findAll('.request-samples tbody tr')).toHaveLength(5)
    expect(wrapper.find('.request-samples').text()).toContain('DB1 bytes 0-99')
    expect(wrapper.find('.request-samples').text()).toContain('3.5 ms')
    expect(wrapper.find('.request-samples').text()).toContain('有效载荷字节')
    expect(wrapper.find('.request-samples').text()).toContain('间隙字节')
    await vi.advanceTimersByTimeAsync(4000)
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
    expect(api.values).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('keeps opaque counts and negative descriptor fields unknown, and never warns without a known session limit', async () => {
    vi.mocked(api.scanTimings).mockResolvedValue({ ...snapshot,
      devices: [{ ...snapshot.devices[0], latestProtocolRequestCount: null, sameEndpointSessionCount: 100, knownSessionLimit: null }],
      records: [{ ...snapshot.records[0], latestProtocolRequestCount: null,
        protocol: { requests: 1, physicalRequests: null, opaqueOperations: 1, failedRequests: 0, cancelledRequests: 0, requestTotalMs: 2, requestMaxMs: 2, queueMs: 0 },
        protocolRequestSamples: [{ descriptor: { operation: 'read', scope: 'opaque', tagCount: -1, payloadBytes: -1, gapBytes: -1 }, elapsedMs: 2, failed: false, cancelled: false }] }] } as any)
    const wrapper = mountPanel()
    await flushPromises()
    expect(wrapper.text()).not.toContain('活动设备会话接近已知上限')
    expect(wrapper.text()).toContain('已知会话上限 —')
    await wrapper.find('.el-table__expand-icon').trigger('click')
    expect(wrapper.find('.request-evidence').text()).toContain('实际协议请求 —')
    expect(wrapper.findAll('.request-samples tbody td').slice(2, 5).map(item => item.text())).toEqual(['—', '—', '—'])
    wrapper.unmount()
  })

  it('shows unavailable protocol fields as dashes rather than zero requests', async () => {
    const wrapper = mountPanel()
    await flushPromises()
    await wrapper.find('.el-table__expand-icon').trigger('click')
    expect(wrapper.find('.protocol-timing').findAll('b').map(item => item.text())).toEqual(['—', '—', '—', '—', '—'])
    wrapper.unmount()
  })

  it('shows actual protocol attempts and cumulative timing only when supplied by a polling record', async () => {
    vi.mocked(api.scanTimings).mockResolvedValue({ ...snapshot, records: [{ ...snapshot.records[0],
      protocol: { requests: 3, failedRequests: 1, cancelledRequests: 0, requestTotalMs: 45, requestMaxMs: 20, queueMs: 7 },
    }] } as any)
    const wrapper = mountPanel()
    await flushPromises()
    await wrapper.find('.el-table__expand-icon').trigger('click')
    expect(wrapper.text()).toContain('实际协议请求数')
    expect(wrapper.text()).toContain('请求累计')
    expect(wrapper.text()).toContain('45 ms')
    expect(wrapper.text()).toContain('20 ms')
    expect(wrapper.text()).toContain('7 ms')
    expect(wrapper.text()).toContain('累计可能大于墙钟耗时')
    expect(wrapper.text()).toContain('仅周期轮询')
    wrapper.unmount()
  })

  const mountPanel = (selectedChannels = new Set<string>()) => mount(ScanTimingPanel, {
    props: { selectedChannels }, global: { plugins: [ElementPlus], mocks: { $t: (value: string, params?: Record<string, string | number>) => i18n.t(value, params) } },
  })
  beforeEach(() => {
    vi.useFakeTimers()
    vi.setSystemTime(new Date('2026-09-07T08:09:10'))
    i18n.setLocale('zh-CN')
    vi.mocked(api.scanTimings).mockReset().mockImplementation(async device => ({
      ...snapshot, device: device || snapshot.device, records: device === 'Line2.Plc2' ? [] : snapshot.records,
    }) as any)
    vi.mocked(api.acquisitionDiagnosticsExport).mockReset().mockResolvedValue(new Blob(['zip'], { type: 'application/zip' }))
    vi.mocked(api.values).mockReset().mockResolvedValue([])
  })
  afterEach(() => { vi.restoreAllMocks(); vi.useRealTimers() })

  it('renders layered overview values with explicitly suspected attribution', async () => {
    vi.mocked(api.scanTimings).mockResolvedValue(layeredSnapshot as any)
    const wrapper = mountPanel()
    await flushPromises()
    const text = wrapper.text()
    expect(text).toContain('IGW 排队 P95')
    expect(text).toContain('6.5 ms')
    expect(text).toContain('批次请求最大耗时 P95')
    expect(text).toContain('18.5 ms')
    expect(text).toContain('最近 / 总体请求最大')
    expect(text).toContain('20.5 ms / 25 ms')
    expect(text).toContain('网络 RTT P95 / 丢包')
    expect(text).toContain('8.4 ms / 12.5%')
    expect(text).toContain('IGW 本地 P95')
    expect(text).toContain('3.5 ms')
    expect(text).toContain('最近归因 / 置信度')
    expect(text).toContain('网络链路疑似 / 疑似')
    wrapper.unmount()
  })

  it('renders bounded batch evidence, probe identity, and all protocol counters when expanded', async () => {
    vi.mocked(api.scanTimings).mockResolvedValue(layeredSnapshot as any)
    const wrapper = mountPanel()
    await flushPromises()
    await wrapper.find('.el-table__expand-icon').trigger('click')
    const text = wrapper.text()
    expect(text).toContain('请求数 / 失败 / 取消')
    expect(text).toContain('4 / 1 / 2')
    expect(text).toContain('45.25 ms')
    expect(text).toContain('20.5 ms')
    expect(text).toContain('7.75 ms')
    expect(text).toContain('同会话重试 / 批次降级')
    expect(text).toContain('2 / 1')
    expect(text).toContain('探测状态')
    expect(text).toContain('退化')
    expect(text).toContain('192.168.1.20')
    expect(text).toContain('192.168.1.30')
    expect(text).toContain('request-max=20.5 ms; queue=7.75 ms')
    expect(text).toContain('loss=12.5%')
    expect(text).toContain('请求拆分或重试')
    expect(api.values).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('explains that unavailable network evidence cannot distinguish network from the PLC server', async () => {
    vi.mocked(api.scanTimings).mockResolvedValue({ ...layeredSnapshot, records: [{
      ...layeredSnapshot.records[0],
      network: { ...layeredSnapshot.records[0].network, status: 'unavailable', error: 'ICMP 被防火墙禁用', latestRttMs: null, p95RttMs: null, lossPercent: null },
      attribution: { code: 'insufficient-evidence', label: '证据不足', confidence: 'low', evidence: ['network unavailable'], factors: [] },
    }] } as any)
    const wrapper = mountPanel()
    await flushPromises()
    await wrapper.find('.el-table__expand-icon').trigger('click')
    expect(wrapper.text()).toContain('当前网络证据不可用，无法区分网络链路与 PLC/OPC UA 服务器')
    expect(wrapper.text()).toContain('ICMP 被防火墙禁用')
    wrapper.unmount()
  })

  it('downloads one validated ZIP with a local timestamp and performs no point reads', async () => {
    let finish!: (blob: Blob) => void
    vi.mocked(api.acquisitionDiagnosticsExport).mockReturnValue(new Promise(resolve => { finish = resolve }))
    const clicked: string[] = []
    Object.defineProperty(URL, 'createObjectURL', { configurable: true, value: vi.fn(() => 'blob:diagnostics') })
    Object.defineProperty(URL, 'revokeObjectURL', { configurable: true, value: vi.fn() })
    vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(function (this: HTMLAnchorElement) { clicked.push(this.download) })
    const wrapper = mountPanel()
    await flushPromises()
    await wrapper.find('.export-button').trigger('click')
    await wrapper.find('.export-button').trigger('click')
    expect(api.acquisitionDiagnosticsExport).toHaveBeenCalledTimes(1)
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
    expect(api.values).not.toHaveBeenCalled()
    finish(new Blob(['zip'], { type: 'application/zip' }))
    await flushPromises()
    expect(clicked).toEqual(['igw-acquisition-diagnostics-20260907-080910.zip'])
    expect(URL.revokeObjectURL).toHaveBeenCalledWith('blob:diagnostics')
    wrapper.unmount()
  })

  it('keeps one cached polling loop across collapse and reopen without point reads', async () => {
    const wrapper = mountPanel()
    await flushPromises()
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
    await wrapper.find('.scan-toggle').trigger('click')
    await vi.advanceTimersByTimeAsync(4000)
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
    await wrapper.find('.scan-toggle').trigger('click')
    await flushPromises()
    expect(api.scanTimings).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(2000)
    expect(api.scanTimings).toHaveBeenCalledTimes(3)
    expect(api.values).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('retains the last good snapshot and shows its age after refresh failure', async () => {
    const wrapper = mountPanel()
    await flushPromises()
    vi.setSystemTime(new Date('2026-09-07T08:09:13'))
    vi.mocked(api.scanTimings).mockRejectedValueOnce(new Error('SignalR disconnected'))
    await vi.advanceTimersByTimeAsync(2000)
    await flushPromises()
    expect(wrapper.text()).toContain('Line1.Plc1')
    expect(wrapper.text()).toContain('SignalR disconnected')
    expect(wrapper.text()).toContain('诊断快照已过期：5 秒前更新')
    vi.mocked(api.scanTimings).mockRejectedValueOnce(new Error('SignalR disconnected'))
    await vi.advanceTimersByTimeAsync(2000)
    await flushPromises()
    expect(wrapper.text()).toContain('诊断快照已过期：7 秒前更新')
    wrapper.unmount()
  })

  it('advances stale age on existing ticks while one follow-up refresh remains in flight', async () => {
    const wrapper = mountPanel()
    await flushPromises()
    vi.setSystemTime(new Date('2026-09-07T08:09:13'))
    vi.mocked(api.scanTimings).mockRejectedValueOnce(new Error('SignalR disconnected'))
    await vi.advanceTimersByTimeAsync(2000)
    await flushPromises()
    expect(wrapper.text()).toContain('诊断快照已过期：5 秒前更新')
    vi.mocked(api.scanTimings).mockReturnValueOnce(new Promise(() => {}) as any)
    await vi.advanceTimersByTimeAsync(6000)
    expect(api.scanTimings).toHaveBeenCalledTimes(3)
    expect(wrapper.text()).toContain('诊断快照已过期：11 秒前更新')
    wrapper.unmount()
  })

  it('does not substitute an older protocol maximum when the newest batch has no protocol timing', async () => {
    vi.mocked(api.scanTimings).mockResolvedValue({
      ...layeredSnapshot,
      devices: [{ ...layeredSnapshot.devices[0], protocolRequestMaxMs: 99 }],
      records: [
        { ...layeredSnapshot.records[0], sequence: 4, protocol: null },
        { ...layeredSnapshot.records[0], sequence: 3, protocol: { ...layeredSnapshot.records[0].protocol, requestMaxMs: 88 } },
      ],
    } as any)
    const wrapper = mountPanel()
    await flushPromises()
    const latestAndOverall = wrapper.findAll('.layered-metrics > div')[2].text()
    expect(latestAndOverall).toContain('— / 99 ms')
    expect(latestAndOverall).not.toContain('88 ms')
    wrapper.unmount()
  })

  it('localizes real insufficient-evidence attribution, probe states, and invalid ZIP errors', async () => {
    const insufficient = {
      ...layeredSnapshot,
      devices: [{ ...layeredSnapshot.devices[0], latestAttribution: '证据不足', latestAttributionConfidence: 'low' }],
      records: [{
        ...layeredSnapshot.records[0],
        network: { ...layeredSnapshot.records[0].network, status: 'unavailable', error: null },
        attribution: { code: 'insufficient-evidence', label: '证据不足', confidence: 'low', evidence: ['network unavailable'], factors: [] },
      }],
    }
    vi.mocked(api.scanTimings).mockResolvedValue(insufficient as any)
    const chinese = mountPanel()
    await flushPromises()
    await chinese.find('.el-table__expand-icon').trigger('click')
    expect(chinese.text()).toContain('证据不足 / 低')
    expect(chinese.text()).toContain('探测状态 不可用')
    chinese.unmount()

    i18n.setLocale('en-US')
    vi.mocked(api.scanTimings).mockResolvedValue(insufficient as any)
    vi.mocked(api.acquisitionDiagnosticsExport).mockRejectedValueOnce(new Error('invalid-acquisition-diagnostics-zip'))
    const english = mountPanel()
    await flushPromises()
    await english.find('.el-table__expand-icon').trigger('click')
    expect(english.text()).toContain('Insufficient evidence / Low')
    expect(english.text()).toContain('Probe status Unavailable')
    await english.find('.export-button').trigger('click')
    await flushPromises()
    expect(english.text()).toContain('Diagnostic export response is not a ZIP file')
    english.unmount()
  })

  it('localizes a healthy probe state in both operator locales', async () => {
    const healthy = {
      ...layeredSnapshot,
      records: [{ ...layeredSnapshot.records[0], network: { ...layeredSnapshot.records[0].network, status: 'healthy' } }],
    }
    vi.mocked(api.scanTimings).mockResolvedValue(healthy as any)
    const chinese = mountPanel()
    await flushPromises()
    await chinese.find('.el-table__expand-icon').trigger('click')
    expect(chinese.text()).toContain('探测状态 健康')
    chinese.unmount()

    i18n.setLocale('en-US')
    vi.mocked(api.scanTimings).mockResolvedValue(healthy as any)
    const english = mountPanel()
    await flushPromises()
    await english.find('.el-table__expand-icon').trigger('click')
    expect(english.text()).toContain('Probe status Healthy')
    english.unmount()
  })

  it('pauses the same polling loop while the page is hidden and resumes it once visible', async () => {
    const hidden = vi.spyOn(document, 'hidden', 'get').mockReturnValue(false)
    const wrapper = mountPanel()
    await flushPromises()
    hidden.mockReturnValue(true)
    document.dispatchEvent(new Event('visibilitychange'))
    await vi.advanceTimersByTimeAsync(4000)
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
    hidden.mockReturnValue(false)
    document.dispatchEvent(new Event('visibilitychange'))
    await flushPromises()
    expect(api.scanTimings).toHaveBeenCalledTimes(2)
    await vi.advanceTimersByTimeAsync(2000)
    expect(api.scanTimings).toHaveBeenCalledTimes(3)
    expect(api.values).not.toHaveBeenCalled()
    wrapper.unmount()
  })

  it('uses management HTTP and rejects a non-ZIP acquisition export response', async () => {
    const actual = await vi.importActual<typeof import('../api')>('../api')
    const zip = new Blob(['zip'], { type: 'application/zip' })
    const get = vi.spyOn(actual.http, 'get')
      .mockResolvedValueOnce({ data: zip, headers: { 'content-type': 'application/zip' } } as any)
      .mockResolvedValueOnce({ data: new Blob(['html'], { type: 'text/html' }), headers: { 'content-type': 'text/html' } } as any)
    await expect((actual.api as any).acquisitionDiagnosticsExport()).resolves.toBe(zip)
    await expect((actual.api as any).acquisitionDiagnosticsExport()).rejects.toThrow('invalid-acquisition-diagnostics-zip')
    expect(get).toHaveBeenNthCalledWith(1, '/api/v1/diagnostics/acquisition-export', expect.objectContaining({ responseType: 'blob' }))
  })

  it('shows selected-channel devices, records, and passive subscription guidance', async () => {
    const wrapper = mountPanel(new Set(['Line1']))
    await flushPromises()
    expect(wrapper.text()).toContain('Line1.Plc1')
    expect(wrapper.text()).not.toContain('Line2.Plc2')
    expect(wrapper.text()).toContain('约 120 ms')
    expect(wrapper.text()).toContain('近 60 秒')
    expect(wrapper.text()).toContain('轮询样本吞吐')
    expect(wrapper.text()).toContain('.000')
    expect(wrapper.text()).toContain('平均完成间隔')
    expect(wrapper.text()).toContain('实际开始间隔')
    await wrapper.find('.el-table__expand-icon').trigger('click')
    expect(wrapper.text()).toContain('Line1.Plc1.A')
    await wrapper.setProps({ selectedChannels: new Set(['Line2']) })
    await flushPromises()
    expect(wrapper.text()).toContain('被动推送')
    wrapper.unmount()
  })

  it('polls every two seconds without overlap and clears polling when unmounted', async () => {
    let finish!: (value: unknown) => void
    vi.mocked(api.scanTimings).mockReturnValueOnce(new Promise(resolve => { finish = resolve }) as any)
    const wrapper = mountPanel()
    await vi.advanceTimersByTimeAsync(4000)
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.el-loading-mask').exists()).toBe(true)
    finish(snapshot); await flushPromises()
    expect(api.scanTimings).toHaveBeenCalledTimes(2)
    wrapper.unmount()
    await vi.advanceTimersByTimeAsync(4000)
    expect(api.scanTimings).toHaveBeenCalledTimes(2)
  })

  it('keeps controls usable while refreshing an existing snapshot', async () => {
    const wrapper = mountPanel()
    await flushPromises()
    vi.mocked(api.scanTimings).mockReturnValueOnce(new Promise(() => {}) as any)
    await vi.advanceTimersByTimeAsync(2000)
    expect(wrapper.find('.el-loading-mask').exists()).toBe(false)
    expect(wrapper.text()).toContain('更新中')
    wrapper.unmount()
  })

  it('pauses automatic refresh while a batch is expanded and catches up after collapse', async () => {
    vi.mocked(api.scanTimings).mockResolvedValue(layeredSnapshot as any)
    const wrapper = mountPanel()
    await flushPromises()
    await wrapper.find('.el-table__expand-icon').trigger('click')
    expect(wrapper.find('.protocol-timing').exists()).toBe(true)
    expect(wrapper.text()).toContain('展开查看中，自动刷新已暂停')

    await vi.advanceTimersByTimeAsync(4000)
    await flushPromises()
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
    expect(wrapper.find('.protocol-timing').exists()).toBe(true)

    await wrapper.find('.el-table__expand-icon').trigger('click')
    await flushPromises()
    expect(api.scanTimings).toHaveBeenCalledTimes(2)
    wrapper.unmount()
  })

  it('applies a same-filter response that takes longer than the two-second refresh interval', async () => {
    let finishSlow!: (value: unknown) => void
    vi.mocked(api.scanTimings)
      .mockReturnValueOnce(new Promise(resolve => { finishSlow = resolve }) as any)
      .mockReturnValueOnce(new Promise(() => {}) as any)
    const wrapper = mountPanel()
    await vi.advanceTimersByTimeAsync(2500)
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
    finishSlow(snapshot)
    await flushPromises()
    expect(wrapper.text()).toContain('Line1.Plc1')
    expect(wrapper.text()).toContain('约 120 ms')
    wrapper.unmount()
  })

  it('discards an in-flight stale response and immediately loads the latest device and tag filters', async () => {
    const wrapper = mountPanel()
    await flushPromises()
    let finishOld!: (value: unknown) => void
    vi.mocked(api.scanTimings).mockReturnValueOnce(new Promise(resolve => { finishOld = resolve }) as any)
    await vi.advanceTimersByTimeAsync(2000)
    const vm = wrapper.vm as any
    vm.selectedDevice = 'Line2.Plc2'
    vm.changeDevice()
    vm.tagQuery = 'Pressure'
    vm.searchTags()
    await vi.advanceTimersByTimeAsync(300)
    expect(api.scanTimings).toHaveBeenCalledTimes(2)
    finishOld(snapshot)
    await flushPromises()
    expect(api.scanTimings).toHaveBeenLastCalledWith('Line2.Plc2', 'Pressure', 200)
    expect(wrapper.text()).toContain('被动推送')
    wrapper.unmount()
  })

  it('ignores a late response after unmount without scheduling a follow-up', async () => {
    let finish!: (value: unknown) => void
    vi.mocked(api.scanTimings).mockReturnValueOnce(new Promise(resolve => { finish = resolve }) as any)
    const wrapper = mountPanel()
    wrapper.unmount()
    finish(snapshot)
    await flushPromises()
    expect(api.scanTimings).toHaveBeenCalledTimes(1)
  })

  it('shows empty and request-error states', async () => {
    vi.mocked(api.scanTimings).mockResolvedValueOnce({ ...snapshot, devices: [], device: null, records: [] } as any)
    const empty = mountPanel()
    await flushPromises(); expect(empty.text()).toContain('暂无采集耗时数据'); empty.unmount()
    vi.mocked(api.scanTimings).mockRejectedValueOnce(new Error('timing unavailable'))
    const failed = mountPanel()
    await flushPromises(); expect(failed.text()).toContain('timing unavailable'); failed.unmount()
  })
})
