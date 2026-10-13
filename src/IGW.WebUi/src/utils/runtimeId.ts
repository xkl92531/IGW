let fallbackSequence = 0

type BrowserCrypto = {
  randomUUID?: () => string
  getRandomValues?: (array: Uint8Array) => Uint8Array
}

/**
 * 生成工程配置使用的 32 位十六进制 ID。
 * randomUUID 只在安全上下文/较新浏览器中存在，现场常通过 HTTP IP 地址访问，
 * 因此必须兼容仍提供 getRandomValues 的旧内核和非安全上下文。
 */
export function createCompactId(): string {
  const browserCrypto = globalThis.crypto as BrowserCrypto | undefined
  if (typeof browserCrypto?.randomUUID === 'function')
    return browserCrypto.randomUUID().replace(/-/g, '')

  if (typeof browserCrypto?.getRandomValues === 'function') {
    const bytes = new Uint8Array(16)
    browserCrypto.getRandomValues(bytes)
    bytes[6] = (bytes[6] & 0x0f) | 0x40
    bytes[8] = (bytes[8] & 0x3f) | 0x80
    return Array.from(bytes, value => value.toString(16).padStart(2, '0')).join('')
  }

  // 极旧 WebView 的最后兜底：时间、单调序号和随机尾数组合，避免同一毫秒内重复。
  fallbackSequence = (fallbackSequence + 1) >>> 0
  const time = Date.now().toString(16).padStart(12, '0').slice(-12)
  const sequence = fallbackSequence.toString(16).padStart(8, '0').slice(-8)
  const random = Math.floor(Math.random() * 0xffffffffffff)
    .toString(16)
    .padStart(12, '0')
    .slice(-12)
  return `${time}${sequence}${random}`
}
