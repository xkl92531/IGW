import axios from 'axios'
import { afterEach, describe, expect, it } from 'vitest'
import { installRequestActivity, uiActivities, beginUiActivity, requestActivityLabel } from './requestActivity'

describe('finite request loading feedback', () => {
  afterEach(() => expect(uiActivities.value).toHaveLength(0))
  it('keeps concurrent activity visible until every request settles, including failures', async () => {
    const pending: { config: any; resolve: (v: any) => void; reject: (v: any) => void }[] = []
    const http = axios.create({ adapter: config => new Promise((resolve, reject) => pending.push({ config, resolve, reject })) })
    installRequestActivity(http)
    const first = http.get('/api/v1/project/tags')
    const second = http.post('/api/v1/project/mutations', {})
    const failed = second.catch(error => error.message)
    await new Promise(resolve => setTimeout(resolve, 0))
    expect(uiActivities.value.map(item => item.label)).toEqual(['正在加载工程点位…', '正在保存并应用配置…'])
    pending[0].resolve({ config: pending[0].config, data: {}, status: 200, headers: {} })
    await first
    expect(uiActivities.value).toHaveLength(1)
    pending[1].reject(new axios.AxiosError('failed', 'ERR_NETWORK', pending[1].config))
    expect(await failed).toBe('failed')
  })
  it('clears cancelled requests and ignores background subscriptions and polling', async () => {
    const http = axios.create({adapter: config => Promise.reject(new axios.CanceledError('cancelled', config))})
    installRequestActivity(http)
    await expect(http.get('/api/v1/project/tags')).rejects.toThrow('cancelled')
    for (const url of ['/api/v1/project/tag-scope', '/api/v1/validation/jobs/job-1', '/api/v1/runtime-logs', '/hubs/runtime/negotiate', '/api/v1/health'])
      expect(requestActivityLabel({url, method:'get'})).toBeNull()
    expect(requestActivityLabel({url:'/api/v1/project', method:'get', igwBackground:true})).toBeNull()
  })
  it('labels a log center query as a log query instead of loading configuration', () => {
    expect(requestActivityLabel({url:'/api/v1/log-center/logs', method:'get'})).toBe('正在查询日志…')
    expect(requestActivityLabel({url:'/api/v1/log-center/logs', method:'get', igwBackground:true})).toBeNull()
  })
  it('ends a local operation idempotently without hiding another operation', () => {
    const a = beginUiActivity('A'), b = beginUiActivity('B')
    a(); a()
    expect(uiActivities.value.map(item => item.label)).toEqual(['B'])
    b()
  })
})
