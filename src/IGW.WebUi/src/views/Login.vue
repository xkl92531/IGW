<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { changePassword, errorMessage, firstAllowedPath, login, logout, restoreSession, session } from '../auth'
import { useI18n } from '../i18n'

/**
 * 登录页。账号还在用初始密码(新建用户、管理员重置过密码、首次安装的 admin)时，
 * 登录后先在这里改密码，改完才能进入网关。
 */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const username = ref('')
const password = ref('')
const oldPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const busy = ref(false)
const error = ref('')
const mustChange = computed(() => !!session.account?.mustChangePassword)

onMounted(async () => {
  // 已登录(刷新页面、从别的页面被送回来)：凭证有效就直接进入或进入改密码
  if (!session.token) return
  try {
    const account = session.account ?? await restoreSession()
    if (account && !account.mustChangePassword) enter()
  } catch { /* 凭证失效，留在登录页 */ }
})

function enter() {
  const redirect = typeof route.query.redirect === 'string' ? route.query.redirect : ''
  const target = redirect && redirect !== '/login' ? redirect : firstAllowedPath()
  if (!target) { error.value = t('这个账号还没有分配任何页面的权限，请联系管理员'); return }
  void router.replace(target)
}

async function submitLogin() {
  if (busy.value) return
  if (!username.value.trim() || !password.value) { error.value = t('请输入用户名和密码'); return }
  busy.value = true; error.value = ''
  try {
    const account = await login(username.value.trim(), password.value)
    if (account.mustChangePassword) oldPassword.value = password.value
    else enter()
  } catch (reason) { error.value = errorMessage(reason, t('登录失败，请检查网关是否在运行')) }
  finally { busy.value = false; password.value = '' }
}

async function submitChange() {
  if (busy.value) return
  if (newPassword.value.length < 6) { error.value = t('新密码至少 6 位'); return }
  if (newPassword.value !== confirmPassword.value) { error.value = t('两次输入的新密码不一致'); return }
  busy.value = true; error.value = ''
  try {
    await changePassword(oldPassword.value, newPassword.value)
    enter()
  } catch (reason) { error.value = errorMessage(reason, t('修改密码失败')) }
  finally { busy.value = false }
}

function backToLogin() {
  logout()
  oldPassword.value = newPassword.value = confirmPassword.value = ''
  error.value = ''
}
</script>

<template>
  <div class="login-page">
    <svg class="network" viewBox="0 0 600 400" aria-hidden="true">
      <g stroke="#36a852" stroke-opacity=".35" stroke-width="1.2" fill="none">
        <path d="M340 120 L380 90 L450 110 L480 60 L540 70 M380 90 L400 170 L470 190 L520 150 L580 160 M400 170 L360 230 L420 290 L500 270 L560 310 M470 190 L500 270 M360 230 L300 260 L320 330 L400 350" />
      </g>
      <g fill="#36a852" fill-opacity=".6">
        <circle cx="340" cy="120" r="4" /><circle cx="380" cy="90" r="4" /><circle cx="450" cy="110" r="4" />
        <circle cx="480" cy="60" r="4" /><circle cx="540" cy="70" r="4" /><circle cx="400" cy="170" r="4" />
        <circle cx="470" cy="190" r="4" /><circle cx="520" cy="150" r="4" /><circle cx="580" cy="160" r="4" />
        <circle cx="360" cy="230" r="4" /><circle cx="420" cy="290" r="4" /><circle cx="500" cy="270" r="4" />
        <circle cx="560" cy="310" r="4" /><circle cx="300" cy="260" r="4" /><circle cx="320" cy="330" r="4" /><circle cx="400" cy="350" r="4" />
      </g>
    </svg>

    <div class="login-card">
      <div class="login-brand">
        <span class="login-logo">IGW</span>
        <div>
          <div class="login-title">{{ t('工业网关') }}</div>
          <div class="login-sub">{{ mustChange ? t('首次登录请先修改密码') : t('请登录后使用') }}</div>
        </div>
      </div>

      <form v-if="!mustChange" class="login-form" @submit.prevent="submitLogin">
        <el-input v-model="username" size="large" :placeholder="t('用户名')" autocomplete="username" autofocus />
        <el-input v-model="password" size="large" type="password" show-password :placeholder="t('密码')" autocomplete="current-password" />
        <div v-if="error" class="login-error" role="alert">{{ error }}</div>
        <el-button type="primary" size="large" native-type="submit" :loading="busy" class="login-submit">{{ t('登录') }}</el-button>
        <div class="login-hint">{{ t('首次使用：管理员账号 admin，初始密码 admin') }}</div>
      </form>

      <form v-else class="login-form" @submit.prevent="submitChange">
        <div class="login-who">{{ t('账号：{name}', { name: session.account?.username || '' }) }}</div>
        <el-input v-if="!oldPassword" v-model="oldPassword" size="large" type="password" show-password :placeholder="t('当前密码')" autocomplete="current-password" />
        <el-input v-model="newPassword" size="large" type="password" show-password :placeholder="t('新密码（至少 6 位）')" autocomplete="new-password" autofocus />
        <el-input v-model="confirmPassword" size="large" type="password" show-password :placeholder="t('再次输入新密码')" autocomplete="new-password" />
        <div v-if="error" class="login-error" role="alert">{{ error }}</div>
        <el-button type="primary" size="large" native-type="submit" :loading="busy" class="login-submit">{{ t('修改密码并进入') }}</el-button>
        <el-button text class="login-back" @click="backToLogin">{{ t('换个账号登录') }}</el-button>
      </form>
    </div>
  </div>
</template>

<style scoped>
.login-page { position: fixed; inset: 0; display: flex; align-items: center; justify-content: center; padding: 16px;
  background: linear-gradient(135deg, #ffffff 0%, #f0f7f1 55%, #dcefe0 100%); overflow: hidden; }
.network { position: absolute; right: -40px; bottom: -30px; width: min(760px, 90vw); height: auto; pointer-events: none; }
.login-card { position: relative; width: 380px; max-width: 100%; background: #fff; border: 1px solid #e3e8ef; border-radius: 12px;
  box-shadow: 0 12px 40px rgba(24, 48, 32, .10); padding: 30px 30px 24px; }
.login-brand { display: flex; align-items: center; gap: 12px; margin-bottom: 24px; }
.login-logo { background: #1A5FB4; color: #fff; font-weight: 700; border-radius: 8px; padding: 6px 10px; letter-spacing: 1px; font-size: 18px; }
.login-title { font-size: 20px; font-weight: 600; color: #1f2d3d; }
.login-sub { font-size: 13px; color: #7a8594; margin-top: 2px; }
.login-form { display: flex; flex-direction: column; gap: 14px; }
.login-who { font-size: 14px; color: #475467; }
.login-error { color: #c0392b; font-size: 13px; line-height: 1.5; }
.login-submit { width: 100%; margin-top: 4px; }
.login-back { align-self: center; margin-left: 0 !important; }
.login-hint { font-size: 12px; color: #98a2b3; text-align: center; }
</style>
