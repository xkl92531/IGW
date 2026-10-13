<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed, watch } from 'vue'
import RequestActivity from './components/RequestActivity.vue'
import ChangePasswordDialog from './components/ChangePasswordDialog.vue'
import { useRoute, useRouter } from 'vue-router'
import en from 'element-plus/es/locale/lang/en'
import zhCn from 'element-plus/es/locale/lang/zh-cn'
import { ElMessage } from 'element-plus'
import { api, setForbiddenHandler, setUnauthorizedHandler, type HealthDto } from './api'
import { canEdit, canView, clearSession, isLoggedIn, logout, moduleOfPath, session, type ModuleId } from './auth'
import { useI18n, type IgwLocale } from './i18n'
import { RealtimeStreamClient } from './utils/realtimeStream'
import { globalRuntimeTopics } from './utils/runtimeTopics'
import { useRealtimeStore } from './stores/realtime'

const route = useRoute()
const router = useRouter()
const { locale, setLocale, t } = useI18n()
const elementLocale = computed(() => locale.value === 'en-US' ? en : zhCn)
const health = ref<HealthDto | null>(null)
const online = ref(false)
const applyingConfiguration = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined
let realtimeRestartTimer: ReturnType<typeof setTimeout> | undefined
let realtimeMounted = false
const realtime = useRealtimeStore()
const runtimeRealtime = new RealtimeStreamClient({
  onFrame: frame => realtime.apply(frame),
  onState: state => {
    realtime.setConnectionState(state)
    if (state === 'project-changed') scheduleRuntimeRestart(1_000)
  },
})

function scheduleRuntimeRestart(delayMs: number) {
  clearTimeout(realtimeRestartTimer)
  if (!realtimeMounted) return
  realtimeRestartTimer = setTimeout(() => void startRuntimeRealtime(), delayMs)
}

async function startRuntimeRealtime() {
  try {
    const scope = await api.projectTagScope({ limit: 1 })
    realtime.configure(scope.realtimeRevision)
    runtimeRealtime.start({
      protocolVersion: 1,
      clientId: `runtime-ui-${Date.now().toString(36)}`,
      projectRevision: scope.realtimeRevision,
      tagIds: [],
      // 全局仅保留设备状态与 MES 可用性；任务状态由 MES 页面按需订阅。
      topics: globalRuntimeTopics(),
      includeSnapshot: false,
    })
  } catch {
    realtime.setConnectionState('reconnecting')
    // 初次加载可能早于登录或网关热重载完成；不能因为一次 HTTP 失败永久失去实时状态。
    scheduleRuntimeRestart(5_000)
  }
}

async function poll() {
  try {
    const result = await api.health()
    if (!realtimeMounted) return
    health.value = result
    online.value = true
    applyingConfiguration.value = false
  } catch (error: any) {
    if (!realtimeMounted) return
    online.value = false
    applyingConfiguration.value = error?.response?.status === 503
      && error?.response?.data?.status === 'reloading'
  } finally {
    // Schedule after settlement so slow requests cannot overlap or overwrite newer health.
    if (realtimeMounted) timer = setTimeout(() => void poll(), 3000)
  }
}
// 实时状态要登录后才订阅(取点位范围的接口需要登录)；退出登录时停掉
let realtimeStarted = false
watch(isLoggedIn, (loggedIn) => {
  if (!realtimeMounted) return
  if (loggedIn && !realtimeStarted) { realtimeStarted = true; void startRuntimeRealtime() }
  else if (!loggedIn && realtimeStarted) { realtimeStarted = false; clearTimeout(realtimeRestartTimer); runtimeRealtime.stop() }
})
onMounted(() => {
  realtimeMounted = true
  void poll()
  if (isLoggedIn.value) { realtimeStarted = true; void startRuntimeRealtime() }
})
onUnmounted(() => { realtimeMounted = false; clearTimeout(timer); clearTimeout(realtimeRestartTimer); runtimeRealtime.stop() })

// 登录失效 → 回登录页(登录后回到原页面)；没有权限 → 提示原因(同一原因 3 秒内只提示一次)
setUnauthorizedHandler(() => {
  clearSession()
  if (!route.meta?.public) void router.replace({ path: '/login', query: { redirect: route.fullPath } })
})
let lastForbidden = { message: '', at: 0 }
setForbiddenHandler((code, message) => {
  if (code === 'password_change_required') {
    if (!route.meta?.public) void router.replace({ path: '/login', query: { redirect: route.fullPath } })
    return
  }
  if (message === lastForbidden.message && Date.now() - lastForbidden.at < 3000) return
  lastForbidden = { message, at: Date.now() }
  ElMessage.warning(message)
})

