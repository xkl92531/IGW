// 网页登录凭证：存在浏览器本地，关掉页面再打开仍保持登录(凭证 7 天有效，改密码/改权限后失效)。
// 单独成文件：api.ts 的请求拦截器要用，auth.ts 也要用，避免两者互相引用。
const KEY = 'igw_session'

function storage(): Storage | null {
  try { return typeof localStorage === 'undefined' ? null : localStorage } catch { return null }
}

export function getSessionToken(): string {
  return storage()?.getItem(KEY) || ''
}

export function setSessionToken(token: string) {
  storage()?.setItem(KEY, token)
}

export function clearSessionToken() {
  storage()?.removeItem(KEY)
}

/** 浏览器直接下载(链接、表单提交)带不了请求头，把凭证放进地址。 */
export function withAccessToken(url: string): string {
  const token = getSessionToken()
  if (!token) return url
  return url + (url.includes('?') ? '&' : '?') + 'access_token=' + encodeURIComponent(token)
}
