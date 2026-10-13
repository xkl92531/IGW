/**
 * 导入 DB 源文件时用过的 DB 号，按块名记在本机浏览器里。
 * TIA「从块生成源」导出的 .db 文件不带 DB 号，同一个块再次导入时直接带出上次填的号。
 */
const KEY = 'igw.siemens.db-numbers.v1'

function load(): Record<string, number> {
  try {
    const saved = JSON.parse(localStorage.getItem(KEY) || '{}')
    return saved && typeof saved === 'object' ? saved : {}
  } catch { return {} }
}

const norm = (name: string) => String(name || '').trim().toLowerCase()

export function rememberedDbNumber(name: string): number | null {
  const value = load()[norm(name)]
  return Number.isInteger(value) && value > 0 ? value : null
}

export function rememberDbNumber(name: string, number: number) {
  if (!norm(name) || !Number.isInteger(number) || number <= 0) return
  const all = load()
  all[norm(name)] = number
  try { localStorage.setItem(KEY, JSON.stringify(all)) } catch { /* 浏览器禁用存储时只是下次不会自动带出 */ }
}

/** 在 PLC 在线列出的数据块里按块名找 DB 号(不区分大小写)。 */
export function findDbNumber(blocks: { name: string; number: number }[] | undefined, name: string): number | null {
  const match = (blocks || []).find((block) => norm(block.name) === norm(name))
  return match && Number.isInteger(match.number) && match.number > 0 ? match.number : null
}
