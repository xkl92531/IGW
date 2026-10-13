<script setup lang="ts">
import { computed, ref } from 'vue'
import { paintProjectFeedback } from '../features/project/useProjectOperationFeedback'
import { ElMessage } from 'element-plus'
import { useI18n } from '../i18n'

const { t: tr } = useI18n()

/** 模板管理板块:模板列表(点数/引用实例数)、导出(客户端)/导入/删除、套用向导/批量导入入口。
 *  只读展示 proj.templates;增删改一律 emit 意图,由 Project.vue 走服务端端点(校验+热重载)后重载工程——不在此就地改 proj,避免与本地未保存缓冲冲突。 */
const props = defineProps<{ proj: any; drivers: any[] }>()
const emit = defineEmits<{
  (e: 'openApply', tpl?: string): void
  (e: 'openCsv'): void
  (e: 'openEdit', tpl: any): void
  (e: 'delTpl', name: string): void
  (e: 'importTpl', json: string): void
}>()

const operation = ref('')
const templates = computed<any[]>(() => props.proj?.templates || [])
function driverName(id: string) { return props.drivers.find((d) => d.driverId === id)?.displayName || id }
function tagCount(t: any): number {
  const walk = (tags: any[], groups: any[]): number =>
    (tags?.length || 0) + (groups || []).reduce((n, g) => n + walk(g.tags, g.subGroups), 0)
  return walk(t.tags, t.groups)
}
function instanceCount(name: string): number {
  return (props.proj?.channels || []).reduce((n: number, c: any) =>
    n + (c.devices || []).filter((d: any) => (d.templateRef || '').toLowerCase() === name.toLowerCase()).length, 0)
}

async function exportTpl(t: any) {
  if (operation.value) return
  operation.value = tr('正在导出模板…')
  try {
    await paintProjectFeedback()
  const blob = new Blob([JSON.stringify(t, null, 2)], { type: 'application/json' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob); a.download = t.name.replace(/[^\w.-]/g, '_') + '.igwtpl.json'; a.click()
  URL.revokeObjectURL(a.href)
  } catch (reason: any) { ElMessage.error(reason?.message || String(reason)) }
  finally { operation.value = '' }
}

function importTpl() {
  if (operation.value) return
  const input = document.createElement('input')
  input.type = 'file'; input.accept = '.json,application/json'
  input.onchange = async () => {
    const file = input.files?.[0]; if (!file || operation.value) return
    operation.value = tr('正在读取模板文件…')
    try {
      await paintProjectFeedback()
      const text = await file.text()
      const template = JSON.parse(text)
      if (!template?.name || !template?.driverId) throw new Error(tr('不是有效的模板文件（需含 name/driverId 的 JSON）'))
      emit('importTpl', text)
    } catch (reason: any) { ElMessage.error(reason?.message || String(reason)) }
    finally { operation.value = '' }
  }
  input.click()
}
</script>

<template>
  <div class="tm" v-loading="!!operation" :element-loading-text="operation" :aria-busy="!!operation">
    <p v-if="operation" role="status">{{ operation }}</p>
    <div class="tm-h">
      <span class="tt">{{ $t('设备模板') }}</span>
      <span class="cnt">{{ templates.length }}</span>
      <span class="grow" />
      <el-button size="small" :loading="!!operation" @click="importTpl">{{ $t('导入模板…') }}</el-button>
      <el-button size="small" @click="emit('openCsv')">{{ $t('批量导入(CSV)…') }}</el-button>
      <el-button size="small" type="primary" @click="emit('openApply')">{{ $t('套用模板建设备…') }}</el-button>
    </div>

    <el-table v-if="templates.length" :data="templates" size="small" border max-height="240">
      <el-table-column prop="name" :label="$t('模板名')" min-width="150" show-overflow-tooltip />
      <el-table-column :label="$t('驱动')" width="150"><template #default="{ row }">{{ driverName(row.driverId) }}</template></el-table-column>
      <el-table-column :label="$t('点数')" width="72" align="right"><template #default="{ row }">{{ tagCount(row) }}</template></el-table-column>
      <el-table-column :label="$t('引用实例')" width="88" align="right"><template #default="{ row }">{{ instanceCount(row.name) }}</template></el-table-column>
      <el-table-column prop="description" :label="$t('说明')" min-width="120" show-overflow-tooltip />
      <el-table-column :label="$t('操作')" width="250">
        <template #default="{ row }">
          <el-button size="small" link type="primary" @click="emit('openApply', row.name)">{{ $t('套用') }}</el-button>
          <el-button size="small" link type="primary" @click="emit('openEdit', row)">{{ $t('编辑点位') }}</el-button>
          <el-button size="small" link @click="exportTpl(row)">{{ $t('导出') }}</el-button>
          <el-button size="small" link type="danger" @click="emit('delTpl', row.name)">{{ $t('删除') }}</el-button>
        </template>
      </el-table-column>
    </el-table>

    <div v-else class="empty">
      {{ $t('还没有模板。把一台配好的设备') }}<b>{{ $t('右键 → 提取为模板') }}</b>{{ $t(',即可批量套用到同机型设备。') }}
    </div>
  </div>
</template>

<style scoped>
.tm { display: flex; flex-direction: column; gap: 8px; }
.tm-h { display: flex; align-items: center; gap: 8px; }
.tm-h .tt { font-weight: 600; font-size: 14px; color: #333; }
.tm-h .cnt { background: #eef2f8; color: #1565C0; font-size: 12px; padding: 0 8px; border-radius: 10px; }
.tm-h .grow { flex: 1; }
.empty { color: #999; font-size: 13px; padding: 14px; background: #fafbfc; border: 1px dashed #e0e4ea; border-radius: 8px; }
.empty b { color: #666; }
</style>