const navItems: { path: string; module: ModuleId; label: string }[] = [
  { path: '/project', module: 'project', label: '工程配置' },
  { path: '/flow', module: 'flow', label: '采集流程' },
  { path: '/monitor', module: 'monitor', label: '监控中心' },
  { path: '/performance', module: 'performance', label: '采集性能' },
  { path: '/logs', module: 'logs', label: '日志中心' },
  { path: '/settings', module: 'settings', label: 'IGW 设置' },
]
/** 用户与权限不按页面授权，只有管理员能看到。 */
const visibleNav = computed(() => [
  ...navItems.filter((item) => canView(item.module)),
  ...(session.account?.isAdmin ? [{ path: '/users', label: '用户与权限' }] : []),
])
const publicPage = computed(() => !!route.meta?.public)
/** 当前页面只有查看权限：页面顶部提示，修改会被网关拒绝。 */
const readOnlyPage = computed(() => {
  const module = moduleOfPath(route.path)
  return !!module && !!session.account && canView(module) && !canEdit(module)
})
const passwordDialog = ref(false)
const accountName = computed(() => session.account?.displayName || session.account?.username || '')

function handleAccountCommand(command: string) {
  if (command === 'password') passwordDialog.value = true
  else if (command === 'logout') { logout(); void router.replace('/login') }
}

const lic = computed(() => health.value?.license)
const licColor = computed(() => {
  const s = lic.value?.status
  if (!s) return '#888'
  if (s === 'Valid') return '#1B7F37'
  if (s === 'Trial' || s === 'ExpiringSoon' || s === 'Grace') return '#B8860B'
  return '#C0392B'
})
const licText = computed(() => {
  const l = lic.value; if (!l) return t('授权: —')
  switch (l.status) {
    case 'Valid': return t('授权: 有效') + (l.expiry ? ' · ' + l.expiry : '')
    case 'Trial': return t('授权: 试用中 · 剩 {days} 天', { days: l.daysLeft ?? 0 })
    case 'ExpiringSoon': return t('授权: 即将到期 · 剩 {days} 天', { days: l.daysLeft ?? 0 })
    case 'Grace': return t('授权: 宽限期')
    case 'Expired': return t('授权: 已过期')
    case 'TrialExpired': return t('授权: 试用已结束')
    case 'NotActivated': return t('授权: 未激活')
    default: return t('授权: {status}', { status: l.status })
  }
})
const active = computed(() => route.path)
let languageSaveQueue: Promise<unknown> = Promise.resolve()

function handleLanguageCommand(command: IgwLocale) {
  setLocale(command)
  languageSaveQueue = languageSaveQueue
    .catch(() => undefined)
    .then(() => api.updateUiPreferences(command))
    .catch(() => ElMessage.warning(t('语言已切换，但保存到网关数据库失败')))
}
</script>

