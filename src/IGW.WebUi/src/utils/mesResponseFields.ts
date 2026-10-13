/**
 * 从一段接口响应 JSON 解析出可直接填进「响应字段」的路径。
 *
 * 路径格式必须和后端 MesUploadEngine.FlattenResponse 一致，否则配出来的字段运行时取不到值：
 * 对象用点分(根层不加前缀)、数组用下标，且只有标量叶子才算字段——对象和数组本身
 * 写不进 PLC 点位，后端也不会把它们放进可取值的字典。
 */

/** 单个可选字段：路径 + 样例值，界面上给个预览，配错了一眼能看出来。 */
export interface MesResponseField {
  path: string
  /** 样例响应里这个字段的值，仅用于界面预览。 */
  preview: string
}

const MaxFields = 2000
const MaxDepth = 64

function previewOf(value: unknown): string {
  if (typeof value === 'string') return value.length > 40 ? value.slice(0, 40) + '…' : value
  return String(value)
}

/**
 * 按后端语义展开响应 JSON。达到字段数或层级上限时停止展开，返回已收集的部分，
 * 不抛异常——样例 JSON 异常大时也不该让配置页卡死或白屏。
 */
export function responseFields(value: unknown): MesResponseField[] {
  const rows: MesResponseField[] = []
  const seen = new Set<string>()
  const visit = (current: unknown, path: string, depth: number) => {
    if (rows.length >= MaxFields || depth > MaxDepth) return
    if (Array.isArray(current)) {
      current.forEach((item, index) => visit(item, `${path}[${index}]`, depth + 1))
      return
    }
    if (current !== null && typeof current === 'object') {
      for (const [key, child] of Object.entries(current as Record<string, unknown>))
        visit(child, path ? `${path}.${key}` : key, depth + 1)
      return
    }
    // 标量叶子才是字段；根位置的标量没有路径可用，和后端一样跳过
    if (!path || seen.has(path)) return
    seen.add(path)
    rows.push({ path, preview: previewOf(current) })
  }
  visit(value, '', 0)
  return rows
}

/** 解析一段响应 JSON 文本。非法 JSON 返回错误文本而不是抛出，调用方直接显示。 */
export function parseResponseFields(text: string): { fields: MesResponseField[]; error: string } {
  const trimmed = (text || '').trim()
  if (!trimmed) return { fields: [], error: '' }
  try {
    return { fields: responseFields(JSON.parse(trimmed)), error: '' }
  } catch (error) {
    return { fields: [], error: error instanceof Error ? error.message : String(error) }
  }
}

/**
 * 从日志系统取回的报文正文里取出响应 JSON。
 * 报文按「===== 发送内容 ===== / ===== 返回内容 =====」分段保存；后端 exchange 接口
 * 已经拆好，但导出的原始报文仍是整段，这里两种都认。
 */
export function extractResponseBody(text: string): string {
  const marker = text.indexOf('===== 返回内容 =====')
  return (marker < 0 ? text : text.slice(marker + '===== 返回内容 ====='.length)).trim()
}
