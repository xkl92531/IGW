<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { projectApi } from '../api'
import type { ProjectImportPreviewDto, ProjectImportResultDto } from '../types'

interface ImportService {
  inspectImport(file: File): Promise<ProjectImportPreviewDto>
  commitImport(file: File, token: string, fileHash: string, migrationPassword?: string): Promise<ProjectImportResultDto | void>
}
const props = defineProps<{ modelValue: boolean; service?: ImportService }>()
const emit = defineEmits<{ 'update:modelValue': [value: boolean]; imported: [] }>()
const file = ref<File | null>(null)
const preview = ref<ProjectImportPreviewDto | null>(null)
const migrationPassword = ref('')
const busy = ref(false)
const phase = ref<'inspect' | 'commit'>('inspect')
let generation = 0
const error = ref('')
const service = computed<ImportService>(() => props.service ?? projectApi)
const blocking = computed(() => preview.value?.compatibility.issues.some(issue => issue.severity === 'error') === true)
const canCommit = computed(() => !!file.value && !!preview.value && !blocking.value && (!preview.value.hasPortableSecrets || !!migrationPassword.value))

async function inspect(event: Event) {
  if (busy.value) return
  const selected = (event.target as HTMLInputElement).files?.[0]
  if (!selected) return
  const request = ++generation
  phase.value = 'inspect'
  file.value = selected
  preview.value = null
  error.value = ''
  busy.value = true
  try { const result = await service.value.inspectImport(selected); if (request === generation) preview.value = result }
  catch (reason) { if (request === generation) error.value = reason instanceof Error ? reason.message : String(reason) }
  finally { if (request === generation) busy.value = false }
}
async function commit() {
  if (busy.value || !canCommit.value || !file.value || !preview.value) return
  const request = ++generation
  phase.value = 'commit'
  busy.value = true
  error.value = ''
  try {
    await service.value.commitImport(file.value, preview.value.token, preview.value.fileHash, migrationPassword.value || undefined)
    if (request !== generation || !props.modelValue) return
    emit('imported')
    emit('update:modelValue', false)
  } catch (reason) { if (request === generation) error.value = reason instanceof Error ? reason.message : String(reason) }
  finally { if (request === generation) busy.value = false }
}
function close() { if (!busy.value) emit('update:modelValue', false) }
function reset() { generation++; busy.value = false; file.value = null; preview.value = null; migrationPassword.value = ''; error.value = '' }
watch(() => props.modelValue, open => { if (!open) reset() })
</script>

<template>
  <div v-if="modelValue" class="dialog-mask" role="presentation" @mousedown.self="close">
    <section class="exchange-dialog" role="dialog" :aria-busy="busy" aria-modal="true" aria-label="导入 JSON">
      <header><strong>导入 JSON</strong><button aria-label="关闭" :disabled="busy" @click="close">×</button></header>
      <label class="file-picker">选择工程 JSON<input type="file" accept=".json,application/json" :disabled="busy" @change="inspect" /></label>
      <p v-if="busy" class="hint" role="status" aria-live="polite">{{ phase === 'commit' ? '正在导入并应用工程，请稍候…' : '正在检查文件，请稍候…' }}</p>
      <p v-if="error" class="error">{{ error }}</p>
      <template v-if="preview">
        <div class="counts">
          <span>通道 <b>{{ preview.channels }}</b></span><span>设备 <b>{{ preview.devices }}</b></span>
          <span>组 <b>{{ preview.groups }}</b></span><span>点位 <b>{{ preview.tags }}</b></span><span>MES 任务 <b>{{ preview.mesTasks }}</b></span>
        </div>
        <ul v-if="preview.compatibility.issues.length" class="issues">
          <li v-for="issue in preview.compatibility.issues" :key="`${issue.path}:${issue.code}`" :class="issue.severity">
            <b>{{ issue.path }}</b> · {{ issue.message }}
          </li>
        </ul>
        <label v-if="preview.hasPortableSecrets">迁移密码<input v-model="migrationPassword" :disabled="busy" type="password" autocomplete="new-password" /></label>
      </template>
      <footer><button :disabled="busy" @click="close">取消</button><button data-action="commit" class="primary" :disabled="!canCommit || busy" @click="commit">替换当前工程</button></footer>
    </section>
  </div>
</template>

<style scoped>
.dialog-mask { position:fixed; inset:0; z-index:4000; display:grid; place-items:center; background:#172a3d66; }
.exchange-dialog { width:min(640px,calc(100vw - 32px)); max-height:80vh; overflow:auto; border-radius:9px; background:#fff; box-shadow:0 18px 60px #10243855; }
header,footer { display:flex; align-items:center; justify-content:space-between; padding:14px 18px; border-bottom:1px solid #e1e7ee; }
header button { border:0; background:transparent; font-size:22px; cursor:pointer; } footer { justify-content:flex-end; gap:8px; border-top:1px solid #e1e7ee; border-bottom:0; }
.file-picker,label { display:grid; gap:7px; margin:16px 18px; color:#506175; } input { padding:8px; border:1px solid #cad5e1; border-radius:5px; }
.counts { display:flex; flex-wrap:wrap; gap:8px; margin:12px 18px; }.counts span { padding:7px 10px; border-radius:5px; background:#f1f6fb; }
.issues { margin:12px 18px; padding-left:20px; }.issues li { margin:5px 0; }.issues .error,.error { color:#d14343; }.issues .warning { color:#b67700; }
.hint,.error { margin:10px 18px; } button { padding:7px 13px; border:1px solid #c9d4df; border-radius:5px; background:#fff; cursor:pointer; }.primary { color:#fff; border-color:#3488df; background:#3488df; }button:disabled { opacity:.5; cursor:not-allowed; }
</style>
