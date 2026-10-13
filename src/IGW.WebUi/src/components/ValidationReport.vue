<script setup lang="ts">
import { ref, onMounted, onUnmounted, computed } from 'vue'
import { ElMessage } from 'element-plus'
import { api } from '../api'
import { useI18n } from '../i18n'

const { t: tr } = useI18n()

/** 套用验证报告:轮询验证作业(jobId)→ 逐设备展示 matched/missing/extra(可浏览)或 readOk/readFail(不可浏览);
 *  extra 点可勾选一键收编进实例;readFail 点可跳转到点位编辑。收编后 emit changed 让父重载工程。 */
const props = defineProps<{ jobId: string }>()
const emit = defineEmits<{ (e: 'changed'): void; (e: 'jump', path: string): void }>()

const job = ref<any>(null)
const loadError = ref('')
const adopting = ref('')
let polling = false
let disposed = false
const sel = ref<Record<string, Set<string>>>({})   // device → 勾选收编的 extra 地址集
let timer: any

async function poll() {
  if (polling || disposed) return
  polling = true; loadError.value = ''
  try {
    const result = await api.validationJob(props.jobId)
    if (disposed) return
    job.value = result
    if (job.value?.status === 'done') clearInterval(timer)
  } catch (e: any) { if (!disposed) loadError.value = e?.message || tr('读取验证结果失败'); clearInterval(timer) }
  finally { polling = false }
}
onMounted(() => { poll(); timer = setInterval(poll, 1000) })
onUnmounted(() => { disposed = true; clearInterval(timer) })

const progress = computed(() => {
  const j = job.value; if (!j) return 0
  return j.total ? Math.round((j.done / j.total) * 100) : 100
})

function resultColor(r: string) {
  if (r === 'Matched' || r === 'ReadOk') return '#1B7F37'
  if (r === 'Missing' || r === 'ReadFail') return '#C0392B'
  if (r === 'Extra') return '#B8860B'
  return '#555'
}
function resultLabel(r: string) {
  return ({ Matched: '✔ 已对上', Missing: '⚠ 真机缺失', Extra: '✚ 真机多出', ReadOk: '✔ 读通', ReadFail: '✘ 读失败' } as any)[r] || r
}

// "通道.设备" → { channel, device }(名字均不含 '.',按首个 '.' 切)
function splitDev(full: string) {
  const i = full.indexOf('.')
  return i < 0 ? { channel: full, device: '' } : { channel: full.slice(0, i), device: full.slice(i + 1) }
}

function toggleExtra(dev: string, addr: string, on: boolean) {
  const s = sel.value[dev] || (sel.value[dev] = new Set())
  if (on) s.add(addr); else s.delete(addr)
}
function isChecked(dev: string, addr: string) { return !!sel.value[dev]?.has(addr) }
function extraCount(dev: string) { return sel.value[dev]?.size || 0 }

async function adopt(rep: any) {
  if (adopting.value) return
  const chosen = (rep.points as any[]).filter((p) => p.result === 'Extra' && isChecked(rep.device, p.address))
  if (!chosen.length) { ElMessage.info(tr('未勾选要收编的点')); return }
  const { channel, device } = splitDev(rep.device)
  adopting.value = rep.device
  try {
    const r = await api.validationAdopt(channel, device, chosen.map((p) => ({ name: p.path, address: p.address, dataType: 'int16' })))
    ElMessage.success(tr('已收编 {count} 个点，已应用', { count: r.count }))
    sel.value[rep.device]?.clear()
    emit('changed')
  } catch (e: any) { ElMessage.error(tr('收编失败:') + (e?.response?.data?.error || e.message)) }
  finally { adopting.value = '' }
}
</script>

