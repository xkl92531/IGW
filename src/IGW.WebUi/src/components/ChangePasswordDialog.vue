<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { changePassword, errorMessage } from '../auth'
import { useI18n } from '../i18n'

const props = defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
const { t } = useI18n()
const oldPassword = ref('')
const newPassword = ref('')
const confirmPassword = ref('')
const busy = ref(false)
const error = ref('')

watch(() => props.modelValue, (open) => {
  if (open) { oldPassword.value = newPassword.value = confirmPassword.value = ''; error.value = '' }
})

async function submit() {
  if (busy.value) return
  if (!oldPassword.value) { error.value = t('请输入当前密码'); return }
  if (newPassword.value.length < 6) { error.value = t('新密码至少 6 位'); return }
  if (newPassword.value !== confirmPassword.value) { error.value = t('两次输入的新密码不一致'); return }
  busy.value = true; error.value = ''
  try {
    await changePassword(oldPassword.value, newPassword.value)
    ElMessage.success(t('密码已修改'))
    emit('update:modelValue', false)
  } catch (reason) { error.value = errorMessage(reason, t('修改密码失败')) }
  finally { busy.value = false }
}
</script>

<template>
  <el-dialog :model-value="modelValue" :title="t('修改密码')" width="400px" append-to-body
    @update:model-value="emit('update:modelValue', $event)">
    <form class="password-form" @submit.prevent="submit">
      <el-input v-model="oldPassword" type="password" show-password :placeholder="t('当前密码')" autocomplete="current-password" />
      <el-input v-model="newPassword" type="password" show-password :placeholder="t('新密码（至少 6 位）')" autocomplete="new-password" />
      <el-input v-model="confirmPassword" type="password" show-password :placeholder="t('再次输入新密码')" autocomplete="new-password" />
      <div v-if="error" class="password-error" role="alert">{{ error }}</div>
      <button type="submit" hidden />
    </form>
    <template #footer>
      <el-button @click="emit('update:modelValue', false)">{{ t('取消') }}</el-button>
      <el-button type="primary" :loading="busy" @click="submit">{{ t('确定') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.password-form { display: flex; flex-direction: column; gap: 12px; }
.password-error { color: #c0392b; font-size: 13px; }
</style>
