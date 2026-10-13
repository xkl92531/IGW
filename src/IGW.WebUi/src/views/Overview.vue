<script setup lang="ts">
import { computed, ref, onMounted, onUnmounted } from 'vue'
import { api, type HealthDto } from '../api'
import { useRealtimeStore } from '../stores/realtime'

const health = ref<HealthDto | null>(null)
const realtime = useRealtimeStore()
const devices = computed(() => [...realtime.deviceStates.values()])
let healthTimer: ReturnType<typeof setInterval> | undefined

async function load() {
  try { health.value = await api.health() } catch { /* 保留上次 */ }
}
onMounted(() => { void load(); healthTimer = setInterval(load, 10_000) })
onUnmounted(() => clearInterval(healthTimer))
</script>

<template>
  <div>
    <el-row :gutter="14">
      <el-col :span="6"><div class="card stat"><div class="k">工程</div><div class="v">{{ health?.project || '—' }}</div></div></el-col>
      <el-col :span="6"><div class="card stat"><div class="k">标记 / 活动</div><div class="v">{{ health?.tags ?? 0 }} <small>/ {{ health?.activeTags ?? 0 }}</small></div></div></el-col>
      <el-col :span="6"><div class="card stat"><div class="k">设备状态</div><div class="v" :style="{color: ((health?.devicesOnline || 0) + (health?.devicesStandby || 0) === health?.devices)?'#1B7F37':'#C0392B'}">{{ health?.devicesOnline ?? 0 }} <small>在线 · {{ health?.devicesStandby ?? 0 }} 待机 / {{ health?.devices ?? 0 }}</small></div></div></el-col>
      <el-col :span="6"><div class="card stat"><div class="k">数据客户端(OPC UA)</div><div class="v">{{ health?.opcUaSessions ?? 0 }}</div></div></el-col>
    </el-row>

    <div class="card" style="margin-top:14px">
      <div class="ttl">设备状态</div>
      <el-table :data="devices" size="small" style="width:100%" :cell-style="{padding:'6px 0'}">
        <el-table-column prop="name" label="设备" min-width="200" />
        <el-table-column label="状态" width="110">
          <template #default="{ row }">
            <el-tag :type="row.state==='Connected'?'success':row.state==='Standby'?'info':'danger'" effect="light" size="small">{{ row.state==='Connected'?'在线':row.state==='Standby'?'待机':row.state }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column prop="tagCount" label="点数" width="90" />
        <el-table-column label="读OK" width="100"><template #default="{row}">{{ row.stats?.readsOk ?? 0 }}</template></el-table-column>
        <el-table-column label="读错" width="90"><template #default="{row}">{{ row.stats?.readsErr ?? 0 }}</template></el-table-column>
        <el-table-column label="成功率%" width="100"><template #default="{row}">{{ (row.stats?.successRate ?? 0).toFixed(0) }}</template></el-table-column>
        <el-table-column label="目标 / P95" width="120"><template #default="{row}">{{ row.stats?.configuredMinPeriodMs || '—' }} / {{ row.stats?.actualP95PeriodMs || '—' }} ms</template></el-table-column>
        <el-table-column label="点/秒" width="100"><template #default="{row}">{{ Math.round(row.stats?.samplesPerSecond || 0) }}</template></el-table-column>
        <el-table-column label="周期状态" width="110"><template #default="{row}"><el-tag v-if="row.stats?.configuredMinPeriodMs" :type="row.stats?.periodHealthy===false?'warning':'success'" size="small" effect="plain">{{ row.stats?.periodHealthy===false ? `超时 ${row.stats?.overduePeriods || 0}` : '正常' }}</el-tag><span v-else>—</span></template></el-table-column>
        <el-table-column label="最近错误" min-width="200"><template #default="{row}"><span style="color:#C0392B">{{ row.stats?.lastError }}</span></template></el-table-column>
      </el-table>
    </div>
  </div>
</template>

<style scoped>
.stat .k { color: #888; font-size: 13px; }
.stat .v { font-size: 26px; font-weight: 700; margin-top: 4px; }
.stat .v small { font-size: 15px; color: #999; font-weight: 500; }
.ttl { font-weight: 600; margin-bottom: 8px; }
</style>