<template>
  <el-config-provider :locale="elementLocale">
    <RequestActivity />
    <router-view v-if="publicPage" />
    <el-container v-else class="app">
      <el-header class="hdr">
        <div class="brand">
          <span class="logo">IGW</span>
          <span class="title">{{ t('工业网关') }}</span>
        </div>
        <el-menu :default-active="active" mode="horizontal" router class="nav" :ellipsis="false">
          <el-menu-item v-for="item in visibleNav" :key="item.path" :index="item.path">{{ t(item.label) }}</el-menu-item>
        </el-menu>
        <div class="right">
          <el-dropdown trigger="click" @command="handleLanguageCommand">
            <button class="language-switch" type="button" :aria-label="t('切换语言')">
              <svg class="language-icon" viewBox="0 0 24 24" aria-hidden="true">
                <circle cx="12" cy="12" r="9" />
                <path d="M3 12h18M12 3c2.5 2.5 3.8 5.5 3.8 9S14.5 18.5 12 21M12 3C9.5 5.5 8.2 8.5 8.2 12S9.5 18.5 12 21" />
              </svg>
              <span>{{ locale === 'zh-CN' ? '中文' : 'EN' }}</span>
              <span class="language-caret">▾</span>
            </button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="zh-CN">
                  <span class="language-check">{{ locale === 'zh-CN' ? '✓' : '' }}</span>中文
                </el-dropdown-item>
                <el-dropdown-item command="en-US">
                  <span class="language-check">{{ locale === 'en-US' ? '✓' : '' }}</span>English
                </el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
          <span class="dot" :style="{ background: applyingConfiguration ? '#B8860B' : online ? '#2E8B30' : '#B33' }"></span>
          <span class="conn">{{ t(applyingConfiguration ? '正在应用配置' : online ? '已连接' : '未连接') }}</span>
          <el-tag class="lic" :style="{ color: licColor, borderColor: licColor }" effect="plain"
                  @click="router.push({ path: '/settings', query: { focus: 'license' } })">🔑 {{ licText }}</el-tag>
          <el-dropdown v-if="session.account" trigger="click" @command="handleAccountCommand">
            <button class="account-switch" type="button" :aria-label="t('账号')">
              <span class="account-avatar">{{ accountName.slice(0, 1).toUpperCase() }}</span>
              <span>{{ accountName }}</span>
              <span v-if="session.account.isAdmin" class="account-role">{{ t('管理员') }}</span>
              <span class="language-caret">▾</span>
            </button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="password">{{ t('修改密码') }}</el-dropdown-item>
                <el-dropdown-item command="logout" divided>{{ t('退出登录') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </el-header>
      <!-- 工程配置页面较重(整份工程数据)：切到其他页面时保留，切回来只校验版本，不再重新下载和渲染。 -->
      <el-main class="main">
        <el-alert v-if="readOnlyPage" class="readonly-banner" type="info" :closable="false" show-icon
          :title="t('只读：你只有查看这个页面的权限，修改不会被保存。需要修改请联系管理员。')" />
        <router-view v-slot="{ Component }"><keep-alive include="Project"><component :is="Component" /></keep-alive></router-view>
      </el-main>
    </el-container>
    <ChangePasswordDialog v-model="passwordDialog" />
  </el-config-provider>
</template>

<style>
/* 全站严重度颜色 token(ISA-18.2 语义,画布/色块/异常中心/徽章共用):
   红=失败/根因离线(需要现在处理) 橙=卡住 黄=慢于基线(预警) 灰=信息 绿=正常 */
:root {
  --sev-fail: #C0392B;  --sev-fail-bg: #FBEAE8;
  --sev-stuck: #E67E22; --sev-stuck-bg: #FDF0E3;
  --sev-slow: #D9970D;  --sev-slow-bg: #FBF4E0;
  --sev-info: #8A94A0;  --sev-info-bg: #F1F3F6;
  --sev-ok: #1B7F37;    --sev-ok-bg: #E8F5EC;
}
html, body, #app { height: 100%; margin: 0; }
body { background: #f3f5f8; }
.app { height: 100vh; }
.hdr { display: flex; align-items: center; gap: 18px; background: #fff; border-bottom: 1px solid #e6e9ee; padding: 0 18px; }
.brand { display: flex; align-items: center; gap: 8px; }
.logo { background: #1A5FB4; color: #fff; font-weight: 700; border-radius: 6px; padding: 3px 8px; letter-spacing: 1px; }
.title { font-weight: 600; font-size: 16px; }
.nav { flex: 1; border-bottom: none !important; }
.right { display: flex; align-items: center; gap: 8px; }
.language-switch { height: 30px; display: inline-flex; align-items: center; gap: 6px; padding: 0 9px; border: 1px solid #d8dee8; border-radius: 7px; background: #fff; color: #344054; font: inherit; font-size: 13px; cursor: pointer; transition: border-color .16s, color .16s, background .16s; }
.language-switch:hover, .language-switch:focus-visible { color: #1a5fb4; border-color: #8eb6e8; background: #f3f8ff; outline: none; }
.language-icon { width: 15px; height: 15px; fill: none; stroke: currentColor; stroke-width: 1.7; stroke-linecap: round; stroke-linejoin: round; }
.language-caret { color: #98a2b3; font-size: 10px; }
.language-check { display: inline-block; width: 18px; color: #1a5fb4; font-weight: 700; }
.dot { width: 10px; height: 10px; border-radius: 50%; display: inline-block; }
.conn { color: #666; font-size: 13px; }
.lic { cursor: pointer; font-weight: 600; }
.account-switch { height: 30px; display: inline-flex; align-items: center; gap: 6px; padding: 0 9px 0 4px; border: 1px solid #d8dee8; border-radius: 16px; background: #fff; color: #344054; font: inherit; font-size: 13px; cursor: pointer; }
.account-switch:hover, .account-switch:focus-visible { border-color: #8eb6e8; background: #f3f8ff; outline: none; }
.account-avatar { width: 22px; height: 22px; border-radius: 50%; background: #1A5FB4; color: #fff; display: inline-flex; align-items: center; justify-content: center; font-size: 12px; font-weight: 700; }
.account-role { color: #1A5FB4; background: #eaf2fd; border-radius: 8px; padding: 0 6px; font-size: 12px; }
.readonly-banner { margin-bottom: 12px; }
.main { padding: 16px; }
.card { background: #fff; border: 1px solid #e6e9ee; border-radius: 8px; padding: 14px 16px; }
/* QC/流程工作窗只在业务层内互相置顶；下拉/菜单必须高于业务弹窗，系统消息再高一层。 */
.el-overlay { z-index: 8000 !important; }
.el-popper { z-index: 8500 !important; }
.el-message { z-index: 9000 !important; }
.el-notification { z-index: 9000 !important; }
</style>