<template>
  <div class="vr">
    <el-alert v-if="loadError" :title="loadError" type="error" :closable="false" />
    <div class="vr-head">
      <span v-if="job && job.status !== 'done'" class="running">{{ $t('验证中 {done}/{total}', { done: job.done, total: job.total }) }}</span>
      <span v-else-if="job" class="done">{{ $t('验证完成 · {count} 台', { count: job.total }) }}</span>
      <span v-else-if="!loadError" class="running" role="status">{{ $t('发起中…') }}</span>
      <el-progress v-if="job && job.status !== 'done'" :percentage="progress" :stroke-width="6" style="flex:1;margin-left:12px" />
    </div>

    <div v-for="rep in job?.devices || []" :key="rep.device" class="dev card">
      <div class="dev-h">
        <b>{{ rep.device }}</b>
        <span class="mode">{{ $t(rep.mode === 'browse' ? '浏览匹配' : rep.mode === 'read' ? '试读' : '错误') }}</span>
        <template v-if="rep.summary">
          <span v-if="rep.summary.matched" class="chip ok">✔ {{ rep.summary.matched }}</span>
          <span v-if="rep.summary.readOk" class="chip ok">{{ $t('✔ 读通 {count}', { count: rep.summary.readOk }) }}</span>
          <span v-if="rep.summary.missing" class="chip bad">{{ $t('⚠ 缺失 {count}', { count: rep.summary.missing }) }}</span>
          <span v-if="rep.summary.readFail" class="chip bad">{{ $t('✘ 失败 {count}', { count: rep.summary.readFail }) }}</span>
          <span v-if="rep.summary.extra" class="chip warn">{{ $t('✚ 多出 {count}', { count: rep.summary.extra }) }}</span>
        </template>
        <span class="grow" />
        <el-button v-if="rep.summary?.extra" size="small" type="primary" plain
                   :loading="adopting === rep.device" :disabled="!!adopting || !extraCount(rep.device)" @click="adopt(rep)">{{ $t('收编所选 ({count})', { count: extraCount(rep.device) }) }}</el-button>
      </div>

      <div v-if="rep.error" class="err">{{ rep.error }}</div>

      <el-table v-else :data="rep.points" size="small" max-height="260" border>
        <el-table-column label="" width="34">
          <template #default="{ row }">
            <el-checkbox v-if="row.result === 'Extra'" :model-value="isChecked(rep.device, row.address)"
                         @change="(v:any)=>toggleExtra(rep.device, row.address, v)" />
          </template>
        </el-table-column>
        <el-table-column prop="path" :label="$t('点位')" min-width="160" show-overflow-tooltip />
        <el-table-column prop="address" :label="$t('地址')" width="130" show-overflow-tooltip />
        <el-table-column :label="$t('结果')" width="110">
          <template #default="{ row }"><span :style="{ color: resultColor(row.result), fontWeight: 600 }">{{ resultLabel(row.result) }}</span></template>
        </el-table-column>
        <el-table-column prop="detail" :label="$t('说明')" min-width="150" show-overflow-tooltip />
        <el-table-column label="" width="86">
          <template #default="{ row }">
            <el-button v-if="row.result === 'ReadFail'" size="small" link type="primary"
                       @click="emit('jump', rep.device + '.' + row.path)">{{ $t('去编辑') }}</el-button>
          </template>
        </el-table-column>
      </el-table>
    </div>

    <div v-if="job && job.status === 'done' && !(job.devices || []).length" class="empty">{{ $t('无验证结果。') }}</div>
  </div>
</template>

<style scoped>
.vr { display: flex; flex-direction: column; gap: 10px; }
.vr-head { display: flex; align-items: center; font-size: 13px; }
.vr-head .running { color: #B8860B; } .vr-head .done { color: #1B7F37; }
.dev { padding: 8px 10px; }
.dev-h { display: flex; align-items: center; gap: 8px; font-size: 13px; margin-bottom: 6px; }
.dev-h .mode { color: #888; font-size: 12px; }
.dev-h .grow { flex: 1; }
.chip { font-size: 12px; padding: 1px 7px; border-radius: 10px; }
.chip.ok { background: #E6F4EA; color: #1B7F37; }
.chip.bad { background: #FBEAE7; color: #C0392B; }
.chip.warn { background: #FBF3E0; color: #B8860B; }
.err { color: #C0392B; font-size: 13px; padding: 4px 0; }
.empty { color: #999; font-size: 13px; padding: 10px; }
.card { background: #fff; border: 1px solid #e6e9ee; border-radius: 8px; }
</style>
