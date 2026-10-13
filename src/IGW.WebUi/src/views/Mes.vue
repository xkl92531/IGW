<script setup lang="ts">
import { ref, computed, onUnmounted, watch } from 'vue'
import { useRoute } from 'vue-router'
import { api } from '../api'
import MesFlow from './MesFlow.vue'
import { useRealtimeStore } from '../stores/realtime'
import { RealtimeStreamClient } from '../utils/realtimeStream'
import { mesStatusTopics } from '../utils/runtimeTopics'

/** MES 上报:状态监控(实时节点状态+日志)。流程画布编辑在下一步(P3 画布)。 */
const tab = ref('status')
const realtime = useRealtimeStore()
const mes = computed<any>(() => {
  const tasks = [...realtime.mesTasks.values()]
  return {
    enabled: realtime.mesEnabled,
    uploadsOk: tasks.reduce((total, task) => total + Number(task.ok || 0), 0),
    uploadsErr: tasks.reduce((total, task) => total + Number(task.err || 0), 0),
    lastError: tasks.find(task => task.lastError)?.lastError || '',
    tasks,
  }
})
const log = ref<any[]>([])
const statusRealtime = new RealtimeStreamClient({
  onFrame: frame => realtime.apply(frame, 'mes-status'),
  onState: state => realtime.setConnectionState(state, 'mes-status'),
})

function syncStatusRealtime() {
  statusRealtime.stop()
  if (tab.value !== 'status' || !realtime.projectRevision) return
  realtime.clearMesTasks()
  statusRealtime.start({
    protocolVersion: 1,
    clientId: `mes-status-${Date.now().toString(36)}`,
    projectRevision: realtime.projectRevision,
    tagIds: [],
    topics: mesStatusTopics(),
    includeSnapshot: false,
  })
}
watch([tab, () => realtime.projectRevision], syncStatusRealtime, { immediate: true })
onUnmounted(() => statusRealtime.stop())

// 监控中心跳转:?task=任务名&node=节点Id → 直接进画布并聚焦肇事节点
const route = useRoute()
const focusTask = computed(() => (route.query.task as string) || '')
const focusNode = computed(() => (route.query.node as string) || '')
watch(() => route.query, () => { if (focusTask.value) tab.value = 'flow' }, { immediate: true })

async function loadLog() {
  try { log.value = await api.mesLog(80) } catch {}
}
watch(tab, value => { if (value === 'log') void loadLog() })

function nodeType(s: any) { if (s.state === 'fail' || s.stuck) return 'danger'; return ({ ok: 'success', wait: 'warning', run: 'primary' } as any)[s.state] || 'info' }
function nodeText(s: any) { const t = ({ ok: '通过', fail: '失败', wait: '等待', run: '执行中' } as any)[s.state] || s.state; return s.stuck ? t + '·卡住' : t }
</script>

<template>
  <div>
    <el-tabs v-model="tab">
      <el-tab-pane label="运行状态" name="status">
        <div v-if="!mes?.enabled" class="card"><el-empty description="本工程未启用 MES 采集上报" /></div>
        <template v-else>
          <div class="card sum">
            MES 采集上报 · 已启用
            <el-tag type="success" effect="plain">上报成功 {{ mes.uploadsOk }}</el-tag>
            <el-tag type="danger" effect="plain" style="margin-left:6px">失败 {{ mes.uploadsErr }}</el-tag>
            <span v-if="mes.lastError" class="err">最近错误:{{ mes.lastError }}</span>
          </div>
          <div class="card" style="margin-top:12px" v-for="t in mes.tasks" :key="t.name">
            <div class="tname">{{ t.name }} <small>{{ t.group }}</small>
              <el-tag size="small" type="success" effect="plain" style="margin-left:8px">OK {{ t.ok }}</el-tag>
              <el-tag size="small" type="danger" effect="plain" style="margin-left:4px">Err {{ t.err }}</el-tag>
              <small v-if="t.lastAt" style="margin-left:8px;color:#999">最近 {{ t.lastAt }}</small>
            </div>
            <div class="nodes">
              <div v-for="n in t.nodes" :key="n.id" class="nchip">
                <el-tag :type="nodeType(n)" effect="light" size="small">{{ n.title || n.id }} · {{ nodeText(n) }}</el-tag>
                <div class="ninfo" v-if="n.info">{{ n.info }}</div>
              </div>
            </div>
          </div>
        </template>
      </el-tab-pane>

      <el-tab-pane label="上报日志" name="log">
        <div class="card">
          <div style="margin-bottom:8px;text-align:right"><el-button size="small" @click="loadLog">刷新日志</el-button></div>
          <el-table :data="log" size="small" max-height="560" :cell-style="{padding:'4px 0'}">
            <el-table-column prop="ts" label="时间" width="180" />
            <el-table-column prop="task" label="任务" min-width="160" />
            <el-table-column prop="step" label="步骤" width="120" />
            <el-table-column label="结果" width="90">
              <template #default="{ row }"><el-tag size="small" :type="row.ok?'success':'danger'" effect="plain">{{ row.ok?'成功':'失败' }}</el-tag></template>
            </el-table-column>
            <el-table-column prop="http" label="HTTP" width="80" />
            <el-table-column prop="msg" label="信息" min-width="240" show-overflow-tooltip />
          </el-table>
        </div>
      </el-tab-pane>

      <el-tab-pane label="流程编辑" name="flow" lazy>
        <MesFlow v-if="tab === 'flow'" :focus-task="focusTask" :focus-node="focusNode" />
      </el-tab-pane>
    </el-tabs>
  </div>
</template>

<style scoped>
.sum { display: flex; align-items: center; gap: 4px; }
.err { color: #C0392B; margin-left: 12px; }
.tname { font-weight: 700; margin-bottom: 10px; }
.tname small { color: #999; font-weight: 400; margin-left: 6px; }
.nodes { display: flex; flex-wrap: wrap; gap: 10px; }
.nchip { min-width: 120px; }
.ninfo { color: #888; font-size: 12px; margin-top: 3px; max-width: 260px; }
</style>
