<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import { ElMessage, ElMessageBox } from 'element-plus'
import { api, type SiemensAuthStatusDto } from '../api'
import { useI18n } from '../i18n'

const { t: tr } = useI18n()

// editable=false(工程配置只能看)：只显示验证状态，不能验证/保存/删除凭据
const props = withDefaults(defineProps<{ modelValue: boolean; deviceName: string; editable?: boolean }>(), { editable: true })
const emit = defineEmits<{
  (e: 'update:modelValue', value: boolean): void
  (e: 'verified'): void
  (e: 'deleted'): void
}>()

const username = ref('')
const password = ref('')
const pending = ref(false)
const loading = ref(false)
const status = ref<SiemensAuthStatusDto | null>(null)
const error = ref('')

function clearForm() {
  username.value = ''
  password.value = ''
  error.value = ''
}

function close() {
  clearForm()
  emit('update:modelValue', false)
}

function messageOf(reason: any): string {
  return reason?.response?.data?.error || reason?.message || tr('请求失败')
}

async function refresh() {
  if (!props.deviceName) return
  loading.value = true
  try { status.value = await api.siemensAuthStatus(props.deviceName) }
  catch (reason: any) { error.value = messageOf(reason) }
  finally { loading.value = false }
}

async function verify() {
  if (pending.value || loading.value || !props.editable) return
  if (!password.value) { error.value = '请输入 PLC 密码'; return }
  pending.value = true
  error.value = ''
  try {
    status.value = await api.verifySiemensAuth(props.deviceName, username.value, password.value)
    ElMessage.success(tr('PLC 身份验证成功，凭据已在本机加密保存'))
    clearForm()
    emit('verified')
    emit('update:modelValue', false)
  } catch (reason: any) {
    error.value = messageOf(reason)
  } finally { pending.value = false }
}

async function removeCredential() {
  if (pending.value || loading.value || !props.editable) return
  try {
    await ElMessageBox.confirm(tr('删除本机保存的 PLC 凭据并恢复匿名连接？'), tr('删除身份验证'), { type: 'warning' })
  } catch { return }
  pending.value = true
  error.value = ''
  try {
    await api.deleteSiemensAuth(props.deviceName)
    clearForm()
    emit('deleted')
    emit('update:modelValue', false)
    ElMessage.success(tr('已恢复匿名连接'))
  } catch (reason: any) { error.value = messageOf(reason) }
  finally { pending.value = false }
}

watch(() => props.modelValue, (open) => {
  if (open) { clearForm(); void refresh() }
  else clearForm()
})
onBeforeUnmount(clearForm)
</script>

<template>
  <el-dialog
    :model-value="modelValue"
    :title="$t('Siemens PLC 身份验证')"
    width="520px"
    append-to-body
    destroy-on-close
    :close-on-click-modal="!pending"
    @close="close"
  >
    <div class="auth-device">{{ deviceName }}</div>
    <el-alert
      v-if="status"
      :title="status.message"
      :type="status.hasCredential ? 'success' : status.state === 'Anonymous' ? 'info' : 'warning'"
      :closable="false"
      show-icon
    />
    <el-form v-loading="loading" :disabled="!editable" label-width="92px" class="auth-form" @submit.prevent="verify">
      <el-form-item :label="$t('用户名')">
        <el-input v-model="username" maxlength="256" autocomplete="username" :placeholder="$t('可选；仅密码验证时留空')" />
      </el-form-item>
      <el-form-item :label="$t('密码')">
        <el-input
          v-model="password"
          type="password"
          show-password
          maxlength="1024"
          autocomplete="new-password"
          :placeholder="$t('不会写入工程文件')"
          @keyup.enter="verify"
        />
      </el-form-item>
    </el-form>
    <div class="auth-note">{{ $t('系统会先在线连接 PLC 并验证；成功后才使用 Windows DPAPI 保存到本机。复制工程到其他电脑后需要重新输入。') }}</div>
    <el-alert v-if="error" :title="error" type="error" :closable="false" show-icon class="auth-error" />
    <template #footer>
      <el-button v-if="editable && status?.hasCredential" type="danger" plain :disabled="pending" @click="removeCredential">{{ $t('删除本机凭据') }}</el-button>
      <span class="footer-spacer" />
      <el-button :disabled="pending" @click="close">{{ $t(editable ? '取消' : '关闭') }}</el-button>
      <el-button v-if="editable" type="primary" :loading="pending" :disabled="loading" @click="verify">{{ $t('验证并保存') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.auth-device { margin: -2px 0 14px; color: #303133; font-weight: 650; }
.auth-form { margin-top: 18px; }
.auth-note { margin: 4px 0 0 92px; color: #7a8491; font-size: 12px; line-height: 1.6; }
.auth-error { margin-top: 14px; }
.footer-spacer { display: inline-block; min-width: 12px; flex: 1; }
:deep(.el-dialog__footer) { display: flex; align-items: center; }
</style>
