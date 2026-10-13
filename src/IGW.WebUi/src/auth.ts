import { computed, reactive } from 'vue'
import { http } from './api'
import { clearSessionToken, getSessionToken, setSessionToken } from './utils/sessionToken'

/** 页面权限：看不到 / 只能看 / 可以修改。 */
export type AccessLevel = 'None' | 'View' | 'Edit'
export type ModuleId = 'project' | 'flow' | 'monitor' | 'performance' | 'logs' | 'settings'

export interface AccountDto {
  username: string
  displayName: string
  isAdmin: boolean
  mustChangePassword: boolean
  disabled: boolean
  createdUtc: string
  lastLoginUtc?: string | null
  permissions: Record<string, AccessLevel>
}

export interface ModuleDto { id: ModuleId; name: string }

/** 左侧菜单(路由)和权限页面的对应关系。 */
export const routeModules: Record<string, ModuleId> = {
  '/project': 'project',
  '/flow': 'flow',
  '/monitor': 'monitor',
  '/performance': 'performance',
  '/logs': 'logs',
  '/settings': 'settings',
}

/** 页面需要授权码里的功能模块：没授权时菜单隐藏、页面进不去。 */
const routeFeatures: Partial<Record<ModuleId, string>> = { flow: 'Mes' }

export const session = reactive({
  token: getSessionToken(),
  account: null as AccountDto | null,
  /** 授权码里没授权的功能模块 Id(来自 /api/v1/license)。 */
  unlicensedFeatures: [] as string[],
})

/** 已登录且改过初始密码(还在用初始密码时只能停在登录页改密码)。 */
export const isLoggedIn = computed(() => !!session.token && !!session.account && !session.account.mustChangePassword)

export function levelOf(module: ModuleId): AccessLevel {
  const account = session.account
  if (!account) return 'None'
  if (account.isAdmin) return 'Edit'
  return account.permissions?.[module] || 'None'
}

export function featureLicensed(module: ModuleId): boolean {
  const feature = routeFeatures[module]
  return !feature || !session.unlicensedFeatures.includes(feature)
}

export function canView(module: ModuleId): boolean {
  return levelOf(module) !== 'None' && featureLicensed(module)
}

export function canEdit(module: ModuleId): boolean {
  return levelOf(module) === 'Edit' && featureLicensed(module)
}

export function moduleOfPath(path: string): ModuleId | undefined {
  return routeModules[path]
}

/** 当前账号能看的第一个页面(登录后、没有权限时跳转用)。 */
export function firstAllowedPath(): string | null {
  for (const [path, module] of Object.entries(routeModules)) if (canView(module)) return path
  return null
}

function applySession(token: string, account: AccountDto) {
  session.token = token
  session.account = account
  setSessionToken(token)
}

export function clearSession() {
  session.token = ''
  session.account = null
  clearSessionToken()
}

async function loadLicenseFeatures() {
  try {
    const license = (await http.get('/api/v1/license')).data as { features?: { id: string; allowed: boolean }[] }
    session.unlicensedFeatures = (license.features || []).filter((item) => !item.allowed).map((item) => item.id)
  } catch { session.unlicensedFeatures = [] }
}

/** 用本地凭证取当前账号；凭证失效时清掉并返回 null。 */
export async function restoreSession(): Promise<AccountDto | null> {
  if (!session.token) return null
  try {
    session.account = (await http.get<AccountDto>('/api/v1/auth/me')).data
    await loadLicenseFeatures()
    return session.account
  } catch (error: any) {
    if (error?.response?.status === 401) clearSession()
    throw error
  }
}

export async function login(username: string, password: string): Promise<AccountDto> {
  const result = (await http.post<{ token: string; account: AccountDto }>('/api/v1/auth/login', { username, password })).data
  applySession(result.token, result.account)
  if (!result.account.mustChangePassword) await loadLicenseFeatures()
  return result.account
}

export async function changePassword(oldPassword: string, newPassword: string): Promise<AccountDto> {
  const result = (await http.post<{ token: string; account: AccountDto }>('/api/v1/auth/password', { oldPassword, newPassword })).data
  applySession(result.token, result.account)
  await loadLicenseFeatures()
  return result.account
}

export function logout() {
  clearSession()
}

/** 接口返回的中文错误说明(登录/用户管理接口返回 { error, message })。 */
export function errorMessage(error: any, fallback: string): string {
  return error?.response?.data?.message || error?.message || fallback
}

export const usersApi = {
  list: () => http.get<{ modules: ModuleDto[]; users: AccountDto[] }>('/api/v1/users').then((r) => r.data),
  create: (body: { username: string; displayName: string; password: string; permissions: Record<string, AccessLevel> }) =>
    http.post<AccountDto>('/api/v1/users', body).then((r) => r.data),
  update: (username: string, body: { displayName?: string; permissions?: Record<string, AccessLevel>; disabled?: boolean; password?: string }) =>
    http.put<AccountDto>(`/api/v1/users/${encodeURIComponent(username)}`, body).then((r) => r.data),
  remove: (username: string) => http.delete(`/api/v1/users/${encodeURIComponent(username)}`).then((r) => r.data),
}
