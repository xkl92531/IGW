<script setup lang="ts">
import { ref, watch } from 'vue'
import { paintProjectFeedback } from '../useProjectOperationFeedback'
import { projectApi } from '../api'
import { withAccessToken } from '../../../utils/sessionToken'

const props = defineProps<{ modelValue: boolean; download?: (includeSecrets: boolean, password: string) => void | Promise<void> }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean] }>()
const includeSecrets = ref(false)
const busy = ref(false)
const error = ref('')
let generation = 0
const migrationPassword = ref('')

function nativeDownload(portable: boolean, password: string) {
  if (portable) {
    const form = document.createElement('form')
    form.method = 'post'
    form.action = withAccessToken('/api/v1/project/export')
    form.style.display = 'none'
    for (const [name, value] of [['includePortableSecrets', 'true'], ['migrationPassword', password]]) {
      const input = document.createElement('input'); input.type = 'hidden'; input.name = name; input.value = value; form.appendChild(input)
    }
    document.body.appendChild(form); form.submit(); form.remove()
  } else {
    const link = document.createElement('a')
    link.href = projectApi.exportUrl({ includePortableSecrets: false })
    link.download = ''
    document.body.appendChild(link); link.click(); link.remove()
  }
}
async function exportJson() {
  if (busy.value || (includeSecrets.value && !migrationPassword.value)) return
  const request = ++generation
  const portable = includeSecrets.value, password = migrationPassword.value
  busy.value = true; error.value = ''
  try {
    await paintProjectFeedback()
    if (request !== generation || !props.modelValue) return
    if (props.download) await props.download(portable, password)
    else nativeDownload(portable, password)
    if (request !== generation || !props.modelValue) return
    emit('update:modelValue', false)
  } catch (reason) { if (request === generation) error.value = reason instanceof Error ? reason.message : String(reason) }
  finally { if (request === generation) busy.value = false }
}
function close() { if (!busy.value) emit('update:modelValue', false) }
watch(() => props.modelValue, open => { if (!open) { generation++; busy.value = false; error.value = ''; includeSecrets.value = false; migrationPassword.value = '' } })
</script>

<template>
  <div v-if="modelValue" class="dialog-mask" @mousedown.self="close">
    <section class="exchange-dialog" role="dialog" :aria-busy="busy" aria-modal="true" aria-label="导出 JSON">
      <header><strong>导出 JSON</strong><button aria-label="关闭" :disabled="busy" @click="close">×</button></header>
      <label class="choice"><input v-model="includeSecrets" :disabled="busy" type="checkbox" />包含可迁移的连接密钥</label>
      <label v-if="includeSecrets">迁移密码<input v-model="migrationPassword" :disabled="busy" type="password" autocomplete="new-password" /></label>
      <p>工程结构按 KEP JSON 格式导出，IGW 专属采集流程和设置保存在同一 JSON 的扩展区。</p>
<p v-if="busy" role="status" aria-live="polite">正在交给浏览器下载…</p>
      <p v-if="error" role="alert">{{ error }}</p>
      <footer><button :disabled="busy" @click="close">取消</button><button data-action="export" class="primary" :disabled="busy || (includeSecrets && !migrationPassword)" @click="exportJson">导出 JSON</button></footer>
    </section>
  </div>
</template>

<style scoped>
.dialog-mask { position:fixed; inset:0; z-index:4000; display:grid; place-items:center; background:#172a3d66; }.exchange-dialog { width:min(520px,calc(100vw - 32px)); border-radius:9px; background:#fff; box-shadow:0 18px 60px #10243855; }header,footer { display:flex; align-items:center; justify-content:space-between; padding:14px 18px; border-bottom:1px solid #e1e7ee; }header button { border:0; background:transparent; font-size:22px; }footer { justify-content:flex-end; gap:8px; border-top:1px solid #e1e7ee; border-bottom:0; }label { display:flex; gap:8px; align-items:center; margin:16px 18px; color:#506175; }label:not(.choice) { display:grid; }label:not(.choice) input { padding:8px; border:1px solid #cad5e1; border-radius:5px; }p { margin:16px 18px; color:#718194; line-height:1.6; }button { padding:7px 13px; border:1px solid #c9d4df; border-radius:5px; background:#fff; cursor:pointer; }.primary { color:#fff; border-color:#3488df; background:#3488df; }button:disabled { opacity:.5; }
</style>
