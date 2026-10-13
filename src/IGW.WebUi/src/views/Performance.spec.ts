import { describe, expect, it } from 'vitest'
import { readFileSync } from 'node:fs'
import { resolve } from 'node:path'

describe('collection performance route', () => {
  it('keeps the complete performance page while also exposing diagnostics in the log workbench', () => {
    const router = readFileSync(resolve('src/router.ts'), 'utf8')
    const app = readFileSync(resolve('src/App.vue'), 'utf8')
    const monitor = readFileSync(resolve('src/views/Monitor.vue'), 'utf8')
    const logs = readFileSync(resolve('src/views/Logs.vue'), 'utf8')
    expect(router).toContain("path: '/performance'")
    expect(router).toContain("component: Performance")
    expect(app).toContain("path: '/performance'")
    expect(app).toContain("path: '/logs'")
    expect(monitor).not.toContain('<ScanTimingPanel')
    expect(monitor).toContain("router.push('/performance')")
    expect(logs).toContain('<ScanTimingPanel />')
  })
})
