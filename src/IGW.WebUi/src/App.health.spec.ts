import { flushPromises, mount } from '@vue/test-utils'
import { afterEach, expect, it, vi } from 'vitest'
import ElementPlus from 'element-plus'
import App from './App.vue'
import { api } from './api'

vi.mock('./api', () => ({
  api: { health: vi.fn(), projectTagScope: vi.fn(async () => ({ realtimeRevision: 'r' })) },
  http: { get: vi.fn(), post: vi.fn() },
  setUnauthorizedHandler: vi.fn(),
  setForbiddenHandler: vi.fn(),
}))
vi.mock('vue-router', () => ({ useRoute: () => ({ path: '/' }), useRouter: () => ({ push: vi.fn() }) }))
vi.mock('./stores/realtime', () => ({ useRealtimeStore: () => ({ configure: vi.fn(), apply: vi.fn(), setConnectionState: vi.fn() }) }))
vi.mock('./utils/realtimeStream', () => ({ RealtimeStreamClient: class { start() {} stop() {} } }))

afterEach(() => { vi.useRealTimers(); vi.clearAllMocks() })

it('distinguishes reloading from network failure and recovers on the next health result', async () => {
  vi.useFakeTimers()
  vi.mocked(api.health).mockRejectedValueOnce({ response: { status: 503, data: { status: 'reloading' } } })
    .mockResolvedValueOnce({ status: 'ok' } as any).mockRejectedValueOnce(new Error('network'))
  const wrapper = mount(App, { global: { plugins: [ElementPlus], stubs: { RouterView: true } } })
  await flushPromises()
  expect(wrapper.find('.conn').text()).toBe('正在应用配置')
  expect(wrapper.find('.dot').attributes('style')).toContain('rgb(184, 134, 11)')
  await vi.advanceTimersByTimeAsync(3000)
  expect(wrapper.find('.conn').text()).toBe('已连接')
  await vi.advanceTimersByTimeAsync(3000)
  expect(wrapper.find('.conn').text()).toBe('未连接')
  wrapper.unmount()
})

it('does not overlap slow health requests or schedule polling after unmount', async () => {
  vi.useFakeTimers()
  let resolve!: (value: any) => void
  vi.mocked(api.health).mockReturnValue(new Promise(done => { resolve = done }))
  const wrapper = mount(App, { global: { plugins: [ElementPlus], stubs: { RouterView: true } } })
  await vi.advanceTimersByTimeAsync(9000)
  expect(api.health).toHaveBeenCalledTimes(1)
  wrapper.unmount()
  resolve({ status: 'ok' })
  await flushPromises()
  await vi.advanceTimersByTimeAsync(9000)
  expect(api.health).toHaveBeenCalledTimes(1)
})
