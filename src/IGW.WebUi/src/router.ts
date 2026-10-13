import { createRouter, createWebHashHistory, type RouteLocationRaw } from 'vue-router'
import { beginUiActivity } from './utils/requestActivity'
import { canView, firstAllowedPath, moduleOfPath, restoreSession, session } from './auth'

const Project = () => import('./views/Project.vue')
const Monitor = () => import('./views/Monitor.vue')
const GatewaySettings = () => import('./views/GatewaySettings.vue')
const Logs = () => import('./views/Logs.vue')
const Performance = () => import('./views/Performance.vue')
const MesFlow = () => import('./views/MesFlow.vue')
const Login = () => import('./views/Login.vue')
const NoAccess = () => import('./views/NoAccess.vue')
const Users = () => import('./views/Users.vue')

// hash 路由:嵌入静态托管无需服务端 rewrite,任何 base 路径都稳
// 主页=工程配置(去掉概览,状态并入工程配置底部状态栏);点位监控=工程配置里 QC 弹窗,不再占导航
const router = createRouter({
  history: createWebHashHistory(),
  routes: [
    { path: '/', redirect: '/project' },
    { path: '/login', component: Login, meta: { title: '登录', public: true } },
    { path: '/no-access', component: NoAccess, meta: { title: '没有权限' } },
    { path: '/project', component: Project, meta: { title: '工程配置' } },
    { path: '/flow', component: MesFlow, meta: { title: '采集流程' } },
    { path: '/monitor', component: Monitor, meta: { title: '监控中心' } },
    { path: '/performance', component: Performance, meta: { title: '采集性能' } },
    { path: '/logs', component: Logs, meta: { title: '日志中心' } },
    { path: '/settings', component: GatewaySettings, meta: { title: 'IGW 设置' } },
    { path: '/users', component: Users, meta: { title: '用户与权限', adminOnly: true } },
    { path: '/license', redirect: (to) => ({ path: '/settings', query: { ...to.query, focus: 'license' } }) },
    // 旧 MES 页面链接继续可用:完整保留 task/node 等查询参数,转入监控中心的内嵌流程画布。
    { path: '/mes', redirect: (to) => ({ path: '/monitor', query: to.query }) },
    { path: '/mes/:pathMatch(.*)*', redirect: (to) => ({ path: '/monitor', query: to.query }) },
    // 旧链接兼容:/overview、/points 都回工程配置
    { path: '/overview', redirect: '/project' },
    { path: '/points', redirect: '/project' },
  ],
})

// Track lazy page loading as well as API requests. A cancelled navigation owns only its own indicator.
const navigations = new WeakMap<object, () => void>()
router.beforeEach(to => { navigations.set(to, beginUiActivity('正在打开页面…')) })

// 登录与页面权限：没登录 → 登录页；还在用初始密码 → 登录页改密码；没有这个页面的权限(用户与权限页只给管理员) → 第一个有权限的页面
router.beforeEach(async to => {
  // 改道时原导航不会走到 afterEach：先把它的「正在打开页面」提示关掉
  const redirect = (location: RouteLocationRaw) => { finishNavigation(to); return location }
  if (to.meta.public) return true
  if (!session.token) return redirect({ path: '/login', query: { redirect: to.fullPath } })
  if (!session.account) {
    try { await restoreSession() } catch { /* 网关暂时连不上：下面按未登录处理 */ }
    if (!session.account) return redirect({ path: '/login', query: { redirect: to.fullPath } })
  }
  if (session.account.mustChangePassword) return redirect({ path: '/login', query: { redirect: to.fullPath } })
  const module = moduleOfPath(to.path)
  if ((module && !canView(module)) || (to.meta.adminOnly && !session.account.isAdmin)) {
    const fallback = firstAllowedPath()
    return redirect(fallback && fallback !== to.path ? { path: fallback } : { path: '/no-access' })
  }
  return true
})
function finishNavigation(to: object) { navigations.get(to)?.(); navigations.delete(to) }
router.afterEach(to => finishNavigation(to))
router.onError((_error, to) => finishNavigation(to))
export default router
