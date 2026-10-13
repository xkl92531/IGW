<script setup lang="ts">
import { ref, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../api'
import ValidationReport from './ValidationReport.vue'
import { useI18n } from '../i18n'

const { t: tr } = useI18n()

/** 批量导入向导:上传/粘贴 CSV → 服务端全量校验(原子)→ 有错逐行报告(行号+原因)/全过则导入并可一键验证全部新设备。 */
const props = defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{ (e: 'update:modelValue', v: boolean): void; (e: 'changed'): void; (e: 'jump', path: string): void }>()

const phase = ref<'input' | 'report'>('input')
const csv = ref('')
const rowErrors = ref<{ line: number; message: string }[]>([])
const busy = ref(false)
const reading = ref(false)
const jobId = ref('')
const importedCount = ref(0)

watch(() => props.modelValue, (v) => { if (v) { phase.value = 'input'; csv.value = ''; rowErrors.value = []; jobId.value = ''; importedCount.value = 0 } })

function pickFile() {
  if (busy.value || reading.value) return
  const input = document.createElement('input')
  input.type = 'file'; input.accept = '.csv,text/csv'
  input.onchange = async () => {
    const file = input.files?.[0]; if (!file) return
    reading.value = true
    try { const text = await file.text(); if (props.modelValue) csv.value = text }
    catch (reason: any) { ElMessage.error(reason?.message || String(reason)) }
    finally { reading.value = false }
  }
  input.click()
}

async function doImport() {
  if (busy.value || reading.value) return
  if (!csv.value.trim()) { ElMessage.warning(tr('请粘贴或上传 CSV')); return }
  busy.value = true; rowErrors.value = []
  try {
    const r = await api.templateImportCsv(csv.value)
    importedCount.value = r.count || 0
    emit('changed')
    ElMessage.success(tr('已导入 {count} 台设备并应用', { count: r.count }))
    const devices: string[] = r.devices || []
    if (devices.length) { const j = await api.validationStart(devices); jobId.value = j.jobId }
    phase.value = 'report'
  } catch (e: any) {
    const data = e?.response?.data
    if (data?.rows) { rowErrors.value = data.rows; ElMessage.error(tr('导入未执行：{count} 行有错，已定位', { count: data.rows.length })) }
    else ElMessage.error(tr('导入失败:') + (data?.error || e.message))
  } finally { busy.value = false }
}

function close() { if (!busy.value && !reading.value) emit('update:modelValue', false) }
</script>

<template>
  <el-dialog :model-value="modelValue" @update:model-value="close" :title="$t('批量导入设备(CSV)')" width="720" append-to-body
             :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy" top="6vh">
    <template v-if="phase === 'input'">
      <div class="bar">
        <el-button size="small" :loading="reading" :disabled="busy" @click="pickFile">{{ $t('选择 CSV 文件…') }}</el-button>
        <a class="dl" :href="api.templateCsvSampleUrl" download="设备清单样例.csv">{{ $t('下载样例模板') }}</a>
        <span class="grow" />
        <span class="tip">{{ $t('列:设备名 / 通道 / 模板名 / 端点(+可选设置列)。UTF-8。') }}</span>
      </div>
      <el-input :disabled="busy" v-model="csv" type="textarea" :rows="10" resize="none"
                :placeholder="$t('设备名,通道,模板名,端点\n注塑机01,Modbus通道,注塑机-A,192.168.1.11:502')" />

      <div v-if="rowErrors.length" class="errbox">
            <div class="errh">{{ $t('✘ 清单有 {count} 处错误,整体未导入(修正后重试):', { count: rowErrors.length }) }}</div>
        <el-table :data="rowErrors" size="small" border max-height="220">
          <el-table-column prop="line" :label="$t('行号')" width="72" align="right" />
          <el-table-column prop="message" :label="$t('原因')" show-overflow-tooltip />
        </el-table>
      </div>
    </template>

    <template v-else>
      <div class="rpt-head">{{ $t('已导入') }} <b>{{ importedCount }}</b> {{ $t('台设备。验证结果:') }}</div>
      <ValidationReport v-if="jobId" :job-id="jobId" @changed="emit('changed')" @jump="(p:string)=>{ emit('jump', p); close() }" />
      <div v-else class="hint">{{ $t('未发起验证(无新设备)。') }}</div>
    </template>

    <p v-if="reading" role="status">{{ $t('正在读取 CSV 文件…') }}</p>
    <p v-if="busy" role="status" aria-live="polite">{{ $t('正在导入设备并启动验证…') }}</p>
    <template #footer>
      <template v-if="phase === 'input'">
        <el-button :disabled="busy" @click="close">{{ $t('取消') }}</el-button>
        <el-button type="primary" :loading="busy" :disabled="reading" @click="doImport">{{ $t('校验并导入') }}</el-button>
      </template>
      <el-button v-else type="primary" :disabled="busy" @click="close">{{ $t('完成') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.bar { display: flex; align-items: center; gap: 10px; margin-bottom: 8px; }
.bar .grow { flex: 1; }
.bar .dl { color: #1565C0; font-size: 13px; text-decoration: none; }
.bar .dl:hover { text-decoration: underline; }
.bar .tip { color: #888; font-size: 12px; }
.errbox { margin-top: 10px; }
.errh { color: #C0392B; font-size: 13px; margin-bottom: 6px; }
.rpt-head { font-size: 13px; margin-bottom: 8px; }
.hint { color: #888; font-size: 12px; }
</style>
