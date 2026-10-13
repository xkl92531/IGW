export type ProjectGridTarget = {
  selectionKey?: string
  kind: string
  def: any
  holder?: any[]
  sourcePath?: string
}

export type ProjectClipboardItem = {
  kind: string
  mode: 'copy' | 'cut'
  snapshot?: string
  def?: any
  sourcePath?: string
}

/** 行包装对象可以重建；操作目标始终按底层定义的稳定键恢复。 */
export function resolveProjectGridTargets(
  rows: ProjectGridTarget[],
  selectedKeys: ReadonlySet<string>,
  currentKey = '',
): ProjectGridTarget[] {
  if (selectedKeys.size) return rows.filter((row) => !!row.selectionKey && selectedKeys.has(row.selectionKey))
  if (!currentKey) return []
  const current = rows.find((row) => row.selectionKey === currentKey)
  return current ? [current] : []
}

/**
 * 复制只做一次 JSON 序列化，粘贴时再反序列化；避免旧实现复制和粘贴各做一次完整深拷贝。
 * 剪切保留已脱离工程树的原对象，粘贴即移动，不再复制大型点位集合。
 */
export function createProjectClipboardItems(
  targets: ProjectGridTarget[],
  mode: 'copy' | 'cut',
): ProjectClipboardItem[] {
  return targets.map((target) => mode === 'copy'
    ? { kind: target.kind, mode, snapshot: JSON.stringify(target.def) }
    : { kind: target.kind, mode, def: target.def, sourcePath: target.sourcePath })
}

export function materializeProjectClipboardItem(item: ProjectClipboardItem): any {
  if (item.mode === 'cut') return item.def
  if (typeof item.snapshot !== 'string') throw new Error('复制项缺少快照')
  return JSON.parse(item.snapshot)
}
