import { describe, expect, it } from 'vitest'
import { enUSMessages } from '../i18n/catalog'
import {
  captureRuntimeLogViewport,
  levelPresentation,
  restoreRuntimeLogViewport,
  runtimeLogQueryParams,
  runtimeLogRefreshPolicy,
  runtimeLogRetentionDraft,
} from './runtimeLogs'

describe('runtime log center presentation', () => {
  it('maps runtime level codes to stable severity labels', () => {
    expect(levelPresentation('ERR')).toEqual({ type: 'danger', label: '错误' })
    expect(levelPresentation('WRN')).toEqual({ type: 'warning', label: '警告' })
    expect(levelPresentation('INF')).toEqual({ type: 'info', label: '信息' })
  })

  it('omits empty filters and keeps the selected remote log day', () => {
    expect(runtimeLogQueryParams({ level: '', search: '  PLC8  ', date: '2026-08-26', limit: 500 }))
      .toEqual({ search: 'PLC8', date: '2026-08-26', limit: 500 })
  })

  it('accepts null after clearing the date picker', () => {
    expect(runtimeLogQueryParams({ level: '', search: '', date: null, limit: 500 }))
      .toEqual({ limit: 500 })
  })

  it('refreshes silently without a loading mask or disruptive error toast', () => {
    expect(runtimeLogRefreshPolicy('silent')).toEqual({
      showLoading: false,
      showError: false,
      preserveViewport: true,
    })
    expect(runtimeLogRefreshPolicy('manual')).toEqual({
      showLoading: true,
      showError: true,
      preserveViewport: false,
    })
  })

  it('does not overwrite a retention value while the user is editing it', () => {
    expect(runtimeLogRetentionDraft(14, 30, true)).toBe(14)
    expect(runtimeLogRetentionDraft(14, 30, false)).toBe(30)
  })

  it('restores the log table viewport after rows are replaced', () => {
    const scrollHost = { scrollTop: 286, scrollLeft: 41 }
    const tableRoot = { querySelector: () => scrollHost }
    const snapshot = captureRuntimeLogViewport(tableRoot)

    scrollHost.scrollTop = 0
    scrollHost.scrollLeft = 0
    restoreRuntimeLogViewport(tableRoot, snapshot)

    expect(scrollHost).toEqual({ scrollTop: 286, scrollLeft: 41 })
  })

  it('translates the always-visible retention settings action', () => {
    expect(enUSMessages['设置']).toBe('Settings')
  })
})
