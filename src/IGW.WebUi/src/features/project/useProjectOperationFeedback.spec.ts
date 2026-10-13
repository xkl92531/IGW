import { describe, expect, it, vi } from 'vitest'
import { useProjectOperationFeedback } from './useProjectOperationFeedback'

describe('project operation feedback', () => {
  it('paints before work, keeps the label while delayed, and coalesces double clicks', async () => {
    let painted!: () => void, finish!: (value: number) => void
    const feedback = useProjectOperationFeedback(() => new Promise<void>(resolve => { painted = resolve }))
    const work = vi.fn(() => new Promise<number>(resolve => { finish = resolve }))
    const first = feedback.run('save', '保存属性…', work)
    expect(feedback.busy.value).toBe(true)
    expect(feedback.label.value).toBe('保存属性…')
    expect(work).not.toHaveBeenCalled()
    expect(feedback.run('save', '保存属性…', work)).toBe(first)
    painted(); await Promise.resolve()
    expect(work).toHaveBeenCalledTimes(1)
    finish(7)
    await expect(first).resolves.toBe(7)
    expect(feedback.busy.value).toBe(false)
  })
  it('clears a failed action and permits retry while independent activity remains visible', async () => {
    const feedback = useProjectOperationFeedback(async () => {})
    let finish!: () => void
    const other = feedback.run('tree', '展开工程树…', () => new Promise<void>(resolve => { finish = resolve }))
    await expect(feedback.run('save', '保存属性…', () => Promise.reject(new Error('offline')))).rejects.toThrow('offline')
    expect(feedback.busy.value).toBe(true)
    expect(feedback.label.value).toBe('展开工程树…')
    await expect(feedback.run('save', '保存属性…', () => 1)).resolves.toBe(1)
    finish(); await other
    expect(feedback.busy.value).toBe(false)
  })
})
