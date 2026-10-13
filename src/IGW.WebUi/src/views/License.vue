<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import { ElMessage } from 'element-plus'
import { api, type LicenseDto } from '../api'

const lic = ref<LicenseDto | null>(null)
const code = ref('')
const busy = ref(false)

async function load() { try { lic.value = await api.license() } catch (e:any) { ElMessage.error('读取失败(运行时未启动?)') } }
onMounted(load)

const label = computed(() => {
  const s = lic.value?.status
  return ({ Valid:'授权有效', Trial:'7 天试用中', ExpiringSoon:'即将到期', Grace:'已过期(宽限期内)', Expired:'已过期(已停采集)',
    TrialExpired:'试用已结束(已停采集)',
    NotActivated:'未激活', MachineMismatch:'授权码与本机不符', Tampered:'系统时间异常', Invalid:'授权码无效' } as any)[s || ''] || s || '—'
})
const color = computed(() => {
  const s = lic.value?.status
  if (s === 'Valid') return '#1B7F37'
  if (s === 'Trial' || s === 'ExpiringSoon' || s === 'Grace') return '#C86A00'
  return '#C0392B'
})

function copyMachine() {
  if (!lic.value?.machineId) return
  navigator.clipboard.writeText(lic.value.machineId).then(() => ElMessage.success('机器码已复制'))
}
async function activate() {
  const c = code.value.trim()
  if (!c) { ElMessage.warning('请先粘贴授权码'); return }
  busy.value = true
  try {
    lic.value = await api.activate(c)
    if (!lic.value.blocked) { ElMessage.success('激活成功,采集已启用'); code.value = '' }
    else ElMessage.error('激活未通过:' + (lic.value.status === 'MachineMismatch' ? '此码是发给别的机器的' : lic.value.status))
  } catch (e:any) { ElMessage.error('激活失败:' + (e?.response?.data?.error || e.message)) }
  finally { busy.value = false }
}
</script>

<template>
  <div style="max-width:720px">
    <div class="card">
      <div class="st"><span class="dot" :style="{background:color}"></span><b :style="{color}">{{ label }}</b></div>
      <div class="detail" v-if="lic">
        <span v-if="lic.customer">客户:{{ lic.customer }}　</span>
        <span v-if="lic.expiry">到期:{{ lic.expiry }}　</span>
        <span v-if="!lic.blocked && lic.daysLeft>=0 && (lic.status==='Valid'||lic.status==='Trial'||lic.status==='ExpiringSoon')">剩 {{ lic.daysLeft }} 天</span>
        <div class="msg">{{ lic.message }}</div>
      </div>
    </div>

    <div class="card" style="margin-top:14px">
      <div class="lbl">本机机器码(发给厂商换取授权码)</div>
      <div class="mrow">
        <code class="machine">{{ lic?.machineId || '—' }}</code>
        <el-button @click="copyMachine">复制机器码</el-button>
      </div>
      <div class="hint">一码一机:授权码只能在本机使用,换机需重新申请。</div>
    </div>

    <div class="card" style="margin-top:14px">
      <div class="lbl">粘贴厂商发来的授权码,点激活</div>
      <el-input v-model="code" type="textarea" :rows="4" placeholder="IGW1...." style="font-family:Consolas,monospace" />
      <el-button type="primary" :loading="busy" style="margin-top:10px" @click="activate">激活</el-button>
    </div>
  </div>
</template>

<style scoped>
.st { display: flex; align-items: center; gap: 8px; font-size: 17px; }
.dot { width: 13px; height: 13px; border-radius: 50%; display: inline-block; }
.detail { color: #555; margin-top: 8px; }
.msg { color: #888; font-size: 13px; margin-top: 4px; }
.lbl { font-weight: 600; margin-bottom: 8px; }
.mrow { display: flex; align-items: center; gap: 12px; }
.machine { font-size: 20px; font-weight: 700; color: #1A5FB4; background: #EEF4FF; border: 1px solid #B7CDEA; border-radius: 6px; padding: 8px 14px; letter-spacing: 1px; }
.hint { color: #888; font-size: 12px; margin-top: 8px; }
</style>
