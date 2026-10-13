<script setup lang="ts">
import { ref, computed, watch } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../api'
import ValidationReport from './ValidationReport.vue'
import { useI18n } from '../i18n'

const { t: tr } = useI18n()

/** 套用向导:选模板 → 选同驱动通道 → 填设备名/端点/设置 → 保存并应用(服务端建实例+热重载)→ 验证报告页。
 *  报告页 extra 可收编、readFail 可跳编辑。全程走服务端端点;成功后 emit changed 让父重载工程。 */
const props = defineProps<{ modelValue: boolean; proj: any; drivers: any[]; preset?: string }>()
const emit = defineEmits<{ (e: 'update:modelValue', v: boolean): void; (e: 'changed'): void; (e: 'jump', path: string): void }>()

const phase = ref<'form' | 'report'>('form')
const jobId = ref('')
const busy = ref(false)
const form = ref<{ templateName: string; channel: string; deviceName: string; endpoint: string; settings: Record<string, string> }>(
  { templateName: '', channel: '', deviceName: '', endpoint: '', settings: {} })

const templates = computed<any[]>(() => props.proj?.templates || [])
const curTpl = computed(() => templates.value.find((t) => t.name === form.value.templateName))
function tagCountText(t: any): string {
  const walk = (tags: any[], groups: any[]): number =>
    (tags?.length || 0) + (groups || []).reduce((n: number, g: any) => n + walk(g.tags, g.subGroups), 0)
  const n = walk(t.tags, t.groups)
  return n ? `${n} 点` : ''
}
// 只列与模板同驱动的通道(套用要求通道驱动一致)
const okChannels = computed<any[]>(() => (props.proj?.channels || []).filter((c: any) => curTpl.value && c.driverId === curTpl.value.driverId))
const driver = computed(() => props.drivers.find((d) => d.driverId === curTpl.value?.driverId))
const settingFields = computed<any[]>(() => driver.value?.deviceSettings || [])

watch(() => props.modelValue, (v) => { if (v) reset() })
watch(() => form.value.templateName, () => {
  // 换模板:预填默认设置,通道若驱动不符则清空
  form.value.settings = { ...(curTpl.value?.defaultSettings || {}) }
  if (!okChannels.value.some((c) => c.name === form.value.channel)) form.value.channel = okChannels.value[0]?.name || ''
})

function reset() {
  phase.value = 'form'; jobId.value = ''; busy.value = false
  form.value = { templateName: props.preset || templates.value[0]?.name || '', channel: '', deviceName: '', endpoint: '', settings: {} }
}

const devFull = computed(() => `${form.value.channel}.${form.value.deviceName}`)

async function submit() {
  if (busy.value) return
  const f = form.value
  if (!f.templateName) { ElMessage.warning(tr('请选择模板')); return }
  if (!f.channel) { ElMessage.warning(tr('请选择同驱动的通道')); return }
  if (!f.deviceName.trim()) { ElMessage.warning(tr('请填设备名')); return }
  if (!f.endpoint.trim()) { ElMessage.warning(tr('请填端点(IP/站号)')); return }
  busy.value = true
  try {
    await api.templateApply({ templateName: f.templateName, channel: f.channel, deviceName: f.deviceName.trim(), endpoint: f.endpoint.trim(), settings: f.settings })
    emit('changed')   // 工程已变(服务端热重载),父重载
    ElMessage.success(tr('设备已创建并应用，开始验证…'))
    const r = await api.validationStart([devFull.value])
    jobId.value = r.jobId; phase.value = 'report'
  } catch (e: any) { ElMessage.error(tr('套用失败:') + (e?.response?.data?.error || e.message)) }
  finally { busy.value = false }
}

function close() { if (!busy.value) emit('update:modelValue', false) }
function onJump(path: string) { emit('jump', path); close() }
</script>

<template>
  <el-dialog :model-value="modelValue" @update:model-value="close" :title="$t('套用模板建设备')" width="720" append-to-body
             :close-on-click-modal="false" :close-on-press-escape="!busy" :show-close="!busy" top="6vh">
    <template v-if="phase === 'form'">
      <el-form :disabled="busy" label-width="110px" size="small">
        <el-form-item :label="$t('模板')">
          <el-select v-model="form.templateName" :placeholder="$t('选择模板')" style="width:100%">
            <el-option v-for="t in templates" :key="t.name" :value="t.name"
                       :label="`${t.name}（${drivers.find(d=>d.driverId===t.driverId)?.displayName || t.driverId}）`" />
          </el-select>
        </el-form-item>
        <el-form-item :label="$t('目标通道')">
          <el-select v-model="form.channel" :placeholder="$t('同驱动的通道')" style="width:100%">
            <el-option v-for="c in okChannels" :key="c.name" :value="c.name" :label="c.name" />
          </el-select>
        <div v-if="curTpl && !okChannels.length" class="hint warn">{{ $t('没有与模板驱动({driver})一致的通道,请先建对应通道。', { driver: curTpl.driverId }) }}</div>
        </el-form-item>
        <el-form-item :label="$t('设备名')"><el-input v-model="form.deviceName" :placeholder="$t('通道内唯一')" /></el-form-item>
        <el-form-item :label="$t('端点')"><el-input v-model="form.endpoint" :placeholder="$t('如 192.168.1.10:502')" /></el-form-item>
        <el-form-item v-for="s in settingFields" :key="s.key" :label="s.label">
          <el-input v-model="form.settings[s.key]" :placeholder="s.default" />
        </el-form-item>
        <div class="hint">{{ $t('保存后将立即套用模板') }} <b>{{ curTpl ? tagCountText(curTpl) : '' }}</b> {{ $t('并自动验证。') }}</div>
      </el-form>
    </template>

    <template v-else>
      <div class="rpt-head">{{ $t('设备') }} <b>{{ devFull }}</b> {{ $t('已创建,验证结果:') }}</div>
      <ValidationReport :job-id="jobId" @changed="emit('changed')" @jump="onJump" />
    </template>

    <p v-if="busy" role="status" aria-live="polite">{{ $t('正在套用模板并启动验证…') }}</p>
    <template #footer>
      <template v-if="phase === 'form'">
        <el-button :disabled="busy" @click="close">{{ $t('取消') }}</el-button>
        <el-button type="primary" :loading="busy" @click="submit">{{ $t('保存并应用') }}</el-button>
      </template>
      <el-button v-else type="primary" :disabled="busy" @click="close">{{ $t('完成') }}</el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.hint { color: #888; font-size: 12px; margin-top: 4px; }
.hint.warn { color: #C0392B; }
.rpt-head { font-size: 13px; margin-bottom: 8px; color: #333; }
</style>
