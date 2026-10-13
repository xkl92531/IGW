<script setup lang="ts">
import { useRouter } from 'vue-router'
import { logout, session } from '../auth'
import { useI18n } from '../i18n'

const { t } = useI18n()
const router = useRouter()
function switchAccount() { logout(); void router.replace('/login') }
</script>

<template>
  <div class="card no-access">
    <h3>{{ t('没有可用的页面') }}</h3>
    <p>{{ t('账号 {name} 还没有分配任何页面的权限，请联系管理员在「用户与权限」里分配。', { name: session.account?.username || '' }) }}</p>
    <el-button type="primary" @click="switchAccount">{{ t('换个账号登录') }}</el-button>
  </div>
</template>

<style scoped>
.no-access { max-width: 520px; margin: 60px auto; text-align: center; }
.no-access p { color: #667085; line-height: 1.7; }
</style>
